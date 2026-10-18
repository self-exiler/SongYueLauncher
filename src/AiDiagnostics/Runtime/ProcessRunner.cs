using System.Diagnostics;
using System.Text;

namespace AiDiagnostics.Runtime;

/// <summary>
/// 一次性命令执行并捕获完整 stdout / stderr（需要超时与分离输出的场景，如 opencode CLI）。
/// </summary>
/// <remarks>
/// 三条硬规则（踩坑换来的，勿改）：
/// <list type="number">
/// <item>stdin 必须显式重定向并主动关闭：GUI 宿主继承的 stdin 是无效句柄，Python 子进程在
/// DuplicateHandle 时会抛 WinError 6；opencode 在非 TTY 下也要读到 EOF 才开工。</item>
/// <item>stdin 写入必须纳入同一超时/取消窗口：子进程不读 stdin 且输入超过管道缓冲时，
/// 无时限的 WriteAsync 会永久阻塞，界面既等不到超时、点取消也无效。</item>
/// <item>不等待「进程结束 + 管道关闭」：被拉起的孙进程可能继承 stdout/stderr 句柄，导致进程结束后管道
/// 仍不关闭。**<c>WaitForExit()</c> 与 <c>WaitForExitAsync()</c> 都会因此卡住**（实测 <c>cmd /c start /b …</c>
/// 场景两者都等满 15 秒），所以进程退出只认 <see cref="Process.Exited"/> 事件；输出读取最多再等 3 秒即返回。</item>
/// </list>
/// </remarks>
public static class ProcessRunner
{
    /// <summary>
    /// 执行命令并捕获输出。传入 <paramref name="standardInput"/> 时会写入该文本再关闭 stdin；
    /// 未传则立即关闭 stdin。传入 <paramref name="environment"/> 时覆盖对应子进程环境变量。
    /// 返回 <c>ExitCode = -1</c> 表示进程未能正常退出（超时或已取消）。
    /// </summary>
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunCapturedAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        int timeoutMs,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default,
        string? standardInput = null,
        IDictionary<string, string>? environment = null)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(fileName, arguments, workingDirectory),
            EnableRaisingEvents = true,
        };
        if (environment is not null)
        {
            foreach (var kv in environment) process.StartInfo.Environment[kv.Key] = kv.Value;
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.Exited += (_, _) => exited.TrySetResult();
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
        if (process.HasExited) exited.TrySetResult(); // 订阅之前就已退出：Exited 不会再触发
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
            try
            {
                if (standardInput is not null)
                {
                    await process.StandardInput.WriteAsync(standardInput).WaitAsync(linked.Token).ConfigureAwait(false);
                }
                process.StandardInput.Close();
            }
            catch (OperationCanceledException)
            {
                return InterruptedRunResult(process, stdout, stderr, timeoutMs, cancellationToken.IsCancellationRequested);
            }
            catch (IOException)
            {
                // 子进程已主动关闭 stdin（或刚退出）：继续等待进程退出并收集其最终输出。
            }

            // 用 Exited 事件而不是 WaitForExitAsync：后者在孙进程继承 stdout 句柄时同样会等到管道
            // 关闭才返回（实测 cmd /c start /b 场景等满 15 秒），等价于踩回硬规则 3 的坑。
            // Exited 只反映「本进程已退出」，与管道无关；残余输出由 finally 里的 3 秒冲刷兜住。
            await exited.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return InterruptedRunResult(process, stdout, stderr, timeoutMs, cancellationToken.IsCancellationRequested);
        }
        finally
        {
            try { await Task.WhenAll(outDone.Task, errDone.Task).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        }

        if (killed) return (SafeExitCode(process), stdout.ToString(), stderr.ToString());
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static (int ExitCode, string StdOut, string StdErr) InterruptedRunResult(
        Process process,
        StringBuilder stdout,
        StringBuilder stderr,
        int timeoutMs,
        bool cancelled)
    {
        var why = cancelled ? "已取消" : $"超时（限时 {timeoutMs / 1000}s）";
        return (SafeExitCode(process), stdout.ToString(), $"{stderr}操作{why}。");
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : -1; }
        catch { return -1; }
    }

    /// <summary>
    /// 子进程启动配置：不建窗口、三流重定向、输出按 UTF-8 解码。
    /// 公开给宿主复用（启动 ComfyUI 也要同一套语义），避免两个程序集各写一份。
    /// stdin 必须重定向：GUI 宿主自身无控制台，默认继承的 stdin 是无效句柄，
    /// Python 的 subprocess 在 DuplicateHandle 时会抛 WinError 6。
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string fileName, string arguments, string workingDirectory) =>
        new()
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
}
