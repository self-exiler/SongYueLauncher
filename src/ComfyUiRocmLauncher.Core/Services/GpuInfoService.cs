using System.Management;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// GPU 信息（SRS FR-1.3）：WMI 读显卡型号。
/// </summary>
public static class GpuInfoService
{
    private const string Unknown = "未知";

    /// <summary>
    /// 进程内缓存的 AMD 显卡名列表：WMI 查询是慢操作，而 <see cref="ToolchainService.DetectArch"/>
    /// 会在每次界面状态变化 / 任务结束时被触发；显卡型号在进程生命周期内不会变化，故只查询一次
    /// （Lazy 默认线程安全，可从 UI 线程与后台任务并发读取）。
    /// </summary>
    private static readonly Lazy<string[]> CachedNames = new(QueryAmdGpuNames);

    /// <summary>
    /// WMI Win32_VideoController 中**全部** AMD/ATI/Radeon 适配器名（只查询一次），失败返回空数组。
    /// Ryzen APU + AMD 独显并存时 WMI 枚举顺序不保证——取用方必须自行挑选能映射到已知架构的名字
    /// （见 <see cref="ToolchainService.DetectArch"/>），否则 iGPU 排在前面会让 torch 更新整体失效（审查 #18）。
    /// </summary>
    public static IReadOnlyList<string> GetAmdGpuNames() => CachedNames.Value;

    /// <summary>第一个 AMD 适配器名（不做架构筛选；展示用）。无 AMD 适配器返回「未知」。</summary>
    public static string GetAmdGpuName() => CachedNames.Value.FirstOrDefault() ?? Unknown;

    private static string[] QueryAmdGpuNames()
    {
        if (!OperatingSystem.IsWindows()) return [];

        try
        {
            var names = new List<string>();
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("ATI", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
            return names.ToArray();
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }
}
