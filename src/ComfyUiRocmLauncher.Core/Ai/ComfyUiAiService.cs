using AiDiagnostics;

namespace ComfyUiRocmLauncher.Core.Ai;

/// <summary>
/// AI 智能分析的宿主门面（SRS FR-5.6~5.9）：把 ComfyUI 领域画像与通用 <see cref="AiDiagnosticsClient"/> 组装起来，
/// 界面层只依赖本类，不直接接触 opencode 的命令行细节。
/// </summary>
public sealed class ComfyUiAiService
{
    private readonly AiDiagnosticsClient _client;

    public ComfyUiAiService(AiDiagnosticsClient? client = null) => _client = client ?? new AiDiagnosticsClient();

    /// <summary>定位本机 opencode CLI；未安装返回 null。</summary>
    public static string? LocateOpenCode() => AiDiagnosticsClient.Locate();

    /// <summary>列举 opencode 当前可用的内嵌模型。</summary>
    public Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        _client.ListModelsAsync(cancellationToken);

    /// <summary>把错误日志交给 ComfyUI 诊断 agent 分析；输出行通过回调流式返回。</summary>
    public Task<AiAnswer> AskAsync(
        string errorText,
        string modelQualifiedId,
        string? comfyUiDir,
        string? pythonExe,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default) =>
        _client.AskAsync(
            new AiRequest
            {
                Prompt = ComfyUiDiagnosis.BuildPrompt(errorText, comfyUiDir, pythonExe),
                ModelId = modelQualifiedId,
                Agent = ComfyUiDiagnosis.Agent,
                Title = ComfyUiDiagnosis.AnalysisTitle,
            },
            onOutputLine,
            cancellationToken);
}
