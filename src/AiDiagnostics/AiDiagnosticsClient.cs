using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiDiagnostics.Tools;

namespace AiDiagnostics;

/// <summary>
/// 通过 OpenAI 兼容本地网关做 AI 分析的客户端：列举模型、把文本交给分析请求并流式取回回答。
/// </summary>
/// <remarks>
/// 网关由宿主安装并拉起（dsh-our-free-model 独立服务，SRS v2.0 FR-5.6），
/// 库只认 <see cref="GatewayOptions"/> 里的 BaseUrl 与 Key，不感知安装与进程生命周期。
/// 联网查证经可选的 <see cref="IAiToolExecutor"/>（function calling 工具循环，最多
/// <see cref="MaxToolRounds"/> 轮；上游不支持 tools 时自动降级为纯离线分析）。
/// </remarks>
public sealed class AiDiagnosticsClient
{
    /// <summary>工具循环的物理轮数上限：每次工具往返算一轮，超出后去掉 tools 让模型直接作答。</summary>
    private const int MaxToolRounds = 2;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Func<GatewayOptions> _optionsFactory;
    private readonly HttpClient _http;
    private readonly IAiToolExecutor? _tools;

    public AiDiagnosticsClient(Func<GatewayOptions>? optionsFactory = null, HttpClient? http = null, IAiToolExecutor? tools = null)
    {
        _optionsFactory = optionsFactory ?? (() => new GatewayOptions());
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });
        _tools = tools;
    }

    /// <summary>网关存活探测（<c>/health</c> 免 Key）。宿主在打开分析窗口前调用。</summary>
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _optionsFactory().BaseUrl.TrimEnd('/') + "/health");
            using var timeout = Timeout(cancellationToken, 3_000);
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>列出网关当前可用的模型（<c>GET /v1/models</c>，OpenAI 格式）。</summary>
    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var options = _optionsFactory();
        using var request = BuildRequest(HttpMethod.Get, options.BaseUrl.TrimEnd('/') + "/v1/models", options.ApiKey);
        using var timeout = Timeout(cancellationToken, options.ModelsTimeoutMs);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(DescribeHttpFailure("模型列举", (int)response.StatusCode, body));

        return ParseModels(body);
    }

    internal static IReadOnlyList<AiModel> ParseModels(string json)
    {
        var result = new List<AiModel>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;

        foreach (var m in data.EnumerateArray())
        {
            var id = m.ValueKind == JsonValueKind.Object && m.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String
                ? i.GetString()
                : null;
            if (string.IsNullOrEmpty(id)) continue;
            var owner = m.TryGetProperty("owned_by", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;

            // 网关广播 `provider/model` 复合 id（聚合渠道、链路）与裸 id（免费车道）两种形态
            var slash = id.IndexOf('/');
            var provider = slash > 0 ? id[..slash] : owner ?? "gateway";
            var name = slash > 0 ? id[(slash + 1)..] : id;
            result.Add(new AiModel(provider, id, name));
        }
        return result;
    }

    /// <summary>
    /// 发送提问并流式取回回答。回答正文增量经 <paramref name="onDelta"/> 回调（SSE 逐段原样拼接，
    /// 不要在调用方额外加分隔）；工具活动与思考状态经 <paramref name="onStatus"/> 回调（只进状态栏）。
    /// </summary>
    public async Task<AiAnswer> AskAsync(
        AiRequest request,
        Action<string>? onDelta = null,
        Action<string>? onStatus = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ModelId)) return new AiAnswer(false, "请先选择一个模型。");

        var options = _optionsFactory();
        var messages = new List<JsonObject>
        {
            Message("system", request.Agent.SystemPrompt),
            Message("user", request.Prompt),
        };

        var offerTools = request.UseTools && _tools is not null;
        var toolsDropped = false;
        var toolRounds = 0;
        var contentBuilder = new StringBuilder();

        for (var round = 0; ; round++)
        {
            var useTools = offerTools && !toolsDropped;
            using var httpRequest = BuildChatRequest(options, request.ModelId, messages, useTools ? _tools!.Definitions : null);
            using var timeout = Timeout(cancellationToken, options.AnswerTimeoutMs);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new AiAnswer(false, PartialOr("分析超时，请检查网关服务或更换模型后重试。", contentBuilder));
            }

            // 上游不支持 tools 时会整单 400：去掉 tools 重发一次，退化为纯离线分析（一期基线行为）
            if ((int)response.StatusCode == 400 && useTools)
            {
                response.Dispose();
                toolsDropped = true;
                continue;
            }

            using (response)
            {
                var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                var state = await ReadChatStreamAsync(body, contentBuilder, onDelta, onStatus, timeout.Token).ConfigureAwait(false);

                if (state is { FinishReason: "tool_calls" } && useTools && state.ToolCalls.Count > 0)
                {
                    messages.Add(AssistantToolCalls(state));
                    toolRounds++;
                    // 轮次熔断：后续请求不再提供工具，模型只能基于已有结果作答
                    if (toolRounds >= MaxToolRounds) toolsDropped = true;
                    foreach (var call in state.ToolCalls)
                    {
                        onStatus?.Invoke($"正在调用 {call.Name} …");
                        // 未知工具名兜底（FR-5.12 安全边界）：无论执行器是否自带守卫，未声明的能力一律拒绝
                        var tools = _tools!;
                        var executor = tools.Definitions.Any(d => d.Name == call.Name) ? tools : null;
                        var result = executor is null
                            ? $"Tool '{call.Name}' is not available."
                            : await executor.ExecuteAsync(call, timeout.Token).ConfigureAwait(false);
                        messages.Add(ToolResult(call.Id, result));
                    }
                    continue;
                }

                if (state.ErrorMessage is { Length: > 0 } error)
                {
                    return new AiAnswer(false, PartialOr(error, contentBuilder));
                }

                var text = contentBuilder.ToString().Trim();
                if (text.Length == 0)
                {
                    return new AiAnswer(false, "网关没有返回可解析的回答内容。");
                }
                return new AiAnswer(true, text);
            }
        }
    }

    // ---------- chat completions 流 ----------

    /// <summary>一轮 chat completions 流的状态：正文、结束原因、累积中的工具调用与流内错误。</summary>
    internal sealed class ChatStreamState
    {
        public string? FinishReason { get; set; }
        public string? ErrorMessage { get; set; }
        public List<AiToolCall> ToolCalls { get; } = [];
        internal Dictionary<int, (string? Id, string? Name, StringBuilder Args)> Pending { get; } = [];
    }

    private static async Task<ChatStreamState> ReadChatStreamAsync(
        Stream body, StringBuilder content, Action<string>? onDelta, Action<string>? onStatus, CancellationToken ct)
    {
        var state = new ChatStreamState();
        var reasoningAnnounced = false;
        var contentStarted = false;

        using var reader = new StreamReader(body);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            // 思考期间的静默由周期性 SSE 注释帧（":" 开头）填充，跳过
            if (line.Length == 0 || line.StartsWith(':')) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]") break;

            JsonObject? frame;
            try
            {
                frame = JsonNode.Parse(payload) as JsonObject;
            }
            catch (JsonException)
            {
                continue; // 半截帧丢弃，完整帧会另起一行
            }
            if (frame is null) continue;

            if (frame["error"] is JsonObject error)
            {
                state.ErrorMessage ??= error["message"]?.GetValue<string>() ?? payload;
                continue;
            }

            if (frame["choices"] is not JsonArray choices || choices.Count == 0) continue;
            if (choices[0] is not JsonObject choice) continue;

            if (choice["finish_reason"] is JsonValue finish && finish.TryGetValue<string>(out var finishReason))
            {
                state.FinishReason = finishReason;
            }

            if (choice["delta"] is not JsonObject delta) continue;

            if (delta["reasoning_content"] is JsonValue reasoning &&
                reasoning.TryGetValue<string>(out var r) && r.Length > 0 && !reasoningAnnounced)
            {
                reasoningAnnounced = true;
                onStatus?.Invoke("模型思考中…");
            }

            if (delta["content"] is JsonValue contentValue && contentValue.TryGetValue<string>(out var chunk) && chunk.Length > 0)
            {
                if (!contentStarted)
                {
                    contentStarted = true;
                    onStatus?.Invoke("正在生成回答…");
                }
                content.Append(chunk);
                onDelta?.Invoke(chunk);
            }

            if (delta["tool_calls"] is JsonArray calls)
            {
                foreach (var node in calls)
                {
                    if (node is not JsonObject call) continue;
                    var index = call["index"] is JsonValue idx && idx.TryGetValue<int>(out var i) ? i : 0;
                    var slot = state.Pending.TryGetValue(index, out var existing)
                        ? existing
                        : (null, null, new StringBuilder());
                    if (call["id"] is JsonValue id && id.TryGetValue<string>(out var idStr) && idStr.Length > 0)
                        slot.Item1 = idStr;
                    if (call["function"] is JsonObject fn)
                    {
                        if (fn["name"] is JsonValue name && name.TryGetValue<string>(out var n) && n.Length > 0)
                            slot.Item2 ??= n;
                        if (fn["arguments"] is JsonValue args && args.TryGetValue<string>(out var a))
                            slot.Item3.Append(a);
                    }
                    state.Pending[index] = slot;
                }
            }
        }

        state.ToolCalls.AddRange(state.Pending
            .Where(kv => kv.Value.Item2 is not null)
            .OrderBy(kv => kv.Key)
            .Select(kv => new AiToolCall(kv.Value.Item1 ?? $"call_{kv.Key}", kv.Value.Item2!, kv.Value.Item3.ToString())));
        return state;
    }

    // ---------- 消息与请求构造 ----------

    private static HttpRequestMessage BuildChatRequest(
        GatewayOptions options, string modelId, List<JsonObject> messages, IReadOnlyList<AiToolDefinition>? definitions)
    {
        var payload = new JsonObject
        {
            ["model"] = modelId,
            ["messages"] = new JsonArray([.. messages.Select(m => (JsonNode)m.DeepClone())]),
            ["stream"] = true,
            ["temperature"] = 0.1,
        };
        if (definitions is { Count: > 0 })
        {
            payload["tool_choice"] = "auto";
            payload["tools"] = new JsonArray([.. definitions.Select(d =>
            {
                JsonNode? schema = JsonNode.Parse(d.JsonSchema);
                return (JsonNode)new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = d.Name,
                        ["description"] = d.Description,
                        ["parameters"] = schema,
                    },
                };
            })]);
        }
        return BuildRequest(HttpMethod.Post, options.BaseUrl.TrimEnd('/') + "/v1/chat/completions", options.ApiKey, payload);
    }

    private static JsonObject Message(string role, string content) => new()
    {
        ["role"] = role,
        ["content"] = content,
    };

    private static JsonObject AssistantToolCalls(ChatStreamState state) => new()
    {
        ["role"] = "assistant",
        ["content"] = null,
        ["tool_calls"] = new JsonArray([.. state.ToolCalls.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id,
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = c.Name,
                ["arguments"] = c.ArgumentsJson,
            },
        })]),
    };

    private static JsonObject ToolResult(string callId, string result) => new()
    {
        ["role"] = "tool",
        ["tool_call_id"] = callId,
        ["content"] = result,
    };

    private static HttpRequestMessage BuildRequest(HttpMethod method, string url, string apiKey, JsonObject? payload = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(apiKey)) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        if (payload is not null)
        {
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        }
        return request;
    }

    private static CancellationTokenSource Timeout(CancellationToken outer, int ms)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(ms);
        return cts;
    }

    private static string PartialOr(string failure, StringBuilder partial) =>
        partial.Length > 0 ? $"{failure}\n\n已收到的部分内容：\n{partial}" : failure;

    private static string DescribeHttpFailure(string action, int status, string body)
    {
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var m))
            {
                message = m.GetString();
            }
            else if (doc.RootElement.TryGetProperty("message", out var m2))
            {
                message = m2.GetString();
            }
        }
        catch (JsonException) { }
        var detail = message is { Length: > 0 } ? message : FirstLine(body);
        return $"{action}失败（HTTP {status}）：{detail}";
    }

    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed.Length > 300 ? trimmed[..300] : trimmed;
        }
        return "(无输出)";
    }
}
