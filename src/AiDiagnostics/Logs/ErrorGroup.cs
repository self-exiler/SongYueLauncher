namespace AiDiagnostics.Logs;

/// <summary>指纹归并后的同类错误组（SRS v2.0 FR-5.11）：同型错误（数字/路径/UUID/IP 已归一）合并计数。</summary>
/// <param name="Title">归一化标题（解释器块取异常末行，普通行取自身），变量已替换为占位符。</param>
/// <param name="Kind">类别。</param>
/// <param name="Count">同类出现次数。</param>
/// <param name="FirstLine">首次出现行号（0 基）。</param>
/// <param name="LastLine">末次出现行号（0 基，含块内行）。</param>
/// <param name="Representative">代表块（首次出现的原始文本）。</param>
public sealed record ErrorGroup(
    string Title,
    LogErrorKind Kind,
    int Count,
    int FirstLine,
    int LastLine,
    LogBlock Representative);
