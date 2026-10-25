namespace AiDiagnostics.Logs;

/// <summary>错误日志扫描参数：关键词与预算全部由宿主注入，库本身不带业务域假设。</summary>
public sealed record LogScanOptions
{
    /// <summary>命中即视为 error 级的关键字（大小写不敏感）。</summary>
    public IReadOnlyList<string> ErrorMarkers { get; init; } =
    [
        "ERROR", "Exception", "CRITICAL", "StopIteration", "Segmentation fault", "Core dumped",
    ];

    /// <summary>
    /// 行首显式 warning 前缀（命中且后随 <c>:</c> 或空格）。判定优先于 ErrorMarkers——
    /// 警告行消息体常含 "error" 字样（如 <c>WARNING: error sending statistics</c>），显式前缀比子串可信。
    /// </summary>
    public IReadOnlyList<string> WarnMarkers { get; init; } = ["WARNING"];

    /// <summary>正常心跳输出特征：即便含关键字也不作为错误（如 "listening at"）。</summary>
    public IReadOnlyList<string> NoiseHints { get; init; } = [];

    /// <summary>输出字节预算，按「解释器 → error → warning」优先级装填，装不下的块被丢弃并标记截断。</summary>
    public int MaxBytes { get; init; } = 12_000;
}
