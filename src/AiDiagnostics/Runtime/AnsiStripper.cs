using System.Text;

namespace AiDiagnostics.Runtime;

/// <summary>去掉子进程输出里的 ANSI 转义序列，得到可直接送入模型或落盘的纯文本。</summary>
public static class AnsiStripper
{
    /// <summary>
    /// 仅识别 ESC [ 数字/分号 m 的 SGR 序列；其它转义按普通字符保留，防止吞正文。
    /// </summary>
    public static string Strip(string line)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < line.Length)
        {
            if (TryMatchSgr(line, i, out var next))
            {
                i = next;
                continue;
            }
            sb.Append(line[i]);
            i++;
        }
        return sb.ToString();
    }

    private static bool TryMatchSgr(string line, int start, out int after)
    {
        after = start + 1;
        if (start + 1 >= line.Length || line[start] != '\u001b' || line[start + 1] != '[') return false;

        var j = start + 2;
        while (j < line.Length && (line[j] == ';' || char.IsAsciiDigit(line[j]))) j++;
        if (j >= line.Length || line[j] != 'm') return false;

        after = j + 1;
        return true;
    }
}
