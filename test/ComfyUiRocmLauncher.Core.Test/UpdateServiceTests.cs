using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.Core.Test;

public class UpdateServiceTests
{
    [Fact]
    public void Filter_RemovesTorchLines()
    {
        var lines = new[]
        {
            "torch==2.9.0",
            "torchvision>=0.24",
            " torchaudio ",
            "torchmetrics",
            "einops==0.8.0",
            "# comment",
            "numpy",
        };

        var filtered = RequirementsFilter.Filter(lines);

        Assert.DoesNotContain("torch==2.9.0", filtered);
        Assert.DoesNotContain("torchvision>=0.24", filtered);
        Assert.DoesNotContain(" torchaudio ", filtered);
        Assert.Contains("torchmetrics", filtered);
        Assert.Contains("einops==0.8.0", filtered);
        Assert.Contains("numpy", filtered);
    }

    [Fact]
    public void ParseRequirements_NormalizesNames()
    {
        var file = Path.GetTempFileName();
        File.WriteAllLines(file, new[] { "comfyui_frontend_package==1.32.9", "av == 14.0.0", "# x", "" });

        var pins = UpdateService.ParseRequirements(file);
        File.Delete(file);

        Assert.Equal("1.32.9", pins["comfyui-frontend-package"]);
        Assert.Equal("14.0.0", pins["av"]);
    }

    [Fact]
    public void DiffPackages_ReportsChanges()
    {
        var before = new Dictionary<string, string> { ["einops"] = "0.8.0", ["gone"] = "1.0.0" };
        var after = new Dictionary<string, string> { ["einops"] = "0.8.1", ["new"] = "2.0.0" };

        var diff = UpdateService.DiffPackages(before, after);

        Assert.Contains("einops: 0.8.0 -> 0.8.1", diff);
        Assert.Contains("new: 未安装 -> 2.0.0", diff);
        Assert.Contains("gone: 1.0.0 -> 已移除", diff);
    }

    [Fact]
    public void BuildInstallArgs_UsesNightlyForCommonArch()
    {
        var args = UpdateService.BuildInstallArgs("gfx1100", "--python \"py\"");
        Assert.Contains("torch[device-gfx1100]", args);
        Assert.Contains(UpdateService.RocmNightlyIndexUrl, args);
    }

    [Fact]
    public void BuildInstallArgs_ThrowsForUnsupportedArch()
    {
        Assert.Throws<NotSupportedException>(() => UpdateService.BuildInstallArgs("gfx115x", "--python \"py\""));
    }

    [Theory]
    [InlineData(0, true)][InlineData(1, true)][InlineData(7, true)]
    [InlineData(8, false)][InlineData(16, false)]
    public void RobocopyExitCode(int code, bool expected) =>
        Assert.Equal(expected, UpdateService.IsRobocopySuccess(code));

    [Fact]
    public void TrackedPackages_HasSixEntries() => Assert.Equal(6, TrackedPackages.Names.Length);

    [Fact]
    public void LaunchStates_TextMapping()
    {
        Assert.Equal("运行中", LaunchStates.Text(LaunchState.Running));
        Assert.Equal("已停止", LaunchStates.Text(LaunchState.Idle));
    }
}
