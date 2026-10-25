using AiDiagnostics;
using AiDiagnostics.Logs;
using AiDiagnostics.Tools;

namespace ComfyUiRocmLauncher.Core.Ai;

/// <summary>
/// AI 智能分析的宿主门面（SRS v2.0 FR-5.6~5.9）：把 ComfyUI 领域画像与通用 <see cref="AiDiagnosticsClient"/> 组装起来。
/// 界面层只依赖本类，既不接触网关的 HTTP 细节，也不接触诊断库的公开类型。
/// </summary>
public sealed class ComfyUiAiService
{
    private readonly AiDiagnosticsClient _client;

    /// <param name="optionsFactory">每次调用时取最新的网关地址与 Key（Key 可能在服务首次启动后才生成）。</param>
    public ComfyUiAiService(Func<GatewayOptions>? optionsFactory = null)
    {
        _client = new AiDiagnosticsClient(
            optionsFactory,
            tools: new CompositeToolExecutor(new WebSearchTool(), new WebFetchTool()));
    }

    /// <summary>列举网关当前可用的模型。</summary>
    public async Task<IReadOnlyList<AiModelOption>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = await _client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        return models.Select(m => new AiModelOption(m.QualifiedId, m.DisplayName)).ToList();
    }

    /// <summary>把错误日志交给 ComfyUI 诊断 agent 分析；正文增量与状态提示分开回调。</summary>
    public async Task<AiAnswerResult> AskAsync(
        ErrorLogExtraction extraction,
        string modelId,
        string? comfyUiDir,
        string? pythonExe,
        Action<string>? onDelta = null,
        Action<string>? onStatus = null,
        CancellationToken cancellationToken = default)
    {
        var answer = await _client.AskAsync(
            new AiRequest
            {
                Prompt = ComfyUiDiagnosis.BuildPrompt(extraction, comfyUiDir, pythonExe),
                ModelId = modelId,
                Agent = ComfyUiDiagnosis.Agent,
                UseTools = true,
            },
            onDelta,
            onStatus,
            cancellationToken).ConfigureAwait(false);
        return new AiAnswerResult(answer.Success, answer.Text);
    }

    /// <summary>
    /// 从日志环形缓冲抓取错误材料并按三类归并分组（SRS v2.0 FR-5.7/5.11）。
    /// 扫描与归并在 <see cref="LogErrorScanner"/> / <see cref="ErrorGrouper"/>，关键词/噪声词/预算由 <see cref="ComfyUiDiagnosis.ScanOptions"/> 注入。
    /// </summary>
    public static ErrorLogExtraction ExtractErrors(IEnumerable<string> lines, int maxBytes = 12_000)
    {
        var options = ComfyUiDiagnosis.ScanOptions;
        if (maxBytes != options.MaxBytes) options = options with { MaxBytes = maxBytes };

        var result = LogErrorScanner.Scan(lines, options);
        return new ErrorLogExtraction(
            result.Groups.Select(g => new ErrorGroupView(
                g.Title, MapKind(g.Kind), g.Count, g.FirstLine, g.LastLine, g.Representative.Text)).ToList(),
            result.TotalLines,
            result.Truncated)
        {
            ErrorLineCount = result.ErrorLineCount,
            WarningLineCount = result.WarningLineCount,
            DroppedBlockCount = result.DroppedBlockCount,
        };
    }

    /// <summary>显式映射取代数值强转：两个枚举只靠书写顺序同步，任何一边重排都会静默错分类（审查候选 7 的雷点）。</summary>
    private static ErrorGroupKind MapKind(LogErrorKind kind) => kind switch
    {
        LogErrorKind.PythonInterpreter => ErrorGroupKind.PythonInterpreter,
        LogErrorKind.Error => ErrorGroupKind.Error,
        _ => ErrorGroupKind.Warning,
    };
}
