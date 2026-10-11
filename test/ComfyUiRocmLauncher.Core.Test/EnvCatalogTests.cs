using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.Core.Test;

public class EnvCatalogTests
{
    [Fact]
    public void MirrorPolicy_GithubPrefixConcatenates()
    {
        var url = "https://github.com/foo/bar.git";
        Assert.Equal("https://gh.example.com/" + url, MirrorPolicy.Github(url, "https://gh.example.com"));
        Assert.Equal(url, MirrorPolicy.Github(url, "")); // 直连
    }

    [Fact]
    public void MirrorPolicy_PypiIndexArg()
    {
        Assert.Equal("", MirrorPolicy.PypiIndexArg(""));
        Assert.Equal("--index-url https://mirrors.aliyun.com/pypi/simple/ ",
            MirrorPolicy.PypiIndexArg("https://mirrors.aliyun.com/pypi/simple/"));
    }

    [Fact]
    public void MirrorPolicy_AmdAlwaysDirect()
    {
        var amd = "https://nightly.repo.amd.com/rocm/whl-next/";
        Assert.Equal(amd, MirrorPolicy.AmdDirect(amd));
    }

    [Fact]
    public void GpuArch_MapsNameToArchAndTemplate()
    {
        var info = GpuArchService.Instance.Map("AMD Radeon RX 7900 XT");
        Assert.True(info.Known);
        Assert.Equal("gfx1100", info.Arch);
        Assert.False(info.IsLegacy);
        Assert.Equal("rdna3", info.TemplateId);
    }

    [Fact]
    public void GpuArch_LegacyInstinct()
    {
        var info = GpuArchService.Instance.Map("AMD Instinct MI300X");
        Assert.Equal("gfx942", info.Arch);
        Assert.True(info.IsLegacy);
    }

    [Fact]
    public void GpuArch_UnknownNameNotKnown()
    {
        Assert.False(GpuArchService.Instance.Map("Intel Iris Xe").Known);
        Assert.False(GpuArchService.Instance.Map(null).Known);
    }

    [Fact]
    public void WheelCatalog_ResolvesArchRuleAndFallback()
    {
        var sage = WheelCatalog.Instance.Wheels.First(w => w.Key == "sageattention");
        Assert.Equal("thehybrid1337/sageattention-rocm-gfx1201-win", sage.ResolveFor("gfx1201")!.GhRepo);
        Assert.Equal("patientx/sageattention-autotune", sage.ResolveFor("gfx1100")!.GhRepo); // "*" 兜底
        Assert.True(sage.ForceReinstall);

        var triton = WheelCatalog.Instance.Wheels.First(w => w.Key == "triton-windows");
        Assert.Equal("3.7.0.post26", triton.PinVersion);
        Assert.Equal("triton-windows==3.7.0.post26", triton.Spec);
    }

    [Fact]
    public void RequirementsFilter_RemovesTorchLines()
    {
        var lines = new[]
        {
            "torch==2.9.0",
            "torchvision>=0.24",
            " torchaudio ",
            "torchmetrics",
            "einops==0.8.0",
            "# torch comment",
            "numpy",
        };

        var filtered = RequirementsFilter.Filter(lines).ToArray();

        Assert.DoesNotContain("torch==2.9.0", filtered);
        Assert.DoesNotContain("torchvision>=0.24", filtered);
        Assert.DoesNotContain(" torchaudio ", filtered);
        Assert.Contains("torchmetrics", filtered); // 非 torch 系统保留
        Assert.Contains("einops==0.8.0", filtered);
        Assert.Contains("numpy", filtered);
        Assert.Contains("# torch comment", filtered); // 注释行不匹配正则
    }

    [Fact]
    public void BuildTorchUninstallList_IncludesFixedAndDynamicPrefixes()
    {
        var list = WheelService.BuildTorchUninstallList(new[] { "rocm-sdk-core", "amd-torch-device-gfx1100", "einops" });
        Assert.Contains("torch", list);
        Assert.Contains("rocm-sdk-core", list);
        Assert.Contains("amd-torch-device-gfx1100", list);
        Assert.DoesNotContain("einops", list);
    }

    [Fact]
    public void BuildTorchInstallArgs_CommonArchUsesNightlyDirect()
    {
        var args = WheelService.BuildTorchInstallArgs("gfx1100", "--python \"py\"");
        Assert.Contains("torch[device-gfx1100]", args);
        Assert.Contains(WheelService.RocmNightlyIndexUrl, args);
        Assert.DoesNotContain("--index-url https://rocm.nightlies", args); // 非常规 arch 不走 legacy
    }

    [Fact]
    public void BuildTorchInstallArgs_LegacyUsesStaging()
    {
        var args = WheelService.BuildTorchInstallArgs("gfx942", "--python \"py\"");
        Assert.Contains("rocm.nightlies.amd.com/v2-staging/gfx942-dcgpu/", args);
    }

    [Fact]
    public void BuildTorchInstallArgs_NoBuildArchThrows() =>
        Assert.Throws<NotSupportedException>(() => WheelService.BuildTorchInstallArgs("gfx115x", "--python \"py\""));

    [Fact]
    public void SelectReleaseAsset_PrefersMatchThenWhl()
    {
        var assets = new[]
        {
            ("notes.txt", "https://x/notes.txt"),
            ("sageattention-2.2.0-cp312-win_amd64.whl", "https://x/sage.whl"),
        };
        Assert.Equal("https://x/sage.whl", WheelService.SelectReleaseAsset(assets, "cp312"));
        Assert.Equal("https://x/sage.whl", WheelService.SelectReleaseAsset(assets, "nonexistent"));
    }

    [Fact]
    public void ParseFreezeAndDiff_Work()
    {
        var before = WheelService.ParseFreeze(new[] { "einops==0.8.0", "gone==1.0", "# c" });
        var after = WheelService.ParseFreeze(new[] { "einops==0.8.1", "new==2.0" });
        var diff = WheelService.DiffPackages(before, after);
        Assert.Contains("einops: 0.8.0 -> 0.8.1", diff);
        Assert.Contains("new: 未安装 -> 2.0", diff);
        Assert.Contains("gone: 1.0 -> 已移除", diff);
    }

    [Fact]
    public void RobocopyArgs_ExcludesGuardedDirs()
    {
        var args = RepoService.RobocopyArgs(@"C:\src", @"C:\dst");
        Assert.Contains("/XD", args);
        Assert.Contains(".venv", args);
        Assert.Contains("models", args);
        Assert.Contains("/E", args);
    }

    [Theory]
    [InlineData(0, true)][InlineData(1, true)][InlineData(7, true)]
    [InlineData(8, false)][InlineData(16, false)]
    public void RobocopyExitCode(int code, bool expected) =>
        Assert.Equal(expected, RepoService.IsRobocopySuccess(code));
}
