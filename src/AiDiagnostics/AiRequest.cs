using AiDiagnostics.Agents;

namespace AiDiagnostics;

/// <summary>一次分析请求：已渲染的 prompt + 模型 + 人设（system prompt）。</summary>
public sealed record AiRequest
{
    /// <summary>完整提问文本（含待分析材料与回答结构要求），经子进程 stdin 原样传入，不受命令行长度限制。</summary>
    public required string Prompt { get; init; }

    /// <summary><c>provider/model</c> 形式的模型标识（<c>opencode --model</c> 原样取值）。</summary>
    public required string ModelId { get; init; }

    /// <summary>本次使用的人设（system prompt 与 agent 标识由宿主注入）。</summary>
    public required AiAgentProfile Agent { get; init; }

    /// <summary>
    /// 是否在临时 opencode agent 配置里放行 <c>websearch</c> / <c>webfetch</c>。
    /// 为 false 时权限矩阵全拒绝，整次分析纯离线。其余工具（含 read / bash）恒定拒绝，不受此开关影响。
    /// </summary>
    public bool UseTools { get; init; }

    /// <summary>会话标题，仅用于在 opencode 里识别本次调用。</summary>
    public string Title { get; init; } = "AI 分析";

    /// <summary>
    /// 写在 argv 末尾的短指令；长 prompt 走 stdin。
    /// 由宿主提供——行为边界属于业务域，通用库只给一个中性默认值。
    /// </summary>
    public string Instruction { get; init; } =
        "完整任务提示词与待分析内容已通过 stdin 附加到本次调用。请依据系统设定与 stdin 内容直接给出最终回答。";
}
