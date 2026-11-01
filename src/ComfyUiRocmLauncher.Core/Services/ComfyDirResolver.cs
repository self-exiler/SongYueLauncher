using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 数据目录生效路径解析（SRS FR-1.4 / FR-1.5）：与上游 folder_paths.py 的取值规则一致——
/// base-directory 重定位 models / custom_nodes / input / output / temp / user 的公共根；
/// 各 *-directory 参数是其对应目录本身（优先级最高，不再拼段）；未启用时一切按 ComfyUI 目录解析。
/// 覆盖值的相对路径按 ComfyUI 目录展开（启动器恒以 ComfyUI 目录为工作目录，与上游 os.path.abspath 同义）。
/// </summary>
public static class ComfyDirResolver
{
    /// <summary>解析 ComfyUI 内相对路径（如 "models"、"user\\workflow"）的生效绝对路径。</summary>
    public static string Resolve(AppConfig config, string relative)
    {
        var parts = relative.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Path.GetFullPath(config.ComfyUiDir);

        var overrideDir = OverrideDir(config, parts[0]);
        if (overrideDir is not null)
            return Path.GetFullPath(parts.Length == 1 ? overrideDir : Path.Combine(overrideDir, Path.Combine(parts[1..])));

        var baseDir = BaseDirectory(config) ?? config.ComfyUiDir;
        return Path.GetFullPath(Path.Combine(baseDir, Path.Combine(parts)));
    }

    /// <summary>base-directory 的生效值（未启用/留空返回 null）；相对值按 ComfyUI 目录展开。</summary>
    public static string? BaseDirectory(AppConfig config)
    {
        var value = EnabledValue(config, "base-directory");
        return value.Length > 0 ? Rooted(config, value) : null;
    }

    /// <summary>首段的单项覆盖目录（models/input/output/temp/user-directory）；未启用返回 null。</summary>
    private static string? OverrideDir(AppConfig config, string topSegment)
    {
        var value = topSegment.ToLowerInvariant() switch
        {
            "models" => EnabledValue(config, "models-directory"),
            "input" => EnabledValue(config, "input-directory"),
            "output" => EnabledValue(config, "output-directory"),
            "temp" => EnabledValue(config, "temp-directory"),
            "user" => EnabledValue(config, "user-directory"),
            _ => string.Empty,
        };
        return value.Length > 0 ? Rooted(config, value) : null;
    }

    private static string Rooted(AppConfig config, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(config.ComfyUiDir, path);

    private static string EnabledValue(AppConfig config, string name) =>
        config.Args is { } args && args.IsEnabled(name)
            ? args.Value(name).Trim().Trim('"')
            : string.Empty;
}
