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

    /// <summary>
    /// 扫描全部行并分类，随后**先指纹归并、再按「解释器 → error → warning」优先级装填字节预算**：
    /// 同一错误刷屏只占一份预算（A5），一类刷屏不再挤掉另一类（如 Traceback）；
    /// 超过整个预算的解释器块截断保留而非整块丢弃（#13）——最高优先级材料不能失明。
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

        var ordered = ErrorGrouper.Group(all)
            .OrderBy(g => ErrorGrouper.Priority(g.Kind))
            .ThenBy(g => g.FirstLine)
            .ToList();

        var included = new List<ErrorGroup>(ordered.Count);
        var totalChars = 0;
        var dropped = 0;
        foreach (var group in ordered)
        {
            var representative = group.Representative;
            var text = representative.Text;
            if (group.Kind == LogErrorKind.PythonInterpreter && text.Length + 2 > o.MaxBytes)
            {
                text = TruncateInterpreter(text, Math.Max(200, o.MaxBytes - 2));
                representative = representative with { Text = text };
            }
            var size = text.Length + 2;
            if (totalChars + size > o.MaxBytes)
            {
                dropped++;
                continue;
            }
            totalChars += size;
            included.Add(group with { Representative = representative });
        }

        return new LogScanResult(included.Select(g => g.Representative).ToList(), lines.Count, dropped > 0)
        {
            Groups = included,
            ErrorLineCount = errorLineCount,
            WarningLineCount = warningLineCount,
            DroppedBlockCount = dropped,
        };
    }

    /// <summary>
    /// 超长解释器块（Traceback 链）截断保留：优先保住尾部 30 行（异常末行是定位根因的关键），
    /// 再用剩余预算从头部尽量多装（最多 20 行），中间以省略标记分隔。
    /// </summary>
    private static string TruncateInterpreter(string text, int maxChars)
    {
        const int Head = 20, Tail = 30;
        var lines = text.Split('\n');
        if (lines.Length <= Head + Tail) return text.Length <= maxChars ? text : text[..maxChars];

        var tailText = string.Join('\n', lines[^Tail..]);
        var marker = $"…[截断 {lines.Length - Head - Tail} 行]";
        var remaining = maxChars - tailText.Length - marker.Length - 2;
        var headCount = 0;
        var headUsed = 0;
        for (var i = 0; i < Head && headUsed + lines[i].Length + 1 <= remaining; i++)
        {
            headUsed += lines[i].Length + 1;
            headCount++;
        }
        if (headCount == 0)
        {
            // 尾部就占满了预算（极端）：直接裁尾部文本
            return tailText.Length <= maxChars ? marker + "\n" + tailText : tailText[..maxChars];
        }
        return string.Join('\n', lines.Take(headCount)) + "\n" + marker + "\n" + tailText;
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
