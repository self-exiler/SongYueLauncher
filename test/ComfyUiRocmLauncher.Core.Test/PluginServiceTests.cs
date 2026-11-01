using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.Core.Test;

/// <summary>
/// FR-10 方案 A 插件扫描与管理：与上游加载器一致的扫描口径（散文件/垃圾项/.disabled/同名遮蔽）、
/// .git 文件直读的分支/commit/remote 解析、改名开关、卸载备份与恢复。
/// </summary>
public class PluginServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crl-plugins-" + Guid.NewGuid().ToString("N"));

    private string Nodes
    {
        get { Directory.CreateDirectory(_root); return Path.Combine(_root, "custom_nodes"); }
    }

    private string Backup => Path.Combine(_root, "plugin_backup");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>造一个带最小 .git 元数据的假插件目录。</summary>
    private PluginService.PluginInfo MakePlugin(string name, bool git = true, bool disabled = false,
        string head = "ref: refs/heads/main", string? remote = "https://github.com/foo/bar.git")
    {
        var dir = Path.Combine(Nodes, disabled ? name + ".disabled" : name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "__init__.py"), "");
        if (git)
        {
            var gitDir = Path.Combine(dir, ".git");
            Directory.CreateDirectory(Path.Combine(gitDir, "refs", "heads"));
            File.WriteAllText(Path.Combine(gitDir, "HEAD"), head + "\n");
            File.WriteAllText(Path.Combine(gitDir, "refs", "heads", "main"), "0123456789abcdef0123456789abcdef01234567\n");
            if (remote is not null)
                File.WriteAllText(Path.Combine(gitDir, "config"),
                    $"[core]\n\trepositoryformatversion = 0\n[remote \"origin\"]\n\turl = {remote}\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n");
        }
        return ScanOne(name, disabled);
    }

    private PluginService.PluginInfo ScanOne(string name, bool disabled) =>
        PluginService.Scan(Nodes).Single(p => p.Name == name && p.IsDisabled == disabled);

    [Fact]
    public void 扫描_目录与py散文件_跳过垃圾项()
    {
        MakePlugin("Beta");
        MakePlugin("alpha");
        File.WriteAllText(Path.Combine(Nodes, "loose.py"), "");
        File.WriteAllText(Path.Combine(Nodes, "notes.txt"), "");
        Directory.CreateDirectory(Path.Combine(Nodes, "__pycache__"));

        var list = PluginService.Scan(Nodes);
        Assert.Equal(["alpha", "Beta", "loose"], list.Select(p => p.Name).ToArray());
        Assert.False(list.Single(p => p.Name == "loose").IsDirectory);
    }

    [Fact]
    public void 扫描_disabled计入禁用项_同名遮蔽的不显示()
    {
        MakePlugin("Dup", disabled: true);
        MakePlugin("Dup");
        MakePlugin("Solo", disabled: true);

        var list = PluginService.Scan(Nodes);
        Assert.Contains(list, p => p.Name == "Solo" && p.IsDisabled);
        Assert.Single(list, p => p.Name == "Dup" && !p.IsDisabled);
    }

    [Fact]
    public void 扫描_git元数据直读()
    {
        var p = MakePlugin("Meta");
        Assert.True(p.HasGit);
        Assert.Equal("main", p.Branch);
        Assert.Equal("0123456", p.Commit);
        Assert.Equal("https://github.com/foo/bar.git", p.RemoteUrl);
    }

    [Fact]
    public void 扫描_detached与无git与packedrefs()
    {
        var detached = MakePlugin("Detached", head: "abcdef1234567890abcdef1234567890abcdef12");
        Assert.Equal("(detached)", detached.Branch);
        Assert.Equal("abcdef1", detached.Commit);

        var local = MakePlugin("LocalOnly", git: false);
        Assert.False(local.HasGit);
        Assert.Null(local.RemoteUrl);

        // 松散 ref 缺失时回落 packed-refs
        var packed = MakePlugin("Packed", remote: null);
        File.Delete(Path.Combine(Nodes, "Packed", ".git", "refs", "heads", "main"));
        File.WriteAllText(Path.Combine(Nodes, "Packed", ".git", "packed-refs"),
            "# pack-refs with: peeled fully-peeled sorted \nfeedfacefeedfacefeedfacefeedfacefeedface refs/heads/main\n");
        var repacked = ScanOne("Packed", false);
        Assert.Equal("feedfac", repacked.Commit);
        Assert.Null(repacked.RemoteUrl);
    }

    [Fact]
    public void 开关_改名往返()
    {
        var p = MakePlugin("Swap");
        PluginService.Toggle(p);
        Assert.True(ScanOne("Swap", true).IsDisabled);

        PluginService.Toggle(ScanOne("Swap", true));
        Assert.False(ScanOne("Swap", false).IsDisabled);
    }

    [Fact]
    public void 开关_同名冲突时拒绝()
    {
        var live = MakePlugin("Both");
        MakePlugin("Ghost", disabled: true);
        Directory.Move(Path.Combine(Nodes, "Ghost.disabled"), Path.Combine(Nodes, "Both.disabled"));

        var ex = Assert.Throws<InvalidOperationException>(() => PluginService.Toggle(live));
        Assert.Contains("同名", ex.Message);
    }

    [Fact]
    public void 卸载与恢复_往返()
    {
        var p = MakePlugin("Bye");
        var backupDir = PluginService.Uninstall(p, Backup);

        Assert.True(Directory.Exists(backupDir));
        Assert.False(Directory.Exists(Path.Combine(Nodes, "Bye")));
        var backup = Assert.Single(PluginService.ListBackups(Backup));
        Assert.Equal("Bye", backup.PluginName);

        PluginService.Restore(backup, Nodes);
        Assert.True(Directory.Exists(Path.Combine(Nodes, "Bye")));
        Assert.Empty(PluginService.ListBackups(Backup));
    }

    [Fact]
    public void 卸载散py插件_备份容器仍可列出并恢复()
    {
        // 真机回归：备份只枚举目录，散 .py 若直接落成文件就会被列表漏掉——卸载必须走容器目录
        Directory.CreateDirectory(Nodes);
        File.WriteAllText(Path.Combine(Nodes, "websocket_image_save.py"), "x");
        var loose = PluginService.Scan(Nodes).Single(p => p.Name == "websocket_image_save");
        Assert.False(loose.IsDirectory);

        var container = PluginService.Uninstall(loose, Backup);
        Assert.True(Directory.Exists(container));
        Assert.True(File.Exists(Path.Combine(container, "websocket_image_save.py")));
        var backup = Assert.Single(PluginService.ListBackups(Backup));

        PluginService.Restore(backup, Nodes);
        Assert.True(File.Exists(Path.Combine(Nodes, "websocket_image_save.py")));
        Assert.False(Directory.Exists(container));
    }

    [Fact]
    public void 恢复_同名占用时加后缀不覆盖()
    {
        var p = MakePlugin("Re");
        PluginService.Uninstall(p, Backup);
        var backup = Assert.Single(PluginService.ListBackups(Backup));

        MakePlugin("Re");
        PluginService.Restore(backup, Nodes);
        Assert.True(Directory.Exists(Path.Combine(Nodes, "Re")));
        Assert.True(Directory.Exists(Path.Combine(Nodes, "Re_restored2")));
    }
}
