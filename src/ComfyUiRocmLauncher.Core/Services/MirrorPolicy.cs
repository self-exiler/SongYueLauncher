namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 镜像三分法实施（SRS FR-8.2 硬规则）：GitHub 镜像仅作用于 git clone 与 GitHub release 轮子；
/// PyPI 镜像仅作用于通用 PyPI 包与项目依赖；AMD 官方源永远直连（本服务不产生任何 AMD 变换）。
/// </summary>
public static class MirrorPolicy
{
    /// <summary>GitHub 资源（仓库 clone / release 资产）经镜像前缀改写；mirror 为空则原样（直连）。</summary>
    public static string Github(string url, string mirror)
    {
        if (string.IsNullOrWhiteSpace(mirror) || string.IsNullOrWhiteSpace(url)) return url;
        return mirror.TrimEnd('/') + "/" + url;
    }

    /// <summary>PyPI 安装的 index 参数；mirror 为空返回空串（用官方源）。</summary>
    public static string PypiIndexArg(string mirror) =>
        string.IsNullOrWhiteSpace(mirror) ? string.Empty : $"--index-url {mirror.Trim()} ";

    /// <summary>AMD nightly / legacy 源永远直连，恒返回原 URL。</summary>
    public static string AmdDirect(string url) => url;
}
