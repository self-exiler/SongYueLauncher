namespace ComfyUiRocmLauncher.Core.Ai;

/// <summary>
/// 可供选择的模型条目（SRS FR-5.8，界面下拉用）。
/// 独立于 <c>AiDiagnostics.AiModel</c>：界面层只依赖 Core，不直接接触诊断库类型（SRS §3.3 禁止跨层引用）。
/// </summary>
public sealed record AiModelOption(string ProviderId, string ModelId, string DisplayName)
{
    /// <summary>opencode <c>--model</c> 需要的 <c>provider/model</c> 形式。</summary>
    public string QualifiedId => $"{ProviderId}/{ModelId}";

    public override string ToString() => DisplayName;
}

/// <summary>一次 AI 分析的结果（SRS FR-5.9）。</summary>
public sealed record AiAnswerResult(bool Success, string Text);

/// <summary>
/// 错误日志提取结果（SRS FR-5.7）：命中的块、扫描总行数、错误行数与是否被预算截断。
/// 界面层拿这个类型渲染预览框，不必接触 <c>AiDiagnostics.Logs.LogScanResult</c>。
/// </summary>
public sealed record ErrorLogExtraction(IReadOnlyList<string> Blocks, int TotalLines, bool Truncated)
{
    /// <summary>识别到的错误行数（不含上下文行）。</summary>
    public int ErrorLineCount { get; init; }

    /// <summary>无错误时的提示文本；否则返回可直接放进输入框的纯文本。</summary>
    public string ToPreviewText() =>
        Blocks.Count == 0
            ? "未在当前日志缓冲区中识别到 error 级日志或 Python 报错。"
            : string.Join("\n\n", Blocks);
}
