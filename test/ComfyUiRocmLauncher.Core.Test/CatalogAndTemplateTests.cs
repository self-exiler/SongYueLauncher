using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.Core.Test;

public class CatalogAndTemplateTests
{
    private static readonly CliArgsCatalog Catalog = CliArgsCatalog.Instance;

    [Fact]
    public void Catalog_MatchesUpstreamArgumentCount()
    {
        // 与 docs/cli_args_upstream.py 逐项核对：110 项
        Assert.Equal(110, Catalog.Count);
    }

    [Theory]
    [InlineData(1, 2)][InlineData(2, 2)][InlineData(3, 7)][InlineData(4, 3)][InlineData(5, 5)]
    [InlineData(6, 5)][InlineData(7, 5)][InlineData(8, 6)][InlineData(9, 2)][InlineData(10, 2)]
    public void MutexGroups_HaveExpectedMembers(int group, int expected)
    {
        Assert.Equal(expected, Catalog.All.Count(a => a.Mutex == group));
    }

    [Fact]
    public void EnforceMutex_ClosesPeers()
    {
        var args = new StructuredArgs();
        args.Enable("use-quad-cross-attention", true);
        args.Enable("use-split-cross-attention", true);

        var closed = Catalog.EnforceMutex(args, "use-ck-attention");

        Assert.Contains("use-quad-cross-attention", closed);
        Assert.Contains("use-split-cross-attention", closed);
        Assert.False(args.IsEnabled("use-quad-cross-attention"));
    }

    [Fact]
    public void ApplyBatDefaults_EnablesBaseGroup()
    {
        var args = new StructuredArgs();
        Catalog.ApplyBatDefaults(args);

        Assert.True(args.IsEnabled("cache-lru"));
        Assert.Equal("20", args.Value("cache-lru"));
        Assert.True(args.IsEnabled("disable-api-nodes"));
        Assert.True(args.IsEnabled("enable-manager-legacy-ui"));
    }

    [Fact]
    public void ClearAll_RemovesEverything()
    {
        var args = new StructuredArgs();
        Catalog.ApplyBatDefaults(args);
        Catalog.ClearAll(args);
        Assert.Empty(args.Enabled);
    }

    [Fact]
    public void Template_Rdna3_ExcludesRocblasVars()
    {
        var env = TemplateCatalog.Instance.ExpandEnvVars("rdna3", @"D:\AI\comfyui-rocm", @"D:\AI\comfyui-rocm\.venv");
        Assert.False(env.ContainsKey("ROCBLAS_TENSILE_DB_PATH"));
        Assert.False(env.ContainsKey("MIOPEN_SYSTEM_DB_PATH"));
        Assert.True(env.ContainsKey("TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL"));
    }

    [Fact]
    public void Template_ExpandsPlaceholders()
    {
        var env = TemplateCatalog.Instance.ExpandEnvVars("rdna4", @"D:\Comfy", @"D:\Comfy\.venv");
        Assert.Equal(@"D:\Comfy\triton-cache", env["TRITON_CACHE_DIR"]);
        Assert.Equal(@"D:\Comfy\.venv\Lib\site-packages\_rocm_sdk_devel\bin", env["MIOPEN_SYSTEM_DB_PATH"]);
    }

    [Fact]
    public void Template_ApplyArgs_SetsStructured()
    {
        var args = new StructuredArgs();
        TemplateCatalog.Instance.ApplyArgs("rdna12", args, Catalog);
        Assert.True(args.IsEnabled("use-quad-cross-attention"));
        Assert.Equal("20", args.Value("cache-lru"));
    }

    [Fact]
    public void Template_ApplyArgs_RespectsMutex()
    {
        var args = new StructuredArgs();
        args.Enable("use-ck-attention", true);
        TemplateCatalog.Instance.ApplyArgs("rdna12", args, Catalog);
        Assert.False(args.IsEnabled("use-ck-attention"));
        Assert.True(args.IsEnabled("use-quad-cross-attention"));
    }

    private static AppConfig NewConfig() => new()
    {
        ComfyUiDir = @"D:\AI\comfyui-rocm",
        PythonExe = @"D:\AI\comfyui-rocm\.venv\Scripts\python.exe",
    };

