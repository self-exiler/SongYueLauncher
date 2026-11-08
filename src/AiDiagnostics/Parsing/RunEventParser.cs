using System.Text;
using System.Text.Json;

namespace AiDiagnostics.Parsing;

/// <summary>NDJSON 单行事件的类别：正文 / 进度 / 错误 / 无需展示。</summary>
internal enum AiRunEventKind
{
    Ignored,
    Text,
    Progress,
    Error,
}

internal readonly record struct AiRunEvent(AiRunEventKind Kind, string Text);

/// <summary>
/// 解析 <c>opencode run --format json</c> 的 NDJSON 事件流（每行一个 JSON 事件）。
/// 逐行解析（流式回调）与整段解析（兜底/单测）共用同一套类型分支，避免两处语义漂移。
/// </summary>
internal static class RunEventParser
{
    /// <summary>
    /// 把一行输出转成事件。非 JSON 行以 <see cref="AiRunEventKind.Ignored"/> 返回原文，
    /// 由客户端作为诊断细节留存（不进回答区）；以 <c>{</c> 开头却解析失败的是被截断的半行，直接丢弃。
    /// </summary>
    public static AiRunEvent ParseLine(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return new(AiRunEventKind.Ignored, string.Empty);
        if (trimmed[0] != '{') return new(AiRunEventKind.Ignored, trimmed);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            return new(AiRunEventKind.Ignored, string.Empty);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var type = ReadString(root, "type");

            if (string.Equals(type, "text", StringComparison.Ordinal))
            {
                var text = root.TryGetProperty("part", out var part) ? ReadString(part, "text") : null;
                text ??= ReadString(root, "text");
                return text is { Length: > 0 }
                    ? new(AiRunEventKind.Text, text)
                    : new(AiRunEventKind.Ignored, string.Empty);
            }

            if (string.Equals(type, "error", StringComparison.Ordinal))
            {
                var message = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                    ? ReadString(err, "message") ?? ReadString(err, "type")
                    : null;
                message ??= ReadString(root, "message") ?? ReadString(root, "error") ?? "opencode 返回了未知错误";
                return new(AiRunEventKind.Error, message);
            }

            if (string.Equals(type, "tool_use", StringComparison.Ordinal))
            {
                var tool = root.TryGetProperty("part", out var part) ? ReadString(part, "tool") : null;
                tool ??= ReadString(root, "tool");
                return new(AiRunEventKind.Progress,
                    string.IsNullOrEmpty(tool) ? "正在调用工具查证资料…" : $"正在调用 {tool} …");
            }

            if (string.Equals(type, "step_start", StringComparison.Ordinal) ||
                string.Equals(type, "reasoning", StringComparison.Ordinal) ||
                string.Equals(type, "thinking", StringComparison.Ordinal))
            {
                return new(AiRunEventKind.Progress, "模型思考中…");
            }

            if (string.Equals(type, "step_finish", StringComparison.Ordinal))
            {
                return new(AiRunEventKind.Progress, "正在继续生成回答…");
            }

            return new(AiRunEventKind.Ignored, string.Empty);
        }
    }

    /// <summary>
    /// 拼接 <c>text</c> 事件的正文（片段间空行分隔）；<c>error</c> 事件优先于半截正文
    /// （失败原因比残缺回答更有诊断价值）。
    /// </summary>
    public static AiAnswer ParseAnswer(string stdout)
    {
        var text = new StringBuilder();
        string? error = null;

        foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var evt = ParseLine(raw);
            switch (evt.Kind)
            {
                case AiRunEventKind.Error:
                    error ??= evt.Text;
                    break;
                case AiRunEventKind.Text:
                    if (text.Length > 0) text.Append("\n\n");
                    text.Append(evt.Text);
                    break;
            }
        }

        if (error is not null)
        {
            return text.Length > 0
                ? new AiAnswer(false, $"{error}\n\n已收到的部分内容：\n{text}")
                : new AiAnswer(false, error);
        }
        return text.Length > 0
            ? new AiAnswer(true, text.ToString())
            : new AiAnswer(false, "opencode 没有返回可解析的回答内容。请确认已配置可用模型 provider（可执行 `opencode models` 检查）。");
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
