namespace AiDiagnostics.Agents;

/// <summary>
/// 自定义 agent 的画像：只有宿主真正会变的两个字段（标识与人设）。
/// 库在每次分析前据此生成临时 <c>opencode.json</c>，因此宿主无需再维护一份随程序发布的 agent 资产文件。
/// </summary>
/// <remarks>
/// 运行方式（primary 模式 / 采样温度 / 权限矩阵）由库内固定，见 <see cref="AgentWorkspace"/>：
/// 分析类任务统一「只读 + 联网查证」，不提供宿主覆写入口，避免多一条从未被用到的配置路径。
/// </remarks>
public sealed record AiAgentProfile
{
    /// <summary>agent 标识，对应 <c>opencode run --agent</c> 的取值。</summary>
    public required string Id { get; init; }

    /// <summary>system prompt：角色设定、工作方式与回答结构。</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>在人选列表里的简要说明。</summary>
    public string Description { get; init; } = string.Empty;
}
