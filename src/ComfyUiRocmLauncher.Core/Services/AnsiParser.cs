namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 子进程输出的一小段同色文本（Core 不依赖 UI 颜色类型，仅给出颜色名）。
/// </summary>
public readonly record struct AnsiRun(string Text, string Color);

/// <summary>
/// 极简 ANSI 解析：识别 SGR 前景色（30-37 / 90-97）与重置，供 UI 着色；另提供去色纯文本。
/// </summary>
public static class AnsiParser
{
    private static readonly string[] Standard =
    {
        "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
    };

    public static IReadOnlyList<AnsiRun> Parse(string line)
    {
        var runs = new List<AnsiRun>();
        if (string.IsNullOrEmpty(line)) return runs;

        var current = new System.Text.StringBuilder();
        var color = "default";
        var i = 0;

        while (i < line.Length)
        {
            if (TryMatchSgr(line, i, out var code, out var next))
            {
                if (current.Length > 0)
                {
                    runs.Add(new AnsiRun(current.ToString(), color));
                    current.Clear();
                }

                color = MapColor(code, color);
                i = next;
                continue;
            }

            current.Append(line[i]);
            i++;
        }

        if (current.Length > 0) runs.Add(new AnsiRun(current.ToString(), color));
        return runs;
    }

    /// <summary>去掉全部 ANSI 转义序列，用于复制 / 落盘纯文本。</summary>
    public static string Strip(string line) => AiDiagnostics.Runtime.AnsiStripper.Strip(line);

    /// <summary>仅识别 ESC [ 数字/分号 m 的 SGR 序列；其它转义按普通字符保留，防止吞正文。</summary>
    private static bool TryMatchSgr(string line, int start, out string code, out int after)
    {
        code = string.Empty;
        after = start + 1;
        if (start + 1 >= line.Length || line[start] != '\u001b' || line[start + 1] != '[') return false;

        var j = start + 2;
        while (j < line.Length && (line[j] == ';' || char.IsAsciiDigit(line[j]))) j++;
        if (j >= line.Length || line[j] != 'm') return false;

        code = line.Substring(start + 2, j - start - 2);
        after = j + 1;
        return true;
    }

    private static string MapColor(string code, string fallback)
    {
        if (code.Length == 0 || code == "0") return "default";
        foreach (var part in code.Split(';'))
        {
            if (int.TryParse(part, out var n))
            {
                if (n == 0 || n == 39) return "default";
                if (n >= 30 && n <= 37) return Standard[n - 30];
                if (n >= 90 && n <= 97) return "bright-" + Standard[n - 90];
            }
        }
        return fallback;
    }
}
