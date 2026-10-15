namespace ComfyUiRocmLauncher.Core.Models;

/// <summary>
/// 结构化启动参数的持久化形态（SRS 5.1 args.structured）。
/// 仅持久化"非默认"的参数：Enabled 为启用的参数名，Values / Multi / Lines 保存各自取值。
/// </summary>
public sealed class StructuredArgs
{
    /// <summary>已启用的参数名集合（-- 后不带前缀的形式）。</summary>
    public HashSet<string> Enabled { get; set; } = new(StringComparer.Ordinal);

    /// <summary>单值参数取值：int / float / string / enum / optionalString / optionalInt / verbose 级别。</summary>
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);

    /// <summary>多值参数取值：multiEnum / floatList / stringList。</summary>
    public Dictionary<string, List<string>> Multi { get; set; } = new(StringComparer.Ordinal);

    /// <summary>逐行参数取值：repeatableStringList / repeatableKeyValue，每行为一次独立传参。</summary>
    public Dictionary<string, List<string>> Lines { get; set; } = new(StringComparer.Ordinal);

    public bool IsEnabled(string name) => Enabled.Contains(name);

    public void Enable(string name, bool enabled)
    {
        if (enabled) Enabled.Add(name);
        else Enabled.Remove(name);
    }

    public string Value(string name) => Values.TryGetValue(name, out var v) ? v : string.Empty;

    public void SetValue(string name, string value)
    {
        if (string.IsNullOrEmpty(value)) Values.Remove(name);
        else Values[name] = value;
    }

    public List<string> MultiOf(string name) =>
        Multi.TryGetValue(name, out var v) ? v : new List<string>();

    public List<string> LinesOf(string name) =>
        Lines.TryGetValue(name, out var v) ? v : new List<string>();
}

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
