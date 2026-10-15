namespace ComfyUiRocmLauncher.Core.Models;

/// <summary>
/// 结构化启动参数的持久化形态（SRS 5.1 args.structured）。
/// 仅持久化"非默认"的参数：Enabled 为启用的参数名，Values / Multi / Lines 保存各自取值。
/// 独立成文件，避免 AppConfig 与 GpuTemplate（快照字段）形成文件级循环引用。
/// </summary>
public sealed class StructuredArgs
{
    /// <summary>已启用的参数名集合（-- 后不带前缀的形式）。</summary>
    public HashSet<string> Enabled { get; set; } = new(StringComparer.Ordinal);

    /// <summary>单值参数取值：int / float / string / enum / optionalString / optionalInt / verbose 级别。</summary>
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);

    /// <summary>多值参数取值：multiEnum / floatList / stringList。</summary>
    public Dictionary<string, List<string>> Multi { get; set; } = new(StringComparer.Ordinal);

    /// <summary>逐行参数取值：repeatableStringList / repeatableKeyValue，每行为一次独立传参。</summary>
    public Dictionary<string, List<string>> Lines { get; set; } = new(StringComparer.Ordinal);

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
