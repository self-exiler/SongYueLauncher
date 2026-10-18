namespace AiDiagnostics.Agents;

/// <summary>
/// 自定义 agent 的画像：人设、权限与温度。库在每次分析前据此生成临时
/// <c>opencode.json</c>，因此宿主无需再维护一份随程序发布的 agent 资产文件。
/// </summary>
public sealed record AiAgentProfile
{
    /// <summary>agent 标识，对应 <c>opencode run --agent</c> 的取值。</summary>
    public required string Id { get; init; }

    /// <summary>system prompt：角色设定、工作方式与回答结构。</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>在人选列表里的简要说明。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>opencode agent 模式，默认 primary。</summary>
    public string Mode { get; init; } = "primary";

    /// <summary>采样温度。</summary>
    public double Temperature { get; init; } = 0.1;

    /// <summary>权限矩阵，键为工具名（<c>*</c> 表示兜底），值为 allow / deny / ask。</summary>
    public IReadOnlyDictionary<string, string> Permissions { get; init; } = AiPermissions.ReadOnlyWeb;
}
