using System.Text;
using System.Text.Json;

namespace AiDiagnostics.Parsing;

/// <summary>
/// 解析 <c>opencode run --format json</c> 的 NDJSON 事件流（每行一个 JSON 事件）。
/// </summary>
public static class RunEventParser
{
    /// <summary>
    /// 拼接 <c>text</c> 事件的正文；<c>error</c> 事件优先于半截正文（失败原因比残缺回答更有诊断价值）。
    /// </summary>
    public static AiAnswer ParseAnswer(string stdout)
    {
        var text = new StringBuilder();
        string? error = null;

        foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (!line.StartsWith('{')) continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                var type = ReadString(root, "type");

                if (string.Equals(type, "text", StringComparison.Ordinal)
                    && root.TryGetProperty("part", out var part)
                    && ReadString(part, "text") is { Length: > 0 } chunk)
                {
                    if (text.Length > 0) text.Append("\n\n");
                    text.Append(chunk);
                    continue;
                }

                if (string.Equals(type, "error", StringComparison.Ordinal)
                    && root.TryGetProperty("error", out var err))
                {
                    error ??= ReadString(err, "message") ?? ReadString(err, "type") ?? "未知错误";
                }
            }
        }

        if (error is not null)
        {
            return text.Length > 0
                ? new AiAnswer(false, $"{error}\n\n已收到的部分内容：\n{text}")
                : new AiAnswer(false, error);
        }
        if (text.Length > 0) return new AiAnswer(true, text.ToString());
        return new AiAnswer(false, "opencode 没有返回可解析的回答内容。");
    }

    /// <summary>
    /// 把单行事件转成可直接显示的文本；无展示价值的控制类事件返回 <c>null</c>。
    /// 进度类文案以「opencode 」开头，宿主可据此区分进度与正文。
    /// </summary>
    public static string? TryFormat(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('{')) return trimmed.Length == 0 ? null : trimmed;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            var type = ReadString(root, "type");

            if (string.Equals(type, "text", StringComparison.Ordinal)
                && root.TryGetProperty("part", out var part)
                && ReadString(part, "text") is { Length: > 0 } chunk)
            {
                return chunk;
            }

            if (string.Equals(type, "error", StringComparison.Ordinal)
                && root.TryGetProperty("error", out var err))
            {
                return ReadString(err, "message") ?? ReadString(err, "type") ?? "未知错误";
            }

            if (string.Equals(type, "step_start", StringComparison.Ordinal)) return ProgressStarted;
            if (string.Equals(type, "step_finish", StringComparison.Ordinal)) return ProgressContinuing;
            if (string.Equals(type, "tool_use", StringComparison.Ordinal)
                && root.TryGetProperty("part", out var toolPart)
                && ReadString(toolPart, "tool") is { Length: > 0 } tool)
            {
                return $"{ProgressToolPrefix}{tool}{ProgressToolSuffix}";
            }
        }
        catch (JsonException)
        {
            return trimmed;
        }

        return null;
    }

    /// <summary>进度文案前缀（宿主据此判断「这行是进度而不是正文」）。</summary>
    public const string ProgressPrefix = "opencode ";

    internal const string ProgressStarted = "opencode 已开始处理请求。";
    internal const string ProgressContinuing = "opencode 正在继续生成回答。";
    internal const string ProgressToolPrefix = "opencode 正在使用 ";
    internal const string ProgressToolSuffix = " 查证资料。";

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
