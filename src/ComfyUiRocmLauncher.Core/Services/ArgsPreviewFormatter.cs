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
    /// <summary>
    /// 一个可显示的命令行片段（已按 Windows 参数规则加好引号）。
    /// </summary>
    /// <param name="Text">已加引号的显示文本。</param>
    /// <param name="IsExtra">是否属于末尾的「额外参数」段。</param>
    /// <param name="IsFlag">
    /// 是否是参数名（原始 token 以 <c>--</c> 开头）。
    /// **必须在这里判定**：<paramref name="Text"/> 已经带引号（<c>"--help"</c>），
    /// 界面若拿它去 <c>StartsWith("--")</c> 会全部判失败，参数段就丢了强调色。
    /// </param>
    public readonly record struct PreviewToken(string Text, bool IsExtra, bool IsFlag);

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
            result.Add(new PreviewToken(
                CommandLine.Quote(tokens[i]),
                i >= extraFrom,
                tokens[i].StartsWith("--", StringComparison.Ordinal)));
        }
        return result;
    }
}
