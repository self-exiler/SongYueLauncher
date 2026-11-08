using System.Text.Json;

namespace AiDiagnostics.Parsing;

/// <summary>解析 <c>opencode api model.list</c> 的响应（<c>{ location, data:[model...] }</c>）。</summary>
internal static class ModelListParser
{
    /// <summary>
    /// 读到 <c>data</c> 数组并过滤 <c>enabled=false</c> / <c>status=inactive</c> 的条目；
    /// 输出 <c>provider/model</c> 复合 id 作为 <c>AiModel.ModelId</c>（<c>--model</c> 原样取值）。
    /// </summary>
    public static IReadOnlyList<AiModel> Parse(string json)
    {
        var result = new List<AiModel>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var m in data.EnumerateArray())
        {
            var provider = ReadString(m, "providerID") ?? ReadString(m, "provider");
            var id = ReadString(m, "id") ?? ReadString(m, "modelID");
            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(id)) continue;

            if (ReadBool(m, "enabled") == false) continue;
            if (string.Equals(ReadString(m, "status"), "inactive", StringComparison.OrdinalIgnoreCase)) continue;

            var qualified = $"{provider}/{id}";
            var name = ReadString(m, "name");
            result.Add(new AiModel(qualified, string.IsNullOrEmpty(name) ? qualified : $"{name} ({qualified})"));
        }
        return result;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;
}
