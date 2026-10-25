using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 专用轮子内置清单（SRS FR-8.6）：供轮子标签页展示与安装解析。
/// 加载顺序：优先 exe 同目录的外部 wheel-catalog.json（换文件即可更新清单，无需重新发版），
/// 缺失或解析失败时回落到编译期内嵌资源。两者都失败才抛异常。
/// </summary>
public sealed class WheelCatalog
{
    /// <summary>外部清单文件名（与 exe 同目录；随 csproj Content 复制到输出与发布目录）。</summary>
    public const string ExternalFileName = "wheel-catalog.json";

    // 静态字段初始化由运行时保证线程安全（等价于此前的 Lazy，少一层包装）。
    private static readonly WheelCatalog Shared = Load();

    private WheelCatalog(WheelCatalogFile file, bool fromExternal, string? externalError)
    {
        Wheels = file.Wheels;
        Updated = file.Updated;
        Version = file.Version;
        FromExternalFile = fromExternal;
        ExternalError = externalError;
    }

    public IReadOnlyList<WheelCatalogEntry> Wheels { get; }

    /// <summary>清单版本标识（内嵌清单随软件发版更新，FR-8.6；外部文件替换时以此比对新旧）。</summary>
    public string Updated { get; }

    public int Version { get; }

    /// <summary>当前清单是否来自外部文件（false = 内嵌资源）。</summary>
    public bool FromExternalFile { get; }

    /// <summary>外部文件存在但解析失败时的原因（供界面提示）；未放置外部文件时为 null。</summary>
    public string? ExternalError { get; }

    /// <summary>进程内单例。</summary>
    public static WheelCatalog Instance => Shared;

    private static WheelCatalog Load()
    {
        var externalPath = Path.Combine(AppContext.BaseDirectory, ExternalFileName);
        string? externalError = null;
        if (File.Exists(externalPath))
        {
            try
            {
                using var stream = File.OpenRead(externalPath);
                var file = System.Text.Json.JsonSerializer.Deserialize<WheelCatalogFile>(
                    stream, ExternalJsonOptions);
                if (file?.Wheels is { Count: > 0 })
                    return new WheelCatalog(file, fromExternal: true, externalError: null);
                externalError = "清单为空或缺少 wheels";
            }
            catch (Exception ex)
            {
                externalError = ex.Message; // 解析失败回落内嵌，但把原因带出去供界面提示
            }
        }

        return new WheelCatalog(
            EmbeddedJson.Load<WheelCatalogFile>(typeof(WheelCatalog).Assembly, "Resources.wheel-catalog.json", "wheel-catalog.json"),
            fromExternal: false, externalError);
    }

    /// <summary>外部清单复用内嵌清单的容错反序列化设置（大小写不敏感、容忍注释与尾逗号）。</summary>
    internal static readonly System.Text.Json.JsonSerializerOptions ExternalJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
