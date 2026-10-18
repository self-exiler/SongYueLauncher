namespace AiDiagnostics.Runtime;

/// <summary>
/// 定位本机 opencode CLI 的真实可执行文件。
/// npm 全局 shim（opencode / opencode.cmd）只是转发脚本，CreateProcess 无法直接执行 sh 脚本，
/// 故优先解析 node_modules 下的 opencode.exe。
/// </summary>
public static class OpenCodeLocator
{
    /// <summary>返回 opencode 可执行文件路径；未安装返回 <c>null</c>（不抛异常）。</summary>
    public static string? Find()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var direct = FindFirst(
            Path.Combine(appData, "npm", "node_modules", "@opencode", "cli", "bin", "opencode.exe"),
            Path.Combine(local, "opencode", "bin", "opencode.exe"),
            Path.Combine(profile, ".opencode", "bin", "opencode.exe"));
        if (direct is not null) return direct;

        foreach (var name in new[] { "opencode.exe", "opencode.cmd", "opencode" })
        {
            var found = FindInPath(name);
            if (found is null) continue;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return found;

            // PATH 命中 npm shim：按 shim 约定回到同目录的 node_modules 里找 exe
            var fromShim = Path.Combine(
                Path.GetDirectoryName(found) ?? appData,
                "node_modules", "@opencode", "cli", "bin", "opencode.exe");
            if (File.Exists(fromShim)) return fromShim;
            return found;
        }
        return null;
    }

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
}
