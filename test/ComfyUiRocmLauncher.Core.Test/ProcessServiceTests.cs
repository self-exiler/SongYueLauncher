using System.Diagnostics;
using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.Core.Test;

public class ProcessServiceTests
{
    [Fact]
    public async Task RunCapturedAsync_SeparatesStdOutAndStdErr()
    {
        var (code, stdout, stderr) = await ProcessService.RunCapturedAsync(
            "cmd.exe", "/c echo out-line & echo err-line 1>&2", Environment.CurrentDirectory, 15_000);

        Assert.Equal(0, code);
        Assert.Contains("out-line", stdout);
        Assert.Contains("err-line", stderr);
        Assert.DoesNotContain("err-line", stdout);
    }

    [Fact]
    public async Task RunCapturedAsync_WritesStandardInputThenCloses()
    {
        var (code, stdout, stderr) = await ProcessService.RunCapturedAsync(
            "cmd.exe", "/c findstr /n \"^\"", Environment.CurrentDirectory, 15_000, standardInput: "alpha\nbeta");

        Assert.Equal(0, code);
        Assert.Contains("alpha", stdout);
        Assert.Contains("beta", stdout);
        Assert.DoesNotContain("超时", stderr);
    }

    [Fact]
    public async Task RunCapturedAsync_AppliesProvidedEnvironment()
    {
        var (code, stdout, _) = await ProcessService.RunCapturedAsync(
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
        var (code, _, stderr) = await ProcessService.RunCapturedAsync(
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

        var (code, _, stderr) = await ProcessService.RunCapturedAsync(
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

        var (code, _, stderr) = await ProcessService.RunCapturedAsync(
            "cmd.exe", "/c ping -n 20 127.0.0.1 >nul", Environment.CurrentDirectory,
            15_000, cancellationToken: cts.Token, standardInput: payload);

        sw.Stop();
        Assert.NotEqual(0, code);
        Assert.Contains("已取消", stderr);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"取消后返回过慢：{sw.Elapsed}");
    }
}
