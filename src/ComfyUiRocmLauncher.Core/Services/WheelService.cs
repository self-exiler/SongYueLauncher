using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 轮子与包管理（SRS FR-8.5–8.7）：torch nightly 更新（快照 + 下载验证门 + 尽力回滚）、
/// 专用轮子（内置清单 + 自定义）、项目依赖同步、通用 PyPI 包增删。
/// AMD 源永远直连；PyPI 操作适用 PyPI 镜像。
/// </summary>
public static class WheelService
{
    public const string RocmNightlyIndexUrl = "https://nightly.repo.amd.com/rocm/whl-next/";
    public static string LegacyStagingIndexUrl(string arch) =>
        $"https://rocm.nightlies.amd.com/v2-staging/{arch}-dcgpu/";

    private static readonly string[] TorchFixed = { "torch", "torchvision", "torchaudio" };
    private static readonly string[] RocmFixed = { "rocm", "rocm-sdk-devel", "rocm-sdk-core", "rocm-sdk-libraries" };
    private static readonly string[] TorchFreezePrefixes = { "rocm", "amd-torch", "amd-torchvision" };

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ComfyUiRocmLauncher");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>torch 更新卸载清单：固定 rocm/torch 名单 + freeze 中匹配前缀的包。</summary>
    public static IReadOnlyList<string> BuildTorchUninstallList(IEnumerable<string> freezeNames)
    {
        var set = new HashSet<string>(RocmFixed.Concat(TorchFixed), StringComparer.OrdinalIgnoreCase);
        foreach (var name in freezeNames)
            if (TorchFreezePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                set.Add(name);
        return set.ToList();
    }

    /// <summary>按 arch 组装 torch 安装命令（FR-8.5，AMD 源直连）。gfx1250/gfx115x 无可用构建。</summary>
    public static string BuildTorchInstallArgs(string arch, string pythonArg) => arch switch
    {
        "gfx942" or "gfx950" =>
            $"pip install \"rocm[devel,libraries]\" torch torchvision torchaudio --pre --index-url {LegacyStagingIndexUrl(arch)} {pythonArg}",
        "gfx1250" or "gfx115x" =>
            throw new NotSupportedException($"arch {arch} 暂无可用 PyTorch 构建"),
        _ =>
            $"pip install \"torch[device-{arch}]\" \"torchvision[device-{arch}]\" torchaudio rocm-sdk-devel --pre --index-url {RocmNightlyIndexUrl} {pythonArg}",
    };

    /// <summary>从 release 资产列表挑选 whl 下载 URL（先匹配 assetMatch，再退回首个 .whl）。</summary>
    public static string? SelectReleaseAsset(IEnumerable<(string Name, string Url)> assets, string? assetMatch)
    {
        var whls = assets.Where(a => a.Name.EndsWith(".whl", StringComparison.OrdinalIgnoreCase)).ToList();
        if (assetMatch is not null)
        {
            var hit = whls.FirstOrDefault(a => a.Name.Contains(assetMatch, StringComparison.OrdinalIgnoreCase));
            if (hit.Url is not null) return hit.Url;
        }
        return whls.FirstOrDefault().Url;
    }

    /// <summary>解析 GitHub releases API 响应中的 assets[].name / browser_download_url。</summary>
    public static IReadOnlyList<(string Name, string Url)> ParseReleaseAssets(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
            return Array.Empty<(string, string)>();

        var list = new List<(string, string)>();
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name is not null && url is not null) list.Add((name, url));
        }
        return list;
    }

    /// <summary>查询仓库 latest release 的 whl 直链；API 直连，镜像前缀由调用方作用于资产 URL。</summary>
    public static async Task<string?> FetchLatestWheelUrlAsync(string ghRepo, string? assetMatch, CancellationToken ct)
    {
        using var resp = await Http
            .GetAsync($"https://api.github.com/repos/{ghRepo}/releases/latest", ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return SelectReleaseAsset(ParseReleaseAssets(json), assetMatch);
    }

    private static string PythonArg(AppConfig config) => $"--python \"{config.PythonExe}\"";
    private static string PypiIndex(AppConfig config) => MirrorPolicy.PypiIndexArg(config.EnvUpdate.PypiMirror);

    /// <summary>pip freeze → name/version 字典（name 规范化小写）。</summary>
    public static Dictionary<string, string> ParseFreeze(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var m = Regex.Match(line, @"^(?<name>[A-Za-z0-9._-]+)\s*==\s*(?<ver>[^;\s]+)");
            if (m.Success) map[m.Groups["name"].Value] = m.Groups["ver"].Value;
        }
        return map;
    }

    /// <summary>对比前后 freeze，输出 "pkg: old -> new"。</summary>
    public static IReadOnlyList<string> DiffPackages(
        IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        var result = new List<string>();
        foreach (var kv in after)
        {
            if (before.TryGetValue(kv.Key, out var old))
            {
                if (old != kv.Value) result.Add($"{kv.Key}: {old} -> {kv.Value}");
            }
            else result.Add($"{kv.Key}: 未安装 -> {kv.Value}");
        }
        foreach (var kv in before)
            if (!after.ContainsKey(kv.Key)) result.Add($"{kv.Key}: {kv.Value} -> 已移除");
        return result;
    }

    private static async Task<Dictionary<string, string>> FreezeAsync(TaskRunner runner, AppConfig config)
    {
        var lines = new List<string>();
        var uv = ToolLocator.FindUv() ?? config.UvExe ?? "uv";
        await runner.Run(uv, $"pip freeze {PythonArg(config)}", SafeDir(config), lines.Add).ConfigureAwait(false);
        return ParseFreeze(lines);
    }

    /// <summary>torch 更新主流程（需已在 TaskRunner 任务槽内）。返回是否成功。</summary>
    public static async Task<bool> TorchUpdateAsync(TaskRunner runner, AppConfig config, ArchInfo arch, Action<string>? onOutput = null)
    {
        if (!arch.Known || arch.Arch is null)
        {
            onOutput?.Invoke("[错误] 未能确定 GPU 架构");
            return false;
        }
        if (config.PythonExe is null or "" || !File.Exists(config.PythonExe))
        {
            onOutput?.Invoke("[错误] venv 未就绪，请先创建 .venv");
            return false;
        }

        string args;
        try { args = BuildTorchInstallArgs(arch.Arch, PythonArg(config)); }
        catch (NotSupportedException ex) { onOutput?.Invoke($"[错误] {ex.Message}"); return false; }

        var uv = ToolLocator.FindUv() ?? config.UvExe ?? "uv";
        var dir = SafeDir(config);

        // ① 快照
        onOutput?.Invoke("[1/6] 生成版本快照 …");
        var before = await FreezeAsync(runner, config).ConfigureAwait(false);
        var snapshot = Path.Combine(config.ComfyUiDir, "_pre_update_freeze.txt");
        try
        {
            File.WriteAllLines(snapshot, before.Select(kv => $"{kv.Key}=={kv.Value}"));
        }
        catch (IOException ex) { onOutput?.Invoke($"      警告：快照写入失败：{ex.Message}"); }

        // ② 下载验证门：全部下载成功才继续
        onOutput?.Invoke("[2/6] 下载验证门（uv pip download）…");
        var cacheDir = Path.Combine(config.ComfyUiDir, "_torch_download");
        var dlArgs = args.Replace("pip install", "pip download");
        var dlCode = await runner.Run(uv, $"{dlArgs} -d \"{cacheDir}\"", dir, onOutput).ConfigureAwait(false);
        if (dlCode != 0)
        {
            onOutput?.Invoke("[错误] 下载失败——旧环境未改动，可稍后重试或检查网络/架构");
            TryCleanup(cacheDir);
            return false;
        }

        // ③ 卸载旧包
        onOutput?.Invoke("[3/6] 卸载旧 rocm/torch 系 …");
        var uninstall = BuildTorchUninstallList(before.Keys);
        await runner.Run(uv, $"pip uninstall {string.Join(' ', uninstall.Select(Quote))} {PythonArg(config)}", dir, onOutput)
            .ConfigureAwait(false);

        // ④ 重装（利用缓存）
        onOutput?.Invoke("[4/6] 安装新 torch[device-arch]（AMD 源直连）…");
        var installCode = await runner.Run(uv, args, dir, onOutput).ConfigureAwait(false);
        TryCleanup(cacheDir);
        if (installCode != 0)
        {
            onOutput?.Invoke($"[错误] 安装失败（退出码 {installCode}），尝试尽力回滚 …");
            await RollbackAsync(runner, config, snapshot, onOutput).ConfigureAwait(false);
            return false;
        }

        // ⑤ rocm-sdk init
        onOutput?.Invoke("[5/6] rocm-sdk init …");
        var sdk = Path.Combine(Path.GetDirectoryName(config.PythonExe) ?? "", "rocm-sdk.exe");
        if (File.Exists(sdk))
            await runner.Run(sdk, "init", dir, onOutput).ConfigureAwait(false);
        else
            onOutput?.Invoke("      警告：未找到 rocm-sdk.exe，跳过 init");

        // ⑥ 验证
        onOutput?.Invoke("[6/6] 验证 import torch …");
        await runner.Run(config.PythonExe,
            "-c \"import torch;print('torch',torch.__version__);print('rocm',torch.cuda.is_available());print('hip',getattr(torch.version,'hip',None));print('device',torch.cuda.get_device_name(0))\"",
            dir, onOutput).ConfigureAwait(false);

        onOutput?.Invoke("√ ROCm / PyTorch 更新完成");
        return true;
    }

    /// <summary>尽力回滚：快照版本可下载则装回，否则提示改装最新 nightly。</summary>
    public static async Task RollbackAsync(TaskRunner runner, AppConfig config, string snapshotPath, Action<string>? onOutput = null)
    {
        if (!File.Exists(snapshotPath))
        {
            onOutput?.Invoke("      无快照，无法回滚");
            return;
        }
        var uv = ToolLocator.FindUv() ?? config.UvExe ?? "uv";
        var specs = File.ReadAllLines(snapshotPath)
            .Where(l => Regex.IsMatch(l, @"^(torch|torchvision|torchaudio|rocm)"))
            .ToArray();
        if (specs.Length == 0) { onOutput?.Invoke("      快照中无 torch/rocm 项"); return; }

        onOutput?.Invoke("      尝试按快照装回（原版本可能已下架）…");
        var arg = $"pip install {specs.Select(Quote).Aggregate((a, b) => a + " " + b)} {PythonArg(config)} --index-url {RocmNightlyIndexUrl}";
        var code = await runner.Run(uv, arg, SafeDir(config), onOutput).ConfigureAwait(false);
        if (code != 0)
            onOutput?.Invoke("      [回滚失败] 原版本可能已从 AMD 源下架，建议重新执行以安装最新 nightly 恢复可运行");
    }

    /// <summary>安装专用轮子（内置清单命中 arch + 用户自定义）。</summary>
    public static async Task InstallSpecialWheelsAsync(TaskRunner runner, AppConfig config, ArchInfo arch, Action<string>? onOutput = null)
    {
        var uv = ToolLocator.FindUv() ?? config.UvExe ?? "uv";
        var dir = SafeDir(config);

        foreach (var entry in WheelCatalog.Instance.Wheels)
        {
            var rule = entry.ResolveFor(arch.Arch);
            string target;
            if (entry.Spec is not null && rule is null) target = entry.Spec;
            else if (rule?.Spec is not null) target = rule.Spec;
            else if (rule?.GhRepo is not null)
            {
                string? url;
                try
                {
                    url = await FetchLatestWheelUrlAsync(rule.GhRepo, rule.AssetMatch, runner.Token)
                        .ConfigureAwait(false);
                    if (url is null)
                        onOutput?.Invoke($"      [跳过] {rule.GhRepo} 最新 release 未找到可用 .whl");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    url = null;
                    onOutput?.Invoke($"      [警告] {entry.DisplayName}：查询 GitHub release 失败（{ex.Message}）");
                }
                if (url is null) continue;
                target = rule.UseGhMirror ? MirrorPolicy.Github(url, config.EnvUpdate.GithubMirror) : url;
                onOutput?.Invoke($"      {entry.DisplayName}：{target}");
            }
            else { continue; }

            var extra = entry.ForceReinstall ? " --force-reinstall --no-deps" : string.Empty;
            var idx = MirrorPolicy.PypiIndexArg(config.EnvUpdate.PypiMirror);
            var code = await runner.Run(uv, $"pip install {Quote(target)}{extra} {idx}{PythonArg(config)}", dir, onOutput)
                .ConfigureAwait(false);
            if (code != 0)
                onOutput?.Invoke(entry.Optional ? $"      {entry.DisplayName} 跳过（可选组件失败）"
                                               : $"      [警告] {entry.DisplayName} 安装失败（退出码 {code}）");
        }

        foreach (var w in config.EnvUpdate.CustomWheels)
        {
            if (string.IsNullOrWhiteSpace(w.Spec)) continue;
            var spec = w.UseGhMirror ? MirrorPolicy.Github(w.Spec, config.EnvUpdate.GithubMirror) : w.Spec;
            await runner.Run(uv, $"pip install {Quote(spec)} {PythonArg(config)}", dir, onOutput).ConfigureAwait(false);
        }
    }

    /// <summary>项目依赖同步（FR-8.6）：过滤 torch 系后 uv pip install -r，报告变更。</summary>
    public static async Task<IReadOnlyList<string>> SyncRequirementsAsync(TaskRunner runner, AppConfig config, Action<string>? onOutput = null)
    {
        var uv = ToolLocator.FindUv() ?? config.UvExe ?? "uv";
        var req = Path.Combine(config.ComfyUiDir, "requirements.txt");
        if (!File.Exists(req)) { onOutput?.Invoke("[错误] 未找到 requirements.txt"); return Array.Empty<string>(); }

        var before = await FreezeAsync(runner, config).ConfigureAwait(false);
        var filtered = RequirementsFilter.WriteFilteredFile(req, config.ComfyUiDir);
        onOutput?.Invoke("同步项目依赖（已过滤 torch 系）…");
        var code = await runner.Run(uv, $"pip install -r \"{filtered}\" {PypiIndex(config)}{PythonArg(config)}",
            SafeDir(config), onOutput).ConfigureAwait(false);
        if (code != 0) { onOutput?.Invoke($"[错误] 依赖安装失败（退出码 {code}）"); return Array.Empty<string>(); }

        var after = await FreezeAsync(runner, config).ConfigureAwait(false);
        return DiffPackages(before, after);
    }

    /// <summary>通用 PyPI：安装输入（包名 / ==版本 / whl URL）。</summary>
    public static async Task<int> InstallGenericAsync(TaskRunner runner, AppConfig config, string spec, Action<string>? onOutput = null)
    {
        var uv = ToolLocator.FindUv() ?? config.UvExe ?? "uv";
        return await runner.Run(uv, $"pip install {Quote(spec)} {PypiIndex(config)}{PythonArg(config)}", SafeDir(config), onOutput)
            .ConfigureAwait(false);
    }

    public static async Task<int> UninstallAsync(TaskRunner runner, AppConfig config, string package, Action<string>? onOutput = null)
    {
        var uv = ToolLocator.FindUv() ?? config.UvExe ?? "uv";
        return await runner.Run(uv, $"pip uninstall {Quote(package)} {PythonArg(config)}", SafeDir(config), onOutput)
            .ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<string>> ListInstalledAsync(TaskRunner runner, AppConfig config)
    {
        var uv = ToolLocator.FindUv() ?? config.UvExe ?? "uv";
        var lines = new List<string>();
        await runner.Run(uv, $"pip list {PythonArg(config)}", SafeDir(config), lines.Add).ConfigureAwait(false);
        return lines;
    }

    private static string SafeDir(AppConfig config) =>
        Directory.Exists(config.ComfyUiDir) ? config.ComfyUiDir : Environment.CurrentDirectory;

    private static string Quote(string s) => "\"" + s + "\"";

    private static void TryCleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
    }
}
