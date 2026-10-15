using System.Text;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 命令行的参数引用与拼接（单点实现）。取代此前三套语义不一致的 Quote：
/// ArgsBuilder（含空格才加引号、不转义）、WheelService（总加引号、不转义）、OpenCodeClient（总加引号、转义）。
/// </summary>
public static class CommandLine
{
    /// <summary>
    /// Windows 参数引用：始终整体加双引号，并按 CommandLineToArgvW 规则转义
    /// （内部双引号前补奇数个反斜杠、结尾反斜杠翻倍）。路径含空格或引号都安全。
    /// </summary>
    public static string Quote(string value)
    {
        var result = new StringBuilder(value.Length + 2);
        result.Append('"');
        var pendingBackslashes = 0;

        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                pendingBackslashes++;
                continue;
            }

            if (ch == '"')
            {
                result.Append('\\', pendingBackslashes * 2 + 1).Append('"');
            }
            else
            {
                result.Append('\\', pendingBackslashes).Append(ch);
            }
            pendingBackslashes = 0;
        }

        result.Append('\\', pendingBackslashes * 2).Append('"');
        return result.ToString();
    }

    /// <summary>把多个 token 各自引用后拼成一条命令行（与真实启动使用同一套规则）。</summary>
    public static string Join(IEnumerable<string> tokens) => string.Join(' ', tokens.Select(Quote));
}
