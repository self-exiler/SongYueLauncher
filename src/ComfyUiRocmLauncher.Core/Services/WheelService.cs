using System.Text.Json;
using System.Text.RegularExpressions;
using AiDiagnostics.Runtime;
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

    /// <summary>
    /// 更新前的版本快照文件名（写在 ComfyUI 目录下）。
    /// 更新与回滚、以及界面上的「回滚」按钮可用性都读同一个文件，故收成一处常量。
    /// </summary>
    public const string SnapshotFileName = "_pre_update_freeze.txt";

    /// <summary>快照文件的完整路径。</summary>
    public static string SnapshotPath(AppConfig config) =>
        Path.Combine(config.ComfyUiDir, SnapshotFileName);

    public static string LegacyStagingIndexUrl(string arch) =>
        $"https://rocm.nightlies.amd.com/v2-staging/{arch}-dcgpu/";

    /// <summary>
    /// torch 系索引（FR-8.5）：gfx942 / gfx950 走 v2-staging，其余走 nightly。
    /// 安装与回滚必须共用这一处判定——回滚若固定用 nightly，gfx942/gfx950 的快照版本
    /// 根本不在该索引上，必然失败并给出误导性的"已下架"提示。
    /// </summary>
    public static string TorchIndexUrl(string arch) =>
        arch is "gfx942" or "gfx950" ? LegacyStagingIndexUrl(arch) : RocmNightlyIndexUrl;

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
            $"pip install \"rocm[devel,libraries]\" torch torchvision torchaudio --pre --index-url {TorchIndexUrl(arch)} {pythonArg}",
        "gfx1250" or "gfx115x" =>
            throw new NotSupportedException($"arch {arch} 暂无可用 PyTorch 构建"),
        _ =>
            $"pip install \"torch[device-{arch}]\" \"torchvision[device-{arch}]\" torchaudio rocm-sdk-devel --pre --index-url {TorchIndexUrl(arch)} {pythonArg}",
    };

    /// <summary>
    /// 把安装命令改成解析校验命令（在 <c>pip install</c> 后插入 <c>--dry-run</c>）。
    /// 这是「下载验证门」的替代：uv 只解析依赖树并校验索引可达 / 目标 arch 存在对应版本，不下载不落盘。
    /// **不可退回 <c>uv pip download</c>——uv 至今没有该子命令（0.11.19 实测报 unrecognized subcommand、退出码 2），
    /// 换回去会让每次 torch 更新在 [2/6] 必然失败。**
    /// </summary>
    public static string ToDryRunArgs(string installArgs) =>
        installArgs.StartsWith("pip install ", StringComparison.Ordinal)
            ? "pip install --dry-run " + installArgs["pip install ".Length..]
            : installArgs;

    /// <summary>
    /// 按 arch 对应的索引组装回滚命令。与 <see cref="BuildTorchInstallArgs"/> 共用 <see cref="TorchIndexUrl"/>，
    /// 保证「装哪个索引、就从哪个索引回滚」。arch 未知时退回 nightly。
    /// </summary>
    public static string BuildRollbackArgs(IEnumerable<string> specs, string? arch, string pythonArg) =>
        $"pip install {CommandLine.Join(specs)} {pythonArg} --index-url {(arch is null ? RocmNightlyIndexUrl : TorchIndexUrl(arch))}";

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

    private static string PythonArg(AppConfig config) => $"--python {CommandLine.Quote(config.PythonExe)}";
    private static string PypiIndex(AppConfig config) => MirrorPolicy.PypiIndexArg(config.EnvUpdate.PypiMirror);

    /// <summary>
    /// 用 uv 跑一条 pip 子命令：统一解析 uv 路径、以 ComfyUI 目录为工作目录、输出转给回调。
    /// 所有 pip 操作都必须经这里——散着写会让「工作目录该不该存在」「uv 从哪来」各自判断一遍。
    /// </summary>
    private static Task<int> RunUvAsync(TaskRunner runner, AppConfig config, string pipArgs, Action<string>? onOutput) =>
        runner.Run(
            ToolLocator.ResolveUv(config),
            pipArgs,
            PathHelpers.ExistingDirOrCurrent(config.ComfyUiDir),
            onOutput);

    /// <summary>同上，但收集输出行（freeze / list 这类要解析结果的操作）。</summary>
    private static async Task<IReadOnlyList<string>> RunUvCaptureAsync(TaskRunner runner, AppConfig config, string pipArgs)
    {
        var lines = new List<string>();
        await RunUvAsync(runner, config, pipArgs, lines.Add).ConfigureAwait(false);
        return lines;
    }

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
        var lines = await RunUvCaptureAsync(runner, config, $"pip freeze {PythonArg(config)}").ConfigureAwait(false);
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

        // 非 pip 的两步（rocm-sdk init、import 验证）直接在本目录执行
        var dir = PathHelpers.ExistingDirOrCurrent(config.ComfyUiDir);

        // ① 快照
        onOutput?.Invoke("[1/6] 生成版本快照 …");
        var before = await FreezeAsync(runner, config).ConfigureAwait(false);
        var snapshot = SnapshotPath(config);
        try
        {
            File.WriteAllLines(snapshot, before.Select(kv => $"{kv.Key}=={kv.Value}"));
        }
        catch (IOException ex) { onOutput?.Invoke($"      警告：快照写入失败：{ex.Message}"); }

        // ② 解析验证门：索引可达 + 目标 arch 有对应版本才继续（不下载、不落盘）
        onOutput?.Invoke("[2/6] 解析验证门（uv pip install --dry-run）…");
        var verifyCode = await RunUvAsync(runner, config, ToDryRunArgs(args), onOutput).ConfigureAwait(false);
        if (verifyCode != 0)
        {
            onOutput?.Invoke("[错误] 解析失败——旧环境未改动，可稍后重试或检查网络 / 架构");
            return false;
        }

        // ③ 卸载旧包
        onOutput?.Invoke("[3/6] 卸载旧 rocm/torch 系 …");
        var uninstall = BuildTorchUninstallList(before.Keys);
        await RunUvAsync(runner, config, $"pip uninstall {CommandLine.Join(uninstall)} {PythonArg(config)}", onOutput)
            .ConfigureAwait(false);

        // ④ 重装
        onOutput?.Invoke("[4/6] 安装新 torch[device-arch]（AMD 源直连）…");
        var installCode = await RunUvAsync(runner, config, args, onOutput).ConfigureAwait(false);
        if (installCode != 0)
        {
            onOutput?.Invoke($"[错误] 安装失败（退出码 {installCode}），尝试尽力回滚 …");
            await RollbackAsync(runner, config, snapshot, arch, onOutput).ConfigureAwait(false);
            return false;
        }

        // ⑤ rocm-sdk init
        onOutput?.Invoke("[5/6] rocm-sdk init …");
        var sdk = LaunchEnvBuilder.RocmSdkExeFrom(config.PythonExe);
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

    /// <summary>
    /// 尽力回滚：快照版本可下载则装回，否则提示重新更新。
    /// 索引按 arch 选（与 <see cref="BuildTorchInstallArgs"/> 同一判定）——gfx942/gfx950 的快照在 v2-staging 上，
    /// nightly 索引里没有这些版本。
    /// </summary>
    public static async Task RollbackAsync(
        TaskRunner runner, AppConfig config, string snapshotPath, ArchInfo arch, Action<string>? onOutput = null)
    {
        if (!File.Exists(snapshotPath))
        {
            onOutput?.Invoke("      无快照，无法回滚");
            return;
        }
        var specs = File.ReadAllLines(snapshotPath)
            .Where(l => Regex.IsMatch(l, @"^(torch|torchvision|torchaudio|rocm)"))
            .ToArray();
        if (specs.Length == 0) { onOutput?.Invoke("      快照中无 torch/rocm 项"); return; }

        // 架构未知时退回 nightly（旧行为）并明确告知：gfx942/gfx950 的快照可能不在该索引上
        if (arch.Arch is null)
            onOutput?.Invoke("      警告：未能确定 GPU 架构，按 nightly 索引回滚（gfx942 / gfx950 可能不适用）");

        var arg = BuildRollbackArgs(specs, arch.Arch, PythonArg(config));
        var index = arch.Arch is null ? RocmNightlyIndexUrl : TorchIndexUrl(arch.Arch);
        onOutput?.Invoke($"      尝试按快照装回（索引 {index}）…");
        var code = await RunUvAsync(runner, config, arg, onOutput).ConfigureAwait(false);
        if (code != 0)
        {
            onOutput?.Invoke(arch.Arch is "gfx942" or "gfx950"
                ? $"      [回滚失败] 快照版本在 v2-staging 索引中已不可用（退出码 {code}），建议重新执行 torch 更新以安装最新构建"
                : $"      [回滚失败] 原始 nightly 版本可能已下架（退出码 {code}），建议重新执行 torch 更新以安装最新构建");
        }
    }

    /// <summary>安装专用轮子（内置清单命中 arch）。</summary>
    public static async Task InstallSpecialWheelsAsync(TaskRunner runner, AppConfig config, ArchInfo arch, Action<string>? onOutput = null)
    {
        foreach (var entry in WheelCatalog.Instance.Wheels)
        {
            var rule = entry.ResolveFor(arch.Arch);
            string target;
            if (entry.Spec is not null && rule is null) target = entry.Spec;
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
            var code = await RunUvAsync(runner, config, $"pip install {CommandLine.Quote(target)}{extra} {idx}{PythonArg(config)}", onOutput)
                .ConfigureAwait(false);
            if (code != 0)
                onOutput?.Invoke(entry.Optional ? $"      {entry.DisplayName} 跳过（可选组件失败）"
                                               : $"      [警告] {entry.DisplayName} 安装失败（退出码 {code}）");
        }
    }

    /// <summary>整合包根目录的项目通用依赖清单（SRS FR-8.6）：按序安装，除主清单外缺失即跳过。</summary>
    public static readonly string[] RequirementFiles = ["requirements.txt", "manager_requirements.txt", "agent_requirements.txt"];

    /// <summary>项目通用依赖安装（FR-8.6）：三个依赖清单逐一过滤 torch 系后 uv pip install -r，报告变更。</summary>
    public static async Task<IReadOnlyList<string>> SyncRequirementsAsync(TaskRunner runner, AppConfig config, Action<string>? onOutput = null)
    {
        var files = RequirementFiles
            .Select(f => Path.Combine(config.ComfyUiDir, f))
            .Where(File.Exists)
            .ToArray();
        if (files.Length == 0) { onOutput?.Invoke("[错误] 整合包目录未找到 requirements.txt"); return Array.Empty<string>(); }

        var before = await FreezeAsync(runner, config).ConfigureAwait(false);
        foreach (var req in files)
        {
            var name = Path.GetFileName(req);
            var filtered = RequirementsFilter.WriteFilteredFile(req, config.ComfyUiDir);
            onOutput?.Invoke($"安装项目通用依赖：{name}（已过滤 torch 系）…");
            var code = await RunUvAsync(runner, config, $"pip install -r {CommandLine.Quote(filtered)} {PypiIndex(config)}{PythonArg(config)}", onOutput)
                .ConfigureAwait(false);
            if (code != 0) { onOutput?.Invoke($"[错误] {name} 安装失败（退出码 {code}）"); return Array.Empty<string>(); }
        }

        var after = await FreezeAsync(runner, config).ConfigureAwait(false);
        return DiffPackages(before, after);
    }

    /// <summary>通用 PyPI：安装输入（包名 / ==版本 / whl URL）。</summary>
    public static async Task<int> InstallGenericAsync(TaskRunner runner, AppConfig config, string spec, Action<string>? onOutput = null) =>
        await RunUvAsync(runner, config, $"pip install {CommandLine.Quote(spec)} {PypiIndex(config)}{PythonArg(config)}", onOutput)
            .ConfigureAwait(false);

    public static async Task<int> UninstallAsync(TaskRunner runner, AppConfig config, string package, Action<string>? onOutput = null) =>
        await RunUvAsync(runner, config, $"pip uninstall {CommandLine.Quote(package)} {PythonArg(config)}", onOutput)
            .ConfigureAwait(false);

    public static async Task<IReadOnlyList<string>> ListInstalledAsync(TaskRunner runner, AppConfig config) =>
        await RunUvCaptureAsync(runner, config, $"pip list {PythonArg(config)}").ConfigureAwait(false);

}
