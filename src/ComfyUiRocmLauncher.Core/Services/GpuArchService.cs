using System.Text.Json;
using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 显卡名称 → ROCm arch 内置映射（SRS FR-8.2）：不依赖项目文件或 venv，仅据 WMI 显卡名判断。
/// </summary>
public sealed class GpuArchService
{
    private const string ResourceSuffix = "Resources.gpu-arch-map.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static GpuArchService? _instance;

    private GpuArchService(GpuArchMapFile file)
    {
        Rules = file.Rules;
        Legacy = new HashSet<string>(file.Legacy, StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlyList<GpuArchRule> Rules { get; }

    private HashSet<string> Legacy { get; }

    public static GpuArchService Instance => _instance ??= Load();

    private static GpuArchService Load()
    {
        var asm = typeof(GpuArchService).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"未找到内嵌资源 {ResourceSuffix}");
        var file = JsonSerializer.Deserialize<GpuArchMapFile>(stream, JsonOptions)
            ?? throw new InvalidOperationException("gpu-arch-map.json 反序列化失败");
        return new GpuArchService(file);
    }

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
