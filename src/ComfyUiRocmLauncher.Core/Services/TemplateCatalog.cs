using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// GPU 模板（SRS FR-7，界面位于主页启动控制区）：内置模板（附录 B）+ 用户自定义模板的统一解析、应用（全量替换）与捕获。
/// </summary>
public sealed class TemplateCatalog
{
    // 静态字段初始化由运行时保证线程安全（等价于此前的 Lazy，少一层包装）。
    private static readonly TemplateCatalog Shared = new(EmbeddedJson.Load<GpuTemplateFile>(
        typeof(TemplateCatalog).Assembly, "Resources.gpu-templates.json", "gpu-templates.json"));

    private TemplateCatalog(GpuTemplateFile file)
    {
        Templates = file.Templates;
        CommonEnvVars = file.CommonEnvVars;
    }

    /// <summary>内置 GPU 模板（只读）。</summary>
    public IReadOnlyList<GpuTemplate> BuiltinTemplates => Templates;

    private IReadOnlyList<GpuTemplate> Templates { get; }

    private IReadOnlyDictionary<string, string> CommonEnvVars { get; }

    /// <summary>进程内单例。</summary>
    public static TemplateCatalog Instance => Shared;

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
    public void ApplyArgs(string templateId, StructuredArgs args)
    {
        var tpl = Get(templateId) ?? throw new ArgumentException($"未知模板 {templateId}", nameof(templateId));
        var catalog = CliArgsCatalog.Instance;
        foreach (var a in tpl.Args)
        {
            catalog.EnforceMutex(args, a.Name);
            args.Enable(a.Name, true);
            if (!string.IsNullOrEmpty(a.Value)) args.SetValue(a.Name, a.Value!);
        }
    }

    /// <summary>内置 + 自定义模板的合并列表（供主页启动控制区下拉）。</summary>
    public IReadOnlyList<GpuTemplate> AllTemplates(AppConfig config) =>
        BuiltinTemplates.Concat(config.CustomTemplates).ToList();

    /// <summary>展开模板环境变量：内置走公共层组合，自定义取快照并替换占位符。</summary>
    public Dictionary<string, string> ResolveEnv(GpuTemplate tpl, AppConfig config)
    {
        var comfyDir = config.ComfyUiDir;
        var venvDir = LaunchEnvBuilder.VenvDirFrom(config.PythonExe);
        if (tpl.IsCustom)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in tpl.EnvSnapshot!)
                result[kv.Key] = Expand(kv.Value, comfyDir, venvDir);
            return result;
        }
        return ExpandEnvVars(tpl.Id, comfyDir, venvDir);
    }

    /// <summary>
    /// 全量替换语义（FR-7.2）：把模板的环境变量与启动参数整体写入 config，
    /// 覆盖并清除既有自定义项（模板应用后仍可在各页自由编辑）。
    /// </summary>
    public void ApplyTo(GpuTemplate tpl, AppConfig config)
    {
        var env = ResolveEnv(tpl, config);
        config.CustomEnvVars.Clear();
        foreach (var kv in env) config.CustomEnvVars[kv.Key] = kv.Value;

        if (tpl.ArgsSnapshot is { } snapshot)
        {
            config.Args.CopyFrom(snapshot);
        }
        else
        {
            config.Args.Clear();
            ApplyArgs(tpl.Id, config.Args);
        }
    }

    /// <summary>把当前配置捕获为自定义模板（FR-7.3）：环境变量反写占位符 + 参数深拷贝。</summary>
    public GpuTemplate CaptureAsTemplate(AppConfig config, string title, string id) => new()
    {
        Id = id,
        Title = title,
        EnvSnapshot = CaptureEnvSnapshot(config),
        ArgsSnapshot = config.Args.Clone(),
    };

    /// <summary>把 CustomEnvVars 中的 ComfyUI / venv 绝对路径反写为 {comfyUiDir}/{venvDir} 占位符。</summary>
    public Dictionary<string, string> CaptureEnvSnapshot(AppConfig config)
    {
        var comfyDir = config.ComfyUiDir;
        var venvDir = LaunchEnvBuilder.VenvDirFrom(config.PythonExe);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in config.CustomEnvVars)
        {
            var value = kv.Value;
            // venvDir 通常以 comfyDir 为前缀，先替换较长的 venvDir
            if (!string.IsNullOrEmpty(venvDir)) value = value.Replace(venvDir, "{venvDir}", StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(comfyDir)) value = value.Replace(comfyDir, "{comfyUiDir}", StringComparison.Ordinal);
            result[kv.Key] = value;
        }
        return result;
    }

    /// <summary>由标题生成模板 id，重名追加 _2 / _3 …</summary>
    public static string CaptureId(string title, AppConfig config)
    {
        var slug = new string(title.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        if (slug.Length == 0) slug = "custom";
        var id = slug;
        for (var i = 2; config.CustomTemplates.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)); i++)
            id = slug + "_" + i;
        return id;
    }

    private static string Expand(string value, string comfyUiDir, string venvDir) =>
        value.Replace("{comfyUiDir}", comfyUiDir, StringComparison.Ordinal)
             .Replace("{venvDir}", venvDir, StringComparison.Ordinal);
}
