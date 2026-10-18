using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 显卡名称 → ROCm arch 内置映射（SRS FR-8.2）：不依赖项目文件或 venv，仅据 WMI 显卡名判断。
/// </summary>
public sealed class GpuArchService
{
    // 静态字段初始化由运行时保证线程安全（等价于此前的 Lazy，少一层包装）。
    private static readonly GpuArchService Shared = new(EmbeddedJson.Load<GpuArchMapFile>(
        typeof(GpuArchService).Assembly, "Resources.gpu-arch-map.json", "gpu-arch-map.json"));

    private GpuArchService(GpuArchMapFile file)
    {
        Rules = file.Rules;
        Legacy = new HashSet<string>(file.Legacy, StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlyList<GpuArchRule> Rules { get; }

    private HashSet<string> Legacy { get; }

    /// <summary>进程内单例。</summary>
    public static GpuArchService Instance => Shared;

    /// <summary>按显卡名称匹配 arch；无匹配返回 Known=false。</summary>
    public ArchInfo Map(string? gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName))
            return new ArchInfo(null, false, false, null);

        var rule = Rules.FirstOrDefault(r =>
            gpuName.Contains(r.Match, StringComparison.OrdinalIgnoreCase));

        if (rule is null)
            return new ArchInfo(null, false, false, null);

        return new ArchInfo(rule.Arch, Legacy.Contains(rule.Arch), rule.ArchAvailable, rule.Template);
    }
}
