using System.Diagnostics;
using AiDiagnostics.Runtime;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 子进程管理（SRS 3.2 / FR-4）：启动 ComfyUI、流式输出、等待优雅退出 + 进程树强杀兜底。
/// 优雅停止由上层经 HTTP /shutdown 触发（FR-4.2），本类只负责「等待 → 强杀」两段。
/// </summary>
public sealed class ProcessService
{
    private Process? _process;

    /// <summary>合并后的 stdout/stderr 行。</summary>
    public event Action<string>? OutputReceived;

    /// <summary>被管理的子进程退出时触发（含自行退出，供上层复位状态）。</summary>
    public event Action? Exited;

    public bool IsRunning => _process is { HasExited: false };

    public void Start(string fileName, string arguments, string workingDirectory, IDictionary<string, string>? environment)
    {
        var process = new Process { StartInfo = ProcessRunner.CreateStartInfo(fileName, arguments, workingDirectory), EnableRaisingEvents = true };
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

    /// <summary>
    /// 停止子进程：先给优雅退出留 gracefulWaitMs 窗口（/shutdown 已由上层发出），
    /// 仍未退出则强杀整个进程树，再等最多 3 秒收尾。
    /// </summary>
    public async Task StopAsync(int gracefulWaitMs = 0)
    {
        var process = _process;
        if (process is null) return;

        if (process.HasExited)
        {
            process.Dispose();
            _process = null;
            return;
        }

        if (gracefulWaitMs > 0)
        {
            try
            {
                using var graceCts = new CancellationTokenSource(gracefulWaitMs);
                await process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 窗口内未退出 → 强杀兜底
            }
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

    /// <summary>
    /// 一次性命令执行（环境管理任务用）：stdout/stderr 合并流式回调，取消即终止整个进程树，返回退出码。
    /// 进程创建、超时/取消、输出冲刷统一委托给 <see cref="ProcessRunner.RunCapturedAsync"/>，Core 不再自持一份。
    /// **不设墙钟超时**（torch 安装可能跑十几分钟），只受取消令牌约束——这里也绝不能再出现无超时的
    /// <c>WaitForExit()</c>：孙进程继承管道时它会永久挂起，任务槽不释放、界面卡在进行中。
    /// </summary>
    public static async Task<int> RunCommandAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var (exitCode, _, _) = await ProcessRunner.RunCapturedAsync(
            fileName, arguments, workingDirectory, Timeout.Infinite, onOutput, cancellationToken)
            .ConfigureAwait(false);

        // RunCapturedAsync 用 -1 表达「被打断」，但环境任务的上层靠异常区分「取消」与「失败」
        cancellationToken.ThrowIfCancellationRequested();
        return exitCode;
    }
}
