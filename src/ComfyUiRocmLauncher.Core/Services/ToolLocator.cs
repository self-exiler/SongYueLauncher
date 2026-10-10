namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 外部可执行依赖发现（SRS 第 6 章）：uv.exe 用于全部 pip 操作。
/// </summary>
public static class ToolLocator
{
    private static string? FindInPath(string exeName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim('"'), exeName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>PATH → 常见安装位置（uv 默认装在 ~/.local/bin）。</summary>
    public static string? FindUv()
    {
        var found = FindInPath("uv.exe");
        if (found is not null) return found;

        var extra = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "uv.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "uv", "uv.exe"),
        };
        return extra.FirstOrDefault(File.Exists);
    }
}
