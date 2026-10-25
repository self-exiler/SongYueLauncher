namespace ComfyUiRocmLauncher.Core.Ai;

/// <summary>
/// 可供选择的模型条目（SRS v2.0 FR-5.8，界面下拉用）。
/// 独立于 <c>AiDiagnostics.AiModel</c>：界面层只依赖 Core，不直接接触诊断库类型（SRS §3.3 禁止跨层引用）。
/// </summary>
public sealed record AiModelOption(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>一次 AI 分析的结果（SRS v2.0 FR-5.9）。</summary>
public sealed record AiAnswerResult(bool Success, string Text);

/// <summary>分组视图的类别标签（界面徽章与提示词分节共用）。</summary>
public enum ErrorGroupKind
{
    /// <summary>Python 解释器自身报错。</summary>
    PythonInterpreter,

    /// <summary>error 级日志。</summary>
    Error,

    /// <summary>warning 级日志。</summary>
    Warning,
}

/// <summary>同型错误组（指纹归并后）：界面分组列表与提示词分节共用。</summary>
public sealed record ErrorGroupView(
    string Title,
    ErrorGroupKind Kind,
    int Count,
    int FirstLine,
    int LastLine,
    string RepresentativeText);

/// <summary>
/// 错误日志提取结果（SRS v2.0 FR-5.7/5.11）：按类别归并的分组、扫描总行数与截断标记。
/// 界面层拿这个类型渲染分组列表，不必接触 <c>AiDiagnostics.Logs</c> 的类型。
/// </summary>
public sealed record ErrorLogExtraction(IReadOnlyList<ErrorGroupView> Groups, int TotalLines, bool Truncated)
{
    /// <summary>识别到的 error 级行数（含解释器块内行，不含 warning）。</summary>
    public int ErrorLineCount { get; init; }

    /// <summary>识别到的 warning 级行数。</summary>
    public int WarningLineCount { get; init; }

    /// <summary>被字节预算丢弃的块数（&gt;0 即有材料被截掉）。</summary>
    public int DroppedBlockCount { get; init; }

    /// <summary>KPI 摘要行（对话框顶部的计数条）。</summary>
    public string ToSummaryText()
    {
        if (Groups.Count == 0) return $"从 {TotalLines} 行日志缓冲区中未识别到错误材料。";
        var interpreter = Groups.Count(g => g.Kind == ErrorGroupKind.PythonInterpreter);
        var errors = Groups.Count(g => g.Kind == ErrorGroupKind.Error);
        var warnings = Groups.Count(g => g.Kind == ErrorGroupKind.Warning);
        var truncated = Truncated ? "（已按优先级截断）" : string.Empty;
        return $"解释器报错 {interpreter} 组 · error {errors} 组 · warning {warnings} 组"
            + $"，共 {ErrorLineCount + WarningLineCount} 行{truncated}";
    }
}
