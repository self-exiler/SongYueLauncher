namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 路径与临时目录的小工具（取代此前 WheelService / RepoService / GUI 各自实现的多份副本）。
/// </summary>
public static class PathHelpers
{
    /// <summary>
    /// 可用的工作目录：目录存在就用它，否则退回当前目录
    /// （避免把不存在的目录传给 Process.Start 触发 WinError 267）。
    /// </summary>
    public static string ExistingDirOrCurrent(string dir) =>
        Directory.Exists(dir) ? dir : Environment.CurrentDirectory;

    /// <summary>尽力递归删除目录；不存在或删除失败（被占用、只读属性等）时静默——仅用于临时目录清理。</summary>
    public static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // 静默：临时目录清理失败不影响主流程
        }
        catch (UnauthorizedAccessException)
        {
            // Windows 上 Directory.Delete 不清只读属性：git clone 产物（.git\objects 等）必抛此异常。
            // 漏捕会让 robocopy 更新的临时目录清理直接炸掉整个任务并永久卡死后续更新（审查 #9）
        }
    }
}
