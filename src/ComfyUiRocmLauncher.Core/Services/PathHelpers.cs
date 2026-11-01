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
            if (Directory.Exists(dir)) DeleteDirectoryTree(dir);
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

    /// <summary>
    /// 递归删除目录：先解只读属性、遇到 junction / 符号链接只删链接本身而不跟随进目标。
    /// 裸 <c>Directory.Delete(recursive)</c> 两条都会出事——git 的 <c>.git\objects</c> 是只读文件（删不动），
    /// 而插件目录里若存在指向外部数据的 junction，跟随删除会把整合包外的数据一起清掉。
    /// </summary>
    public static void DeleteDirectoryTree(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var f = new FileInfo(file);
            if (f.IsReadOnly) f.IsReadOnly = false;
            f.Delete();
        }
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var d = new DirectoryInfo(sub);
            if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) d.Delete();
            else DeleteDirectoryTree(sub);
        }
        Directory.Delete(dir);
    }
}
