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

    /// <summary>
    /// torch 系包判定——卸载与回滚**必须共用**这一处（审查 #7）：
    /// 命中固定名单，或以 rocm / amd-torch / amd-torchvision 为前缀且边界正确
    /// （前缀后是结尾或非字母数字字符）。无边界的前缀匹配会把 torchmetrics 这类无关包
    /// 拖进回滚命令——它不在 AMD 索引上，uv 会整单解析失败，把本可装回的 torch 也一起葬送。
    /// </summary>
    public static bool IsTorchFamily(string nameOrSpec)
    {
        var name = nameOrSpec.Trim();
        var cut = name.IndexOfAny(['=', '[', ' ', '<', '>', ';', '!', '<', '>']);
        var pkg = (cut < 0 ? name : name[..cut]).Trim();
        if (TorchFixed.Contains(pkg, StringComparer.OrdinalIgnoreCase)) return true;
        if (RocmFixed.Contains(pkg, StringComparer.OrdinalIgnoreCase)) return true;
        return TorchFreezePrefixes.Any(p => HasPrefixBoundary(pkg, p));
    }

    private static bool HasPrefixBoundary(string pkg, string prefix)
    {
        if (!pkg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = pkg[prefix.Length..];
        return rest.Length == 0 || !char.IsAsciiLetterOrDigit(rest[0]);
    }

    /// <summary>torch 更新卸载清单：固定 rocm/torch 名单（无论 freeze 中是否存在）+ freeze 中的 torch 系包。</summary>
    public static IReadOnlyList<string> BuildTorchUninstallList(IEnumerable<string> freezeNames)
    {
        var set = new HashSet<string>(RocmFixed.Concat(TorchFixed), StringComparer.OrdinalIgnoreCase);
        foreach (var name in freezeNames)
            if (IsTorchFamily(name)) set.Add(name);
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

    /// <summary>
    /// 从 release 资产中选 whl：assetMatch 命中优先。
    /// 指定了 assetMatch 却没有命中时**必须返回 null**——release 里常混有 Linux 轮
    /// （如 ROCm/aiter 只发 manylinux），退回任意轮会把必然装失败的 Linux 包丢给 uv。
    /// assetMatch 为 null 时才允许取第一个 whl（清单作者确认该仓库全资产可用）。
    /// </summary>
    public static string? SelectReleaseAsset(IEnumerable<(string Name, string Url)> assets, string? assetMatch)
    {
        var whls = assets.Where(a => a.Name.EndsWith(".whl", StringComparison.OrdinalIgnoreCase)).ToList();
        if (assetMatch is not null)
            return whls.FirstOrDefault(a => a.Name.Contains(assetMatch, StringComparison.OrdinalIgnoreCase)).Url;
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

    /// <summary>
    /// 查询仓库 release 的 whl 直链；API 直连，镜像前缀由调用方作用于资产 URL。
    /// tag 为空走 releases/latest，钉死 tag 时走 releases/tags/{tag}（上游 latest 未必可用，见清单注释）。
    /// </summary>
    public static async Task<string?> FetchLatestWheelUrlAsync(string ghRepo, string? assetMatch, CancellationToken ct, string? tag = null)
    {
        var endpoint = string.IsNullOrEmpty(tag) ? "releases/latest" : $"releases/tags/{tag}";
        using var resp = await Http
            .GetAsync($"https://api.github.com/repos/{ghRepo}/{endpoint}", ct)
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

        // ① 快照（只取内存值；落盘推迟到验证门之后——门失败时环境未动，旧快照必须保留。
        // 否则更新失败后重试会用「损坏状态」覆盖最后一次完好快照，回滚能力被一次重试焚毁，审查 #8）
        onOutput?.Invoke("[1/6] 生成版本快照 …");
        var before = await FreezeAsync(runner, config).ConfigureAwait(false);
        var snapshot = SnapshotPath(config);

        // ② 解析验证门：索引可达 + 目标 arch 有对应版本才继续（不下载、不落盘）
        onOutput?.Invoke("[2/6] 解析验证门（uv pip install --dry-run）…");
        var verifyCode = await RunUvAsync(runner, config, ToDryRunArgs(args), onOutput).ConfigureAwait(false);
        if (verifyCode != 0)
        {
            onOutput?.Invoke("[错误] 解析失败——旧环境未改动，可稍后重试或检查网络 / 架构");
            return false;
        }

        // 验证门通过、环境即将被改动，此刻的快照才是「最后一次完好状态」
        try
        {
            File.WriteAllLines(snapshot, before.Select(kv => $"{kv.Key}=={kv.Value}"));
        }
        catch (IOException ex) { onOutput?.Invoke($"      警告：快照写入失败：{ex.Message}"); }

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
        {
            var initCode = await runner.Run(sdk, "init", dir, onOutput).ConfigureAwait(false);
            if (initCode != 0) onOutput?.Invoke("      警告：rocm-sdk init 返回非零（不阻断更新）");
        }
        else
            onOutput?.Invoke("      警告：未找到 rocm-sdk.exe，跳过 init");

        // ⑥ 验证：import 失败 = 安装产物不完整，必须报失败并让回滚入口可用（审查 #16）
        onOutput?.Invoke("[6/6] 验证 import torch …");
        var verify = await runner.Run(config.PythonExe,
            "-c \"import torch;print('torch',torch.__version__);print('rocm',torch.cuda.is_available());print('hip',getattr(torch.version,'hip',None));print('device',torch.cuda.get_device_name(0))\"",
            dir, onOutput).ConfigureAwait(false);
        if (verify != 0)
        {
            onOutput?.Invoke($"[错误] import torch 验证失败（退出码 {verify}），快照已保留，可点击「回滚」恢复更新前版本");
            return false;
        }

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
            .Where(IsTorchFamily)
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
                    url = await FetchLatestWheelUrlAsync(rule.GhRepo, rule.AssetMatch, runner.Token, rule.Tag)
                        .ConfigureAwait(false);
                    if (url is null)
                        onOutput?.Invoke($"      [跳过] {entry.DisplayName}：{rule.GhRepo} 最新 release 未找到可用 .whl");
                }
                catch (OperationCanceledException) { throw; }
                catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // 404 = 仓库已删除或从未发布 release：这是清单过期/上游弃坑，不是网络故障，
                    // 按「跳过」提示而不是甩原始异常文本
                    url = null;
                    onOutput?.Invoke($"      [跳过] {entry.DisplayName}：上游仓库 {rule.GhRepo} 无可用 release（可能已下架），已跳过");
                }
                catch (Exception ex)
                {
                    url = null;
                    onOutput?.Invoke($"      [警告] {entry.DisplayName}：查询 GitHub release 失败（{ex.Message}）");
                }
                if (url is null) continue;
                target = rule.UseGhMirror ? MirrorPolicy.Github(url, config.EnvUpdate.GithubMirror) : url;
                onOutput?.Invoke($"      {entry.DisplayName}：{target}");
            }
            else if (entry.Spec is null)
            {
                // 有清单条目但当前架构没有任何 gh 规则命中：上游本就没有该架构的构建
                onOutput?.Invoke($"      [跳过] {entry.DisplayName}：当前架构（{arch.Arch ?? "未识别"}）无可用构建，已跳过");
                continue;
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
            // 过滤产物写到系统临时目录：写进 ComfyUI 检出目录会污染 git status 且逐次累积（A9）
            var filtered = RequirementsFilter.WriteFilteredFile(req, Path.GetTempPath());
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
