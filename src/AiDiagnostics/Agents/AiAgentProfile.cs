namespace AiDiagnostics.Agents;

/// <summary>
/// 分析人设：只有宿主真正会变的字段（人设与说明）。
/// 运行方式（采样温度 0.1、只读边界、工具能力）由库与客户端固定，见 <see cref="AiDiagnosticsClient"/>，
/// 不提供覆写入口，避免多一条从未被用到的配置路径。
/// </summary>
public sealed record AiAgentProfile
{
    /// <summary>system prompt：角色设定、工作方式与回答结构。</summary>
    public required string SystemPrompt { get; init; }
}
