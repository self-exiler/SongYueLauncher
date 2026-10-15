using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 子进程管理（SRS 3.2 / FR-4）：启动 ComfyUI、流式输出、CTRL_BREAK 优雅停止 + 进程树强杀兜底。
/// </summary>
public sealed class ProcessService
{
    private const int CtrlBreakEvent = 1;
    private const int StopGraceMs = 5000;

    private Process? _process;

    /// <summary>合并后的 stdout/stderr 行。</summary>
    public event Action<string>? OutputReceived;

    /// <summary>被管理的子进程退出时触发（含自行退出，供上层复位状态）。</summary>
    public event Action? Exited;

    public bool IsRunning => _process is { HasExited: false };

    public void Start(string fileName, string arguments, string workingDirectory, IDictionary<string, string>? environment)
    {
        var process = new Process { StartInfo = CreateStartInfo(fileName, arguments, workingDirectory), EnableRaisingEvents = true };
        if (environment is not null)
        {
            foreach (var kv in environment) process.StartInfo.Environment[kv.Key] = kv.Value;
        }

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) OutputReceived?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) OutputReceived?.Invoke(e.Data); };
        process.Exited += (_, _) => Exited?.Invoke();

        if (!process.Start())
            throw new InvalidOperationException($"无法启动进程：{fileName}");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
    }

    /// <summary>FR-4.2：先发 CTRL_BREAK_EVENT，等待最多 5 秒，未退出则强杀整个进程树。</summary>
    public async Task StopAsync()
    {
        var process = _process;
        if (process is null) return;

        if (process.HasExited)
        {
            process.Dispose();
            _process = null;
            return;
        }

        SendCtrlBreak((uint)process.Id);

        try
        {
            using var graceCts = new CancellationTokenSource(StopGraceMs);
            await process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 5 秒内未退出 → 强杀进程树兜底
        }

        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }

            try
            {
                using var killCts = new CancellationTokenSource(3000);
                await process.WaitForExitAsync(killCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 强杀后仍未退出：不再等待，交由系统回收
            }
        }

        process.Dispose();
        _process = null;
    }

    /// <summary>
    /// 查找由该 venv 拉起的 Python 进程（含 ComfyUI 派生的子进程）：
    /// 进程可执行文件位于 venv 根目录内且名为 python/python3/pythonw（.exe 可能带版本号）。
    /// </summary>
    public static Process[] FindPythonProcesses(string venvRoot)
    {
        var normalized = Path.GetFullPath(venvRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return Process.GetProcesses().Where(p =>
        {
            try
            {
                var name = p.ProcessName;
                if (name.Length is < 6 or > 10) return false; // python ~ python3.13
                if (!name.StartsWith("python", StringComparison.OrdinalIgnoreCase)) return false;
                return Path.GetFullPath(p.MainModule!.FileName!)
                    .StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 无权限或已退出：视为无关进程
                return false;
            }
            finally
            {
                p.Dispose();
            }
        }).ToArray();
    }

    /// <summary>强杀指定 Python 进程（含进程树），等待最多 3 秒。</summary>
    public static async Task KillProcessesAsync(IEnumerable<Process> processes)
    {
        var exited = new List<Task>();
        foreach (var p in processes)
        {
            try
            {
                p.Kill(entireProcessTree: true);
                exited.Add(p.WaitForExitAsync());
            }
            catch (InvalidOperationException) { }
            catch (NotSupportedException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally
            {
                p.Dispose();
            }
        }

        if (exited.Count == 0) return;
        await Task.WhenAny(Task.WhenAll(exited), Task.Delay(3000)).ConfigureAwait(false);
    }

    /// <summary>一次性命令执行（环境管理任务用），取消即终止当前子进程。</summary>
    public static async Task<int> RunCommandAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(fileName, arguments, workingDirectory),
            EnableRaisingEvents = true,
        };
        var tcs = new TaskCompletionSource<int>();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutput?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutput?.Invoke(e.Data); };
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

        if (!process.Start()) throw new InvalidOperationException($"无法启动进程：{fileName}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var registration = cancellationToken.Register(() =>
        {
            // Kill 可能因进程恰已退出而抛 InvalidOperationException，取消信号必须照常置位
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            tcs.TrySetCanceled();
        });

        try
        {
            var code = await tcs.Task.ConfigureAwait(false);
            // 冲刷异步输出读取，确保尾部行不丢
            try { process.WaitForExit(); }
            catch (InvalidOperationException) { }
            return code;
        }
        finally
        {
            registration.Dispose();
        }
    }

    /// <summary>
    /// 一次性命令执行并捕获完整 stdout / stderr（需要超时与分离输出的场景，如 opencode CLI）。
    /// 与 <see cref="RunCommandAsync"/> 共用 CreateStartInfo，因此同样重定向 stdin，
    /// 不会让 Python 子进程在 DuplicateHandle 时抛 WinError 6。
    /// 返回 <c>ExitCode = -1</c> 表示进程未能正常退出（超时或已取消）。
    /// </summary>
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunCapturedAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        int timeoutMs,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(fileName, arguments, workingDirectory),
            EnableRaisingEvents = true,
        };
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
            return (SafeExitCode(process), stdout.ToString(), $"{stderr}操作{why}（限时 {timeoutMs / 1000}s）。");
        }
        finally
        {
            // 等待尾部输出冲刷完毕
            try { process.WaitForExit(); }
            catch (InvalidOperationException) { }
        }

        await Task.WhenAll(outDone.Task, errDone.Task).ConfigureAwait(false);
        if (killed) return (SafeExitCode(process), stdout.ToString(), stderr.ToString());
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : -1; }
        catch { return -1; }
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, string arguments, string workingDirectory) =>
        new()
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            // GUI 宿主自身无控制台，默认继承的 stdin 是无效句柄；Python 的 subprocess
            // 在 DuplicateHandle 时会抛 WinError 6。显式重定向以提供有效的匿名管道。
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

    private static void SendCtrlBreak(uint processId)
    {
        // 附加控制台可能失败（无权限 / 进程已无控制台）：失败不影响后续强杀兜底。
        if (SetConsoleCtrlHandler(IntPtr.Zero, true) && AttachConsole(processId))
        {
            GenerateConsoleCtrlEvent(CtrlBreakEvent, processId);
            FreeConsole();
        }
        SetConsoleCtrlHandler(IntPtr.Zero, false);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, bool add);
}
