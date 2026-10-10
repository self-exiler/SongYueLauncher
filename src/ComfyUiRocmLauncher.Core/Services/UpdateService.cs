using System.Text.RegularExpressions;
using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 环境管理任务（SRS FR-6）：追踪包同步、依赖安装、ROCm/PyTorch 更新、程序更新。
/// 所有 pip 操作统一为 <c>uv pip ... --python &lt;python.exe&gt;</c>。
/// </summary>
public static class UpdateService
{
    public const string UpdateRepoUrl = "https://github.com/patientx-cfz/comfyui-rocm";
    public const string RocmNightlyIndexUrl = "https://nightly.repo.amd.com/rocm/whl-next/";
    public const string MirrorIndexUrl = "https://mirrors.aliyun.com/pypi/simple/";

    public static readonly string[] TrackedCustomNodes =
    {
        "ComfyUI-Manager",
        "ComfyUI-INT8-Fast-ROCM",
        "comfyui-h3-sla-attention-rocm",
    };

    private static readonly Regex RequirementPinRegex = new(
        @"^\s*(?<name>[A-Za-z0-9_.\-]+)\s*(?<op>[=<>!~]=?)\s*(?<ver>[^\s;#]+)",
        RegexOptions.Compiled);

    private static readonly Regex FreezeRegex = new(
        @"^\s*(?<name>[A-Za-z0-9_.\-]+)\s*==\s*(?<ver>[^\s]+)",
        RegexOptions.Compiled);

