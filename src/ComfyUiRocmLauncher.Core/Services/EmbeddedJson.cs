using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 内嵌 JSON 清单的统一加载：按资源名后缀定位 → 反序列化 → 失败时给出明确异常。
/// 供启动参数清单 / GPU 模板 / 轮子清单 / GPU 架构映射四个内置清单共用，
/// 取代各清单各自实现的 ResourceSuffix + JsonOptions + GetManifestResourceNames 重复逻辑。
/// </summary>
internal static class EmbeddedJson
{
    /// <summary>四个清单共用的反序列化设置（大小写不敏感、容忍注释与尾逗号、宽松字符串）。</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(), new FlexibleStringConverter() },
    };

    /// <summary>定位并反序列化内嵌资源；<paramref name="displayName"/> 仅用于异常文案。</summary>
    public static T Load<T>(Assembly assembly, string resourceSuffix, string displayName)
    {
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"未找到内嵌资源 {resourceSuffix}");
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"未找到内嵌资源 {resourceSuffix}");
        return JsonSerializer.Deserialize<T>(stream, Options)
            ?? throw new InvalidOperationException($"{displayName} 反序列化失败");
    }
}
