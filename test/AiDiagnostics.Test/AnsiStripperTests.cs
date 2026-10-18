using AiDiagnostics.Runtime;

namespace AiDiagnostics.Test;

/// <summary>ANSI 去色与 SGR 扫描的契约（扫描逻辑现在只有这一份，Core 的 AnsiParser 复用 <c>TryMatchSgr</c>）。</summary>
public class AnsiStripperTests
{
    [Fact]
    public void Strip_RemovesSgrAndKeepsOtherEscapes()
    {
        Assert.Equal("ok tail", AnsiStripper.Strip("\u001b[32mok\u001b[0m tail"));
        Assert.Equal("x\u001b[2Jy", AnsiStripper.Strip("x\u001b[2Jy")); // 非 SGR（J）保留，防止吞正文
    }

    [Fact]
    public void TryMatchSgr_ExtractsColorCodeAndEnd()
    {
        Assert.True(AnsiStripper.TryMatchSgr("a\u001b[1;31mb", 1, out var code, out var after));
        Assert.Equal("1;31", code);
        Assert.Equal(8, after);

        Assert.False(AnsiStripper.TryMatchSgr("\u001b[2J", 0, out _, out _));
        Assert.False(AnsiStripper.TryMatchSgr("plain", 0, out _, out _));
    }
}

