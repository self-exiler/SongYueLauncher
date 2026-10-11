using System.Reflection;
using System.Text.Json;
using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 专用轮子内置清单（SRS FR-8.6）：加载 Core 内嵌 wheel-catalog.json，供轮子标签页展示与安装解析。
/// </summary>
public sealed class WheelCatalog
{
    private const string ResourceSuffix = "Resources.wheel-catalog.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static WheelCatalog? _instance;

    private WheelCatalog(WheelCatalogFile file)
    {
        Wheels = file.Wheels;
        Updated = file.Updated;
        Version = file.Version;
    }

    public IReadOnlyList<WheelCatalogEntry> Wheels { get; }

    /// <summary>清单版本标识（随软件发版更新，FR-8.6）。</summary>
    public string Updated { get; }

    public int Version { get; }

    public static WheelCatalog Instance => _instance ??= Load();

    private static WheelCatalog Load()
    {
        var asm = typeof(WheelCatalog).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"未找到内嵌资源 {ResourceSuffix}");
        var file = JsonSerializer.Deserialize<WheelCatalogFile>(stream, JsonOptions)
            ?? throw new InvalidOperationException("wheel-catalog.json 反序列化失败");
        return new WheelCatalog(file);
    }
}
