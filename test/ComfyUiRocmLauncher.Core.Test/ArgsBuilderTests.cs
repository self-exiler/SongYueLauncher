using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.Core.Test;

public class ArgsBuilderTests
{
    private static readonly CliArgsCatalog Catalog = CliArgsCatalog.Instance;

    [Fact]
    public void Flag_EmitsOnlyName()
    {
        var args = new StructuredArgs();
        args.Enable("enable-manager", true);
        var tokens = ArgsBuilder.Build(args, Catalog);
        Assert.Contains("--enable-manager", tokens);
    }

    [Fact]
    public void IntArg_EmitsNameAndValue()
    {
        var args = new StructuredArgs();
        args.Enable("port", true);
        args.SetValue("port", "8189");
        var tokens = ArgsBuilder.Build(args, Catalog);
        Assert.Equal(new[] { "--port", "8189" }, tokens);
    }

    [Fact]
    public void OptionalString_WithoutValue_EmitsBareFlag()
    {
        var args = new StructuredArgs();
        args.Enable("listen", true);
        Assert.Equal(new[] { "--listen" }, ArgsBuilder.Build(args, Catalog));
    }

    [Fact]
    public void MultiEnum_EmitsPickedValues()
    {
        var args = new StructuredArgs();
        args.Enable("fast", true);
        args.Multi["fast"] = new List<string> { "autotune" };
        Assert.Equal(new[] { "--fast", "autotune" }, ArgsBuilder.Build(args, Catalog));
    }

    [Fact]
    public void RepeatableList_RepeatsFlagPerLine()
    {
        var args = new StructuredArgs();
        args.Enable("feature-flag", true);
        args.Lines["feature-flag"] = new List<string> { "a=1", "b" };
        Assert.Equal(new[] { "--feature-flag", "a=1", "--feature-flag", "b" }, ArgsBuilder.Build(args, Catalog));
    }

    [Fact]
    public void ExtraArgs_AppendedLast()
    {
        var args = new StructuredArgs();
        args.Enable("enable-manager", true);
        var tokens = ArgsBuilder.Build(args, Catalog, "--cache-lru 5");
        Assert.Equal("--cache-lru", tokens[^2]);
        Assert.Equal("5", tokens[^1]);
    }

    [Fact]
    public void SplitExtra_RespectsQuotes()
    {
        var tokens = ArgsBuilder.SplitExtra("--tls-keyfile \"C:\\a b\\key.pem\" --port 8188");
        Assert.Equal(new[] { "--tls-keyfile", "C:\\a b\\key.pem", "--port", "8188" }, tokens);
    }

    [Fact]
    public void DeriveBrowserUrl_DefaultsToLoopback8188()
    {
        Assert.Equal("http://127.0.0.1:8188", ArgsBuilder.DeriveBrowserUrl(new StructuredArgs()));
    }

    [Fact]
    public void DeriveBrowserUrl_WildcardListen_UsesLoopback()
    {
        var args = new StructuredArgs();
        args.Enable("listen", true);
        args.SetValue("listen", "0.0.0.0,::");
        Assert.Equal("http://127.0.0.1:8188", ArgsBuilder.DeriveBrowserUrl(args));
    }

    [Fact]
    public void DeriveBrowserUrl_ExplicitListenAndPort()
    {
        var args = new StructuredArgs();
        args.Enable("listen", true);
        args.SetValue("listen", "192.168.1.5");
        args.Enable("port", true);
        args.SetValue("port", "9000");
        Assert.Equal("http://192.168.1.5:9000", ArgsBuilder.DeriveBrowserUrl(args));
    }

    [Fact]
    public void DeriveBrowserUrl_TlsWhenBothFilesGiven()
    {
        var args = new StructuredArgs();
        args.Enable("tls-keyfile", true);
        args.SetValue("tls-keyfile", "k.pem");
        args.Enable("tls-certfile", true);
        args.SetValue("tls-certfile", "c.pem");
        Assert.StartsWith("https://", ArgsBuilder.DeriveBrowserUrl(args));
    }
}
