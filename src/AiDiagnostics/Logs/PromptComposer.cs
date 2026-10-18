using System.Text.RegularExpressions;

namespace AiDiagnostics.Logs;

/// <summary>极简提示词模板：把 <c>{name}</c> 占位符替换为宿主提供的值。</summary>
/// <remarks>
/// 只替换「已知键」的占位符，未知占位符与其它花括号内容原样保留，
/// 因此模板里可以安全地写代码块、JSON 片段等含花括号的示例。
/// </remarks>
public static partial class PromptComposer
{
    public static string Render(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(template, match =>
            values.TryGetValue(match.Groups["name"].Value, out var value) ? value : match.Value);

    [GeneratedRegex(@"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex Placeholder();
}
