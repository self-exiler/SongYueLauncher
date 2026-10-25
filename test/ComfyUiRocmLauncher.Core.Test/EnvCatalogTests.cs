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
    public void MirrorPolicy_GithubEndpointUsesManagerReplaceForm()
    {
        // ComfyUI-Manager 按 url.replace("https://github.com", GITHUB_ENDPOINT) 生效
        Assert.Equal("https://ghproxy.net/https://github.com", MirrorPolicy.GithubEndpoint("https://ghproxy.net/"));
        Assert.Equal("", MirrorPolicy.GithubEndpoint(""));
    }

    [Fact]
    public void MirrorCatalog_IndexOfMatchesSavedUrlIgnoringTrailingSpace()
    {
        Assert.Equal(0, MirrorCatalog.IndexOf(MirrorCatalog.GithubMirrors, ""));
        Assert.Equal(1, MirrorCatalog.IndexOf(MirrorCatalog.GithubMirrors, "https://ghproxy.net/"));
        Assert.Equal(1, MirrorCatalog.IndexOf(MirrorCatalog.PypiMirrors, "https://mirrors.aliyun.com/pypi/simple/"));
        Assert.Equal(3, MirrorCatalog.IndexOf(MirrorCatalog.PypiMirrors, "https://mirrors.huaweicloud.com/repository/pypi/simple/"));
    }

    [Fact]
    public void MirrorCatalog_UnknownLegacyValueFallsBackToFirstOption()
    {
        Assert.Equal(0, MirrorCatalog.IndexOf(MirrorCatalog.GithubMirrors, "https://my-old-custom-proxy.example/"));
        Assert.Equal(0, MirrorCatalog.IndexOf(MirrorCatalog.PypiMirrors, "garbage"));
    }

    [Fact]
    public void MirrorCatalog_FirstOptionIsAlwaysDirect()
    {
        Assert.Equal("", MirrorCatalog.GithubMirrors[0].Url);
        Assert.Equal("", MirrorCatalog.PypiMirrors[0].Url);
    }

    [Fact]
    public void BuildTorchInstallArgs_AmdSourceIsNeverMirrored()
    {
        var args = WheelService.BuildTorchInstallArgs("gfx1100", "--python \"py\"");
        Assert.Contains($"--index-url {WheelService.RocmNightlyIndexUrl}", args);
        Assert.DoesNotContain("ghproxy", args);
        Assert.DoesNotContain("mirror", args);
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

    // RDNA2 家族按整合包 detect_gpu.py 的芯片划分：Navi21=gfx1030、Navi22=gfx1031、Navi23=gfx1032、Navi24=gfx1034。
    // 6800M/6800S 是 Navi 22，必须排在同为 Navi 21 的桌面 6800 之前，否则被「Radeon RX 68」抢先误判。
    [Theory]
    [InlineData("AMD Radeon RX 6900 XT", "gfx1030")]
    [InlineData("AMD Radeon RX 6800", "gfx1030")]
    [InlineData("AMD Radeon RX 6800M", "gfx1031")]
    [InlineData("AMD Radeon RX 6800S", "gfx1031")]
    [InlineData("AMD Radeon RX 6700 XT", "gfx1031")]
    [InlineData("AMD Radeon RX 6650 XT", "gfx1032")]
    [InlineData("AMD Radeon RX 6500 XT", "gfx1034")]
    public void GpuArch_Rdna2FamilyChipsMapToOwnArch(string name, string expectedArch)
    {
        var info = GpuArchService.Instance.Map(name);
        Assert.True(info.Known);
        Assert.Equal(expectedArch, info.Arch);
    }

    // RDNA1 不能按「Radeon RX 5」前缀一刀切成 Navi 14：5700/5600 是 Navi 10（gfx1010），
    // 5500/5300 才是 Navi 14（gfx1012）；RX 500 系（Polaris）是 gfx803。
    [Theory]
    [InlineData("AMD Radeon RX 5700 XT", "gfx1010")]
    [InlineData("AMD Radeon RX 5600 XT", "gfx1010")]
    [InlineData("AMD Radeon RX 5500 XT", "gfx1012")]
    [InlineData("AMD Radeon RX 5300M", "gfx1012")]
    [InlineData("AMD Radeon RX 6550M", "gfx1034")] // 移动版仍是 Navi 24
    public void GpuArch_Rdna1AndMobileChipsUseOwnArch(string name, string expectedArch)
    {
        var info = GpuArchService.Instance.Map(name);
        Assert.True(info.Known);
        Assert.Equal(expectedArch, info.Arch);
    }

    [Theory]
    [InlineData("AMD Radeon RX 580")]
    [InlineData("AMD Radeon RX 560")]
    public void GpuArch_PolarisIsKnownButHasNoWindowsRocmBuild(string name)
    {
        var info = GpuArchService.Instance.Map(name);

        Assert.Equal("gfx803", info.Arch);
        Assert.False(info.IsAvailable);
    }

    [Fact]
    public void GpuArch_VegaIsGfx900NotMi100()
    {
        Assert.Equal("gfx900", GpuArchService.Instance.Map("AMD Radeon RX Vega 64").Arch);
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
    public void RequirementsFilter_FilteredFileNamedPerSource()
    {
        // 三清单（requirements / manager_requirements / agent_requirements）共用输出目录时，过滤件必须各自派生名字
        var dir = Path.Combine(Path.GetTempPath(), "reqfilter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "manager_requirements.txt");
            File.WriteAllText(src, "torch==2.9.0\ncomfyui_manager==4.2.2\n");

            var target = RequirementsFilter.WriteFilteredFile(src, dir);
            var lines = File.ReadAllLines(target);

            Assert.Equal(Path.Combine(dir, "manager_requirements_filtered.txt"), target);
            Assert.DoesNotContain("torch==2.9.0", lines);
            Assert.Contains("comfyui_manager==4.2.2", lines);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
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

    /// <summary>审查 #7 回归：无边界前缀匹配曾把 torchmetrics 拖进回滚命令，在 AMD 索引上整单解析失败。</summary>
    [Theory]
    [InlineData("torchmetrics==1.2.3", false)]      // 前缀撞车但非 torch 系
    [InlineData("torchcodec==0.1", false)]
    [InlineData("torch==2.4.0", true)]
    [InlineData("amd-torch-device-gfx1100==6.2.41136", true)] // 卸载名单会卸掉它，回滚也必须装得回
    [InlineData("amd-torchvision-device-gfx1100==0.19", true)]
    [InlineData("rocm-sdk-core==6.2.41136", true)]
    [InlineData("torchaudio==2.4.0", true)]
    [InlineData("einops==0.8.0", false)]
    public void IsTorchFamily_PrefixBoundary_ExcludesUnrelatedPackages(string spec, bool expected) =>
        Assert.Equal(expected, WheelService.IsTorchFamily(spec));

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

    // F-01：验证门必须是 --dry-run。uv 没有 pip download 子命令（0.11.19 实测退出码 2），
    // 退回 download 会让每次 torch 更新在 [2/6] 必然失败。
    [Theory]
    [InlineData("gfx1100")]
    [InlineData("gfx942")]
    public void ToDryRunArgs_InsertsDryRunInsteadOfDownload(string arch)
    {
        var install = WheelService.BuildTorchInstallArgs(arch, "--python \"py\"");
        var verify = WheelService.ToDryRunArgs(install);

        Assert.StartsWith("pip install --dry-run ", verify);
        Assert.Contains(WheelService.TorchIndexUrl(arch), verify);
        Assert.DoesNotContain("pip download", verify);
    }

    [Fact]
    public void ToDryRunArgs_LeavesNonInstallCommandUntouched() =>
        Assert.Equal("pip freeze --python \"py\"", WheelService.ToDryRunArgs("pip freeze --python \"py\""));

    // F-02：回滚索引必须与安装索引同源，否则 gfx942/gfx950 的快照在 nightly 里找不到。
    [Theory]
    [InlineData("gfx1100")]
    [InlineData("gfx942")]
    [InlineData("gfx950")]
    public void BuildRollbackArgs_UsesSameIndexAsInstall(string arch)
    {
        var install = WheelService.BuildTorchInstallArgs(arch, "--python \"py\"");
        var rollback = WheelService.BuildRollbackArgs(new[] { "torch==1.2.3" }, arch, "--python \"py\"");

        Assert.Contains($"--index-url {WheelService.TorchIndexUrl(arch)}", install);
        Assert.Contains($"--index-url {WheelService.TorchIndexUrl(arch)}", rollback);
    }

    [Fact]
    public void BuildRollbackArgs_UnknownArchFallsBackToNightly() =>
        Assert.Contains($"--index-url {WheelService.RocmNightlyIndexUrl}",
            WheelService.BuildRollbackArgs(new[] { "torch==1.2.3" }, null, "--python \"py\""));

    [Fact]
    public void SelectReleaseAsset_PrefersMatchThenWhl()
    {
        var assets = new[]
        {
            ("notes.txt", "https://x/notes.txt"),
            ("sageattention-2.2.0-cp312-win_amd64.whl", "https://x/sage.whl"),
        };
        Assert.Equal("https://x/sage.whl", WheelService.SelectReleaseAsset(assets, "cp312"));
        Assert.Equal("https://x/sage.whl", WheelService.SelectReleaseAsset(assets, null));
    }

    /// <summary>
    /// 指定了 assetMatch 却没有命中时必须返回 null，而不是退回 release 里的任意 whl——
    /// 上游常混发 Linux 轮（如 ROCm/aiter 只有 manylinux），退回会把必然装失败的包交给 uv。
    /// </summary>
    [Fact]
    public void SelectReleaseAsset_NoFallbackWhenMatchMisses()
    {
        var assets = new[]
        {
            ("amd_aiter-0.1.26-cp312-manylinux.whl", "https://x/aiter.whl"),
        };
        Assert.Null(WheelService.SelectReleaseAsset(assets, "win_amd64"));
    }

    [Fact]
    public void ParseReleaseAssets_ExtractsNameAndDownloadUrl()
    {
        var json = """
        {"assets":[
          {"name":"notes.txt","browser_download_url":"https://x/notes.txt"},
          {"name":"sage-2.2-cp312-win_amd64.whl","browser_download_url":"https://x/sage.whl"}
        ]}
        """;
        var assets = WheelService.ParseReleaseAssets(json);
        Assert.Equal(2, assets.Count);
        Assert.Equal("https://x/sage.whl", WheelService.SelectReleaseAsset(assets, "cp312"));
        Assert.Empty(WheelService.ParseReleaseAssets("{}"));
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

    // F-03：远端查询失败返回 null（而不是空串），界面才不会把「查不到」当成「有更新」。
    // ShortHash 必须能吃下 null——空串曾被当成合法值一路传到界面比较里。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ShortHash_TreatsMissingVersionAsUnknown(string? hash) =>
        Assert.Equal("unknown", RepoService.ShortHash(hash));

    [Fact]
    public void ShortHash_TruncatesToTenChars() =>
        Assert.Equal("0123456789", RepoService.ShortHash("0123456789abcdef"));
}
