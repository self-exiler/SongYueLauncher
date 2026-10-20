using AiDiagnostics;
using AiDiagnostics.Logs;
using AiDiagnostics.Parsing;

namespace ComfyUiRocmLauncher.Core.Ai;

/// <summary>
/// AI 智能分析的宿主门面（SRS FR-5.6~5.9）：把 ComfyUI 领域画像与通用 <see cref="AiDiagnosticsClient"/> 组装起来。
/// 界面层只依赖本类，既不接触 opencode 的命令行细节，也不接触诊断库的公开类型。
/// </summary>
public sealed class ComfyUiAiService
{
    private readonly AiDiagnosticsClient _client;

    public ComfyUiAiService(AiDiagnosticsClient? client = null) => _client = client ?? new AiDiagnosticsClient();

    /// <summary>定位本机 opencode CLI；未安装返回 null。</summary>
    public static string? LocateOpenCode() => AiDiagnosticsClient.Locate();

    /// <summary>列举 opencode 当前可用的内嵌模型。</summary>
    public async Task<IReadOnlyList<AiModelOption>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = await _client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        return models.Select(m => new AiModelOption(m.ProviderId, m.ModelId, m.DisplayName)).ToList();
    }

    /// <summary>把错误日志交给 ComfyUI 诊断 agent 分析；输出行通过回调流式返回。</summary>
    public async Task<AiAnswerResult> AskAsync(
        string errorText,
        string modelQualifiedId,
        string? comfyUiDir,
        string? pythonExe,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        var answer = await _client.AskAsync(
            new AiRequest
            {
                Prompt = ComfyUiDiagnosis.BuildPrompt(errorText, comfyUiDir, pythonExe),
                ModelId = modelQualifiedId,
                Agent = ComfyUiDiagnosis.Agent,
                Title = ComfyUiDiagnosis.AnalysisTitle,
                Instruction = ComfyUiDiagnosis.AnalysisInstruction,
            },
            onOutputLine,
            cancellationToken).ConfigureAwait(false);
        return new AiAnswerResult(answer.Success, answer.Text);
    }

    /// <summary>
    /// 把一行 opencode 输出转成可显示文本，并告知它是不是进度提示（SRS FR-5.9）。
    /// 界面层据此分派到「状态区」还是「正文区」，不再靠字符串前缀猜测。
    /// </summary>
    public static bool TryFormatEvent(string line, out string? text, out bool isProgress)
    {
        (text, isProgress) = RunEventParser.TryFormat(line);
        return !string.IsNullOrEmpty(text);
    }

    /// <summary>
    /// 从日志环形缓冲抓取 error 级日志与 Python 报错（SRS FR-5.7）。
    /// 扫描算法在 <see cref="LogErrorScanner"/>，关键词/噪声词/预算由 <see cref="ComfyUiDiagnosis.ScanOptions"/> 注入。
    /// </summary>
    public static ErrorLogExtraction ExtractErrors(IEnumerable<string> lines, int maxBytes = 12_000)
    {
        var options = ComfyUiDiagnosis.ScanOptions;
        if (maxBytes != options.MaxBytes) options = options with { MaxBytes = maxBytes };

        var result = LogErrorScanner.Scan(lines, options);
        return new ErrorLogExtraction(result.Blocks, result.TotalLines, result.Truncated)
        {
            ErrorLineCount = result.ErrorLineCount,
        };
    }

    /// <summary>单行是否像错误行（ComfyUI/uvicorn 的正常心跳已被排除）。</summary>
    public static bool LooksLikeError(string plainLine) =>
        LogErrorScanner.LooksLikeError(plainLine, ComfyUiDiagnosis.ScanOptions);
}
