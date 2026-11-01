using System.Security.Cryptography;
using AiDiagnostics.Runtime;
using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 插件更新检测与执行（SRS FR-10 方案 C）。
/// 检测用 <c>git ls-remote</c>（只问一次远端 ref，不下载对象），并发受限、结果带 TTL 缓存；
/// 更新默认 <c>--ff-only</c>（插件目录被用户改过或分支分叉时直接失败并说明，绝不动手覆盖），
/// 显式选择「强制覆盖」才 fetch + checkout -f 丢弃本地改动。
/// </summary>
public static class PluginUpdateService
{
    /// <summary>检测结果（按插件路径缓存，避免每次进页面都打一遍网络）。</summary>
    public sealed record CheckResult(bool HasUpdate, string? RemoteHash, string? Error, DateTimeOffset CheckedAt);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly Dictionary<string, CheckResult> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>网络类子进程统一带上这些环境变量：禁掉凭据交互弹窗（GUI 里会永久挂住）。</summary>
    private static Dictionary<string, string> GitEnv() => new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "never",
    };

    /// <summary>连接停滞时让 git 自己掐掉：低速率阈值 + 秒数（避免整页检测被一个死地址拖住）。</summary>
    private const string LowSpeedConfig = "-c http.lowSpeedLimit=1000 -c http.lowSpeedTime=15 ";

    /// <summary>解析 <c>git ls-remote</c> 输出里某个 ref 的 hash；没有命中行返回 null。</summary>
    public static string? ParseRemoteHash(string lsRemoteOutput, string refName)
    {
        foreach (var line in lsRemoteOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && string.Equals(parts[1], refName, StringComparison.Ordinal) && parts[0].Length >= 7)
                return parts[0];
        }
        return null;
    }

    /// <summary>
    /// 是否可更新：远端 hash 与本地 HEAD 完整 hash 不同即为可更新。
    /// 本地 hash 允许是截短形式（列表里就是截短显示的），按前缀比对。
    /// </summary>
    public static bool NeedsUpdate(string? localHash, string? remoteHash) =>
        !string.IsNullOrEmpty(localHash) && !string.IsNullOrEmpty(remoteHash)
        && !remoteHash.StartsWith(localHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 剥掉仓库地址上的镜像前缀。gh-proxy 一类镜像只代理对象下载，**不支持 git 协议里的
    /// info/refs**（真机实测：ls-remote 走镜像会一直卡到 lowSpeedTime 超时，直连毫秒级返回），
    /// 因此检测与更新一律回到真实地址；镜像只在克隆时发挥作用。
    /// </summary>
    public static string StripMirror(string url, string mirror)
    {
        if (string.IsNullOrWhiteSpace(mirror) || string.IsNullOrWhiteSpace(url)) return url;
        var prefix = mirror.TrimEnd('/') + "/";
        return url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? url[prefix.Length..] : url;
    }

    /// <summary>清空缓存（更新成功后调用，避免旧结论继续显示「可更新」）。</summary>
    public static void Forget(string pluginPath) => Cache.Remove(pluginPath);

    public static void ForgetAll() => Cache.Clear();

    /// <summary>
    /// 并发检测所有 git 插件是否有更新。返回「插件路径 → 结果」，非 git 插件不在其中。
    /// 命中 TTL 缓存的直接复用；<paramref name="onProgress"/> 每个插件完成时报一次进度。
    /// </summary>
    public static async Task<Dictionary<string, CheckResult>> CheckAllAsync(
        TaskRunner runner, AppConfig config, IReadOnlyCollection<PluginService.PluginInfo> plugins,
        Func<string, CheckResult, Task>? onProgress = null)
    {
        var git = ToolLocator.FindGit();
        var result = new Dictionary<string, CheckResult>(StringComparer.OrdinalIgnoreCase);
        if (git is null) return result;

        var mirror = config.EnvUpdate.GithubMirror;
        var candidates = plugins
            .Where(p => p.IsDirectory && p.HasGit && StripMirror(p.RemoteUrl ?? "", mirror).Length > 0)
            .ToList();
        foreach (var p in candidates)
            if (Cache.TryGetValue(p.Path, out var cached) && DateTime.UtcNow - cached.CheckedAt < CacheTtl)
                result[p.Path] = cached;

        var todo = candidates.Where(p => !result.ContainsKey(p.Path)).ToList();
        if (todo.Count == 0) return result;

        // 4 路并发：再多容易把 GitHub 打到限流（403 secondary rate limit），而 ls-remote 本身是秒级请求
        using var gate = new SemaphoreSlim(4);
        var tasks = todo.Select(async plugin =>
        {
            await gate.WaitAsync(runner.Token).ConfigureAwait(false);
            CheckResult check;
            try
            {
                check = await CheckOneAsync(runner, git, plugin, mirror).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }

            lock (Cache) Cache[plugin.Path] = check;
            lock (result) result[plugin.Path] = check;
            if (onProgress is not null) await onProgress(plugin.Name, check).ConfigureAwait(false);
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result;
    }

    private static async Task<CheckResult> CheckOneAsync(
        TaskRunner runner, string git, PluginService.PluginInfo plugin, string mirror)
    {
        var head = PluginService.ReadGitHead(plugin.Path);
        if (head is null || string.IsNullOrEmpty(head.Value.Branch))
            return new CheckResult(false, null, "无法读取本地分支", DateTimeOffset.UtcNow);

        var branch = head.Value.Branch;
        // detached HEAD 没有可比的远端分支名，跳过（交给强制覆盖/重装路径处理）
        if (branch == "(detached)")
            return new CheckResult(false, null, "游离 HEAD（detached），跳过检测", DateTimeOffset.UtcNow);

        var url = StripMirror(head.Value.RemoteUrl ?? "", mirror);
        var refName = "refs/heads/" + branch;
        var args = LowSpeedConfig + $"ls-remote {CommandLine.Quote(url)} {CommandLine.Quote(refName)}";
        var lines = new List<string>();
        var code = await runner.Run(git, args, PathHelpers.ExistingDirOrCurrent(plugin.Path), lines.Add, GitEnv())
            .ConfigureAwait(false);

        var output = string.Join('\n', lines);
        if (code != 0)
            return new CheckResult(false, null, FirstRealLine(output) ?? $"ls-remote 退出码 {code}", DateTimeOffset.UtcNow);

        var remote = ParseRemoteHash(output, refName);
        if (remote is null)
            return new CheckResult(false, null, $"远端没有分支 {branch}", DateTimeOffset.UtcNow);

        return new CheckResult(NeedsUpdate(head.Value.HeadHash, remote), remote, null, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 更新单个插件。默认快进（<c>pull --ff-only</c>）；<paramref name="force"/> 时改为 fetch + <c>checkout -f</c>，
    /// 这会丢弃插件目录里的本地改动——调用方必须在弹窗里说清楚。
    /// 更新后若 requirements.txt 内容变了且 <paramref name="reinstallDeps"/> 为真，自动重装依赖。
    /// </summary>
    public static async Task<bool> UpdateAsync(
        TaskRunner runner, AppConfig config, PluginService.PluginInfo plugin, bool force,
        bool reinstallDeps, Action<string>? onOutput = null)
    {
        var git = ToolLocator.FindGit();
        if (git is null || !plugin.IsDirectory || !plugin.HasGit)
        {
            onOutput?.Invoke($"[错误] 「{plugin.Name}」不是可更新的 git 插件");
            return false;
        }

        var head = PluginService.ReadGitHead(plugin.Path);
        var branch = head?.Branch;
        if (string.IsNullOrEmpty(branch) || branch == "(detached)")
        {
            onOutput?.Invoke($"[跳过] 「{plugin.Name}」处于游离 HEAD，先切回分支再更新");
            return false;
        }

        var before = RequirementsFingerprint(plugin.Path);
        var from = Short(head!.Value.HeadHash);
        var url = StripMirror(head.Value.RemoteUrl ?? "", config.EnvUpdate.GithubMirror);
        var ok = await FetchAsync(runner, git, plugin, url, branch, onOutput).ConfigureAwait(false)
            && (force
                ? await ResetHardAsync(runner, git, plugin, onOutput).ConfigureAwait(false)
                : await FastForwardAsync(runner, git, plugin, onOutput).ConfigureAwait(false));
        if (!ok) return false;

        Forget(plugin.Path);
        onOutput?.Invoke($"√ 「{plugin.Name}」已更新：{from} → {Short(PluginService.ReadGitHead(plugin.Path)?.HeadHash)}");

        if (reinstallDeps && RequirementsFingerprint(plugin.Path) != before)
        {
            onOutput?.Invoke($"[依赖] requirements.txt 已变化，重装依赖…");
            await PluginInstallService.InstallDepsAsync(runner, config, plugin.Path, onOutput).ConfigureAwait(false);
        }
        else if (reinstallDeps)
        {
            onOutput?.Invoke($"[依赖] requirements.txt 未变化，跳过重装");
        }
        return true;
    }

    /// <summary>
    /// 显式带地址 fetch：不走 origin（origin 可能是克隆时留下的镜像地址，而镜像不支持 git 协议），
    /// 结果落在 FETCH_HEAD，由后续 merge / reset 决定要不要动工作区。
    /// </summary>
    private static async Task<bool> FetchAsync(
        TaskRunner runner, string git, PluginService.PluginInfo plugin, string url, string branch, Action<string>? onOutput)
    {
        if (string.IsNullOrEmpty(url))
        {
            onOutput?.Invoke($"[跳过] 「{plugin.Name}」没有远端地址（本地仓库），无法更新");
            return false;
        }

        // refspec 整体加引号：前导 + 必须和 refspec 同属一个参数，分开引会被 git 拆成两个词
        var refspec = "+refs/heads/" + branch + ":refs/remotes/__launcher/fetch";
        var args = LowSpeedConfig + $"fetch --prune {CommandLine.Quote(url)} {CommandLine.Quote(refspec)}";
        var code = await runner.Run(git, args, PathHelpers.ExistingDirOrCurrent(plugin.Path), onOutput, GitEnv())
            .ConfigureAwait(false);
        if (code != 0) onOutput?.Invoke($"[错误] 「{plugin.Name}」fetch 失败（退出码 {code}）");
        return code == 0;
    }

    /// <summary>快进：等价于 <c>pull --ff-only</c>——本地有改动或分支已分叉时 git 自己拒绝，不动用户文件。</summary>
    private static async Task<bool> FastForwardAsync(TaskRunner runner, string git, PluginService.PluginInfo plugin, Action<string>? onOutput)
    {
        var code = await runner.Run(git, "merge --ff-only refs/remotes/__launcher/fetch",
            PathHelpers.ExistingDirOrCurrent(plugin.Path), onOutput, GitEnv()).ConfigureAwait(false);
        if (code != 0)
            onOutput?.Invoke($"[错误] 「{plugin.Name}」快进失败（插件目录被本地改过，或分支已分叉）。确认要丢弃本地改动，用「强制覆盖」。");
        return code == 0;
    }

    /// <summary>强制覆盖：留在当前分支上把 HEAD 与工作区一起对齐到远端，丢弃本地改动（不可恢复，弹窗已说明）。</summary>
    private static async Task<bool> ResetHardAsync(TaskRunner runner, string git, PluginService.PluginInfo plugin, Action<string>? onOutput)
    {
        var code = await runner.Run(git, "reset --hard refs/remotes/__launcher/fetch",
            PathHelpers.ExistingDirOrCurrent(plugin.Path), onOutput, GitEnv()).ConfigureAwait(false);
        if (code != 0) onOutput?.Invoke($"[错误] 「{plugin.Name}」强制覆盖失败（退出码 {code}）");
        return code == 0;
    }

    /// <summary>requirements.txt 内容指纹；文件不存在返回 null（用于判断更新后要不要重装依赖）。</summary>
    private static string? RequirementsFingerprint(string pluginDir)
    {
        var file = Path.Combine(pluginDir, "requirements.txt");
        try
        {
            return File.Exists(file) ? Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(file))) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Short(string? hash) => hash is { Length: > 7 } h ? h[..7] : hash;

    private static string? FirstRealLine(string output) => output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.Trim())
        .FirstOrDefault(l => l.Length > 0);
}
