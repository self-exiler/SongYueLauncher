namespace ComfyUiRocmLauncher.GUI.Helpers;

public static class UiHelpers
{
    /// <summary>在资源管理器中打开目录（目录不存在时返回 false）。</summary>
    public static bool OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = path,
            UseShellExecute = true,
        });
        return true;
    }

    public static void OpenUrl(string url)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }
}
