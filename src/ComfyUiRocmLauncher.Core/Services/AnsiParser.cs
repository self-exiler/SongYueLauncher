using AiDiagnostics.Runtime;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 子进程输出的一小段同色文本（Core 不依赖 UI 颜色类型，仅给出颜色名）。
/// </summary>
public readonly record struct AnsiRun(string Text, string Color);

/// <summary>
/// 极简 ANSI 解析：识别 SGR 前景色（30-37 / 90-97）与重置，供 UI 着色。
/// 去色能力见 <see cref="AnsiStripper"/>（由 LogService 在导出纯文本时使用），这里只负责颜色。
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
            if (AnsiStripper.TryMatchSgr(line, i, out var code, out var next))
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
