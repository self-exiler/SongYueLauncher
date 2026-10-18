using System.Diagnostics;
using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.Core.Test;

/// <summary>
/// <see cref="ProcessService.RunCommandAsync"/> 是全部环境任务（uv / git / robocopy）的唯一出口。
/// 它现在委托给 <c>ProcessRunner.RunCapturedAsync</c>，这里只断言「委托后必须保持」的两条外部契约：
/// 输出合并流式回调 + 取消抛异常（GUI 靠异常区分「已取消」与「失败」）。
/// 进程层的契约（分离流 / stdin / 超时）见 AiDiagnostics.Test 的 ProcessRunnerTests。
/// </summary>
public class ProcessServiceTests
{
    [Fact]
    public async Task RunCommandAsync_MergesBothStreamsIntoOneCallback()
    {
        var lines = new List<string>();

        var code = await ProcessService.RunCommandAsync(
            "cmd.exe", "/c echo out-line & echo err-line 1>&2", Environment.CurrentDirectory, lines.Add);

        Assert.Equal(0, code);
        Assert.Contains(lines, l => l.Contains("out-line"));
        Assert.Contains(lines, l => l.Contains("err-line"));
    }

    [Fact]
    public async Task RunCommandAsync_ReturnsRealExitCode()
    {
        var code = await ProcessService.RunCommandAsync(
            "cmd.exe", "/c exit 3", Environment.CurrentDirectory);

        Assert.Equal(3, code);
    }

    [Fact]
    public async Task RunCommandAsync_CancellationThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource(800);
        var sw = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessService.RunCommandAsync(
                "cmd.exe", "/c ping -n 20 127.0.0.1 >nul", Environment.CurrentDirectory, null, cts.Token));

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"取消后返回过慢：{sw.Elapsed}");
    }

    /// <summary>
    /// F-05 回归：孙进程继承 stdout 句柄时，无超时的 WaitForExit() 会一直挂到孙进程自己退出。
    /// 这里 cmd 立即退出但 start /b 派生的 ping 仍持有管道，所以方法必须在「输出冲刷最多再等 3 秒」内返回，
    /// 而不是等 ping 跑完（约 14 秒）。
    /// </summary>
    [Fact]
    public async Task RunCommandAsync_ReturnsPromptlyWhenGrandchildHoldsThePipe()
    {
        var sw = Stopwatch.StartNew();

        await ProcessService.RunCommandAsync(
            "cmd.exe", "/c start /b ping -n 15 127.0.0.1 >nul & exit", Environment.CurrentDirectory);

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8),
            $"孙进程持有管道导致等待过久：{sw.Elapsed}（应只等 ≤3 秒输出冲刷）");
    }
}
