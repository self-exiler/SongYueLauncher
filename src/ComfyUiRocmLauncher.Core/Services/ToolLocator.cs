namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 外部可执行依赖发现（SRS FR-1.1 / FR-8.2）：PATH + 已知安装路径（winget 装完后当前进程 PATH 不刷新，
/// 仅靠 PATH 会误报未安装，故额外查已知路径）。
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

    private static string? FindFirst(params string[] candidates) => candidates.FirstOrDefault(File.Exists);

    /// <summary>uv.exe：PATH → ~/.local/bin（uv 默认用户级安装位置）等。</summary>
    public static string? FindUv()
    {
        var found = FindInPath("uv.exe");
        if (found is not null) return found;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return FindFirst(
            Path.Combine(profile, ".local", "bin", "uv.exe"),
            Path.Combine(local, "Programs", "uv", "uv.exe"),
            Path.Combine(local, "Microsoft", "WinGet", "Links", "uv.exe"));
    }

    /// <summary>git.exe：PATH → Program Files\Git\cmd 等。</summary>
    public static string? FindGit()
    {
        var found = FindInPath("git.exe");
        if (found is not null) return found;

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return FindFirst(
            Path.Combine(pf, "Git", "cmd", "git.exe"),
            Path.Combine(pf86, "Git", "cmd", "git.exe"),
            Path.Combine(local, "Programs", "Git", "cmd", "git.exe"));
    }

    /// <summary>winget.exe：PATH → WindowsApps 别名目录。</summary>
    public static string? FindWinget()
    {
        var found = FindInPath("winget.exe");
        if (found is not null) return found;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return FindFirst(Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe"));
    }
}
