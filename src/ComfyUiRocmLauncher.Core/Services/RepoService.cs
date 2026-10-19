using AiDiagnostics.Runtime;
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

    /// <summary>版本号短形态（前 10 位）供界面展示；无版本返回 "unknown"（FR-1.3）。</summary>
    public static string ShortHash(string? hash) =>
        string.IsNullOrEmpty(hash) ? "unknown" : hash.Length <= 10 ? hash : hash[..10];

    /// <summary>
    /// 读取 <c>comfyui_version.py</c> 里的 <c>__version__</c>（FR-1.3 兜底）：
    /// 手动解压的整合包既无 <c>.last_update_hash</c> 也可能无 <c>.git</c>，但版本文件总在仓库里。
    /// </summary>
    public static string? ReadComfyVersion(string comfyUiDir)
    {
        if (string.IsNullOrWhiteSpace(comfyUiDir)) return null;
        try
        {
            var file = Path.Combine(comfyUiDir, "comfyui_version.py");
            if (!File.Exists(file)) return null;
            foreach (var line in File.ReadLines(file))
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    line, """__version__\s*=\s*["']([^"']+)["']""");
                if (match.Success) return match.Groups[1].Value;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// git 检出但缺 <c>.last_update_hash</c> 时补查本地 HEAD 并回填版本文件（自愈）；
    /// 返回短版本号，非 git 检出或查询失败返回 null。
    /// </summary>
    public static async Task<string?> DetectGitHeadAsync(AppConfig config)
    {
        if (!IsGitCheckout(config.ComfyUiDir)) return null;

        var git = ToolLocator.ResolveGit(config);
        var lines = new List<string>();
        var code = await ProcessService.RunCommandAsync(
            git, "rev-parse HEAD", config.ComfyUiDir, lines.Add).ConfigureAwait(false);
        if (code != 0) return null;

        var hash = lines.FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(hash)) return null;

        try { File.WriteAllText(HashFilePath(config.ComfyUiDir), hash); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return ShortHash(hash);
    }

    /// <summary>
    /// git ls-remote 取远端 HEAD hash（走镜像）。
    /// **失败返回 null，不要返回空串**——空串会被界面当成「远端版本为空」，进而误判成「有更新」。
    /// </summary>
    public static async Task<string?> QueryRemoteHashAsync(TaskRunner runner, AppConfig config, Action<string>? onOutput = null)
    {
        var git = ToolLocator.ResolveGit(config);
        var repo = config.EnvUpdate.RepoUrl;
        if (string.IsNullOrWhiteSpace(repo)) return null;

        var lines = new List<string>();
        var code = await runner.Run(git, $"ls-remote {CommandLine.Quote(MirrorPolicy.Github(repo, config.EnvUpdate.GithubMirror))} HEAD",
            PathHelpers.ExistingDirOrCurrent(config.ComfyUiDir), line => lines.Add(line)).ConfigureAwait(false);
        if (code != 0)
        {
            onOutput?.Invoke($"      远端版本查询失败（退出码 {code}）——镜像不通 / 仓库私有 / 网络中断");
            return null;
        }

        onOutput?.Invoke($"      远端 {repo}");
        var hash = lines.FirstOrDefault()?.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(hash) ? null : hash;
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
        var code = await runner.Run(git, $"clone --depth 1 {CommandLine.Quote(url)} {CommandLine.Quote(config.ComfyUiDir)}",
            ParentDir(config.ComfyUiDir), onOutput).ConfigureAwait(false);
        if (code != 0)
        {
            onOutput?.Invoke($"[错误] clone 失败（退出码 {code}）");
            return false;
        }
        await WriteLocalHashAsync(runner, config, config.ComfyUiDir, onOutput).ConfigureAwait(false);
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
                await WriteLocalHashAsync(runner, config, config.ComfyUiDir, onOutput).ConfigureAwait(false);
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
        var cloneCode = await runner.Run(git, $"clone --depth 1 {CommandLine.Quote(url)} {CommandLine.Quote(tempDir)}",
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

        // 版本号必须在删除临时 clone 之前取：临时目录里的 HEAD 就是本次覆盖进去的代码。
        // 覆盖成功才取，失败时不该动版本文件。
        var hash = IsRobocopySuccess(code) ? await ReadHeadAsync(runner, config, tempDir).ConfigureAwait(false) : null;

        PathHelpers.TryDeleteDirectory(tempDir);
        if (!IsRobocopySuccess(code))
        {
            onOutput?.Invoke($"      robocopy 失败（退出码 {code}），未写入版本号");
            return false;
        }
        onOutput?.Invoke($"      robocopy 成功（退出码 {code}）");
        WriteHash(config, hash, onOutput);
        onOutput?.Invoke("√ 项目覆盖更新完成（提示：可在轮子页同步项目依赖）");
        return true;
    }

    /// <summary>robocopy 覆盖命令：/E + 排除目录/文件。</summary>
    public static string RobocopyArgs(string source, string target)
    {
        var excludeDirs = "/XD .venv models output input user custom_nodes _update_temp .git";
        var excludeFiles = "/XF comfyui-user.bat .last_update_hash";
        return $"{CommandLine.Quote(source)} {CommandLine.Quote(target)} /E {excludeDirs} {excludeFiles}";
    }

    /// <summary>robocopy 退出码 0–7 成功，≥8 失败。</summary>
    public static bool IsRobocopySuccess(int exitCode) => exitCode < 8;

    /// <summary>读取指定 git 目录的 HEAD（即该目录里实际落盘的代码版本）。失败返回 null。</summary>
    private static async Task<string?> ReadHeadAsync(TaskRunner runner, AppConfig config, string gitDir)
    {
        var git = ToolLocator.ResolveGit(config);
        var lines = new List<string>();
        var code = await runner.Run(git, "rev-parse HEAD", PathHelpers.ExistingDirOrCurrent(gitDir), lines.Add)
            .ConfigureAwait(false);
        var hash = code == 0 ? lines.FirstOrDefault()?.Trim() : null;
        return string.IsNullOrEmpty(hash) ? null : hash;
    }

    /// <summary>
    /// 用本地产物写版本号（F-04）：pull / clone / robocopy 成功后就地取 HEAD，
    /// 不再回头查远端——两步之间远端若有新提交，记录下来的就不是本地实际代码的版本。
    /// 顺带省掉一次经镜像的网络往返。
    /// </summary>
    private static async Task WriteLocalHashAsync(TaskRunner runner, AppConfig config, string gitDir, Action<string>? onOutput)
    {
        var hash = await ReadHeadAsync(runner, config, gitDir).ConfigureAwait(false);
        WriteHash(config, hash, onOutput);
    }

    /// <summary>写入版本文件；失败只警告，不阻断更新流程。</summary>
    private static void WriteHash(AppConfig config, string? hash, Action<string>? onOutput)
    {
        if (string.IsNullOrEmpty(hash))
        {
            onOutput?.Invoke("      警告：未能确定本地版本，未写入版本号");
            return;
        }
        try { File.WriteAllText(HashFilePath(config.ComfyUiDir), hash); }
        catch (IOException ex) { onOutput?.Invoke($"      警告：写入版本失败：{ex.Message}"); }
        catch (UnauthorizedAccessException ex) { onOutput?.Invoke($"      警告：写入版本失败：{ex.Message}"); }
    }

    private static string ParentDir(string dir)
    {
        var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Directory.Exists(parent) ? parent! : Environment.CurrentDirectory;
    }

}
