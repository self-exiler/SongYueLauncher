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
}
