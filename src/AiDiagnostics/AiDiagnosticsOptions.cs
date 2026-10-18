namespace AiDiagnostics;

/// <summary>客户端行为参数（全部有可用默认值）。</summary>
public sealed class AiDiagnosticsOptions
{
    /// <summary>opencode 可执行文件路径；为空时自动定位（npm 全局 / 用户目录 / PATH）。</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>列举模型的超时。</summary>
    public int ModelsTimeoutMs { get; init; } = 60_000;

    /// <summary>单次分析默认超时。</summary>
    public int AnswerTimeoutMs { get; init; } = 5 * 60_000;

    /// <summary>
    /// 模型列表为空时的重试次数（含首次）。
    /// 常驻后台服务刚拉起时 provider 目录尚未装载完，<c>model.list</c> 会短暂返回空列表。
    /// </summary>
    public int ModelListAttempts { get; init; } = 2;

    /// <summary>模型列表为空时的重试间隔。</summary>
    public int ModelListRetryDelayMs { get; init; } = 2_000;
}
