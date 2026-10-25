using System.Net;
using System.Text;
using AiDiagnostics;
using AiDiagnostics.Agents;
using AiDiagnostics.Tools;

namespace AiDiagnostics.Test;

/// <summary>
/// 网关客户端契约测试（SRS v2.0 FR-5.8/5.9/5.12）：用内存桩模拟
/// OpenAI 兼容网关的 /v1/models 与 /v1/chat/completions（SSE）。
/// </summary>
public class AiDiagnosticsClientTests : IDisposable
{
    private readonly HttpStub _stub = new();

    public void Dispose() => _stub.Dispose();

    private AiDiagnosticsClient CreateClient(IAiToolExecutor? tools = null) =>
        new(() => new GatewayOptions { BaseUrl = _stub.BaseUrl, ApiKey = "test-key" }, http: _stub.Http, tools: tools);

    private static AiAgentProfile Analyst => new()
    {
        SystemPrompt = "只读分析日志。",
    };

    private static AiRequest Request(string modelId = "deepseek-v4.1-flash-free", bool useTools = false) => new()
    {
        Prompt = "分析这段日志。",
        ModelId = modelId,
        Agent = Analyst,
        UseTools = useTools,
    };

    // ---------- /v1/models ----------

    [Fact]
    public async Task ListModels_ParsesOpenAiFormat()
    {
        _stub.Respond["/v1/models"] = _ => (200, "application/json",
            """{"data":[{"id":"deepseek-v4.1-flash-free","object":"model","owned_by":"our-free-model"},{"id":"kilo-auto/free","object":"model"}]}""");

        var models = await CreateClient().ListModelsAsync();

        Assert.Equal(2, models.Count);
        // 复合 id 在首个 / 处拆出 provider 展示用；ModelId 保留原始 id（发送给网关的原样取值）
        Assert.Equal("deepseek-v4.1-flash-free", models[0].ModelId);
        Assert.Equal("deepseek-v4.1-flash-free", models[0].DisplayName);
        Assert.Equal("kilo-auto/free", models[1].ModelId);
        Assert.Equal("free", models[1].DisplayName);
    }

