using System.Management;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// GPU 信息（SRS FR-1.3）：WMI 读显卡型号。
/// </summary>
public static class GpuInfoService
{
    private const string Unknown = "未知";

    /// <summary>
    /// 进程内缓存的 AMD 显卡名：WMI 查询是慢操作，而 <see cref="ToolchainService.DetectArch"/>
    /// 会在每次界面状态变化 / 任务结束时被触发；显卡型号在进程生命周期内不会变化，故只查询一次
    /// （Lazy 默认线程安全，可从 UI 线程与后台任务并发读取）。
    /// </summary>
    private static readonly Lazy<string> CachedName = new(QueryAmdGpuName);

    /// <summary>WMI Win32_VideoController 中 AMD/ATI/Radeon 适配器的 Name（只查询一次），失败返回"未知"。</summary>
    public static string GetAmdGpuName() => CachedName.Value;

    private static string QueryAmdGpuName()
    {
        if (!OperatingSystem.IsWindows()) return Unknown;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("ATI", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or NotSupportedException)
        {
            return Unknown;
        }

        return Unknown;
    }
}
