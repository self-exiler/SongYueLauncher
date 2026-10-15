using System.Text.Json.Serialization;

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

    // 自定义模板字段（内置模板不填）。EnvSnapshot 值内可含 {comfyUiDir}/{venvDir} 占位符。
    public Dictionary<string, string>? EnvSnapshot { get; set; }
    public StructuredArgs? ArgsSnapshot { get; set; }

    /// <summary>是否为自定义模板（含环境变量快照）。</summary>
    [JsonIgnore]
    public bool IsCustom => EnvSnapshot is not null;

    /// <summary>模板启动参数项数。</summary>
    [JsonIgnore]
    public int ArgCount => ArgsSnapshot?.Enabled.Count ?? Args.Count;
}

public sealed class GpuTemplateFile
{
    public Dictionary<string, string> CommonEnvVars { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<GpuTemplate> Templates { get; set; } = new();
}
