using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 专用轮子内置清单（SRS FR-8.6）：加载 Core 内嵌 wheel-catalog.json，供轮子标签页展示与安装解析。
/// </summary>
public sealed class WheelCatalog
{
    // 静态字段初始化由运行时保证线程安全（等价于此前的 Lazy，少一层包装）。
    private static readonly WheelCatalog Shared = new(EmbeddedJson.Load<WheelCatalogFile>(
        typeof(WheelCatalog).Assembly, "Resources.wheel-catalog.json", "wheel-catalog.json"));

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

    /// <summary>进程内单例。</summary>
    public static WheelCatalog Instance => Shared;
}
