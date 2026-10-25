namespace AiDiagnostics.Logs;

/// <summary>
/// 错误日志扫描结果：按「解释器 → error → warning」优先序排列的块、扫描总行数、计数与截断标记。
/// </summary>
public sealed record LogScanResult(IReadOnlyList<LogBlock> Blocks, int TotalLines, bool Truncated)
{
    /// <summary>识别到的 error 级行数（含 Traceback 块内行，不含 warning）。</summary>
    public int ErrorLineCount { get; init; }

    /// <summary>识别到的 warning 级行数。</summary>
    public int WarningLineCount { get; init; }

    /// <summary>扫描到的全部块数（含被预算丢弃的）。</summary>
    public int TotalBlockCount { get; init; }

    /// <summary>被预算丢弃的块数。</summary>
    public int DroppedBlockCount { get; init; }
}
