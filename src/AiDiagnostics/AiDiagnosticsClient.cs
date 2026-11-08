using System.Text;
using AiDiagnostics.Agents;
using AiDiagnostics.Parsing;
using AiDiagnostics.Runtime;

namespace AiDiagnostics;

/// <summary>
/// 通过本机 opencode CLI 做 AI 分析的客户端：列举可用模型、把文本交给自定义 agent 分析并流式取回回答。
/// </summary>
/// <remarks>
/// 走 CLI（<c>opencode api model.list</c> / <c>opencode run --standalone --format json</c>），
/// 不依赖任何常驻 HTTP 网关、端口与 Bearer Key；provider、凭据与模型可用性完全由用户本机
/// opencode 配置决定。库不感知业务域：人设、提示词与日志材料全部由宿主注入。
/// <para>
/// 联网查证交回 opencode 自身的 <c>websearch</c> / <c>webfetch</c> 权限（临时 agent 配置里放行），
/// 不再由客户端做 function calling 工具循环。
/// </para>
/// </remarks>
public sealed class AiDiagnosticsClient
{
    private readonly Func<OpenCodeOptions> _optionsFactory;
    private readonly ICommandRunner _runner;

    public AiDiagnosticsClient(Func<OpenCodeOptions>? optionsFactory = null)
        : this(optionsFactory, new ProcessCommandRunner())
    {
    }

    /// <summary>测试桩注入点：替换子进程执行层，客户端契约不出网、不真的拉起 opencode。</summary>
    internal AiDiagnosticsClient(Func<OpenCodeOptions>? optionsFactory, ICommandRunner runner)
    {
        _optionsFactory = optionsFactory ?? (() => new OpenCodeOptions());
        _runner = runner;
    }

    /// <summary>定位本机 opencode 可执行文件；未安装返回 <c>null</c>（供宿主做入口引导）。</summary>
    public static string? Locate() => OpenCodeLocator.Find();

    /// <summary>
    /// 列出 opencode 当前可用（已启用）的模型。
    /// </summary>
    /// <remarks>
    /// 必须走默认（常驻后台）服务：<c>--standalone</c> 会为单次查询新建私有服务，
    /// 其 provider 目录要等插件异步装载完才可见，冷启动时 <c>model.list</c> 恒为空列表。
    /// 后台服务刚拉起时同样可能未装载完，因此空列表时短暂等待后重试。
    /// </remarks>
    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var options = _optionsFactory();
        var exe = ResolveExecutable(options);
        var attempts = Math.Max(1, options.ModelListAttempts);
        IReadOnlyList<AiModel> models = [];

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(options.ModelListRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }

            var run = await _runner.RunAsync(
                exe, "api model.list", Environment.CurrentDirectory, options.ModelsTimeoutMs,
                null, cancellationToken).ConfigureAwait(false);

