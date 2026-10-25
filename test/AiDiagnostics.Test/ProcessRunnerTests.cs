using System.Diagnostics;
using AiDiagnostics.Runtime;

namespace AiDiagnostics.Test;

/// <summary>
/// ProcessRunner 的行为契约（从 Core.Test 的 ProcessServiceTests 迁入——它们测的本来就是这一层，
/// 之前是靠 Core 的转发壳间接调用）。三条硬规则对应的边界都覆盖：stdin 主动关闭、
/// stdin 写入纳入同一超时/取消窗口、不出现无超时的 WaitForExit。
/// </summary>
public class ProcessRunnerTests
{
    [Fact]
    public async Task RunCapturedAsync_SeparatesStdOutAndStdErr()
    {
        var (code, stdout, stderr) = await ProcessRunner.RunCapturedAsync(
            "cmd.exe", "/c echo out-line & echo err-line 1>&2", Environment.CurrentDirectory, 15_000);

        Assert.Equal(0, code);
        Assert.Contains("out-line", stdout);
        Assert.Contains("err-line", stderr);
        Assert.DoesNotContain("err-line", stdout);
    }

    [Fact]
    public async Task RunCapturedAsync_WritesStandardInputThenCloses()
    {
        var (code, stdout, stderr) = await ProcessRunner.RunCapturedAsync(
            "cmd.exe", "/c findstr /n \"^\"", Environment.CurrentDirectory, 15_000, standardInput: "alpha\nbeta");

        Assert.Equal(0, code);
        Assert.Contains("alpha", stdout);
        Assert.Contains("beta", stdout);
        Assert.DoesNotContain("超时", stderr);
    }

    [Fact]
    public async Task RunCapturedAsync_AppliesProvidedEnvironment()
    {
        var (code, stdout, _) = await ProcessRunner.RunCapturedAsync(
            "cmd.exe", "/c echo %OPENCODE_CONFIG%", Environment.CurrentDirectory, 15_000,
            environment: new Dictionary<string, string>
            {
                ["OPENCODE_CONFIG"] = @"C:\temp launcher\opencode.json",
            });

        Assert.Equal(0, code);
        Assert.Contains(@"C:\temp launcher\opencode.json", stdout);
    }

    [Fact]
    public async Task RunCapturedAsync_TimesOutAndReportsTimeout()
    {
        var (code, _, stderr) = await ProcessRunner.RunCapturedAsync(
            "cmd.exe", "/c ping -n 20 127.0.0.1 >nul", Environment.CurrentDirectory, 800);

        Assert.NotEqual(0, code);
        Assert.Contains("超时", stderr);
    }

    [Fact]
    public async Task RunCapturedAsync_TimeoutAppliesToStandardInputWrite()
    {
        // cmd 不读取 stdin；超过匿名管道缓冲后，未加时限的 WriteAsync 会永久阻塞。
        var payload = new string('x', 256 * 1024);
        var sw = Stopwatch.StartNew();

        var (code, _, stderr) = await ProcessRunner.RunCapturedAsync(
            "cmd.exe", "/c ping -n 20 127.0.0.1 >nul", Environment.CurrentDirectory,
            800, standardInput: payload);

        sw.Stop();
        Assert.NotEqual(0, code);
        Assert.Contains("超时", stderr);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"stdin 写入超时返回过慢：{sw.Elapsed}");
    }

    [Fact]
    public async Task RunCapturedAsync_CancellationDuringStandardInputWrite_ReturnsPromptly()
    {
        using var cts = new CancellationTokenSource(800);
        var payload = new string('x', 256 * 1024);
        var sw = Stopwatch.StartNew();

        var (code, _, stderr) = await ProcessRunner.RunCapturedAsync(
            "cmd.exe", "/c ping -n 20 127.0.0.1 >nul", Environment.CurrentDirectory,
            15_000, cancellationToken: cts.Token, standardInput: payload);

        sw.Stop();
        Assert.NotEqual(0, code);
        Assert.Contains("已取消", stderr);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"取消后返回过慢：{sw.Elapsed}");
    }

    /// <summary>
    /// Timeout.Infinite 表示只受取消令牌约束（环境任务如 torch 安装要跑十几分钟，不能设墙钟超时）。
    /// </summary>
    [Fact]
    public async Task RunCapturedAsync_InfiniteTimeout_StillHonoursCancellation()
    {
        using var cts = new CancellationTokenSource(800);
        var sw = Stopwatch.StartNew();

        var (code, _, stderr) = await ProcessRunner.RunCapturedAsync(
            "cmd.exe", "/c ping -n 20 127.0.0.1 >nul", Environment.CurrentDirectory,
            Timeout.Infinite, cancellationToken: cts.Token);

        sw.Stop();
        Assert.NotEqual(0, code);
        Assert.Contains("已取消", stderr);
        Assert.DoesNotContain("限时", stderr); // 无墙钟超时时不该报出「限时 -1s」
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"取消后返回过慢：{sw.Elapsed}");
    }

    [Fact]
    public void CreateStartInfo_RedirectsAllStreamsWithoutShell()
    {
        var si = ProcessRunner.CreateStartInfo("cmd.exe", "/c echo x", @"C:\");

        Assert.False(si.UseShellExecute);
        Assert.True(si.CreateNoWindow);
        Assert.True(si.RedirectStandardInput);
        Assert.True(si.RedirectStandardOutput);
        Assert.True(si.RedirectStandardError);
        Assert.Equal(System.Text.Encoding.UTF8.WebName, si.StandardOutputEncoding?.WebName);
        Assert.Equal(System.Text.Encoding.UTF8.WebName, si.StandardErrorEncoding?.WebName);
    }

    /// <summary>
    /// 回归：stdout / stderr 两条读取线程会并发进回调，若不加锁，宿主传进来的普通
    /// <c>List.Add</c> 会因并发写而丢行（Core 的 ProcessServiceTests 曾因此偶发失败）。
    /// 两路各打 150 行，断言一行不少。
    /// </summary>
    [Fact]
    public async Task RunCapturedAsync_SerializesCallbackAcrossBothStreams()
    {
        var lines = new List<string>(); // 故意用非线程安全容器：契约要求回调串行

        var (code, _, _) = await ProcessRunner.RunCapturedAsync(
            "cmd.exe",
            "/c (for /L %i in (1,1,150) do @echo out%i) & (for /L %i in (1,1,150) do @echo err%i 1>&2)",
            Environment.CurrentDirectory,
            timeoutMs: 60_000,
            onOutputLine: lines.Add);

        Assert.Equal(0, code);
        Assert.Equal(150, lines.Count(l => l.StartsWith("out", StringComparison.Ordinal)));
        Assert.Equal(150, lines.Count(l => l.StartsWith("err", StringComparison.Ordinal)));
    }
}
