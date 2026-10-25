using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>工具链项状态。<c>Installed</c> 由 <c>Path</c> 派生，避免构造时传两份互相矛盾的事实。</summary>
public sealed record ToolStatus(string Name, string? Path, string? Version)
{
    public bool Installed => Path is not null;
}

/// <summary>
/// 工具链检测与安装（SRS FR-8.2）：git / uv / winget 发现（含已知路径），版本查询，
/// winget 安装（需 UAC 提权子进程），uv 用户级安装引导。检测为一次性命令，安装经 TaskRunner。
/// </summary>
public static class ToolchainService
{
    public static async Task<IReadOnlyList<ToolStatus>> DetectAsync(CancellationToken cancellationToken = default)
    {
        var git = ToolLocator.FindGit();
        var uv = ToolLocator.FindUv();
        var winget = ToolLocator.FindWinget();

        return new List<ToolStatus>
        {
            new("git", git, await QueryVersionAsync(git, "--version", cancellationToken)),
            new("uv", uv, await QueryVersionAsync(uv, "--version", cancellationToken)),
            new("winget", winget, await QueryVersionAsync(winget, "--version", cancellationToken)),
        };
    }

    /// <summary>
    /// GPU arch 解析（复用 WMI 名称 + 内置映射，不依赖项目文件 / venv）。
    /// 多个 AMD 适配器（APU + 独显）时取第一个能映射到已知架构的名字——iGPU 名
    /// 不匹配任何规则，此前取第一个 AMD 名会让识别整体失效（审查 #18）。
    /// </summary>
    public static ArchInfo DetectArch()
    {
        var names = GpuInfoService.GetAmdGpuNames();
        foreach (var name in names)
        {
            var arch = GpuArchService.Instance.Map(name);
            if (arch.Known) return arch;
        }
        return GpuArchService.Instance.Map(names.FirstOrDefault() ?? "未知");
    }

    private static async Task<string?> QueryVersionAsync(string? exe, string args, CancellationToken ct)
    {
        if (exe is null) return null;
        try
        {
            var lines = new List<string>();
            await ProcessService.RunCommandAsync(exe, args, Environment.CurrentDirectory,
                line => { if (lines.Count == 0) lines.Add(line); }, ct).ConfigureAwait(false);
            return lines.FirstOrDefault()?.Trim();
        }
        catch (OperationCanceledException)
        {
            throw; // 取消不是「版本读取失败」，吞掉会让取消的任务以正常文案收尾
        }
        catch
        {
            return "已安装（版本读取失败）";
        }
    }

    /// <summary>winget 安装参数（--source winget 避免命中 Store 版本）。</summary>
    public static string WingetInstallArgs(string packageId) =>
        $"install --id {packageId} --source winget -e --accept-source-agreements --accept-package-agreements";

    public const string GitWingetId = "Git.Git";
    public const string UvWingetId = "astral-sh.uv";
}
