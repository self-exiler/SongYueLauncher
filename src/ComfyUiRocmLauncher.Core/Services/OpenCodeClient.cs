using System.Diagnostics;
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

    /// <summary>定位本机 opencode 可执行文件；未安装返回 null。</summary>
    public static string? Locate() => ToolLocator.FindOpenCode();

    /// <summary>列出 opencode 当前可用（已启用）的内嵌模型。</summary>
    public async Task<IReadOnlyList<OpenCodeModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var exe = Locate() ?? throw new InvalidOperationException("未检测到 opencode。");
        var (code, stdout, stderr) = await RunCapturedAsync(
            exe, "api model.list", Environment.CurrentDirectory, ModelsTimeoutMs, null, cancellationToken)
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
    /// 发送提问并取回回答。长文本经临时文件由 --file 附件传入（argv 长度受限），
    /// 正文同时截断到 <paramref name="maxInlinePromptChars"/> 以兼容不同 opencode 版本。
    /// </summary>
    public async Task<OpenCodeAnswer> AskAsync(
        string prompt,
        string modelQualifiedId,
        Action<string>? onOutputLine = null,
        int maxInlinePromptChars = 1500,
        CancellationToken cancellationToken = default)
    {
        var exe = Locate() ?? throw new InvalidOperationException("未检测到 opencode。");
        if (string.IsNullOrWhiteSpace(modelQualifiedId))
        {
            return new OpenCodeAnswer(false, "请先选择一个模型。");
        }

        var inline = Truncate(prompt, maxInlinePromptChars);
        var tempFile = Path.Combine(Path.GetTempPath(), $"comfyui_ai_prompt_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tempFile, prompt, Encoding.UTF8);

            // 含空格的路径必须按命令行规则逐段引用（不能整体包一对引号）
            var args = new StringBuilder("run --format json --model ")
                .Append(CommandLine.Quote(modelQualifiedId))
                .Append(" --file ").Append(CommandLine.Quote(tempFile))
                .Append(' ').Append(CommandLine.Quote(inline));

            var (code, stdout, stderr) = await RunCapturedAsync(
                exe, args.ToString(), Environment.CurrentDirectory, AskTimeoutMs, onOutputLine, cancellationToken)
                .ConfigureAwait(false);

            var parsed = ParseRunOutput(stdout);
            if (parsed.Success) return parsed;

            if (code != 0 && string.IsNullOrWhiteSpace(parsed.Text))
            {
                return new OpenCodeAnswer(false, $"opencode 执行失败（退出码 {code}）：{FirstLine(stderr)}");
            }
            return parsed;
        }
        finally
        {
            TryDelete(tempFile);
        }
    }

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

        if (text.Length > 0) return new OpenCodeAnswer(true, text.ToString());
        if (error is not null) return new OpenCodeAnswer(false, error);
        return new OpenCodeAnswer(false, "opencode 没有返回可解析的回答内容。");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCapturedAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        int timeoutMs,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { outDone.TrySetResult(); return; }
            stdout.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { errDone.TrySetResult(); return; }
            stderr.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };

        if (!process.Start()) throw new InvalidOperationException($"无法启动进程：{fileName}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var killed = false;
        using var registration = linked.Token.Register(() =>
        {
            killed = true;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var why = cancellationToken.IsCancellationRequested ? "已取消" : "超时";
            return (process.HasExited ? SafeExitCode(process) : -1,
                stdout.ToString(),
                $"{stderr}操作{why}（限时 {timeoutMs / 1000}s）。");
        }
        finally
        {
            // 等待尾部输出冲刷完毕
            try { process.WaitForExit(); } catch (InvalidOperationException) { }
        }

        await Task.WhenAll(outDone.Task, errDone.Task).ConfigureAwait(false);
        if (killed) return (SafeExitCode(process), stdout.ToString(), stderr.ToString());
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.HasExited ? p.ExitCode : -1; }
        catch { return -1; }
    }

    private static string Truncate(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars) return text;
        return text[..maxChars] + "\n……（正文过长，完整内容见附加文件）";
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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
