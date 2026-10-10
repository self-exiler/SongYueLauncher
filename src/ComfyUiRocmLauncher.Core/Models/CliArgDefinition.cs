namespace ComfyUiRocmLauncher.Core.Models;

/// <summary>
/// 参数控件/取值类型，与内置 Resources/cli-args.json 的 type 字段一一对应。
/// </summary>
public enum ArgType
{
    Flag,
    Int,
    Float,
    String,
    OptionalString,
    OptionalInt,
    Enum,
    MultiEnum,
    FloatList,
    StringList,
    RepeatableStringList,
    RepeatableVerbose,
    RepeatableKeyValue,
}

public sealed class CliArgDefinition
{
    public string Name { get; set; } = string.Empty;
    public ArgType Type { get; set; } = ArgType.Flag;
    public string Description { get; set; } = string.Empty;
    public string? Default { get; set; }
    public string? Const { get; set; }
    public List<string>? Choices { get; set; }
    public int? Mutex { get; set; }
    public bool Bat { get; set; }
    public string? BatValue { get; set; }

    /// <summary>是否需要额外取值输入（flag 型只有开关）。</summary>
    public bool HasValue => Type != ArgType.Flag;

    /// <summary>取值以多行文本录入。</summary>
    public bool IsMultiLine => Type is ArgType.RepeatableStringList or ArgType.RepeatableKeyValue;
}

public sealed class CliArgGroup
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<CliArgDefinition> Args { get; set; } = new();
}

public sealed class CliArgsFile
{
    public int Version { get; set; }
    public string? Source { get; set; }
    public List<CliArgGroup> Groups { get; set; } = new();
}
