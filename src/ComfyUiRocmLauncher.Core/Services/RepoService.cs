using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 核心仓库安装与更新（SRS FR-8.4）：仓库地址由用户指定；clone 走 GitHub 镜像。
/// 更新按目录形态双策略：git 检出 → git pull --ff-only；非检出 → shallow clone + robocopy 覆盖。
/// </summary>
public static class RepoService
{
    public static string HashFilePath(string comfyUiDir) => Path.Combine(comfyUiDir, ".last_update_hash");

    public static bool IsInstalled(string comfyUiDir) =>
        !string.IsNullOrWhiteSpace(comfyUiDir) && File.Exists(Path.Combine(comfyUiDir, "main.py"));

    public static bool IsGitCheckout(string comfyUiDir) =>
        !string.IsNullOrWhiteSpace(comfyUiDir) && Directory.Exists(Path.Combine(comfyUiDir, ".git"));

    public static string ReadLocalHash(string comfyUiDir)
    {
        try
        {
            var f = HashFilePath(comfyUiDir);
            return File.Exists(f) ? File.ReadAllText(f).Trim() : string.Empty;
        }
        catch (IOException) { return string.Empty; }
    }

    /// <summary>版本号短形态（前 10 位）供界面展示；无版本文件返回 "unknown"（FR-1.3）。</summary>
    public static string ShortHash(string hash) =>
        string.IsNullOrEmpty(hash) ? "unknown" : hash.Length <= 10 ? hash : hash[..10];

    /// <summary>git ls-remote 取远端 HEAD hash（走镜像）。</summary>
    public static async Task<string> QueryRemoteHashAsync(TaskRunner runner, AppConfig config, Action<string>? onOutput = null)
    {
        var git = ToolLocator.ResolveGit(config);
        var repo = config.EnvUpdate.RepoUrl;
        if (string.IsNullOrWhiteSpace(repo)) return string.Empty;

        var lines = new List<string>();
        await runner.Run(git, $"ls-remote {MirrorPolicy.Github(repo, config.EnvUpdate.GithubMirror)} HEAD",
            PathHelpers.ExistingDirOrCurrent(config.ComfyUiDir), line => lines.Add(line)).ConfigureAwait(false);

        onOutput?.Invoke($"      远端 {repo}");
        return lines.FirstOrDefault()?.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
    }

    /// <summary>未安装：shallow clone 到 ComfyUI 目录。</summary>
    public static async Task<bool> CloneAsync(TaskRunner runner, AppConfig config, Action<string>? onOutput = null)
    {
        var git = ToolLocator.ResolveGit(config);
        var repo = config.EnvUpdate.RepoUrl;
        if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(config.ComfyUiDir))
        {
            onOutput?.Invoke("[错误] 需先设置仓库地址与安装目录");
            return false;
        }

        var url = MirrorPolicy.Github(repo, config.EnvUpdate.GithubMirror);
        onOutput?.Invoke($"[1/2] git clone --depth 1 …");
        var code = await runner.Run(git, $"clone --depth 1 {url} \"{config.ComfyUiDir}\"",
            ParentDir(config.ComfyUiDir), onOutput).ConfigureAwait(false);
        if (code != 0)
        {
            onOutput?.Invoke($"[错误] clone 失败（退出码 {code}）");
            return false;
        }
        await WriteHashFromRemoteAsync(runner, config, onOutput).ConfigureAwait(false);
        onOutput?.Invoke("√ 仓库安装完成");
        return true;
    }

    /// <summary>已安装：git 检出走 pull，否则 robocopy 覆盖。</summary>
    public static async Task<bool> UpdateAsync(TaskRunner runner, AppConfig config, bool forceOverwrite, Action<string>? onOutput = null)
    {
        var git = ToolLocator.ResolveGit(config);
        if (string.IsNullOrWhiteSpace(config.ComfyUiDir))
        {
            onOutput?.Invoke("[错误] 需先安装 ComfyUI 项目");
            return false;
        }

        if (!forceOverwrite && IsGitCheckout(config.ComfyUiDir))
        {
            onOutput?.Invoke("[策略] git 检出 → git pull --ff-only");
            var code = await runner.Run(git, "pull --ff-only", config.ComfyUiDir, onOutput).ConfigureAwait(false);
            if (code == 0)
            {
                await WriteHashFromRemoteAsync(runner, config, onOutput).ConfigureAwait(false);
                return true;
            }
            onOutput?.Invoke($"      pull 失败（退出码 {code}），可尝试「强制覆盖更新」");
            return false;
        }

        return await RobocopyUpdateAsync(runner, config, onOutput).ConfigureAwait(false);
    }

    private static async Task<bool> RobocopyUpdateAsync(TaskRunner runner, AppConfig config, Action<string>? onOutput)
    {
        var git = ToolLocator.ResolveGit(config);
        var repo = config.EnvUpdate.RepoUrl;
        var tempDir = Path.Combine(config.ComfyUiDir, "_update_temp");
        PathHelpers.TryDeleteDirectory(tempDir);

        onOutput?.Invoke("[1/3] shallow clone 到临时目录 …");
        var url = MirrorPolicy.Github(repo, config.EnvUpdate.GithubMirror);
        var cloneCode = await runner.Run(git, $"clone --depth 1 {url} \"{tempDir}\"",
            config.ComfyUiDir, onOutput).ConfigureAwait(false);
        if (cloneCode != 0)
        {
            onOutput?.Invoke($"[错误] clone 失败（退出码 {cloneCode}），中止覆盖");
            PathHelpers.TryDeleteDirectory(tempDir);
            return false;
        }

        onOutput?.Invoke("[2/3] robocopy 覆盖 …");
        var code = await runner.Run("robocopy", RobocopyArgs(tempDir, config.ComfyUiDir), config.ComfyUiDir, onOutput)
            .ConfigureAwait(false);

        PathHelpers.TryDeleteDirectory(tempDir);
        if (!IsRobocopySuccess(code))
        {
            onOutput?.Invoke($"      robocopy 失败（退出码 {code}），未写入版本号");
            return false;
        }
        onOutput?.Invoke($"      robocopy 成功（退出码 {code}）");
        await WriteHashFromRemoteAsync(runner, config, onOutput).ConfigureAwait(false);
        onOutput?.Invoke("√ 项目覆盖更新完成（提示：可在轮子页同步项目依赖）");
        return true;
    }

    /// <summary>robocopy 覆盖命令：/E + 排除目录/文件。</summary>
    public static string RobocopyArgs(string source, string target)
    {
        var excludeDirs = "/XD .venv models output input user custom_nodes _update_temp .git";
        var excludeFiles = "/XF comfyui-user.bat .last_update_hash";
        return $"\"{source}\" \"{target}\" /E {excludeDirs} {excludeFiles}";
    }

    /// <summary>robocopy 退出码 0–7 成功，≥8 失败。</summary>
    public static bool IsRobocopySuccess(int exitCode) => exitCode < 8;

    private static async Task WriteHashFromRemoteAsync(TaskRunner runner, AppConfig config, Action<string>? onOutput)
    {
        var remote = await QueryRemoteHashAsync(runner, config, null).ConfigureAwait(false);
        if (string.IsNullOrEmpty(remote)) { onOutput?.Invoke("      警告：未能获取远端 hash，未写入版本"); return; }
        try { File.WriteAllText(HashFilePath(config.ComfyUiDir), remote); }
        catch (IOException ex) { onOutput?.Invoke($"      警告：写入版本失败：{ex.Message}"); }
    }

    private static string ParentDir(string dir)
    {
        var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Directory.Exists(parent) ? parent! : Environment.CurrentDirectory;
    }

}
