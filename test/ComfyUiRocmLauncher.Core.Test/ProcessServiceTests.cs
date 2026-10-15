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
    public async Task RunCapturedAsync_TimesOutAndReportsTimeout()
    {
        var (code, _, stderr) = await ProcessService.RunCapturedAsync(
            "cmd.exe", "/c ping -n 20 127.0.0.1 >nul", Environment.CurrentDirectory, 800);

        Assert.NotEqual(0, code);
        Assert.Contains("超时", stderr);
    }
}
