using System.Text;
using AiDiagnostics.Runtime;

namespace AiDiagnostics.Logs;

/// <summary>
/// 从日志行中分类抓取错误材料（SRS v2.0 FR-5.7）：Python 解释器报错（Traceback 块 + 裸异常尾行）、
/// error 级日志、warning 级日志，按字节预算以「解释器 → error → warning」优先级装填。
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
        return IsTracebackHeader(plainLine) || IsWarningLine(plainLine, o) || ContainsMarker(plainLine, o);
    }

    /// <summary>
    /// 扫描全部行并分类归块；随后按「解释器 → error → warning」优先级装填字节预算——
    /// 一类刷屏（如良性警告）不再挤掉另一类（如 Traceback）。
    /// </summary>
    public static LogScanResult Scan(IEnumerable<string> rawLines, LogScanOptions? options = null)
    {
        var o = options ?? new LogScanOptions();
        // 日志源一律可能带 ANSI 转义序列，统一先剥色再匹配（不开放关掉：关掉只会让关键字漏匹配）
        var lines = rawLines.Select(AnsiStripper.Strip).ToList();
        var all = new List<LogBlock>();
        var errorLineCount = 0;
        var warningLineCount = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (IsTracebackHeader(line))
            {
                var end = FindTracebackEnd(lines, i, o);
                all.Add(new LogBlock(string.Join('\n', lines.Skip(i).Take(end - i + 1)), LogErrorKind.PythonInterpreter, i));
                errorLineCount += end - i + 1;
                i = end;
                continue;
            }

            if (IsNoise(line, o)) continue;

            if (IsExceptionTail(line))
            {
                all.Add(new LogBlock(line, LogErrorKind.PythonInterpreter, i));
                errorLineCount++;
            }
            else if (IsWarningLine(line, o))
            {
                all.Add(new LogBlock(line, LogErrorKind.Warning, i));
                warningLineCount++;
            }
            else if (ContainsMarker(line, o))
            {
                all.Add(new LogBlock(line, LogErrorKind.Error, i));
                errorLineCount++;
            }
        }

        var ordered = all
            .OrderBy(b => ErrorGrouper.Priority(b.Kind))
            .ThenBy(b => b.StartLine)
            .ToList();

        var included = new List<LogBlock>(ordered.Count);
        var totalChars = 0;
        var dropped = 0;
        foreach (var block in ordered)
        {
            var size = block.Text.Length + 2;
            if (totalChars + size > o.MaxBytes)
            {
                dropped++;
                continue;
            }
            totalChars += size;
            included.Add(block);
        }

        return new LogScanResult(included, lines.Count, dropped > 0)
        {
            ErrorLineCount = errorLineCount,
            WarningLineCount = warningLineCount,
            TotalBlockCount = all.Count,
            DroppedBlockCount = dropped,
        };
    }

    /// <summary>行首显式 warning 前缀（命中且后随 <c>:</c> 或空格才算，避免误伤 "WARNING" 出现在句中的错误行）。</summary>
    private static bool IsWarningLine(string line, LogScanOptions options)
    {
        var trimmed = line.TrimStart();
        foreach (var marker in options.WarnMarkers)
        {
            if (!trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.Length > marker.Length)
            {
                var next = trimmed[marker.Length];
                if (next == ':' || next == ' ') return true;
            }
        }
        return false;
    }

    private static bool IsNoise(string line, LogScanOptions options)
    {
        foreach (var hint in options.NoiseHints)
        {
            if (line.Contains(hint, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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
}
