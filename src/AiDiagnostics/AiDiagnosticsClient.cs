using System.Text;
using AiDiagnostics.Agents;
using AiDiagnostics.Parsing;
using AiDiagnostics.Runtime;

namespace AiDiagnostics;

/// <summary>
/// 通过本机 opencode CLI 做 AI 分析的客户端：列举可用模型、把文本交给自定义 agent 分析并取回回答。
/// </summary>
/// <remarks>
/// 走 CLI 而非 HTTP，避免依赖后台服务端口与鉴权。库不感知业务域：人设、提示词与日志关键词
/// 全部由宿主注入（<see cref="AiRequest.Agent"/> + 宿主自己的扫描/组装逻辑）。
/// </remarks>
public sealed class AiDiagnosticsClient
{
    /// <summary>列举模型的超时。冷启动时 opencode 要拉起后台服务，比单次分析更需要余量。</summary>
    private const int ModelsTimeoutMs = 60_000;

    /// <summary>单次分析的超时（推理 + 联网查证可能较长）。</summary>
    private const int AnswerTimeoutMs = 5 * 60_000;

    /// <summary>模型列表为空时的重试次数（含首次）。</summary>
    private const int ModelListAttempts = 2;

    /// <summary>模型列表为空时的重试间隔。</summary>
    private const int ModelListRetryDelayMs = 2_000;

    /// <summary>定位本机 opencode 可执行文件；未安装返回 null。</summary>
    public static string? Locate() => OpenCodeLocator.Find();

    /// <summary>
    /// 列出 opencode 当前可用（已启用）的内嵌模型。
    /// </summary>
    /// <remarks>
    /// 必须走默认（常驻后台）服务：<c>--standalone</c> 会为单次查询新建私有服务，
    /// 其 provider 目录要等插件异步装载完才可见，冷启动时 <c>model.list</c> 恒为空列表。
    /// 后台服务刚拉起时同样可能未装载完，因此空列表时短暂等待后重试。
    /// </remarks>
    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var exe = ResolveExecutable();
        var attempts = Math.Max(1, ModelListAttempts);
        IReadOnlyList<AiModel> models = [];

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(ModelListRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }

            var (code, stdout, stderr) = await ProcessRunner.RunCapturedAsync(
                exe, "api model.list", Environment.CurrentDirectory, ModelsTimeoutMs, null, cancellationToken)
                .ConfigureAwait(false);

            if (code != 0)
            {
                throw new InvalidOperationException($"opencode 模型列举失败（退出码 {code}）：{FirstLine(stderr.Length > 0 ? stderr : stdout)}");
            }

            models = ModelListParser.Parse(stdout);
            if (models.Count > 0) break;
        }

        return models;
    }

    /// <summary>
    /// 发送提问并取回回答。完整 prompt 经 stdin 传入，argv 只保留短指令，避免超长命令行与默认 agent 干扰。
    /// </summary>
    public async Task<AiAnswer> AskAsync(
        AiRequest request,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        var exe = ResolveExecutable();
        if (string.IsNullOrWhiteSpace(request.ModelId))
        {
            return new AiAnswer(false, "请先选择一个模型。");
        }

        using var workspace = AgentWorkspace.Create(request.Agent);

        // 显式指定临时配置并校正 opencode v2 的 PWD 根目录解析，避免父进程遗留的
        // PWD 环境变量让 CLI 忽略 WorkingDirectory，从而找不到本次生成的自定义 agent。
        var environment = new Dictionary<string, string>
        {
            ["OPENCODE_CONFIG"] = workspace.ConfigPath,
            ["PWD"] = workspace.Root,
        };

        var (code, stdout, stderr) = await ProcessRunner.RunCapturedAsync(
            exe,
            BuildAskArguments(request),
            workspace.Root,
            AnswerTimeoutMs,
            onOutputLine,
            cancellationToken,
            request.Prompt,
            environment)
            .ConfigureAwait(false);

        var parsed = RunEventParser.ParseAnswer(stdout);
        if (code != 0)
        {
            // 超时/取消说明也会追加在 stderr 末尾，因此取最后一行；已有正文时保留给用户排查。
            var detail = LastNonEmptyLine(stderr) ?? parsed.Text;
            if (parsed.Success && parsed.Text.Length > 0)
            {
                detail = $"{detail}\n\n已收到的部分内容：\n{parsed.Text}";
            }
            return new AiAnswer(false, detail);
        }
        return parsed;
    }

    internal static string BuildAskArguments(AiRequest request) =>
        new StringBuilder("run --standalone --format json --agent ")
            .Append(CommandLine.Quote(request.Agent.Id))
            .Append(" --model ")
            .Append(CommandLine.Quote(request.ModelId))
            .Append(" --title ")
            .Append(CommandLine.Quote(request.Title))
            .Append(' ')
            .Append(CommandLine.Quote(request.Instruction))
            .ToString();

    private static string ResolveExecutable() =>
        OpenCodeLocator.Find() ?? throw new InvalidOperationException("未检测到 opencode。");

    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed.Length > 300 ? trimmed[..300] : trimmed;
        }
        return "(无输出)";
    }

    private static string? LastNonEmptyLine(string text)
    {
        string? result = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) result = trimmed;
        }
        return result is { Length: > 300 } ? result[..300] : result;
    }
}
