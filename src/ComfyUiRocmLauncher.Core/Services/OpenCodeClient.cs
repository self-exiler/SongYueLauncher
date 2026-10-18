using System.Text;
using System.Text.Json;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>opencode 内嵌模型条目（下拉列表用）。</summary>
public sealed record OpenCodeModel(string ProviderId, string ModelId, string DisplayName)
{
    /// <summary>opencode --model 需要的 provider/model 形式。</summary>
    public string QualifiedId => $"{ProviderId}/{ModelId}";

    public override string ToString() => DisplayName;
}

/// <summary>一次 AI 分析的结果。</summary>
public sealed record OpenCodeAnswer(bool Success, string Text);

/// <summary>
/// opencode 客户端（SRS FR-5.8/5.9）：列举内嵌模型、把错误日志交给 opencode 分析并取回回答。
/// 走 CLI 而非 HTTP，避免依赖后台服务端口与鉴权。Core 不依赖 UI；输出行通过回调流出。
/// </summary>
public sealed class OpenCodeClient
{
    private const int ModelsTimeoutMs = 60_000;
    private const int AskTimeoutMs = 5 * 60_000;
    private const string AnalystAgentId = "comfyui-error-analyst";
    private const string AnalystResourcePath = @"Assets\Agents\comfyui-error-analyst.json";
    private const string RunStdinInstruction =
        "完整任务提示词与错误日志已通过 stdin 附加到本次调用。请只依据 stdin 内容分析，不要要求我重新粘贴日志；不要修改文件，不要运行命令。遇到不确定或版本相关问题时，可使用 websearch/webfetch 查证。";

    /// <summary>定位本机 opencode 可执行文件；未安装返回 null。</summary>
    public static string? Locate() => ToolLocator.FindOpenCode();

    /// <summary>列出 opencode 当前可用（已启用）的内嵌模型。</summary>
    public async Task<IReadOnlyList<OpenCodeModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var exe = Locate() ?? throw new InvalidOperationException("未检测到 opencode。");
        var (code, stdout, stderr) = await ProcessService.RunCapturedAsync(
            exe, "api model.list --standalone", Environment.CurrentDirectory, ModelsTimeoutMs, null, cancellationToken)
            .ConfigureAwait(false);

        if (code != 0)
        {
            throw new InvalidOperationException($"opencode 模型列举失败（退出码 {code}）：{FirstLine(stderr.Length > 0 ? stderr : stdout)}");
        }

