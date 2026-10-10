using System.Management;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// GPU 信息（SRS FR-1.3 / FR-6.1）：WMI 读显卡型号；detect_gpu.py 读 arch。
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

    /// <summary>执行 detect_gpu.py 取 arch（stderr 落 gpu_detect_debug.log，失败返回 null）。</summary>
    public static async Task<string?> DetectArchAsync(
        string pythonExe,
        string comfyUiDir,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var script = Path.Combine(comfyUiDir, "detect_gpu.py");
        if (!File.Exists(script))
        {
            onOutput?.Invoke($"[错误] 未找到 detect_gpu.py：{script}");
            return null;
        }

        var captured = new List<string>();
        var code = await ProcessService.RunCommandAsync(
            pythonExe,
            "detect_gpu.py",
            comfyUiDir,
            line =>
            {
                captured.Add(line);
                onOutput?.Invoke(line);
            },
            cancellationToken).ConfigureAwait(false);

        if (code != 0)
        {
            try
            {
                File.WriteAllLines(Path.Combine(comfyUiDir, "gpu_detect_debug.log"), captured);
            }
            catch (IOException) { }
            return null;
        }

        foreach (var line in captured)
        {
            var text = line.Trim();
            if (text.StartsWith("gfx", StringComparison.OrdinalIgnoreCase)) return text;
            var idx = text.IndexOf("arch", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var tail = text[(idx + 4)..].Trim(' ', '=', ':');
                if (tail.StartsWith("gfx", StringComparison.OrdinalIgnoreCase)) return tail;
            }
        }
        return null;
    }
}
