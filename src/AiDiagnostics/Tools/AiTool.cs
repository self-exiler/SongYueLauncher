using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiDiagnostics.Tools;

/// <summary>提供给模型的工具声明（OpenAI function 形态）。</summary>
public sealed record AiToolDefinition(string Name, string Description, string JsonSchema);

/// <summary>模型发起的一次工具调用（参数为 JSON 字符串，原样转交执行器）。</summary>
public sealed record AiToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// 工具执行器：模型可调用的全部能力都在这里。客户端只声明执行器给出的工具，
/// 模型没有任何其它可调用能力（能力式安全边界，取代 opencode 时代的权限矩阵）。
/// </summary>
public interface IAiToolExecutor
{
    /// <summary>向模型声明的工具清单。</summary>
    IReadOnlyList<AiToolDefinition> Definitions { get; }

    /// <summary>执行一次调用；未知工具返回「不可用」说明而不是抛异常，让回合正常收尾。</summary>
    Task<string> ExecuteAsync(AiToolCall call, CancellationToken cancellationToken);
}

/// <summary>把多个工具组合为一个执行器；未知工具名与执行异常都折算成回填给模型的文本。</summary>
public sealed class CompositeToolExecutor : IAiToolExecutor
{
    private readonly IReadOnlyList<IAiToolExecutor> _tools;

    public CompositeToolExecutor(params IAiToolExecutor[] tools) => _tools = tools;

    public IReadOnlyList<AiToolDefinition> Definitions =>
        _tools.SelectMany(t => t.Definitions).ToArray();

    public async Task<string> ExecuteAsync(AiToolCall call, CancellationToken cancellationToken)
    {
        var tool = _tools.FirstOrDefault(t => t.Definitions.Any(d => d.Name == call.Name));
        if (tool is null) return $"Tool '{call.Name}' is not available.";
        try
        {
            return await tool.ExecuteAsync(call, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Tool '{call.Name}' failed: {ex.Message}";
        }
    }
}

/// <summary>
/// webfetch：抓取模型给出的 URL 并剥为纯文本。约束在客户端写死——
/// 仅 http/https、禁环回/私网/link-local 目标（SSRF 守卫）、重定向逐跳校验（最多 3 跳）、
/// 响应体上限 200 KB、超时 30 秒、输出截断至 16 K 字符。
/// </summary>
public sealed partial class WebFetchTool : IAiToolExecutor
{
    private const int MaxBodyBytes = 200 * 1024;
    private const int MaxOutputChars = 16 * 1024;
    private const int MaxRedirects = 3;

    private readonly HttpClient _http;

    public WebFetchTool(HttpClient? http = null) =>
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            // 禁自动重定向：公开 URL 可 302 到内网/环回地址，必须逐跳过同一套守卫（审查 A2）
            AllowAutoRedirect = false,
        });

    public IReadOnlyList<AiToolDefinition> Definitions { get; } =
    [
        new("webfetch",
            "Fetch a web page and return its readable text. Use only for verifying documentation or error references.",
            """
            {"type":"object","properties":{"url":{"type":"string","description":"Absolute http(s) URL to fetch"}},"required":["url"]}
            """),
    ];

    public async Task<string> ExecuteAsync(AiToolCall call, CancellationToken cancellationToken)
    {
        string url;
        try
        {
            using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            url = args.RootElement.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return "Invalid arguments: expected {\"url\": \"...\"}.";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return $"Rejected: only absolute http(s) URLs are allowed, got '{url}'.";
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            for (var hop = 0; ; hop++)
            {
                if (!AllowTarget(uri))
                {
                    return $"Rejected: target host '{uri.Host}' is a loopback, link-local or private address.";
                }

                using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                    .ConfigureAwait(false);

                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
                {
                    if (hop >= MaxRedirects) return "Rejected: too many redirects.";
                    uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                    continue;
                }

                if (!response.IsSuccessStatusCode) return $"HTTP {(int)response.StatusCode} {response.StatusCode}.";
                await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
                var buffer = new byte[MaxBodyBytes];
                var read = 0;
                int n;
                while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), timeoutCts.Token).ConfigureAwait(false)) > 0)
                {
                    read += n;
                }
                var text = HtmlToText(Encoding.UTF8.GetString(buffer, 0, read));
                return text.Length <= MaxOutputChars ? text : text[..MaxOutputChars] + "\n…[truncated]";
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "Fetch timed out after 30 seconds.";
        }
    }

    /// <summary>
    /// SSRF 守卫：拒绝环回（127/8、::1、localhost）、link-local（169.254/16、fe80::/10）、
    /// 私网（10/8、172.16/12、192.168/16、fc00::/7）与未指定地址。日志是不可信文本，
    /// 提示注入可以借模型之手让本工具访问本机 ComfyUI/网关管理端口或把内容拼进 URL 外发（审查 A2）。
    /// </summary>
    internal static bool AllowTarget(Uri uri)
    {
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (!System.Net.IPAddress.TryParse(uri.Host, out var ip)) return true; // 域名：字面量检查已过

        if (System.Net.IPAddress.IsLoopback(ip)) return false;
        if (ip.IsIPv6LinkLocal) return false;
        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => bytes[0] switch
            {
                10 => false,                                   // 10/8
                172 => bytes[1] is >= 16 and <= 31 ? false : true, // 172.16/12
                192 => bytes[1] == 168 ? false : true,          // 192.168/16
                169 => bytes[1] == 254 ? false : true,          // 169.254/16
                0 => false,                                    // 0.0.0.0/8
                _ => true,
            },
            System.Net.Sockets.AddressFamily.InterNetworkV6 => bytes[0] is 0xfc or 0xfd ? false : true, // fc00::/7
            _ => true,
        };
    }

    /// <summary>剥 script/style 块与标签、还原少量实体、折叠空白；失败开放——剥不出结构就返回原文修剪版。</summary>
    private static string HtmlToText(string html)
    {
        if (html.IndexOf('<') < 0) return html.Trim();
        html = ScriptOrStyle().Replace(html, " ");
        html = Tag().Replace(html, " ");
        html = html
            .Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<")
            .Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");
        return Whitespace().Replace(html, " ").Trim();
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Whitespace();
}

