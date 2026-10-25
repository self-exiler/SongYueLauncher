namespace ComfyUiRocmLauncher.Core.Models;

/// <summary>专用轮子按 arch 的安装规则（SRS FR-8.6，wheel-catalog.json）。</summary>
public sealed class WheelArchRule
{
    /// <summary>命中架构；"*" 为兜底。</summary>
    public string Arch { get; set; } = "*";
    /// <summary>GitHub 仓库（默认 releases/latest 动态解析）。</summary>
    public string? GhRepo { get; set; }
    /// <summary>钉死 release tag：设置时按 /releases/tags/{tag} 解析而非 latest——
    /// 上游 latest 未必是可用版本（如 0xDELUXA/flash-attention 的 latest 是 v3，作者实际用 v2.8.4）。</summary>
    public string? Tag { get; set; }
    /// <summary>release 资产名匹配子串。</summary>
    public string? AssetMatch { get; set; }
    public bool UseGhMirror { get; set; }
}

/// <summary>内置专用轮子清单条目。</summary>
public sealed class WheelCatalogEntry
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>可选组件：失败仅警告跳过。</summary>
    public bool Optional { get; set; }
    /// <summary>版本号不变需强制重装。</summary>
    public bool ForceReinstall { get; set; }
    /// <summary>无 archRules 时的固定 PyPI 规格。</summary>
    public string? Spec { get; set; }
    public List<WheelArchRule> ArchRules { get; set; } = new();

    /// <summary>解析指定 arch 命中的规则（先具体后 "*"）。</summary>
    public WheelArchRule? ResolveFor(string? arch)
    {
        if (ArchRules.Count == 0) return null;
        var exact = arch is null ? null : ArchRules.FirstOrDefault(r =>
            !string.Equals(r.Arch, "*", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Arch, arch, StringComparison.OrdinalIgnoreCase));
        return exact ?? ArchRules.FirstOrDefault(r => string.Equals(r.Arch, "*", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>wheel-catalog.json 根结构。</summary>
public sealed class WheelCatalogFile
{
    public int Version { get; set; }
    public string Updated { get; set; } = string.Empty;
    public List<WheelCatalogEntry> Wheels { get; set; } = new();
}
