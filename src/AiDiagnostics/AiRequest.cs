using AiDiagnostics.Agents;

namespace AiDiagnostics;

/// <summary>一次分析请求：已渲染的 prompt + 模型 + 人设（system prompt）。</summary>
public sealed record AiRequest
{
    /// <summary>完整提问文本（含待分析材料与回答结构要求），作为 user 消息发送。</summary>
    public required string Prompt { get; init; }

    /// <summary>网关广播的原始模型 id。</summary>
    public required string ModelId { get; init; }

    /// <summary>本次使用的人设（system prompt 由宿主注入）。</summary>
    public required AiAgentProfile Agent { get; init; }

    /// <summary>是否启用联网查证工具（需客户端构造时传入执行器；上游不支持时自动降级）。</summary>
    public bool UseTools { get; init; }
}
