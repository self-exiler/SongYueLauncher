using System.Text.RegularExpressions;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 项目依赖清单过滤（SRS FR-8.6，适用于 requirements / manager_requirements / agent_requirements）：
/// 剔除 torch / torchvision / torchaudio 行（由 FR-8.5 单独管理）。
/// </summary>
public static class RequirementsFilter
{
    private static readonly Regex TorchLine = new(
        @"^\s*(torch|torchvision|torchaudio)\s*([=<>!~]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>过滤掉 torch 系统行；其余（含空行/注释）原样保留。</summary>
    public static IEnumerable<string> Filter(IEnumerable<string> lines) =>
        lines.Where(line => !TorchLine.IsMatch(line));

    /// <summary>读取 requirements 文件、过滤、写同名派生的过滤件（requirements.txt → requirements_filtered.txt），返回其路径。</summary>
    public static string WriteFilteredFile(string requirementsPath, string outputDir)
    {
        var lines = File.ReadAllLines(requirementsPath);
        var filtered = Filter(lines).ToArray();
        var stem = Path.GetFileNameWithoutExtension(requirementsPath);
        var target = Path.Combine(outputDir, stem + "_filtered.txt");
        File.WriteAllLines(target, filtered);
        return target;
    }
}