            if (run.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"opencode 模型列举失败（退出码 {run.ExitCode}）：{FirstLine(run.StdErr.Length > 0 ? run.StdErr : run.StdOut)}");
            }

            try
            {
                models = ModelListParser.Parse(run.StdOut);
            }
            catch (System.Text.Json.JsonException)
            {
                throw new InvalidOperationException($"opencode 模型列举返回了非 JSON 输出：{FirstLine(run.StdOut)}");
            }
            if (models.Count > 0) break;
        }

        return models;
    }

    /// <summary>
    /// 发送提问并流式取回回答。完整 prompt 经 stdin 传入，argv 只保留短指令；
    /// 正文片段经 <paramref name="onDelta"/>（宿主直接 Append），思考与工具状态经 <paramref name="onStatus"/>。
    /// </summary>
    public async Task<AiAnswer> AskAsync(
        AiRequest request,
        Action<string>? onDelta = null,
        Action<string>? onStatus = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ModelId)) return new AiAnswer(false, "请先选择一个模型。");

        var options = _optionsFactory();
        var exe = ResolveExecutable(options);

        // 每次分析独立临时项目：只含本次自定义 agent，不污染用户全局 opencode 配置
        using var workspace = AgentWorkspace.Create(request.Agent, request.UseTools);

        // 显式指定临时配置并校正 opencode v2 的 PWD 根目录解析，避免父进程遗留的
        // PWD 环境变量让 CLI 忽略 WorkingDirectory，从而找不到本次生成的自定义 agent
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OPENCODE_CONFIG"] = workspace.ConfigPath,
            ["PWD"] = workspace.Root,
        };

        var content = new StringBuilder();
        var diagnostics = new List<string>();
        string? streamError = null;
        var contentStarted = false;
        var thinkingAnnounced = false;
        var lastCumulative = string.Empty;

        // 兼容两种 NDJSON 正文语义：逐段独立文本，或每次回传累积全文（只派发新增尾部）
        void AppendDelta(string chunk)
        {
            if (lastCumulative.Length > 0 && chunk.StartsWith(lastCumulative, StringComparison.Ordinal))
            {
                var delta = chunk[lastCumulative.Length..];
                lastCumulative = chunk;
                if (delta.Length == 0) return;
                content.Append(delta);
                onDelta?.Invoke(delta);
                return;
            }

            if (content.Length > 0)
            {
                content.Append("\n\n");
                onDelta?.Invoke("\n\n");
            }
            content.Append(chunk);
            onDelta?.Invoke(chunk);
            lastCumulative = chunk;
        }

        void OnOutputLine(string line)
        {
            var evt = RunEventParser.ParseLine(line);
            switch (evt.Kind)
            {
                case AiRunEventKind.Text:
                    if (!contentStarted)
                    {
                        contentStarted = true;
                        onStatus?.Invoke("正在生成回答…");
                    }
                    AppendDelta(evt.Text);
                    break;

                case AiRunEventKind.Progress:
                    if (evt.Text == "模型思考中…")
                    {
                        if (thinkingAnnounced) break;
                        thinkingAnnounced = true;
                    }
                    onStatus?.Invoke(evt.Text);
                    break;

                case AiRunEventKind.Error:
                    streamError ??= evt.Text;
                    break;

                default:
                    // 非 JSON 行（CLI 日志 / ANSI 输出）只留作失败诊断，绝不进回答区
                    if (evt.Text.Length > 0 && diagnostics.Count < 20) diagnostics.Add(evt.Text);
                    break;
            }
        }

        var run = await _runner.RunAsync(
            exe,
            BuildAskArguments(request),
            workspace.Root,
            options.AnswerTimeoutMs,
            OnOutputLine,
            cancellationToken,
            request.Prompt,
            environment).ConfigureAwait(false);

        var text = content.ToString().Trim();
        if (streamError is { Length: > 0 } error)
        {
            return new AiAnswer(false, PartialOr(error, text));
        }

        if (run.ExitCode != 0)
        {
            var detail = LastNonEmptyLine(run.StdErr)
                         ?? (diagnostics.Count > 0 ? diagnostics[^1] : null)
                         ?? "（无输出）";
            var reason = $"opencode 分析失败（退出码 {run.ExitCode}）：{detail}";
            if (detail.Contains("超时", StringComparison.Ordinal) || detail.Contains("取消", StringComparison.Ordinal))
            {
                reason += "\n\n首次冷启动可能较慢；也请确认本机已配置可用模型 provider（可执行 `opencode models` 检查）。";
            }
            return new AiAnswer(false, PartialOr(reason, text));
        }

        return text.Length > 0
            ? new AiAnswer(true, text)
            : new AiAnswer(false, "opencode 没有返回可解析的回答内容。请确认已配置可用模型 provider（可执行 `opencode models` 检查）。");
    }

    /// <summary>构造 <c>opencode run</c> 参数：长 prompt 走 stdin，argv 只放短指令。</summary>
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

    private static string ResolveExecutable(OpenCodeOptions options)
    {
        var exe = !string.IsNullOrWhiteSpace(options.ExecutablePath)
            ? options.ExecutablePath
            : OpenCodeLocator.Find();
        return exe ?? throw new InvalidOperationException(
            "未检测到本机 opencode CLI。请安装 @opencode/cli（npm i -g @opencode/cli）后重试。");
    }

    private static string PartialOr(string failure, string partial) =>
        partial.Length > 0 ? $"{failure}\n\n已收到的部分内容：\n{partial}" : failure;

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
