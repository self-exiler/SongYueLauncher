namespace AiDiagnostics.Logs;

/// <summary>错误日志扫描参数：关键词与预算全部由宿主注入，库本身不带业务域假设。</summary>
public sealed record LogScanOptions
{
    /// <summary>命中即视为错误行的关键字（大小写不敏感）。</summary>
    public IReadOnlyList<string> ErrorMarkers { get; init; } =
    [
        "ERROR", "Exception", "CRITICAL", "StopIteration", "Segmentation fault", "Core dumped",
    ];

    /// <summary>正常心跳输出特征：即便含关键字也不作为错误（如 "listening at"）。</summary>
    public IReadOnlyList<string> NoiseHints { get; init; } = [];

    /// <summary>Traceback 头（Python 解释器报错块起点）。</summary>
    public string TracebackHeader { get; init; } = "Traceback (most recent call last):";

    /// <summary>输出字节预算，超出后按块截断并标记 Truncated。</summary>
    public int MaxBytes { get; init; } = 12_000;

    /// <summary>是否先剥离 ANSI 转义序列（false 时按原样匹配，适合已去色的日志源）。</summary>
    public bool StripAnsi { get; init; } = true;
}
