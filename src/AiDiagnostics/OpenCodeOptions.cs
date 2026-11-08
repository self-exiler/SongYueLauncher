namespace AiDiagnostics;

/// <summary>
/// 本机 opencode CLI 的调用参数（SRS v3.4 FR-5.6）：不含任何网关地址与鉴权信息。
/// 库只负责定位并拉起 CLI，provider / 凭据 / 模型可用性全部由用户本机 opencode 配置决定。
/// </summary>
public sealed record OpenCodeOptions
{
    /// <summary>opencode 可执行文件完整路径；留空时由 <see cref="AiDiagnosticsClient.Locate"/> 自动定位。</summary>
    public string ExecutablePath { get; init; } = string.Empty;

    /// <summary>模型列举超时：冷启动要拉起常驻后台服务，比单次查询更需要余量。</summary>
    public int ModelsTimeoutMs { get; init; } = 60_000;

    /// <summary>模型清单为空时的重试次数（含首次）：常驻后台服务的 provider 目录是异步装载的。</summary>
    public int ModelListAttempts { get; init; } = 3;

    /// <summary>模型清单重试间隔。</summary>
    public int ModelListRetryDelayMs { get; init; } = 2_000;

    /// <summary>单次分析超时（推理 + 联网查证可能较长；冷启动也可能静默数十秒）。</summary>
    public int AnswerTimeoutMs { get; init; } = 10 * 60_000;
}
