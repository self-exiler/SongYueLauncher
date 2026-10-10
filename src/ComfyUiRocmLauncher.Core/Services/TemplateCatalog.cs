using System.Reflection;
using System.Text.Json;
using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// GPU 模板（附录 B）加载与应用：模板的环境变量与启动参数写入普通配置，之后可自由编辑（FR-3.2）。
/// </summary>
public sealed class TemplateCatalog
{
    private const string ResourceSuffix = "Resources.gpu-templates.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static TemplateCatalog? _instance;

    private TemplateCatalog(GpuTemplateFile file)
    {
        Templates = file.Templates;
        CommonEnvVars = file.CommonEnvVars;
    }

    private IReadOnlyList<GpuTemplate> Templates { get; }

    private IReadOnlyDictionary<string, string> CommonEnvVars { get; }

    public static TemplateCatalog Instance => _instance ??= Load();

    private static TemplateCatalog Load()
    {
        var asm = typeof(TemplateCatalog).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"未找到内嵌资源 {ResourceSuffix}");
        var file = JsonSerializer.Deserialize<GpuTemplateFile>(stream, JsonOptions)
            ?? throw new InvalidOperationException("gpu-templates.json 反序列化失败");
        return new TemplateCatalog(file);
    }

    private GpuTemplate? Get(string id) =>
        Templates.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 展开模板环境变量（公共层 - 排除项 + 模板追加项），并替换 {comfyUiDir} / {venvDir} 占位符。
    /// </summary>
    public Dictionary<string, string> ExpandEnvVars(string templateId, string comfyUiDir, string venvDir)
    {
        var tpl = Get(templateId) ?? throw new ArgumentException($"未知模板 {templateId}", nameof(templateId));
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(tpl.ExcludeCommonEnvVars, StringComparer.OrdinalIgnoreCase);

        foreach (var kv in CommonEnvVars)
        {
            if (excluded.Contains(kv.Key)) continue;
            result[kv.Key] = Expand(kv.Value, comfyUiDir, venvDir);
        }
        foreach (var kv in tpl.ExtraEnvVars)
        {
            result[kv.Key] = Expand(kv.Value, comfyUiDir, venvDir);
        }
        return result;
    }

    /// <summary>
    /// 把模板的启动参数写入结构化参数；会先关闭同互斥组的冲突项。
    /// </summary>
    public void ApplyArgs(string templateId, StructuredArgs args, CliArgsCatalog catalog)
    {
        var tpl = Get(templateId) ?? throw new ArgumentException($"未知模板 {templateId}", nameof(templateId));
        foreach (var a in tpl.Args)
        {
            catalog.EnforceMutex(args, a.Name);
            args.Enable(a.Name, true);
            if (!string.IsNullOrEmpty(a.Value)) args.SetValue(a.Name, a.Value!);
        }
    }

    private static string Expand(string value, string comfyUiDir, string venvDir) =>
        value.Replace("{comfyUiDir}", comfyUiDir, StringComparison.Ordinal)
             .Replace("{venvDir}", venvDir, StringComparison.Ordinal);
}
