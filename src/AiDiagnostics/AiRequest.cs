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

    /// <summary>写在 argv 末尾的短指令，长 prompt 走 stdin。</summary>
    public string Instruction { get; init; } = DefaultInstruction;

    /// <summary>默认短指令：说明 prompt 与日志已从 stdin 传入，避免模型再索要材料。</summary>
    public const string DefaultInstruction =
        "完整任务提示词与待分析内容已通过 stdin 附加到本次调用。请只依据 stdin 内容分析，不要要求我重新粘贴内容；不要修改文件，不要运行命令。遇到不确定或版本相关问题时，可使用 websearch/webfetch 查证。";
}
