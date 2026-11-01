using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.Core.Test;

/// <summary>
/// FR-10 方案 B 的纯解析部分：仓库地址识别、目录名净化（防穿越/非法字符）、GitHub 镜像只对 github.com 生效。
/// clone 与装依赖要联网与子进程，不在单测覆盖范围（真机验收清单里）。
/// </summary>
public class PluginInstallServiceTests
{
    [Theory]
    [InlineData("https://github.com/a/b", true)]
    [InlineData("  http://gitlab.example.com/x/y.git  ", true)]
    [InlineData("git@github.com:author/repo.git", true)]
    [InlineData("", false)]
    [InlineData("ftp://host/repo", false)]
    [InlineData("not a url at all", false)]
    [InlineData("C:\\local\\path", false)]
    public void 地址识别(string url, bool expected) =>
        Assert.Equal(expected, PluginInstallService.IsClonableUrl(url));

    [Theory]
    [InlineData("https://github.com/author/ComfyUI-Thing", "ComfyUI-Thing")]
    [InlineData("https://github.com/author/ComfyUI-Thing.git", "ComfyUI-Thing")]
    [InlineData("https://github.com/author/ComfyUI-Thing/", "ComfyUI-Thing")]
    [InlineData("git@github.com:author/repo-name.git", "repo-name")]
    [InlineData("https://gitlab.com/group/sub/proj", "proj")]
    public void 目录名取末段(string url, string expected) =>
        Assert.Equal(expected, PluginInstallService.RepoName(url));

    [Theory]
    [InlineData("https://github.com/author/..git", "plugin")]
    [InlineData("https://github.com/author/", "author")]
    [InlineData("https://github.com/author/%3Cbad%3E:name", "%3Cbad%3E_name")]
    public void 目录名净化为安全文件名(string url, string expected) =>
        Assert.Equal(expected, PluginInstallService.RepoName(url));

    [Theory]
    [InlineData("https://github.com/author/..%2f..%2fetc")]
    [InlineData("https://github.com/author/..")]
    [InlineData("https://github.com/author/../..")]
    [InlineData("https://github.com/author/a..b")]
    public void 任意畸形地址_拼接结果都落在custom_nodes内(string url)
    {
        var root = Path.GetFullPath(@"D:\comfy\custom_nodes");
        var target = Path.GetFullPath(Path.Combine(root, PluginInstallService.RepoName(url)));
        Assert.StartsWith(root + Path.DirectorySeparatorChar, target, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(root, target);
    }

    [Fact]
    public void GitHub镜像只作用于github地址()
    {
        const string mirror = "https://ghproxy.net";
        Assert.Equal("https://ghproxy.net/https://github.com/a/b",
            PluginInstallService.ApplyMirror("https://github.com/a/b", mirror));
        // 非 GitHub 站点直连：套前缀会得到一个不存在的地址
        Assert.Equal("https://gitlab.com/a/b", PluginInstallService.ApplyMirror("https://gitlab.com/a/b", mirror));
        // 镜像留空 = 直连
        Assert.Equal("https://github.com/a/b", PluginInstallService.ApplyMirror("https://github.com/a/b", ""));
    }
}
