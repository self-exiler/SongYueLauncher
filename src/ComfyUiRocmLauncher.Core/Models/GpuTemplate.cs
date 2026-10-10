namespace ComfyUiRocmLauncher.Core.Models;

/// <summary>
/// GPU 模板定义（SRS 附录 B），来自内置 Resources/gpu-templates.json。
/// </summary>
public sealed class GpuTemplateArg
{
    public string Name { get; set; } = string.Empty;
    public string? Value { get; set; }
}

public sealed class GpuTemplate
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<string> ExcludeCommonEnvVars { get; set; } = new();
    public Dictionary<string, string> ExtraEnvVars { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<GpuTemplateArg> Args { get; set; } = new();
}

public sealed class GpuTemplateFile
{
    public int Version { get; set; }
    public Dictionary<string, string> CommonEnvVars { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<GpuTemplate> Templates { get; set; } = new();
}
