using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.Core.Test;

/// <summary>
/// PathHelpers.DeleteDirectoryTree：git 产物里的只读文件必须能删净
/// （裸 Directory.Delete(recursive) 在 Windows 上遇到只读文件直接抛 UnauthorizedAccessException，
/// 卸载插件时会留下「备份已生成、原目录还在」的双份状态）。
/// </summary>
public class PathHelpersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crl-pathhelpers-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { PathHelpers.DeleteDirectoryTree(_root); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 递归删除_含只读文件与嵌套目录()
    {
        var nested = Path.Combine(_root, ".git", "objects", "ab");
        Directory.CreateDirectory(nested);
        var ro = Path.Combine(nested, "deadbeef");
        File.WriteAllText(ro, "x");
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(_root, "top.txt"), "y");

        PathHelpers.DeleteDirectoryTree(_root);

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void 尽力删除_目录不存在时静默()
    {
        PathHelpers.TryDeleteDirectory(Path.Combine(_root, "never-created"));
        Assert.False(Directory.Exists(Path.Combine(_root, "never-created")));
    }
}
