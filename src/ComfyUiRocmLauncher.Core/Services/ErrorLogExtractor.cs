using System.Text;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>AI 智能分析的错误日志提取结果。</summary>
public sealed record ErrorLogExtraction(IReadOnlyList<string> Blocks, int TotalLines, bool Truncated)
{
    /// <summary>识别到的错误行数（不含上下文行）。</summary>
    public int ErrorLineCount { get; init; }

    public static readonly ErrorLogExtraction Empty = new(Array.Empty<string>(), 0, false);

    /// <summary>无错误时的提示文本；否则返回可直接放进输入框的纯文本。</summary>
    public string ToPreviewText() =>
        Blocks.Count == 0 ? "未在当前日志缓冲区中识别到 error 级日志或 Python 报错。" : string.Join("\n\n", Blocks);
}

/// <summary>
/// 错误日志提取（SRS FR-5.7）：从日志环形缓冲中抓取 error 级日志与 Python 解释器自身报错
/// （Traceback 块 + 异常末行），按字节预算截断后组装为发给 AI 的提问。
/// </summary>
public static class ErrorLogExtractor
{
    private const string TracebackHeader = "Traceback (most recent call last):";

    /// <summary>error 级判定关键字（大小写不敏感）。</summary>
    private static readonly string[] ErrorMarkers =
    [
        "ERROR", "Error:", "Traceback (most recent call last)",
        "ModuleNotFoundError", "ImportError", "RuntimeError", "TypeError", "ValueError",
        "KeyError", "AttributeError", "NameError", "IndexError", "SyntaxError",
        "IndentationError", "OSError", "FileNotFoundError", "PermissionError",
        "MemoryError", "RecursionError", "StopIteration", "AssertionError",
        "Exception", "CRITICAL", "Fatal Python error", "Segmentation fault", "Core dumped",
    ];

    /// <summary>ComfyUI / 启动器自身的正常心跳输出，即便含关键字也不作为错误。</summary>
    private static readonly string[] NoiseHints =
    [
        "To see the GUI go to:", "Starting server", "listening at", "Connection interrupted",
    ];

    public static bool LooksLikeError(string plainLine)
    {
        if (string.IsNullOrWhiteSpace(plainLine)) return false;
        foreach (var hint in NoiseHints)
        {
            if (plainLine.Contains(hint, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return ContainsMarker(plainLine);
    }

    /// <summary>Python 异常类名结尾行（如 torch.OutOfMemoryError: CUDA out of memory）。</summary>
    private static bool IsExceptionTail(string plainLine)
    {
        var trimmed = plainLine.TrimEnd();
        if (trimmed.Length == 0 || !char.IsAsciiLetter(trimmed[0])) return false;
        var colon = trimmed.IndexOf(':');
        if (colon <= 0) return false;
        for (var i = 0; i < colon; i++)
        {
            var c = trimmed[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '_') return false;
        }
        var head = trimmed[..colon];
        return head.Contains("Error", StringComparison.Ordinal)
            || head.Contains("Exception", StringComparison.Ordinal);
    }

    private static bool ContainsMarker(string line)
    {
        foreach (var marker in ErrorMarkers)
        {
            if (line.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsTracebackHeader(string line) =>
        line.StartsWith(TracebackHeader, StringComparison.Ordinal)
        || line.EndsWith(TracebackHeader, StringComparison.Ordinal);

    /// <summary>
    /// 扫描全部行：命中 error 标记的行、以及 Traceback 块的完整区间（含缩进的 File/代码行与异常末行）
    /// 归并为一个块；超过 maxBytes 后停止并标记截断。
    /// </summary>
    public static ErrorLogExtraction Extract(IEnumerable<string> rawLines, int maxBytes = 12_000)
    {
        var lines = rawLines.Select(AnsiParser.Strip).ToList();
        var included = new bool[lines.Count];
        var errorLineCount = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (IsTracebackHeader(line))
            {
                var end = FindTracebackEnd(lines, i);
                for (var k = i; k <= end; k++)
                {
                    if (!included[k]) errorLineCount++;
                    included[k] = true;
                }
                i = end;
                continue;
            }

            if (LooksLikeError(line) || IsExceptionTail(line))
            {
                if (!included[i]) errorLineCount++;
                included[i] = true;
            }
        }

        var blocks = new List<string>();
        var totalChars = 0;
        var truncated = false;

        foreach (var start in FindBlockStarts(included, lines))
        {
            var sb = new StringBuilder();
            for (var i = start; i < lines.Count && included[i]; i++)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(lines[i]);
            }

            if (totalChars + sb.Length > maxBytes)
            {
                truncated = true;
                break;
            }

            blocks.Add(sb.ToString());
            totalChars += sb.Length + 2;
        }

        return new ErrorLogExtraction(blocks, lines.Count, truncated) { ErrorLineCount = errorLineCount };
    }

    /// <summary>从 Traceback 头向后延伸，直到遇到下一条非堆栈普通输出为止。</summary>
    private static int FindTracebackEnd(List<string> lines, int headerIndex)
    {
        var lastIncluded = headerIndex;
        for (var i = headerIndex + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                if (lastIncluded == i - 1) lastIncluded = i;
                continue;
            }

            var indented = char.IsWhiteSpace(line[0]);
            if (indented || IsExceptionTail(line) || ContainsMarker(line))
            {
                lastIncluded = i;
                continue;
            }
            break;
        }
        return lastIncluded;
    }

    private static IEnumerable<int> FindBlockStarts(bool[] included, IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (included[i] && (i == 0 || !included[i - 1])) yield return i;
        }
    }

    /// <summary>组装最终发送给 opencode 的提问文本。</summary>
    public static string BuildPrompt(string errorText, string? comfyUiDir = null, string? pythonExe = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是 ComfyUI + AMD ROCm（Windows）环境的故障排查助手。");
        if (!string.IsNullOrWhiteSpace(comfyUiDir)) sb.AppendLine($"整合包目录：{comfyUiDir}");
        if (!string.IsNullOrWhiteSpace(pythonExe)) sb.AppendLine($"Python 解释器：{pythonExe}");
        sb.AppendLine();
        sb.AppendLine("下面是启动器日志中抓取到的 error 级日志与 Python 解释器报错，请分析根因并给出可执行的解决步骤：");
        sb.AppendLine();
        sb.AppendLine("```log");
        sb.AppendLine(errorText);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("请按以下结构用中文回答：");
        sb.AppendLine("1. 一句话结论（最可能的根因）");
        sb.AppendLine("2. 关键证据（引用日志中的具体行）");
        sb.AppendLine("3. 解决步骤（按顺序、可直接执行，包含命令）");
        sb.AppendLine("4. 需要进一步确认的信息（如有）");
        sb.AppendLine("不要修改任何文件，只做分析与建议。");
        return sb.ToString();
    }
}
