using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.Core.Test;

/// <summary>
/// FR-1.5 目录外置链接：junction 的建立 / 现状识别 / 内容迁移 / 解除还原 / 目标位置校验。
/// 同盘场景覆盖「目录改名瞬时迁移」与「目标已有内容时 robocopy 合并」两条分支。
/// </summary>
public class DirLinkServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "crl-dirlink-" + Guid.NewGuid().ToString("N"));
    private readonly TaskRunner _runner = new();

    private string ComfyDir
    {
        get { Directory.CreateDirectory(Path.Combine(_tempRoot, "comfy")); return Path.Combine(_tempRoot, "comfy"); }
    }

    private string DataRoot
    {
        get { Directory.CreateDirectory(Path.Combine(_tempRoot, "data")); return Path.Combine(_tempRoot, "data"); }
    }

    public void Dispose()
    {
        // 递归删除遇 junction 会失败（甚至波及联接目标），先只删联接点本身再清树
        TryRemoveJunctions(_tempRoot);
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static void TryRemoveJunctions(string dir)
    {
        string[] subs;
        try { subs = Directory.GetDirectories(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (var sub in subs)
        {
            try
            {
                if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    Directory.Delete(sub, recursive: false);
                else
                    TryRemoveJunctions(sub);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<string?> LinkAsync(string comfy, string name, string root)
    {
        Assert.True(_runner.TryStart());
        try { return await DirLinkService.LinkAsync(_runner, comfy, name, root); }
        finally { _runner.Complete(); }
    }

    private async Task<string?> UnlinkAsync(string comfy, string name)
    {
        Assert.True(_runner.TryStart());
        try { return await DirLinkService.UnlinkAsync(_runner, comfy, name); }
        finally { _runner.Complete(); }
    }

    [Fact]
    public void Inspect_PlainDirectory_ReportsContent()
    {
        var comfy = ComfyDir;
        Directory.CreateDirectory(Path.Combine(comfy, "models"));

        var empty = DirLinkService.Inspect(comfy, "models");
        Assert.True(empty.Exists);
        Assert.False(empty.IsLink);
        Assert.False(empty.HasContent);

        File.WriteAllText(Path.Combine(comfy, "models", "a.safetensors"), "x");
        var full = DirLinkService.Inspect(comfy, "models");
        Assert.True(full.HasContent);

        var missing = DirLinkService.Inspect(comfy, "output");
        Assert.False(missing.Exists);
    }

    [Fact]
    public async Task Link_MovesExistingContentAndCreatesJunction()
    {
        var comfy = ComfyDir;
        Directory.CreateDirectory(Path.Combine(comfy, "models", "checkpoints"));
        File.WriteAllText(Path.Combine(comfy, "models", "checkpoints", "a.safetensors"), "model");

        await LinkAsync(comfy, "models", DataRoot);

        var info = DirLinkService.Inspect(comfy, "models");
        Assert.True(info.IsLink);
        Assert.True(info.TargetExists);
        Assert.Equal(
            DirLinkService.TargetPath(DataRoot, "models"),
            info.TargetPath, ignoreCase: true);

        // 原路径经联接透明可见，内容已在外部目录
        Assert.True(File.Exists(Path.Combine(comfy, "models", "checkpoints", "a.safetensors")));
        Assert.True(File.Exists(Path.Combine(DirLinkService.TargetPath(DataRoot, "models"), "checkpoints", "a.safetensors")));
    }

    [Fact]
    public async Task Link_MissingDirectory_CreatesJunctionDirectly()
    {
        var comfy = ComfyDir;

        await LinkAsync(comfy, "output", DataRoot);

        var info = DirLinkService.Inspect(comfy, "output");
        Assert.True(info.IsLink);
        // 经联接可正常写入，数据落在联接目标
        Directory.CreateDirectory(Path.Combine(comfy, "output", "sub"));
        Assert.True(Directory.Exists(Path.Combine(DirLinkService.TargetPath(DataRoot, "output"), "sub")));
    }

    [Fact]
    public async Task Link_AlreadyLinkedToSameTarget_IsIdempotent()
    {
        var comfy = ComfyDir;
        await LinkAsync(comfy, "models", DataRoot);

        var warning = await LinkAsync(comfy, "models", DataRoot);

        Assert.NotNull(warning);
        Assert.Contains("无需重复操作", warning);
        Assert.True(DirLinkService.Inspect(comfy, "models").IsLink);
    }

    [Fact]
    public async Task Link_TargetHasContent_MergesInsteadOfRename()
    {
        var comfy = ComfyDir;
        Directory.CreateDirectory(Path.Combine(comfy, "models"));
        File.WriteAllText(Path.Combine(comfy, "models", "new.txt"), "new");
        var target = DirLinkService.TargetPath(DataRoot, "models");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "existing.txt"), "existing");

        await LinkAsync(comfy, "models", DataRoot);

        // 目标已有内容 → 不能整目录改名，走 robocopy 合并，双方文件都在
        Assert.True(File.Exists(Path.Combine(target, "existing.txt")));
        Assert.True(File.Exists(Path.Combine(target, "new.txt")));
        Assert.True(DirLinkService.Inspect(comfy, "models").IsLink);
    }

    [Fact]
    public async Task Link_TargetInsideComfyDir_IsRejected()
    {
        var comfy = ComfyDir;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => LinkAsync(comfy, "models", Path.Combine(comfy, "external")));

        Assert.Contains("ComfyUI 目录内", ex.Message);
        Assert.False(DirLinkService.Inspect(comfy, "models").IsLink);
    }

    [Fact]
    public async Task Link_MissingComfyDir_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => LinkAsync(Path.Combine(_tempRoot, "nope"), "models", DataRoot));

        Assert.Contains("ComfyUI 目录不存在", ex.Message);
    }

    [Fact]
    public async Task Unlink_RemovesJunctionAndMovesContentBack()
    {
        var comfy = ComfyDir;
        Directory.CreateDirectory(Path.Combine(comfy, "models"));
        File.WriteAllText(Path.Combine(comfy, "models", "a.safetensors"), "model");
        await LinkAsync(comfy, "models", DataRoot);

        await UnlinkAsync(comfy, "models");

        var info = DirLinkService.Inspect(comfy, "models");
        Assert.True(info.Exists);
        Assert.False(info.IsLink);
        Assert.True(File.Exists(Path.Combine(comfy, "models", "a.safetensors")));
        // 同盘还原是目录改名：外部目录整体搬回，不再残留
        Assert.False(Directory.Exists(DirLinkService.TargetPath(DataRoot, "models")));
    }

    [Fact]
    public async Task Unlink_PlainDirectory_IsRejected()
    {
        var comfy = ComfyDir;
        Directory.CreateDirectory(Path.Combine(comfy, "models"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => UnlinkAsync(comfy, "models"));

        Assert.Contains("不是目录链接", ex.Message);
    }

    [Fact]
    public void PathGuards_IsInsideAndSameVolume()
    {
        Assert.True(DirLinkService.IsInside(@"D:\a\b", @"D:\a"));
        Assert.True(DirLinkService.IsInside(@"D:\a\b\", @"D:\a\"));
        Assert.False(DirLinkService.IsInside(@"D:\ab", @"D:\a"));
        Assert.False(DirLinkService.IsInside(@"D:\a", @"D:\a"));
        Assert.True(DirLinkService.SameVolume(@"D:\a", @"d:\b\c"));
        Assert.False(DirLinkService.SameVolume(@"D:\a", @"E:\b"));
    }
}