        return ParseModels(stdout);
    }

    internal static IReadOnlyList<OpenCodeModel> ParseModels(string json)
    {
        var result = new List<OpenCodeModel>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var m in data.EnumerateArray())
        {
            var provider = ReadString(m, "providerID");
            var id = ReadString(m, "id") ?? ReadString(m, "modelID");
            if (provider is null || id is null) continue;

            if (ReadBool(m, "enabled") == false) continue;
            if (string.Equals(ReadString(m, "status"), "inactive", StringComparison.OrdinalIgnoreCase)) continue;

            var name = ReadString(m, "name");
            result.Add(new OpenCodeModel(provider, id, name is null ? id : $"{name} ({provider}/{id})"));
        }
        return result;
    }

    /// <summary>
    /// 发送提问并取回回答。完整 prompt 经 stdin 传入，argv 只保留短指令，避免超长命令行与默认 agent 干扰。
    /// </summary>
    public async Task<OpenCodeAnswer> AskAsync(
        string prompt,
        string modelQualifiedId,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        var exe = Locate() ?? throw new InvalidOperationException("未检测到 opencode。");
        if (string.IsNullOrWhiteSpace(modelQualifiedId))
        {
            return new OpenCodeAnswer(false, "请先选择一个模型。");
        }

        using var workspace = AnalystWorkspace.Create();

        // 显式指定临时配置并校正 opencode v2 的 PWD 根目录解析，避免父进程遗留的
        // PWD 环境变量让 CLI 忽略 WorkingDirectory，从而找不到本次复制的自定义 agent。
        var environment = new Dictionary<string, string>
        {
            ["OPENCODE_CONFIG"] = workspace.ConfigPath,
            ["PWD"] = workspace.Root,
        };

        var (code, stdout, stderr) = await ProcessService.RunCapturedAsync(
            exe,
            BuildAskArguments(modelQualifiedId),
            workspace.Root,
            AskTimeoutMs,
            onOutputLine,
            cancellationToken,
            prompt,
            environment)
            .ConfigureAwait(false);

        var parsed = ParseRunOutput(stdout);
        if (code != 0)
        {
            // 超时/取消说明也会追加在 stderr 末尾，因此取最后一行；已有正文时保留给用户排查。
            var detail = LastNonEmptyLine(stderr) ?? parsed.Text;
            if (parsed.Success && parsed.Text.Length > 0)
            {
                detail = $"{detail}\n\n已收到的部分内容：\n{parsed.Text}";
            }
            return new OpenCodeAnswer(false, detail);
        }
        return parsed;
    }

    internal static string BuildAskArguments(string modelQualifiedId) =>
        new StringBuilder("run --standalone --format json --agent ")
            .Append(CommandLine.Quote(AnalystAgentId))
            .Append(" --model ")
            .Append(CommandLine.Quote(modelQualifiedId))
            .Append(" --title ")
            .Append(CommandLine.Quote("ComfyUI 错误日志分析"))
            .Append(' ')
            .Append(CommandLine.Quote(RunStdinInstruction))
            .ToString();

    internal static OpenCodeAnswer ParseRunOutput(string stdout)
    {
        var text = new StringBuilder();
        string? error = null;

        foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (!line.StartsWith('{')) continue;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                var type = ReadString(root, "type");

                if (string.Equals(type, "text", StringComparison.Ordinal)
                    && root.TryGetProperty("part", out var part)
                    && ReadString(part, "text") is { Length: > 0 } chunk)
                {
                    if (text.Length > 0) text.Append("\n\n");
                    text.Append(chunk);
                    continue;
                }

                if (string.Equals(type, "error", StringComparison.Ordinal)
                    && root.TryGetProperty("error", out var err))
                {
                    error ??= ReadString(err, "message") ?? ReadString(err, "type") ?? "未知错误";
                }
            }
        }

        if (error is not null)
        {
            return text.Length > 0
                ? new OpenCodeAnswer(false, $"{error}\n\n已收到的部分内容：\n{text}")
                : new OpenCodeAnswer(false, error);
        }
        if (text.Length > 0) return new OpenCodeAnswer(true, text.ToString());
        return new OpenCodeAnswer(false, "opencode 没有返回可解析的回答内容。");
    }

    /// <summary>解析 opencode run --format json 的单行事件；非 JSON 或无展示价值的事件返回 null。</summary>
    public static string? TryFormatRunEvent(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('{')) return trimmed.Length == 0 ? null : trimmed;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            var type = ReadString(root, "type");

            if (string.Equals(type, "text", StringComparison.Ordinal)
                && root.TryGetProperty("part", out var part)
                && ReadString(part, "text") is { Length: > 0 } chunk)
            {
                return chunk;
            }

            if (string.Equals(type, "error", StringComparison.Ordinal)
                && root.TryGetProperty("error", out var err))
            {
                return ReadString(err, "message") ?? ReadString(err, "type") ?? "未知错误";
            }

            if (string.Equals(type, "step_start", StringComparison.Ordinal)) return "opencode 已开始处理请求。";
            if (string.Equals(type, "step_finish", StringComparison.Ordinal)) return "opencode 正在继续生成回答。";
            if (string.Equals(type, "tool_use", StringComparison.Ordinal)
                && root.TryGetProperty("part", out var toolPart)
                && ReadString(toolPart, "tool") is { Length: > 0 } tool)
            {
                return $"opencode 正在使用 {tool} 查证资料。";
            }
        }
        catch (JsonException)
        {
            return trimmed;
        }

        return null;
    }

    private sealed class AnalystWorkspace : IDisposable
    {
        private AnalystWorkspace(string root, string configPath)
        {
            Root = root;
            ConfigPath = configPath;
        }

        public string Root { get; }

        public string ConfigPath { get; }

        public static AnalystWorkspace Create()
        {
            var source = FindAnalystAgentSource();
            if (source is null)
            {
                throw new InvalidOperationException($"未找到 AI 分析 agent 资源：{AnalystResourcePath}");
            }

            var root = Path.Combine(Path.GetTempPath(), "comfyui_ai_analysis", Guid.NewGuid().ToString("N"));
            var opencodeDirectory = Path.Combine(root, ".opencode");
            Directory.CreateDirectory(opencodeDirectory);
            var configPath = Path.Combine(opencodeDirectory, "opencode.json");
            File.Copy(source, configPath, overwrite: true);
            return new AnalystWorkspace(root, configPath);
        }

        private static string? FindAnalystAgentSource()
        {
            var candidates = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, AnalystResourcePath),
                Path.Combine(Environment.CurrentDirectory, AnalystResourcePath),
            };

            var guiProject = Path.Combine("src", "ComfyUiRocmLauncher.GUI", AnalystResourcePath);
            AddRepositoryCandidates(candidates, guiProject);

            return candidates.FirstOrDefault(File.Exists);
        }

        private static void AddRepositoryCandidates(List<string> candidates, string relativePath)
        {
            foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                var directory = new DirectoryInfo(start);
                for (var depth = 0; directory is not null && depth < 6; depth++, directory = directory.Parent)
                {
                    candidates.Add(Path.Combine(directory.FullName, relativePath));
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (System.IO.Directory.Exists(Root)) System.IO.Directory.Delete(Root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

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

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;
}
