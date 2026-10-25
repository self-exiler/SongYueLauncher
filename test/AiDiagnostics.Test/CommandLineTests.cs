using AiDiagnostics.Runtime;

namespace AiDiagnostics.Test;

/// <summary>Windows 命令行引用规则（从 Core.Test 迁入——实现本来就在这一层）。</summary>
public class CommandLineTests
{
    [Theory]
    [InlineData("simple", "\"simple\"")]
    [InlineData("C:\\Program Files\\ComfyUI", "\"C:\\Program Files\\ComfyUI\"")]
    [InlineData("C:\\pack\\", "\"C:\\pack\\\\\"")]          // 结尾反斜杠需翻倍，否则会吃掉引号
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]        // 内部引号前补反斜杠
    [InlineData("--index-url https://x/y", "\"--index-url https://x/y\"")]
    public void Quote_FollowsWindowsArgumentRules(string input, string expected) =>
        Assert.Equal(expected, CommandLine.Quote(input));

    [Fact]
    public void Join_QuotesEveryToken() =>
        Assert.Equal("\"main.py\" \"--port\" \"8188\"",
            CommandLine.Join(new[] { "main.py", "--port", "8188" }));
}
