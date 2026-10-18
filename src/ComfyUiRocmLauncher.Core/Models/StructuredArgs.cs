using System.Text.Json.Serialization;

namespace ComfyUiRocmLauncher.Core.Models;

/// <summary>
/// 结构化启动参数的持久化形态（SRS 5.1 args.structured）。
/// 仅持久化"非默认"的参数：Enabled 为启用的参数名，Values / Multi / Lines 保存各自取值。
/// 独立成文件，避免 AppConfig 与 GpuTemplate（快照字段）形成文件级循环引用。
/// </summary>
/// <remarks>
/// 四个集合的 setter 收窄为 private（仅供 <see cref="System.Text.Json"/> 反序列化使用），
/// 外部写路径统一走 <see cref="Clear"/> / <see cref="CopyFrom"/> / <see cref="Clone"/> 与
/// <c>Enable</c> / <c>SetValue</c> / <c>SetMulti</c> / <c>SetLines</c>。
/// 这样将来加"空值清理""变更通知""脏标记"只需改这一处。
/// </remarks>
public sealed class StructuredArgs
{
    /// <summary>已启用的参数名集合（-- 后不带前缀的形式）。</summary>
    [JsonInclude]
    public HashSet<string> Enabled { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>单值参数取值：int / float / string / enum / optionalString / optionalInt / verbose 级别。</summary>
    [JsonInclude]
    public Dictionary<string, string> Values { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>多值参数取值：multiEnum / floatList / stringList。</summary>
    [JsonInclude]
    public Dictionary<string, List<string>> Multi { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>逐行参数取值：repeatableStringList / repeatableKeyValue，每行为一次独立传参。</summary>
    [JsonInclude]
    public Dictionary<string, List<string>> Lines { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>清空全部内容（保留集合实例，避免外部持有的引用失效）。</summary>
    public void Clear()
    {
        Enabled.Clear();
        Values.Clear();
        Multi.Clear();
        Lines.Clear();
    }

    /// <summary>整体覆盖为 <paramref name="source"/> 的内容（深拷贝四个集合，保持实例不变）。</summary>
    public void CopyFrom(StructuredArgs source)
    {
        Clear();
        foreach (var name in source.Enabled) Enabled.Add(name);
        foreach (var kv in source.Values) Values[kv.Key] = kv.Value;
        foreach (var kv in source.Multi) Multi[kv.Key] = new List<string>(kv.Value);
        foreach (var kv in source.Lines) Lines[kv.Key] = new List<string>(kv.Value);
    }

    /// <summary>深拷贝一份副本。</summary>
    public StructuredArgs Clone()
    {
        var copy = new StructuredArgs();
        copy.CopyFrom(this);
        return copy;
    }

    public bool IsEnabled(string name) => Enabled.Contains(name);

    public void Enable(string name, bool enabled)
    {
        if (enabled) Enabled.Add(name);
        else Enabled.Remove(name);
    }

    public string Value(string name) => Values.TryGetValue(name, out var v) ? v : string.Empty;

    public void SetValue(string name, string value)
    {
        if (string.IsNullOrEmpty(value)) Values.Remove(name);
        else Values[name] = value;
    }

    public List<string> MultiOf(string name) =>
        Multi.TryGetValue(name, out var v) ? v : new List<string>();

    /// <summary>写入多值参数取值；空列表表示清除该参数取值。</summary>
    public void SetMulti(string name, IEnumerable<string> values)
    {
        var list = values.ToList();
        if (list.Count == 0) Multi.Remove(name);
        else Multi[name] = list;
    }

    public List<string> LinesOf(string name) =>
        Lines.TryGetValue(name, out var v) ? v : new List<string>();

    /// <summary>写入逐行参数取值；空列表表示清除该参数取值。</summary>
    public void SetLines(string name, IEnumerable<string> values)
    {
        var list = values.ToList();
        if (list.Count == 0) Lines.Remove(name);
        else Lines[name] = list;
    }
}
