namespace AiDiagnostics.Agents;

/// <summary>
/// 分析人设：宿主可变的字段（agent 标识、说明、system prompt）。
/// 运行方式（采样温度 0.1、只读权限矩阵）由库固定，见 <see cref="AiAgentProfile"/> 的使用方
/// <c>AgentWorkspace</c>，不提供覆写入口，避免多一条从未被用到的配置路径。
/// </summary>
public sealed record AiAgentProfile
{
    /// <summary>临时 opencode 配置的 agent 名（对应 <c>run --agent</c>）。</summary>
    public string Id { get; init; } = "diagnostics-analyst";

    /// <summary>agent 说明，写入 opencode.json 的 description。</summary>
    public string Description { get; init; } = "Read-only diagnostics analyst.";

    /// <summary>system prompt：角色设定、工作方式与回答结构。</summary>
    public required string SystemPrompt { get; init; }
}
