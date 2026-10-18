using AiDiagnostics.Logs;
using ComfyUiRocmLauncher.Core.Ai;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>AI 智能分析的错误日志提取结果。</summary>
public sealed record ErrorLogExtraction(IReadOnlyList<string> Blocks, int TotalLines, bool Truncated)
{
    /// <summary>识别到的错误行数（不含上下文行）。</summary>
    public int ErrorLineCount { get; init; }

    public static readonly ErrorLogExtraction Empty = new(Array.Empty<string>(), 0, false);

    /// <summary>无错误时的提示文本；否则返回可直接放进输入框的纯文本。</summary>
    public string ToPreviewText() =>
        Blocks.Count == 0 ? "未在当前日志缓冲区中识别到 error 级日志或 Python 报错。" : string.Join("\n\n", Blocks);
}

/// <summary>
/// 错误日志提取（SRS FR-5.7）：从日志环形缓冲中抓取 error 级日志与 Python 解释器自身报错
/// （Traceback 块 + 异常末行），按字节预算截断后组装为发给 AI 的提问。
/// </summary>
/// <remarks>
/// 扫描与提示词实现已下沉到 <c>AiDiagnostics</c>（<see cref="LogErrorScanner"/> / <see cref="PromptComposer"/>），
/// 这里只注入 ComfyUI 的关键词与模板；公开签名保持不变，既有调用点与测试无需改动。
/// </remarks>
public static class ErrorLogExtractor
{
    /// <summary>单行是否像错误行（ComfyUI/uvicorn 的正常心跳已被排除）。</summary>
    public static bool LooksLikeError(string plainLine) =>
        LogErrorScanner.LooksLikeError(plainLine, ComfyUiDiagnosis.ScanOptions);

    /// <summary>
    /// 扫描全部行：命中 error 标记的行、以及 Traceback 块的完整区间归并为块；
    /// 超过 <paramref name="maxBytes"/> 后停止并标记截断。
    /// </summary>
    public static ErrorLogExtraction Extract(IEnumerable<string> rawLines, int maxBytes = 12_000)
    {
        var options = maxBytes == ComfyUiDiagnosis.ScanOptions.MaxBytes
            ? ComfyUiDiagnosis.ScanOptions
            : ComfyUiDiagnosis.ScanOptions with { MaxBytes = maxBytes };

        var result = LogErrorScanner.Scan(rawLines, options);
        return new ErrorLogExtraction(result.Blocks, result.TotalLines, result.Truncated)
        {
            ErrorLineCount = result.ErrorLineCount,
        };
    }

    /// <summary>组装最终发送给 opencode 的提问文本。</summary>
    public static string BuildPrompt(string errorText, string? comfyUiDir = null, string? pythonExe = null) =>
        ComfyUiDiagnosis.BuildPrompt(errorText, comfyUiDir, pythonExe);
}
