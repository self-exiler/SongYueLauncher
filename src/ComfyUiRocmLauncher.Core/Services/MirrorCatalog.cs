namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>一个可选镜像条目：界面显示名 + 实际生效值（空串 = 直连 / 官方源）。</summary>
public sealed record MirrorOption(string Label, string Url)
{
    /// <summary>下拉框用：空值显示为「直连」标签，其余显示域名，避免长 URL 挤爆控件。</summary>
    public string Display => string.IsNullOrEmpty(Url) ? Label : $"{Label}  {Url}";
}

/// <summary>
/// 镜像候选清单（SRS FR-8.2）：界面为不可编辑下拉，取值只能来自这里。
/// GitHub 镜像必须是「前缀型」（拼接方式见 <see cref="MirrorPolicy.Github"/>），
/// 替换型（如 kkgithub）与语义不同的（gitclone.com）不在此列。
/// </summary>
public static class MirrorCatalog
{
    public static IReadOnlyList<MirrorOption> GithubMirrors { get; } =
    [
        new("直连 GitHub", ""),
        new("ghproxy", "https://ghproxy.net/"),
        new("gh-proxy", "https://gh-proxy.com/"),
        new("ghfast", "https://ghfast.top/"),
    ];

    public static IReadOnlyList<MirrorOption> PypiMirrors { get; } =
    [
        new("官方 PyPI", ""),
        new("阿里云", "https://mirrors.aliyun.com/pypi/simple/"),
        new("清华", "https://pypi.tuna.tsinghua.edu.cn/simple/"),
        new("华为云", "https://mirrors.huaweicloud.com/repository/pypi/simple/"),
        new("腾讯云", "https://mirrors.cloud.tencent.com/pypi/simple/"),
        new("中科大", "https://mirrors.ustc.edu.cn/pypi/simple/"),
    ];

    /// <summary>按已保存值定位候选项；历史自定义地址不在清单内时回落到首项（直连 / 官方）。</summary>
    public static int IndexOf(IReadOnlyList<MirrorOption> options, string saved)
    {
        var normalized = (saved ?? string.Empty).Trim();
        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].Url, normalized, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return 0;
    }
}
