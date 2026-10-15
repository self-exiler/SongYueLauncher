namespace ComfyUiRocmLauncher.Core.Models;

/// <summary>显卡名称片段 → arch 映射条目（SRS FR-8.2，gpu-arch-map.json）。</summary>
public sealed class GpuArchRule
{
    public string Match { get; set; } = string.Empty;
    public string Arch { get; set; } = string.Empty;
    public bool ArchAvailable { get; set; } = true;
    public string? Template { get; set; }
}

/// <summary>arch 解析结果。</summary>
public sealed record ArchInfo(string? Arch, bool IsLegacy, bool IsAvailable, string? TemplateId)
{
    public bool Known => Arch is not null;
}

/// <summary>gpu-arch-map.json 根结构。</summary>
public sealed class GpuArchMapFile
{
    public List<string> Legacy { get; set; } = new();
    public List<GpuArchRule> Rules { get; set; } = new();
}
