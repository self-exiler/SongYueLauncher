namespace AiDiagnostics.Logs;

/// <summary>一个带类别与起始行号的日志块。</summary>
/// <param name="Text">块文本（已剥 ANSI，多行块以 \n 连接）。</param>
/// <param name="Kind">类别（解释器 / error / warning）。</param>
/// <param name="StartLine">块首行在扫描输入中的 0 基行号。</param>
public sealed record LogBlock(string Text, LogErrorKind Kind, int StartLine)
{
    /// <summary>块行数（末行号 = StartLine + LineCount - 1）。</summary>
    public int LineCount => Text.Count(c => c == '\n') + 1;
}
