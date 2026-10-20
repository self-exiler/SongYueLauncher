using AiDiagnostics.Agents;

namespace AiDiagnostics;

/// <summary>一次分析请求：已渲染的 prompt + 模型 + agent 画像。</summary>
public sealed record AiRequest
{
    /// <summary>完整提问文本，经子进程 stdin 原样传入（不受命令行长度限制）。</summary>
    public required string Prompt { get; init; }

    /// <summary><c>provider/model</c> 形式的模型标识。</summary>
    public required string ModelId { get; init; }

    /// <summary>本次使用的 agent 画像。</summary>
    public required AiAgentProfile Agent { get; init; }

    /// <summary>会话标题，仅用于在 opencode 里识别本次调用。</summary>
    public string Title { get; init; } = "AI 分析";

    /// <summary>
    /// 写在 argv 末尾的短指令，长 prompt 走 stdin。由宿主提供——行为边界属于业务域，
    /// 通用库不预设「能不能读文件 / 联网几次」这类规则。
    /// </summary>
    public required string Instruction { get; init; }
}
