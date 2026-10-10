using System.Text.RegularExpressions;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// requirements.txt 过滤（FR-6.2）：剔除 torch / torchvision / torchaudio 行。
/// </summary>
public static class RequirementsFilter
{
    private static readonly Regex TorchLineRegex = new(
        @"^\s*(torch|torchvision|torchaudio)\s*([=<>!~]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>返回保留下来的行。</summary>
    public static IReadOnlyList<string> Filter(IEnumerable<string> lines) =>
        lines.Where(l => !TorchLineRegex.IsMatch(l)).ToList();

    /// <summary>读取文件并过滤。</summary>
    private static IReadOnlyList<string> FilterFile(string path) =>
        File.Exists(path) ? Filter(File.ReadAllLines(path)) : Array.Empty<string>();

    /// <summary>把过滤结果写成 &lt;ComfyUI目录&gt;\requirements_filtered.txt，返回路径。</summary>
    public static string WriteFilteredFile(string requirementsPath, string comfyUiDir)
    {
        var filtered = FilterFile(requirementsPath);
        var target = Path.Combine(comfyUiDir, "requirements_filtered.txt");
        File.WriteAllLines(target, filtered);
        return target;
    }
}
