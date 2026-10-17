namespace ComfyUiRocmLauncher.Core.Models;

/// <summary>
/// config.json 根结构（SRS 5.1）。
/// </summary>
public sealed class AppConfig
{
    public string ComfyUiDir { get; set; } = string.Empty;
    public string PythonExe { get; set; } = string.Empty;
    public string? UvExe { get; set; }
    public string? GitExe { get; set; }
    public bool AutoOpenBrowser { get; set; }
    public StructuredArgs Args { get; set; } = new();
    public string ExtraArgs { get; set; } = string.Empty;
    public Dictionary<string, string> CustomEnvVars { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<GpuTemplate> CustomTemplates { get; set; } = new();
    public EnvUpdateSettings EnvUpdate { get; set; } = new();
    public int LogRetainCount { get; set; } = 30;
    public int LogRetainTotalMB { get; set; } = 200;

    // ---------- 界面状态（下次启动恢复，FR-9） ----------

    /// <summary>窗口左上角 X（AppWindow 像素坐标）。</summary>
    public int WindowX { get; set; }

    /// <summary>窗口左上角 Y。</summary>
    public int WindowY { get; set; }

    /// <summary>还原态窗口宽；为 0 表示尚未记录（首次运行用系统默认尺寸）。</summary>
    public int WindowWidth { get; set; }

    /// <summary>还原态窗口高；为 0 表示尚未记录。</summary>
    public int WindowHeight { get; set; }

    /// <summary>上次退出时是否最大化。</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>上次停留的页面 Tag（MainWindow 导航恢复用）；空 = 主页。</summary>
    public string LastPage { get; set; } = string.Empty;
}

/// <summary>
/// 环境与更新页配置（SRS FR-8 / 5.1 envUpdate）。镜像空串 = 直连（GitHub）/ 官方源（PyPI）。
/// </summary>
public sealed class EnvUpdateSettings
{
    /// <summary>整合包仓库地址（用户指定，无内置默认）。</summary>
    public string RepoUrl { get; set; } = string.Empty;

    /// <summary>GitHub 加速镜像前缀（仅作用于 git clone 与 GitHub release 轮子）；空 = 直连。</summary>
    public string GithubMirror { get; set; } = string.Empty;

    /// <summary>PyPI 镜像 URL（仅作用于通用包与项目依赖）；空 = 官方源。</summary>
    public string PypiMirror { get; set; } = string.Empty;
}
