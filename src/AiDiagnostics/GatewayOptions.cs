namespace AiDiagnostics;

/// <summary>
/// OpenAI 兼容本地网关的连接参数（SRS v2.0 FR-5.6）。
/// 网关由启动器安装并拉起（dsh-our-free-model 独立服务），只监听回环地址。
/// </summary>
public sealed record GatewayOptions
{
    /// <summary>网关根地址（不含末尾斜杠），如 <c>http://127.0.0.1:18900</c>。</summary>
    public string BaseUrl { get; init; } = "http://127.0.0.1:18900";

    /// <summary>Bearer Key（网关数据目录 settings.json 的 forwardKey）。</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>列举模型的超时。</summary>
    public int ModelsTimeoutMs { get; init; } = 30_000;

    /// <summary>单次分析的超时（推理 + 联网查证可能较长；上游长思考可静默 60 秒以上）。</summary>
    public int AnswerTimeoutMs { get; init; } = 10 * 60_000;
}
