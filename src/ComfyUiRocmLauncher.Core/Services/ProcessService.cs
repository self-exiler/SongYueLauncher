using System.Diagnostics;
using System.Runtime.InteropServices;

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

    private static ProcessStartInfo CreateStartInfo(string fileName, string arguments, string workingDirectory) =>
        new()
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

    private static void SendCtrlBreak(uint processId)
    {
        // MSIX 下附加控制台受限（SRS 风险表）：失败不影响后续强杀兜底。
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