    /// <summary>FR-4.1：启动前同步 6 个追踪包，版本一致则跳过。</summary>
    public static async Task SyncTrackedPackagesAsync(
        AppConfig config,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var uv = ResolveUv(config);
        var requirementsPath = Path.Combine(config.ComfyUiDir, "requirements.txt");
        if (!File.Exists(requirementsPath))
        {
            onOutput?.Invoke($"[启动器] 警告：未找到 {requirementsPath}，跳过追踪包同步");
            return;
        }

        var pins = ParseRequirements(requirementsPath);
        var installed = await GetInstalledAsync(uv, config, cancellationToken).ConfigureAwait(false);

        foreach (var pkg in TrackedPackages.Names)
        {
            if (!pins.TryGetValue(pkg, out var want) || string.IsNullOrEmpty(want))
            {
                onOutput?.Invoke($"[启动器] requirements.txt 未固定 {pkg}，跳过");
                continue;
            }
            if (installed.TryGetValue(pkg, out var current) &&
                string.Equals(current, want, StringComparison.OrdinalIgnoreCase))
            {
                onOutput?.Invoke($"[启动器] {pkg}=={want} 一致，跳过");
                continue;
            }

            onOutput?.Invoke($"[启动器] 同步 {pkg}：{current ?? "未安装"} -> {want}");
            await ProcessService.RunCommandAsync(
                uv,
                $"pip install {pkg}=={want} --python \"{config.PythonExe}\"",
                config.ComfyUiDir,
                onOutput,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>FR-6.2：过滤 torch 系后安装，并输出变更包列表。</summary>
    public static async Task<IReadOnlyList<string>> InstallDependenciesAsync(
        AppConfig config,
        bool useMirror = false,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var uv = ResolveUv(config);
        var requirementsPath = Path.Combine(config.ComfyUiDir, "requirements.txt");
        var filteredPath = RequirementsFilter.WriteFilteredFile(requirementsPath, config.ComfyUiDir);
        onOutput?.Invoke($"过滤 requirements.txt → {filteredPath}（{File.ReadAllLines(filteredPath).Length} 项）");

        var before = await GetInstalledAsync(uv, config, cancellationToken).ConfigureAwait(false);
        var indexArg = useMirror ? $"--index-url {MirrorIndexUrl} " : string.Empty;
        await ProcessService.RunCommandAsync(
            uv,
            $"pip install -r \"{filteredPath}\" {indexArg}--python \"{config.PythonExe}\"",
            config.ComfyUiDir,
            onOutput,
            cancellationToken).ConfigureAwait(false);

        var after = await GetInstalledAsync(uv, config, cancellationToken).ConfigureAwait(false);
        return DiffPackages(before, after);
    }

    /// <summary>FR-6.1：按 arch 分支更新 ROCm / PyTorch。</summary>
    public static async Task UpdateRocmAsync(
        AppConfig config,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var uv = ResolveUv(config);
        var python = $"--python \"{config.PythonExe}\"";

        onOutput?.Invoke("[1/6] 校验 python.exe …");
        if (!File.Exists(config.PythonExe))
        {
            onOutput?.Invoke($"[错误] 未找到 Python 解释器：{config.PythonExe}");
            return;
        }

        onOutput?.Invoke("[2/6] 执行 detect_gpu.py 取 arch …");
        var arch = await GpuInfoService.DetectArchAsync(config.PythonExe, config.ComfyUiDir, onOutput, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(arch))
        {
            onOutput?.Invoke("[错误] 未能确定 GPU arch，中止");
            return;
        }
        onOutput?.Invoke($"      arch = {arch}");

        onOutput?.Invoke("[3/6] 卸载旧组件 …");
        var installed = await GetInstalledAsync(uv, config, cancellationToken).ConfigureAwait(false);
        var toUninstall = new List<string>
        {
            "rocm", "rocm-sdk-devel", "rocm-sdk-core", "rocm-sdk-libraries",
            "torch", "torchaudio", "torchvision",
        };
        toUninstall.AddRange(installed.Keys.Where(k =>
            k.StartsWith("rocm", StringComparison.OrdinalIgnoreCase) ||
            k.StartsWith("amd-torch", StringComparison.OrdinalIgnoreCase)));
        await ProcessService.RunCommandAsync(
            uv,
            $"pip uninstall {string.Join(' ', toUninstall.Distinct())} {python}",
            config.ComfyUiDir, onOutput, cancellationToken).ConfigureAwait(false);

        onOutput?.Invoke($"[4/6] 按 arch={arch} 安装 …");
        var installArgs = BuildInstallArgs(arch, python);
        await ProcessService.RunCommandAsync(uv, installArgs, config.ComfyUiDir, onOutput, cancellationToken)
            .ConfigureAwait(false);

        onOutput?.Invoke("[5/6] rocm-sdk init …");
        var sdk = LaunchEnvBuilder.RocmSdkExeFrom(config.PythonExe);
        if (File.Exists(sdk))
        {
            await ProcessService.RunCommandAsync(sdk, "init", config.ComfyUiDir, onOutput, cancellationToken)
                .ConfigureAwait(false);
            EnsureKpack(config, onOutput);
        }
        else
        {
            onOutput?.Invoke($"      警告：未找到 {sdk}，跳过 init");
        }

        onOutput?.Invoke("[6/6] 验证 …");
        await ProcessService.RunCommandAsync(
            config.PythonExe,
            "-c \"import torch;print('torch', torch.__version__);print('rocm', torch.cuda.is_available());print('hip', getattr(torch.version,'hip',None));print('device', torch.cuda.get_device_name(0))\"",
            config.ComfyUiDir, onOutput, cancellationToken).ConfigureAwait(false);

        onOutput?.Invoke("√ ROCm / PyTorch 更新完成");
    }

    /// <summary>FR-6.3：robocopy 覆盖式程序更新。</summary>
    public static async Task UpdateProgramAsync(
        AppConfig config,
        bool includePackages,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var git = ToolLocator.FindGit();
        if (git is null)
        {
            onOutput?.Invoke("[错误] 未找到 git.exe，请先安装 Git 后重试");
            return;
        }

        onOutput?.Invoke("[1/5] 校验 git.exe / python.exe … OK");

        var remote = new List<string>();
        await ProcessService.RunCommandAsync(git, $"ls-remote {UpdateRepoUrl} HEAD", config.ComfyUiDir,
            line => { remote.Add(line); onOutput?.Invoke(line); }, cancellationToken).ConfigureAwait(false);
        var remoteHash = remote.FirstOrDefault()?.Split('\t', ' ')[0] ?? string.Empty;

        var hashFile = Path.Combine(config.ComfyUiDir, ".last_update_hash");
        var localHash = File.Exists(hashFile) ? File.ReadAllText(hashFile).Trim() : string.Empty;
        onOutput?.Invoke($"[2/5] 远程 {remoteHash} / 本地 {localHash}");

        if (string.IsNullOrEmpty(remoteHash))
        {
            onOutput?.Invoke("[错误] 无法获取远程版本，中止");
            return;
        }

        if (string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase))
        {
            onOutput?.Invoke("      版本一致，跳过核心更新");
        }
        else
        {
            var tempDir = Path.Combine(config.ComfyUiDir, "_update_temp");
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
            }

            onOutput?.Invoke("[3/5] git clone --depth 1 …");
            await ProcessService.RunCommandAsync(
                git, $"clone --depth 1 {UpdateRepoUrl} \"{tempDir}\"",
                config.ComfyUiDir, onOutput, cancellationToken).ConfigureAwait(false);

            onOutput?.Invoke("[4/5] robocopy 覆盖 …");
            var code = await ProcessService.RunCommandAsync(
                "robocopy",
                $"\"{tempDir}\" \"{config.ComfyUiDir}\" /E /XD .venv models output input user custom_nodes _update_temp /XF comfyui-user.bat",
                config.ComfyUiDir, onOutput, cancellationToken).ConfigureAwait(false);

            if (IsRobocopySuccess(code))
            {
                onOutput?.Invoke($"      robocopy 成功（退出码 {code}）");
                onOutput?.Invoke("[5/5] 清理临时目录并写入版本 …");
                try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
                File.WriteAllText(hashFile, remoteHash);
            }
            else
            {
                // 覆盖失败不写入版本号，下次检查更新会重试
                onOutput?.Invoke($"      robocopy 失败（退出码 {code}），未写入版本号");
                try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
            }
        }

        onOutput?.Invoke("[仅代码] 更新追踪 custom_nodes …");
        foreach (var node in TrackedCustomNodes)
        {
            var dir = Path.Combine(config.ComfyUiDir, "custom_nodes", node);
            if (!Directory.Exists(dir))
            {
                await ProcessService.RunCommandAsync(git, $"clone --depth 1 {UpdateRepoUrl}/{node} \"{dir}\"",
                    config.ComfyUiDir, onOutput, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (!Directory.Exists(Path.Combine(dir, ".git")))
            {
                onOutput?.Invoke($"      {node}：目录存在但非 git 仓库，跳过");
                continue;
            }
            await ProcessService.RunCommandAsync(git, "pull --ff-only", dir, onOutput, cancellationToken)
                .ConfigureAwait(false);
        }

        if (includePackages)
        {
            onOutput?.Invoke("[代码+包] 安装依赖 …");
            await InstallDependenciesAsync(config, false, onOutput, cancellationToken).ConfigureAwait(false);
        }

        onOutput?.Invoke("√ 程序更新完成");
    }

    /// <summary>按 arch 生成安装命令（FR-6.1 第 4 步分支）。</summary>
    public static string BuildInstallArgs(string arch, string pythonArg) => arch switch
    {
        "gfx942" or "gfx950" =>
            $"pip install \"rocm[devel,libraries]\" torch torchvision torchaudio --pre --index-url https://rocm.nightlies.amd.com/v2-staging/{arch}-dcgpu/ {pythonArg}",
        "gfx1250" or "gfx110x" or "gfx115x" =>
            throw new NotSupportedException($"arch {arch} 暂无可用 PyTorch 构建"),
        _ =>
            $"pip install \"torch[device-{arch}]\" \"torchvision[device-{arch}]\" torchaudio rocm-sdk-devel --pre --index-url {RocmNightlyIndexUrl} {pythonArg}",
    };

    /// <summary>robocopy 退出码语义：0-7 成功，>=8 失败。</summary>
    public static bool IsRobocopySuccess(int exitCode) => exitCode < 8;

    /// <summary>对比前后 pip freeze，输出 "pkg: old -> new"。</summary>
    public static IReadOnlyList<string> DiffPackages(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        var changes = new List<string>();
        foreach (var kv in after)
        {
            if (!before.TryGetValue(kv.Key, out var old))
                changes.Add($"{kv.Key}: 未安装 -> {kv.Value}");
            else if (!string.Equals(old, kv.Value, StringComparison.OrdinalIgnoreCase))
                changes.Add($"{kv.Key}: {old} -> {kv.Value}");
        }
        foreach (var key in before.Keys.Except(after.Keys, StringComparer.OrdinalIgnoreCase))
            changes.Add($"{key}: {before[key]} -> 已移除");
        return changes;
    }

    /// <summary>requirements.txt → 包名/版本字典。</summary>
    public static Dictionary<string, string> ParseRequirements(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var m = RequirementPinRegex.Match(line);
            if (!m.Success) continue;
            result[Normalize(m.Groups["name"].Value)] = m.Groups["ver"].Value;
        }
        return result;
    }

    /// <summary>uv pip freeze → 已安装包字典。</summary>
    public static async Task<Dictionary<string, string>> GetInstalledAsync(
        string uvExe,
        AppConfig config,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await ProcessService.RunCommandAsync(
            uvExe,
            $"pip freeze --python \"{config.PythonExe}\"",
            config.ComfyUiDir,
            line =>
            {
                var m = FreezeRegex.Match(line);
                if (m.Success) result[Normalize(m.Groups["name"].Value)] = m.Groups["ver"].Value;
            },
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    public static string ResolveUv(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.UvExe) && File.Exists(config.UvExe)) return config.UvExe;
        var found = ToolLocator.FindUv();
        return found ?? throw new InvalidOperationException(
            "未找到 uv.exe。请安装 uv（winget install astral-sh.uv）或在主页手动指定路径。");
    }

    private static void EnsureKpack(AppConfig config, Action<string>? onOutput)
    {
        try
        {
            var venv = LaunchEnvBuilder.VenvDirFrom(config.PythonExe);
            var devel = Path.Combine(venv, "Lib", "site-packages", "_rocm_sdk_devel", "bin");
            var core = Path.Combine(venv, "Lib", "site-packages", "_rocm_sdk_core", "bin");
            var target = Path.Combine(devel, "rocm_kpack.dll");
            var source = Path.Combine(core, "rocm_kpack.dll");
            if (!File.Exists(target) && File.Exists(source))
            {
                File.Copy(source, target, overwrite: false);
                onOutput?.Invoke("      已复制 rocm_kpack.dll 到 _rocm_sdk_devel");
            }
        }
        catch (IOException ex)
        {
            onOutput?.Invoke($"      警告：复制 rocm_kpack.dll 失败：{ex.Message}");
        }
    }

    private static string Normalize(string name) => name.Replace('_', '-').ToLowerInvariant();
}
