namespace AiDiagnostics.Logs;

/// <summary>
/// 错误日志扫描结果：按「解释器 → error → warning」优先序、先指纹归并再按预算装填的分组，
/// 附扫描总行数、计数与截断标记。
/// </summary>
public sealed record LogScanResult(IReadOnlyList<LogBlock> Blocks, int TotalLines, bool Truncated)
{
    /// <summary>预算内入选的分组（已按优先级排序；<see cref="Blocks"/> 为各组代表块，保持旧形参兼容）。</summary>
    public IReadOnlyList<ErrorGroup> Groups { get; init; } = [];

    /// <summary>识别到的 error 级行数（含 Traceback 块内行，不含 warning）。</summary>
    public int ErrorLineCount { get; init; }

    /// <summary>识别到的 warning 级行数。</summary>
    public int WarningLineCount { get; init; }

    /// <summary>被预算丢弃的分组数。</summary>
    public int DroppedBlockCount { get; init; }
}
