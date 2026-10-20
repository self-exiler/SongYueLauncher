using System.Text;
using System.Text.Json;

namespace AiDiagnostics.Parsing;

/// <summary>
/// 解析 <c>opencode run --format json</c> 的 NDJSON 事件流（每行一个 JSON 事件）。
/// </summary>
public static class RunEventParser
{
    /// <summary>单行事件的解析结果；<c>Text</c> 与 <c>Error</c> 互斥，都为空表示该事件没有展示价值。</summary>
    private readonly record struct RunEvent(string? Text, string? Error, bool IsProgress);

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

            var evt = ParseEvent(line);
            if (evt.Error is { Length: > 0 } failure)
            {
                error ??= failure;
                continue;
            }
            // 进度类事件也带 Text，拼进正文会污染回答
            if (evt is { IsProgress: false, Text: { Length: > 0 } chunk })
            {
                if (text.Length > 0) text.Append("\n\n");
                text.Append(chunk);
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
    /// 把单行事件转成可直接显示的文本，并标注它是不是进度提示（是则宿主应显示在状态区而不是正文区）。
    /// </summary>
    public static (string? Text, bool IsProgress) TryFormat(string line)
    {
        var evt = ParseEvent(line.Trim());
        if (evt.Error is { Length: > 0 } failure) return (failure, false);
        return (evt.Text, evt.IsProgress);
    }

    /// <summary>
    /// 逐行解析的单一实现：<see cref="ParseAnswer"/> 与 <see cref="TryFormat"/> 共享同一套类型分支，
    /// 避免两处各写一遍 JSON→type 分发。
    /// </summary>
    private static RunEvent ParseEvent(string trimmed)
    {
        if (!trimmed.StartsWith('{')) return new(trimmed.Length == 0 ? null : trimmed, null, false);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            // 以 { 开头却解析失败 = 被截断的 NDJSON 半行。当成正文会把 JSON 片段写进回答；
            // 当成错误又过于严重，直接丢弃（完整事件会另起一行重发）。
            return default;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var type = ReadString(root, "type");

            if (string.Equals(type, "text", StringComparison.Ordinal)
                && root.TryGetProperty("part", out var part)
                && ReadString(part, "text") is { Length: > 0 } chunk)
            {
                return new(chunk, null, false);
            }

            if (string.Equals(type, "error", StringComparison.Ordinal)
                && root.TryGetProperty("error", out var err))
            {
                return new(null, ReadString(err, "message") ?? ReadString(err, "type") ?? "未知错误", false);
            }

            if (string.Equals(type, "step_start", StringComparison.Ordinal)) return new(ProgressStarted, null, true);
            if (string.Equals(type, "step_finish", StringComparison.Ordinal)) return new(ProgressContinuing, null, true);
            if (string.Equals(type, "tool_use", StringComparison.Ordinal)
                && root.TryGetProperty("part", out var toolPart)
                && ReadString(toolPart, "tool") is { Length: > 0 } tool)
            {
                return new($"{ProgressToolPrefix}{tool}{ProgressToolSuffix}", null, true);
            }

            return default;
        }
    }

    internal const string ProgressStarted = "opencode 已开始处理请求。";
    internal const string ProgressContinuing = "opencode 正在继续生成回答。";
    internal const string ProgressToolPrefix = "opencode 正在使用 ";
    internal const string ProgressToolSuffix = " 查证资料。";

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
