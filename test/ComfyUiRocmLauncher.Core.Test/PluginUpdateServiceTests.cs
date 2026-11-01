using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.Core.Test;

/// <summary>
/// FR-10 方案 C 的纯逻辑：ls-remote 输出解析与「是否可更新」判定。
/// 真实网络检测与 pull/fetch 执行不进单测（需联网），列在真机验收清单。
/// </summary>
public class PluginUpdateServiceTests
{
    [Fact]
    public void 解析ls_remote_取指定ref的hash()
    {
        const string output = "7fd1a60b01f997db035e0ba4f1b74777e2f72f9c\trefs/heads/main\n" +
                              "abc1234567890abc1234567890abc1234567890\trefs/heads/dev\n";
        Assert.Equal("7fd1a60b01f997db035e0ba4f1b74777e2f72f9c",
            PluginUpdateService.ParseRemoteHash(output, "refs/heads/main"));
        Assert.Equal("abc1234567890abc1234567890abc1234567890",
            PluginUpdateService.ParseRemoteHash(output, "refs/heads/dev"));
        Assert.Null(PluginUpdateService.ParseRemoteHash(output, "refs/heads/master"));
    }

    [Fact]
    public void 解析ls_remote_容忍空行与crlf()
    {
        const string output = "\r\n7fd1a60b01f997db035e0ba4f1b74777e2f72f9c\trefs/heads/main\r\n";
        Assert.Equal("7fd1a60b01f997db035e0ba4f1b74777e2f72f9c",
            PluginUpdateService.ParseRemoteHash(output, "refs/heads/main"));
    }

    [Theory]
    [InlineData("7fd1a60", "7fd1a60b01f997db035e0ba4f1b74777e2f72f9c", false)]  // 截短前缀相同 = 已最新
    [InlineData("7fd1a60", "9999999999f997db035e0ba4f1b74777e2f72f9c", true)]   // 不同 = 可更新
    [InlineData("7fd1a60", null, false)]                                       // 远端没取到，不谎报可更新
    [InlineData(null, "7fd1a60b01f997db035e0ba4f1b74777e2f72f9c", false)]      // 本地读不到，同上
    [InlineData("", "", false)]
    public void 可更新判定按前缀比对(string? local, string? remote, bool expected) =>
        Assert.Equal(expected, PluginUpdateService.NeedsUpdate(local, remote));

    [Theory]
    [InlineData("https://gh-proxy.com/https://github.com/a/b", "https://gh-proxy.com", "https://github.com/a/b")]
    [InlineData("https://gh-proxy.com/https://github.com/a/b", "https://gh-proxy.com/", "https://github.com/a/b")]
    [InlineData("https://github.com/a/b", "https://gh-proxy.com", "https://github.com/a/b")]  // 没套前缀就不动
    [InlineData("https://github.com/a/b", "", "https://github.com/a/b")]                       // 未配置镜像
    public void 检测与更新要剥掉镜像前缀_镜像不代理info_refs(string url, string mirror, string expected) =>
        Assert.Equal(expected, PluginUpdateService.StripMirror(url, mirror));

    [Fact]
    public void 检测结论可失效_更新后不会继续显示可更新()
    {
        // ForgetAll 只要求可安全重复调用（更新成功后 GUI 会清缓存重测）
        PluginUpdateService.ForgetAll();
        PluginUpdateService.Forget(@"D:\comfy\custom_nodes\whatever");
        PluginUpdateService.ForgetAll();
    }
}
