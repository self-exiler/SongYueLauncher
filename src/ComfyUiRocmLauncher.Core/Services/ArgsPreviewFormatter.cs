using AiDiagnostics.Runtime;
using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 启动参数页的命令预览（SRS FR-2.4）：与真实启动共用 <see cref="ArgsBuilder.Build"/> 的同一次合成，
/// 保证预览不会与实际 spawn 漂移。本类只产出「文本 + 是否属于额外参数」，不含任何 UI 概念，
/// 着色由界面层决定——这样 GUI 不必直接依赖 <c>AiDiagnostics</c>（SRS §3.3 禁止跨层引用）。
/// </summary>
public static class ArgsPreviewFormatter
{
    /// <summary>一个可显示的命令行片段（已按 Windows 参数规则加好引号）。</summary>
    public readonly record struct PreviewToken(string Text, bool IsExtra);

    /// <summary>解释器路径（已加引号），预览首段。</summary>
    public static string QuoteInterpreter(string pythonExe) => CommandLine.Quote(pythonExe);

    /// <summary>
    /// 合成整条预览：返回解释器之后的所有 token，末尾若干项标记为「额外参数」。
    /// </summary>
    public static IReadOnlyList<PreviewToken> Format(StructuredArgs args, CliArgsCatalog catalog, string? extra)
    {
        var tokens = ArgsBuilder.Build(args, catalog, extra);
        var extraCount = ArgsBuilder.SplitExtra(extra ?? string.Empty).Count;
        var extraFrom = tokens.Count - extraCount;

        var result = new List<PreviewToken>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            result.Add(new PreviewToken(CommandLine.Quote(tokens[i]), i >= extraFrom));
        }
        return result;
    }
}
