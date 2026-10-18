namespace AiDiagnostics.Logs;

/// <summary>错误日志扫描结果：命中的块、扫描总行数、错误行数与是否被预算截断。</summary>
public sealed record LogScanResult(IReadOnlyList<string> Blocks, int TotalLines, bool Truncated)
{
    /// <summary>识别到的错误行数（含 Traceback 块内行，不含普通上下文行）。</summary>
    public int ErrorLineCount { get; init; }

    public static readonly LogScanResult Empty = new([], 0, false);

    /// <summary>把块拼成可直接放进输入框的纯文本。</summary>
    public string ToText() => string.Join("\n\n", Blocks);
}
