using System.Text.RegularExpressions;

namespace AiDiagnostics.Logs;

/// <summary>
/// 指纹归并分组（借鉴 LogDetective 的确定性做法）：把数字、文件路径、UUID、IP、十六进制地址
/// 归一为占位符后，同型错误合并为一条，附出现次数与首末行号。
/// </summary>
public static partial class ErrorGrouper
{
    /// <summary>类别优先序：解释器 → error → warning。</summary>
    internal static int Priority(LogErrorKind kind) => kind switch
    {
        LogErrorKind.PythonInterpreter => 0,
        LogErrorKind.Error => 1,
        _ => 2,
    };

    public static IReadOnlyList<ErrorGroup> Group(IReadOnlyList<LogBlock> blocks)
    {
        var groups = new List<ErrorGroup>();
        foreach (var block in blocks)
        {
            var title = Normalize(TitleOf(block));
            var last = block.StartLine + block.LineCount - 1;
            var existing = groups.FirstOrDefault(g => g.Kind == block.Kind && g.Title == title);
            if (existing is null)
            {
                groups.Add(new ErrorGroup(title, block.Kind, 1, block.StartLine, last, block));
            }
            else
            {
                var index = groups.IndexOf(existing);
                groups[index] = existing with
                {
                    Count = existing.Count + 1,
                    FirstLine = Math.Min(existing.FirstLine, block.StartLine),
                    LastLine = Math.Max(existing.LastLine, last),
                };
            }
        }
        return groups;
    }

    /// <summary>标题来源：Traceback 块的块首行全库一致（"Traceback (most recent call last):"），
    /// 用异常末行区分；单行块用自身。</summary>
    private static string TitleOf(LogBlock block)
    {
        var lines = block.Text.Split('\n');
        if (block.Kind == LogErrorKind.PythonInterpreter)
        {
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (lines[i].Trim().Length > 0) return lines[i];
            }
            return block.Text;
        }
        foreach (var line in lines)
        {
            if (line.Trim().Length > 0) return line;
        }
        return block.Text;
    }

    /// <summary>指纹归一：按特异性从高到低替换变量成分，再折叠空白、截断到 160 字符。</summary>
    internal static string Normalize(string line)
    {
        var text = Uuid().Replace(line, "<uuid>");
        text = Ip().Replace(text, "<ip>");
        text = Hex().Replace(text, "<hex>");
        text = Path().Replace(text, "<path>");
        text = Number().Replace(text, "#");
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length <= 160 ? text : text[..160];
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Uuid();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}(?::\d+)?\b")]
    private static partial Regex Ip();

    [GeneratedRegex(@"\b0x[0-9a-fA-F]+\b")]
    private static partial Regex Hex();

    /// <summary>文件路径：盘符 / UNC / ~ 开头，或空白与引号之后的 a/b/c 形式片段。</summary>
    [GeneratedRegex(@"(?:[A-Za-z]:[\\/]|~/|\\\\)(?:[\w.\-]+[\\/])*[\w.\-]+|(?<=[\s(""'])[\w.\-]+(?:[/\\][\w.\-]+)+")]
    private static partial Regex Path();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Whitespace();
}
