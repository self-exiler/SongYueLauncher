using System.Text;
using AiDiagnostics.Runtime;

namespace AiDiagnostics.Logs;

/// <summary>
/// 从日志行中抓取 error 级日志与 Python 解释器自身报错（Traceback 块 + 异常末行），
/// 按字节预算截断后交给宿主组装提示词。
/// </summary>
public static class LogErrorScanner
{
    /// <summary>Traceback 头（Python 解释器报错块起点）。不开放配置：所有 Python 版本的输出一致。</summary>
    private const string TracebackHeader = "Traceback (most recent call last):";

    /// <summary>单行是否像错误行（先排除噪声特征，再匹配关键字 / Traceback 头）。</summary>
    public static bool LooksLikeError(string plainLine, LogScanOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(plainLine)) return false;
        var o = options ?? new LogScanOptions();

        foreach (var hint in o.NoiseHints)
        {
            if (plainLine.Contains(hint, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return IsTracebackHeader(plainLine) || ContainsMarker(plainLine, o);
    }

    /// <summary>
    /// 扫描全部行：命中错误标记的行、以及 Traceback 块的完整区间归并为块；超过预算后停止并标记截断。
    /// </summary>
    public static LogScanResult Scan(IEnumerable<string> rawLines, LogScanOptions? options = null)
    {
        var o = options ?? new LogScanOptions();
        // 日志源一律可能带 ANSI 转义序列，统一先剥色再匹配（不开放关掉：关掉只会让关键字漏匹配）
        var lines = rawLines.Select(AnsiStripper.Strip).ToList();
        var included = new bool[lines.Count];
        var errorLineCount = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (IsTracebackHeader(line))
            {
                var end = FindTracebackEnd(lines, i, o);
                for (var k = i; k <= end; k++)
                {
                    if (!included[k]) errorLineCount++;
                    included[k] = true;
                }
                i = end;
                continue;
            }

            if (LooksLikeError(line, o) || IsExceptionTail(line))
            {
                if (!included[i]) errorLineCount++;
                included[i] = true;
            }
        }

        var blocks = new List<string>();
        var totalChars = 0;
        var truncated = false;

        foreach (var start in FindBlockStarts(included))
        {
            var sb = new StringBuilder();
            for (var i = start; i < lines.Count && included[i]; i++)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(lines[i]);
            }

            if (totalChars + sb.Length > o.MaxBytes)
            {
                truncated = true;
                break;
            }

            blocks.Add(sb.ToString());
            totalChars += sb.Length + 2;
        }

        return new LogScanResult(blocks, lines.Count, truncated) { ErrorLineCount = errorLineCount };
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

    private static bool ContainsMarker(string line, LogScanOptions options)
    {
        foreach (var marker in options.ErrorMarkers)
        {
            if (line.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsTracebackHeader(string line) =>
        line.StartsWith(TracebackHeader, StringComparison.Ordinal)
        || line.EndsWith(TracebackHeader, StringComparison.Ordinal);

    /// <summary>从 Traceback 头向后延伸，直到遇到下一条非堆栈普通输出为止。</summary>
    private static int FindTracebackEnd(List<string> lines, int headerIndex, LogScanOptions options)
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
            if (indented || IsExceptionTail(line) || ContainsMarker(line, options))
            {
                lastIncluded = i;
                continue;
            }
            break;
        }
        return lastIncluded;
    }

    private static IEnumerable<int> FindBlockStarts(bool[] included)
    {
        for (var i = 0; i < included.Length; i++)
        {
            if (included[i] && (i == 0 || !included[i - 1])) yield return i;
        }
    }
}