    [Fact]
    public void ApplyTo_Builtin_ReplacesEnvAndArgsEntirely()
    {
        var config = NewConfig();
        config.CustomEnvVars["OLD_KEY"] = "x";
        config.Args.Enable("use-ck-attention", true);

        var tpl = TemplateCatalog.Instance.Resolve("rdna12", config)!;
        TemplateCatalog.Instance.ApplyTo(tpl, config, Catalog);

        Assert.False(config.CustomEnvVars.ContainsKey("OLD_KEY"));
        Assert.True(config.CustomEnvVars.ContainsKey("TRITON_CACHE_DIR"));
        Assert.True(config.Args.IsEnabled("use-quad-cross-attention"));
        Assert.False(config.Args.IsEnabled("use-ck-attention"));
    }

    [Fact]
    public void CaptureAsTemplate_WritesPlaceholdersIntoSnapshot()
    {
        var config = NewConfig();
        config.CustomEnvVars["MY_VAR"] = @"D:\AI\comfyui-rocm\some\path";
        config.CustomEnvVars["VENV_VAR"] = @"D:\AI\comfyui-rocm\.venv\Lib";

        var tpl = TemplateCatalog.Instance.CaptureAsTemplate(config, "我的模板", "mine");

        Assert.True(tpl.IsCustom);
        Assert.Equal(@"{comfyUiDir}\some\path", tpl.EnvSnapshot!["MY_VAR"]);
        Assert.Equal(@"{venvDir}\Lib", tpl.EnvSnapshot["VENV_VAR"]);
    }

    [Fact]
    public void ApplyTo_Custom_RestoresWithNewDirsAndClearsJunk()
    {
        var config = NewConfig();
        config.CustomEnvVars["MY_VAR"] = @"D:\AI\comfyui-rocm\some\path";
        config.Args.Enable("cache-lru", true);
        config.Args.SetValue("cache-lru", "20");

        var tpl = TemplateCatalog.Instance.CaptureAsTemplate(config, "mine", "mine");

        // 换目录 + 塞入无关变量/参数后再应用，应恢复快照并整体替换
        config.ComfyUiDir = @"E:\New\comfyui";
        config.PythonExe = @"E:\New\comfyui\.venv\Scripts\python.exe";
        config.CustomEnvVars.Clear();
        config.CustomEnvVars["JUNK"] = "1";
        config.Args.Enable("preview-method", true);
        TemplateCatalog.Instance.ApplyTo(tpl, config, Catalog);

        Assert.False(config.CustomEnvVars.ContainsKey("JUNK"));
        Assert.Equal(@"E:\New\comfyui\some\path", config.CustomEnvVars["MY_VAR"]);
        Assert.False(config.Args.IsEnabled("preview-method"));
        Assert.True(config.Args.IsEnabled("cache-lru"));
        Assert.Equal("20", config.Args.Value("cache-lru"));
    }

    [Fact]
    public void CaptureArgs_IsDeepCopy()
    {
        var config = NewConfig();
        config.Args.Enable("port", true);
        config.Args.SetValue("port", "8188");

        var snapshot = TemplateCatalog.Instance.CaptureArgs(config);
        config.Args.Enable("port", false);

        Assert.True(snapshot.IsEnabled("port"));
        Assert.Equal("8188", snapshot.Value("port"));
    }

    [Fact]
    public void CaptureId_DeduplicatesBySuffix()
    {
        var config = new AppConfig();
        config.CustomTemplates.Add(new GpuTemplate { Id = "My-Tpl", Title = "My Tpl" });

        Assert.Equal("My-Tpl_2", TemplateCatalog.CaptureId("My Tpl", config));
    }

    [Fact]
    public void Resolve_FindsBuiltinAndCustom()
    {
        var config = new AppConfig();
        var custom = new GpuTemplate { Id = "zz", Title = "zz", EnvSnapshot = new() { ["A"] = "1" } };
        config.CustomTemplates.Add(custom);

        Assert.NotNull(TemplateCatalog.Instance.Resolve("rdna3", config));
        Assert.Same(custom, TemplateCatalog.Instance.Resolve("zz", config));
        Assert.Null(TemplateCatalog.Instance.Resolve("nope", config));
        Assert.Equal(4, TemplateCatalog.Instance.AllTemplates(config).Count);
    }
}
