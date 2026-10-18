using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.Core.Test;

/// <summary>
/// 启动参数页的命令预览（FR-2.4）：预览必须与真实启动同源，因此这里钉住
/// 「解释器已加引号」「参数段逐项引用」「额外参数段带 IsExtra 标记」三条契约。
/// </summary>
public class ArgsPreviewFormatterTests
{
    private static readonly CliArgsCatalog Catalog = CliArgsCatalog.Instance;

    [Fact]
    public void QuoteInterpreter_AlwaysQuotes()
    {
        Assert.Equal("\"D:\\AI\\comfyui-rocm\\.venv\\Scripts\\python.exe\"",
            ArgsPreviewFormatter.QuoteInterpreter(@"D:\AI\comfyui-rocm\.venv\Scripts\python.exe"));
        Assert.Equal("\"\"", ArgsPreviewFormatter.QuoteInterpreter(string.Empty));
    }

    [Fact]
    public void Format_EmitsQuotedTokensForEnabledArgs()
    {
        var args = new StructuredArgs();
        args.Enable("enable-manager", true);

        var tokens = ArgsPreviewFormatter.Format(args, Catalog, null);

        // Flag 参数只有一个 token，且加引号后仍以 -- 开头（界面据此上强调色）
        var token = Assert.Single(tokens);
        Assert.Equal("\"--enable-manager\"", token.Text);
        Assert.False(token.IsExtra);
        Assert.True(token.IsFlag);
    }

    /// <summary>
    /// 这是 2026-10-18 报障（切换开关后命令预览完全不更新）的回归点：
    /// 启用的参数必须出现在预览里，且不得被误标为「额外参数」。
    /// </summary>
    [Fact]
    public void Format_ReflectsToggleOnAndOff()
    {
        var args = new StructuredArgs();

        Assert.Empty(ArgsPreviewFormatter.Format(args, Catalog, null));

        args.Enable("enable-manager", true);
        var on = ArgsPreviewFormatter.Format(args, Catalog, null);
        Assert.Contains(on, t => t.Text == "\"--enable-manager\"");
        Assert.All(on, t => Assert.False(t.IsExtra));

        args.Enable("enable-manager", false);
        Assert.Empty(ArgsPreviewFormatter.Format(args, Catalog, null));
    }

    [Fact]
    public void Format_MarksOnlyExtraTailAsExtra()
    {
        var args = new StructuredArgs();
        args.Enable("port", true);
        args.SetValue("port", "8189");

        var tokens = ArgsPreviewFormatter.Format(args, Catalog, "--lowvram --foo \"a b\"");
        var extras = tokens.Where(t => t.IsExtra).Select(t => t.Text).ToArray();

        Assert.Equal(new[] { "\"--port\"", "\"8189\"" }, tokens.Where(t => !t.IsExtra).Select(t => t.Text));
        Assert.Equal(new[] { "\"--lowvram\"", "\"--foo\"", "\"a b\"" }, extras);
    }

    [Fact]
    public void Format_EmptyExtraDoesNotMarkAnything()
    {
        var args = new StructuredArgs();
        args.Enable("enable-manager", true);

        Assert.All(ArgsPreviewFormatter.Format(args, Catalog, "   "), t => Assert.False(t.IsExtra));
    }

    /// <summary>
    /// 2026-10-18 报障的根因：加引号后文本变成 <c>"--port"</c>，界面若仍用
    /// <c>Text.StartsWith("--")</c> 判断参数名就会全部判失败、参数段拿不到颜色（不可见）。
    /// 因此 IsFlag 必须看「加引号前的原始 token」。
    /// </summary>
    [Fact]
    public void Format_IsFlagIsJudgedBeforeQuoting()
    {
        var args = new StructuredArgs();
        args.Enable("port", true);
        args.SetValue("port", "8189");

        var tokens = ArgsPreviewFormatter.Format(args, Catalog, "--lowvram extras");

        // 断言界面不会再错用「已加引号的 Text 是否以 -- 开头」：
        Assert.All(tokens, t => Assert.False(t.Text.StartsWith("--", StringComparison.Ordinal)));
        // 而 IsFlag 正确标记了两个参数名（--port / --lowvram），取值与裸参数不算
        Assert.Equal(2, tokens.Count(t => t.IsFlag));
        Assert.True(tokens[0].IsFlag);
        Assert.False(tokens[1].IsFlag);
    }
}