/// <summary>
/// websearch：域特化的免 Key 搜索（GitHub Issues + Stack Overflow）。
/// 结果附来源 URL；需要原文时模型再走 webfetch。
/// </summary>
public sealed class WebSearchTool : IAiToolExecutor
{
    private readonly HttpClient _http;

    public WebSearchTool(HttpClient? http = null)
    {
        // Stack Exchange API 一律 gzip 压缩：必须开自动解压，否则 ReadAsStringAsync 得到压缩字节、
        // JsonDocument.Parse 抛 JsonException（审查 A6）。共享单例也便于桩测。
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        });
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AiDiagnostics");
    }

    public IReadOnlyList<AiToolDefinition> Definitions { get; } =
    [
        new("websearch",
            "Search GitHub issues and Stack Overflow for programming error messages. Returns titles, links and excerpts.",
            """
            {"type":"object","properties":{"query":{"type":"string","description":"Search keywords, ideally the error message"}},"required":["query"]}
            """),
    ];

    public async Task<string> ExecuteAsync(AiToolCall call, CancellationToken cancellationToken)
    {
        string query;
        try
        {
            using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            query = args.RootElement.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String
                ? q.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return "Invalid arguments: expected {\"query\": \"...\"}.";
        }

        if (string.IsNullOrWhiteSpace(query)) return "Invalid arguments: query is empty.";

        // 两个源各自容错（审查 A6）：一侧失败（限流/解析）不能把另一侧的结果也带丢
        var github = await SearchGitHubAsync(query, cancellationToken).ConfigureAwait(false);
        var stack = await SearchStackOverflowAsync(query, cancellationToken).ConfigureAwait(false);

        var sb = new StringBuilder();
        AppendSection(sb, "GitHub issues", github);
        AppendSection(sb, "Stack Overflow", stack);
        return sb.Length == 0 ? "No results found." : sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string title, IReadOnlyList<string> results)
    {
        if (results.Count == 0) return;
        sb.Append("## ").Append(title).Append('\n');
        foreach (var r in results) sb.Append("- ").Append(r).Append('\n');
    }

    private async Task<IReadOnlyList<string>> SearchGitHubAsync(string query, CancellationToken ct)
    {
        try
        {
            var url = "https://api.github.com/search/issues?per_page=5&q=" + Uri.EscapeDataString(query + " is:issue");
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];

            var results = new List<string>();
            foreach (var item in items.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                var link = item.TryGetProperty("html_url", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                var state = item.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                if (title is null || link is null) continue;
                results.Add($"{title} [{state}] — {link}");
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return []; // GitHub 未认证搜索限 10 次/分钟：静默降级，SO 侧照常返回
        }
    }

    private async Task<IReadOnlyList<string>> SearchStackOverflowAsync(string query, CancellationToken ct)
    {
        try
        {
            var url = "https://api.stackexchange.com/2.3/search/advanced?order=desc&sort=relevance&pagesize=5&site=stackoverflow&q="
                      + Uri.EscapeDataString(query);
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];

            var results = new List<string>();
            foreach (var item in items.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                var link = item.TryGetProperty("link", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                var score = item.TryGetProperty("score", out var s) && s.TryGetInt32(out var v) ? v : 0;
                if (title is null || link is null) continue;
                results.Add($"{title} [score {score}] — {link}");
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return [];
        }
    }
}
