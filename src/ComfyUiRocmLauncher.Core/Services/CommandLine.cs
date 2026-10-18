namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 命令行参数引用（单点实现）。真正实现已下沉到 <see cref="AiDiagnostics.Runtime.CommandLine"/>，
/// 供 AI 诊断库与宿主共用；此处保留转发以免改动既有调用点。
/// </summary>
public static class CommandLine
{
    /// <summary>
    /// Windows 参数引用：始终整体加双引号，并按 CommandLineToArgvW 规则转义
    /// （内部双引号前补奇数个反斜杠、结尾反斜杠翻倍）。路径含空格或引号都安全。
    /// </summary>
    public static string Quote(string value) => AiDiagnostics.Runtime.CommandLine.Quote(value);

    /// <summary>把多个 token 各自引用后拼成一条命令行（与真实启动使用同一套规则）。</summary>
    public static string Join(IEnumerable<string> tokens) => AiDiagnostics.Runtime.CommandLine.Join(tokens);
}