    [Fact]
    public async Task ListModels_AuthError_ThrowsWithBodyMessage()
    {
        _stub.Respond["/v1/models"] = _ => (401, "application/json", """{"error":{"message":"Invalid API key"}}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateClient().ListModelsAsync());

        Assert.Contains("401", ex.Message);
        Assert.Contains("Invalid API key", ex.Message);
    }

    // ---------- /v1/chat/completions 的非 2xx（审查 #12a：真实错误不能被吞成「无可解析内容」） ----------

    [Fact]
    public async Task AskAsync_AuthError_ReturnsFailureWithStatusAndBody()
    {
        _stub.Respond["/v1/chat/completions"] = _ => (401, "application/json", """{"error":{"message":"Invalid API key"}}""");

        var answer = await CreateClient().AskAsync(Request());

        Assert.False(answer.Success);
        Assert.Contains("401", answer.Text);
        Assert.Contains("Invalid API key", answer.Text);
    }

    [Fact]
    public async Task AskAsync_RateLimited_ReturnsFailureWithStatus()
    {
        _stub.Respond["/v1/chat/completions"] = _ => (429, "application/json", """{"error":{"message":"Rate limit exceeded"}}""");

        var answer = await CreateClient().AskAsync(Request());

        Assert.False(answer.Success);
        Assert.Contains("429", answer.Text);
    }

    // ---------- /v1/chat/completions（SSE） ----------

    [Fact]
    public async Task AskAsync_DeltasAreJoinedInOrder()
    {
        _stub.Respond["/v1/chat/completions"] = _ => (200, "text/event-stream", Sse(
            """{"choices":[{"delta":{"role":"assistant","content":"结论：显存不足"}}]}""",
            """{"choices":[{"delta":{"content":"。步骤：加 --lowvram"}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]"));

        var deltas = new List<string>();
        var answer = await CreateClient().AskAsync(Request(), onDelta: deltas.Add);

        Assert.True(answer.Success);
        Assert.Equal("结论：显存不足。步骤：加 --lowvram", answer.Text);
        Assert.Equal(new[] { "结论：显存不足", "。步骤：加 --lowvram" }, deltas);
    }

    [Fact]
    public async Task AskAsync_CommentHeartbeatAndReasoning_AreNotContent()
    {
        _stub.Respond["/v1/chat/completions"] = _ => (200, "text/event-stream", Sse(
            ": keep-alive",
            """{"choices":[{"delta":{"reasoning_content":"思考中"}}]}""",
            """{"choices":[{"delta":{"content":"正文"}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]"));

        var statuses = new List<string>();
        var answer = await CreateClient().AskAsync(Request(), onStatus: statuses.Add);

        Assert.True(answer.Success);
        Assert.Equal("正文", answer.Text);
        Assert.Equal(new[] { "模型思考中…", "正在生成回答…" }, statuses);
    }

    [Fact]
    public async Task AskAsync_InStreamError_IsReportedAsFailure()
    {
        _stub.Respond["/v1/chat/completions"] = _ => (200, "text/event-stream", Sse(
            """{"choices":[{"delta":{"content":"部分"}}]}""",
            """{"error":{"message":"Model is unavailable"}}"""));

        var answer = await CreateClient().AskAsync(Request());

        Assert.False(answer.Success);
        Assert.Contains("Model is unavailable", answer.Text);
        Assert.Contains("部分", answer.Text);
    }

    [Fact]
    public async Task AskAsync_EmptyStream_Fails()
    {
        _stub.Respond["/v1/chat/completions"] = _ => (200, "text/event-stream", Sse(
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""", "[DONE]"));

        var answer = await CreateClient().AskAsync(Request());

        Assert.False(answer.Success);
        Assert.Contains("没有返回", answer.Text);
    }

    [Fact]
    public async Task AskAsync_NoModelSelected_FailsFast() =>
        Assert.False((await CreateClient().AskAsync(Request(""))).Success);

    // ---------- 工具循环（FR-5.12） ----------

    [Fact]
    public async Task AskAsync_ToolCallsAreExecutedAndFedBack()
    {
        _stub.Respond["/v1/chat/completions"] = _ =>
        {
            if (_stub.Bodies.Count == 1)
            {
                return (200, "text/event-stream", Sse(
                    """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"websearch","arguments":"{\"query\":\"HIP\"}"}}]}}]}""",
                    """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
                    "[DONE]"));
            }
            return (200, "text/event-stream", Sse(
                """{"choices":[{"delta":{"content":"结合检索结果的结论"}}]}""",
                """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
                "[DONE]"));
        };

        var answer = await CreateClient(tools: new RecordingTools()).AskAsync(Request(useTools: true));

        Assert.True(answer.Success);
        Assert.Equal("结合检索结果的结论", answer.Text);
        // 第二次请求必须携带 assistant 工具调用与 role:"tool" 结果
        var second = _stub.Bodies[1];
        Assert.Contains("websearch", second);
        Assert.Contains("call_1", second);
        Assert.Contains("\"role\":\"tool\"", second);
        Assert.Contains("SEARCH RESULT", second);
    }

    [Fact]
    public async Task AskAsync_ToolRoundLimit_ThenAnswerWithoutTools()
    {
        _stub.Respond["/v1/chat/completions"] = _ => (200, "text/event-stream", Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_loop","type":"function","function":{"name":"websearch","arguments":"{}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            "[DONE]"));

        var answer = await CreateClient(tools: new RecordingTools()).AskAsync(Request(useTools: true));

        // 达到轮次上限后去掉 tools 再发一次；桩恒返回 tool_calls 且无正文，最后一轮按正常结束判失败
        Assert.False(answer.Success);
        Assert.Equal(3, _stub.Bodies.Count); // 2 轮工具 + 1 次无 tools 请求
        Assert.DoesNotContain("\"tools\"", _stub.Bodies[^1]);
    }

    [Fact]
    public async Task AskAsync_ToolsRejected400_DegradesToPlainRequest()
    {
        _stub.Respond["/v1/chat/completions"] = _ =>
            _stub.Bodies.Count == 1 && _stub.Bodies[0].Contains("\"tools\"")
                ? (400, "application/json", """{"error":{"message":"tools not supported"}}""")
                : (200, "text/event-stream", Sse(
                    """{"choices":[{"delta":{"content":"离线结论"}}]}""",
                    """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
                    "[DONE]"));

        var answer = await CreateClient(tools: new RecordingTools()).AskAsync(Request(useTools: true));

        Assert.True(answer.Success);
        Assert.Equal("离线结论", answer.Text);
        Assert.Equal(2, _stub.Bodies.Count);
        Assert.DoesNotContain("\"tools\"", _stub.Bodies[1]);
    }

    [Fact]
    public async Task AskAsync_UnknownToolName_ResultsInToolNotAvailableText()
    {
        _stub.Respond["/v1/chat/completions"] = _ =>
        {
            if (_stub.Bodies.Count == 1)
            {
                return (200, "text/event-stream", Sse(
                    """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_x","type":"function","function":{"name":"bash","arguments":"{}"}}]}}]}""",
                    """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
                    "[DONE]"));
            }
            return (200, "text/event-stream", Sse(
                """{"choices":[{"delta":{"content":"done"}}]}""",
                """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
                "[DONE]"));
        };

        var answer = await CreateClient(tools: new RecordingTools()).AskAsync(Request(useTools: true));

        Assert.True(answer.Success);
        Assert.Contains("not available", _stub.Bodies[1]);
    }

    // ---------- 工具实现 ----------

    [Fact]
    public async Task WebFetch_RejectsNonHttp()
    {
        var tool = new WebFetchTool();
        var result = await tool.ExecuteAsync(new AiToolCall("c1", "webfetch", """{"url":"file:///C:/x"}"""), default);
        Assert.Contains("Rejected", result);
    }

    [Fact]
    public async Task CompositeExecutor_UnknownTool_ReturnsNotAvailable()
    {
        var executor = new CompositeToolExecutor(new RecordingTools());
        var result = await executor.ExecuteAsync(new AiToolCall("c1", "bash", "{}"), default);
        Assert.Contains("'bash' is not available", result);
    }

    // ---------- 辅助 ----------

    private static string Sse(params string[] frames) =>
        string.Join("\n\n", frames.Select(f => f.StartsWith(':') ? f : "data: " + f)) + "\n\n";

    /// <summary>桩执行器：声明 websearch/webfetch，调用返回固定文本。</summary>
    private sealed class RecordingTools : IAiToolExecutor
    {
        public IReadOnlyList<AiToolDefinition> Definitions { get; } =
        [
            new("websearch", "search", """{"type":"object"}"""),
            new("webfetch", "fetch", """{"type":"object"}"""),
        ];

        public Task<string> ExecuteAsync(AiToolCall call, CancellationToken cancellationToken) =>
            Task.FromResult($"[SEARCH RESULT for {call.Name}]");
    }

    /// <summary>内存 HTTP 桩：按路径路由，记录请求体。</summary>
    private sealed class HttpStub : IDisposable
    {
        public readonly Dictionary<string, Func<HttpRequestMessage, (int Status, string ContentType, string Body)>> Respond = new();
        public readonly List<string> Bodies = new();

        public HttpStub()
        {
            Http = new HttpClient(new StubHandler(this));
            using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
        }

        public HttpClient Http { get; }

        public string BaseUrl { get; }

        public void Dispose() => Http.Dispose();

        private sealed class StubHandler(HttpStub owner) : DelegatingHandler(new HttpClientHandler())
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                lock (owner.Bodies) owner.Bodies.Add(body);

                var responder = owner.Respond.TryGetValue(request.RequestUri?.AbsolutePath ?? "/", out var r) ? r : null;
                var (status, contentType, content) = responder is not null
                    ? responder(request)
                    : (404, "application/json", """{"error":{"message":"not found"}}""");
                return new HttpResponseMessage((HttpStatusCode)status)
                {
                    Content = new StringContent(content, Encoding.UTF8, contentType),
                };
            }
        }
    }
}
