using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 外部可执行依赖发现（SRS FR-1.1 / FR-8.2）：PATH + 已知安装路径（winget 装完后当前进程 PATH 不刷新，
/// 仅靠 PATH 会误报未安装，故额外查已知路径）。
/// </summary>
public static class ToolLocator
{
    /// <summary>
    /// 解析 uv.exe 供命令执行使用：自动发现优先，其次配置里的显式路径，都找不到则抛错。
    /// 取代各处散落的 `FindUv() ?? config.UvExe ?? "uv"`——裸 "uv" 回退会静默调到 PATH 上任意版本。
    /// </summary>
    public static string ResolveUv(AppConfig config) =>
        FindUv() ?? config.UvExe
        ?? throw new InvalidOperationException("未找到 uv.exe：请先在「环境与更新 → 工具链」安装 uv，或在配置中指定路径。");

    /// <summary>解析 git.exe 供命令执行使用：自动发现优先，其次配置里的显式路径，都找不到则抛错。</summary>
    public static string ResolveGit(AppConfig config) =>
        FindGit() ?? config.GitExe
        ?? throw new InvalidOperationException("未找到 git.exe：请先在「环境与更新 → 工具链」安装 Git，或在配置中指定路径。");

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

    /// <summary>wt.exe（Windows Terminal）：PATH → WindowsApps 别名目录。</summary>
    public static string? FindWindowsTerminal()
    {
        var found = FindInPath("wt.exe");
        if (found is not null) return found;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return FindFirst(Path.Combine(local, "Microsoft", "WindowsApps", "wt.exe"));
    }

    /// <summary>
    /// opencode CLI 的真实可执行文件。实现已下沉到 <see cref="AiDiagnostics.Runtime.OpenCodeLocator"/>，
    /// 由 AI 诊断库自行负责定位，宿主仅在提示「未安装」时用到。
    /// </summary>
    public static string? FindOpenCode() => AiDiagnostics.Runtime.OpenCodeLocator.Find();
}
