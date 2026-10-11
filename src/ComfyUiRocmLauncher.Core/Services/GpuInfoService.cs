using System.Management;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// GPU 信息（SRS FR-1.3）：WMI 读显卡型号。
/// </summary>
public static class GpuInfoService
{
    private const string Unknown = "未知";

    /// <summary>WMI Win32_VideoController 中 AMD/ATI/Radeon 适配器的 Name，失败返回"未知"。</summary>
    public static string GetAmdGpuName()
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
