using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.Core.Test;

/// <summary>
/// FR-8.9 ComfyUI-Manager 数据通道接管：channels.list / config.ini 的改写规则与文件编排。
/// 依据 docs/评估-ComfyUI-Manager通道镜像接管.md。
/// </summary>
public class ComfyManagerChannelTests : IDisposable
{
    private const string Mirror = "https://ghproxy.net";
    private const string Root = ComfyManagerChannelService.UpstreamChannelRoot;

    private const string Template =
        "default::" + Root + "/main\r\n" +
        "recent::" + Root + "/main/node_db/new\r\n" +
        "legacy::" + Root + "/main/node_db/legacy\r\n" +
        "forked::" + Root + "/main/node_db/forked\r\n" +
        "dev::" + Root + "/main/node_db/dev\r\n" +
        "tutorial::" + Root + "/main/node_db/tutorial\r\n";

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "crl-channel-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ---------- channels.list ----------

    [Fact]
    public void ChannelList_AppliesMirrorToAllUpstreamLines()
    {
        var result = ComfyManagerChannelService.RewriteChannelList(Template, Mirror + "/");

        var lines = Split(result);
        Assert.Equal(6, lines.Length);
        Assert.All(lines, l => Assert.Contains(Mirror + "/" + Root, l));
        Assert.Contains("default::" + Mirror + "/" + Root + "/main", lines);
        Assert.Contains("tutorial::" + Mirror + "/" + Root + "/main/node_db/tutorial", lines);
    }

    [Fact]
    public void ChannelList_EmptyMirrorRestoresDirect()
    {
        var mirrored = ComfyManagerChannelService.RewriteChannelList(Template, Mirror);

        Assert.Equal(Template, ComfyManagerChannelService.RewriteChannelList(mirrored, string.Empty));
    }

    [Fact]
    public void ChannelList_IsIdempotent()
    {
        var once = ComfyManagerChannelService.RewriteChannelList(Template, Mirror);
        var twice = ComfyManagerChannelService.RewriteChannelList(once, Mirror);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void ChannelList_KeepsForeignChannelsCommentsAndBlankLines()
    {
        var content = string.Join("\r\n",
            "# mine",
            "",
            "myfork::https://raw.githubusercontent.com/someone/ComfyUI-Manager/main",
            "weird::https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager-fork/main",
            "default::" + Root + "/main",
            "");

        var result = ComfyManagerChannelService.RewriteChannelList(content, Mirror);

        var lines = Split(result);
        Assert.Equal("# mine", lines[0]);
        Assert.Equal(string.Empty, lines[1]);
        Assert.Equal("myfork::https://raw.githubusercontent.com/someone/ComfyUI-Manager/main", lines[2]);
        Assert.Equal("weird::https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager-fork/main", lines[3]);
        Assert.Equal("default::" + Mirror + "/" + Root + "/main", lines[4]);
    }

    [Fact]
    public void ChannelList_PreservesLfStyle()
    {
        var content = "default::" + Root + "/main\nrecent::" + Root + "/main/node_db/new\n";

        var result = ComfyManagerChannelService.RewriteChannelList(content, Mirror);

        Assert.DoesNotContain("\r", result);
        Assert.Equal(2, result.Count(c => c == '\n'));
    }

    // ---------- config.ini ----------

    [Fact]
    public void ConfigIni_RewritesOnlyDefaultSection()
    {
        var content = string.Join("\r\n",
            "[default]",
            "channel_url = " + Root + "/main",
            "network_mode = public",
            "",
            "[other]",
            "channel_url = " + Root + "/main",
            "");

        var result = ComfyManagerChannelService.RewriteConfigIni(content, Mirror);

        var lines = Split(result);
        Assert.Equal("channel_url = " + Mirror + "/" + Root + "/main", lines[1]);
        Assert.Equal("network_mode = public", lines[2]);
        Assert.Equal("channel_url = " + Root + "/main", lines[5]); // 非 [default] 段不动
    }

    [Fact]
    public void ConfigIni_RestoresDirect()
    {
        var content = "[default]\r\nchannel_url = " + Mirror + "/" + Root + "/main\r\n";
        var mirrored = ComfyManagerChannelService.RewriteConfigIni(content, Mirror);

        var restored = ComfyManagerChannelService.RewriteConfigIni(mirrored, string.Empty);

        Assert.Equal("[default]\r\nchannel_url = " + Root + "/main\r\n", restored);
    }

    [Fact]
    public void ConfigIni_AddsMissingKeyInsideDefaultSection()
    {
        var content = "[default]\r\nnetwork_mode = public\r\n\r\n[other]\r\nchannel_url = x\r\n";

        var result = ComfyManagerChannelService.RewriteConfigIni(content, Mirror);

        var lines = Split(result);
        Assert.Equal("[default]", lines[0]);
        Assert.Equal("channel_url = " + Mirror + "/" + Root + "/main", lines[1]);
        Assert.Equal("network_mode = public", lines[2]);
        Assert.Equal("channel_url = x", lines[5]);
    }

    [Fact]
    public void ConfigIni_DoesNothingWhenKeyMissingAndMirrorEmpty()
    {
        var content = "[default]\r\nnetwork_mode = public\r\n";

        Assert.Equal(content, ComfyManagerChannelService.RewriteConfigIni(content, string.Empty));
    }

    [Fact]
    public void ConfigIni_AppendsDefaultSectionWhenAbsent()
    {
        var content = "[other]\r\nfoo = 1\r\n";

        var result = ComfyManagerChannelService.RewriteConfigIni(content, Mirror);

        Assert.Contains("[default]", result);
        Assert.Contains("channel_url = " + Mirror + "/" + Root + "/main", result);
        Assert.StartsWith("[other]", result);
    }

    // ---------- Sync 编排 ----------

    [Fact]
    public void Sync_CreatesChannelListButNeverConfigIni()
    {
        var config = NewConfig();
        var managerDir = Path.Combine(UserDir(config), "__manager");
        Directory.CreateDirectory(managerDir);
        File.WriteAllText(Path.Combine(managerDir, "config.ini"), "[default]\r\nnetwork_mode = public\r\n");
        config.EnvUpdate.GithubMirror = Mirror;

        var report = ComfyManagerChannelService.Sync(config);

        var channels = Path.Combine(managerDir, "channels.list");
        Assert.True(File.Exists(channels));
        Assert.All(Split(File.ReadAllText(channels)), l => Assert.Contains(Mirror, l));
        Assert.Equal(2, report.ChangedFiles.Count);
        Assert.Empty(report.Warnings);
        Assert.Contains("channel_url = " + Mirror, File.ReadAllText(Path.Combine(managerDir, "config.ini")));
    }

    [Fact]
    public void Sync_NeverCreatesFilesWhenManagerMissing()
    {
        var config = NewConfig();
        config.EnvUpdate.GithubMirror = Mirror;

        var report = ComfyManagerChannelService.Sync(config);

        Assert.Empty(report.ChangedFiles);
        Assert.Contains("尚未初始化", report.Summary);
        Assert.False(Directory.Exists(Path.Combine(UserDir(config), "__manager")));
    }

    [Fact]
    public void Sync_SecondRunIsNoOp()
    {
        var config = NewConfig();
        var managerDir = Path.Combine(UserDir(config), "__manager");
        Directory.CreateDirectory(managerDir);
        File.WriteAllText(Path.Combine(managerDir, "channels.list"), Template);
        config.EnvUpdate.GithubMirror = Mirror;

        ComfyManagerChannelService.Sync(config);
        var second = ComfyManagerChannelService.Sync(config);

        Assert.Empty(second.ChangedFiles);
        Assert.False(File.Exists(Path.Combine(managerDir, "channels.list.tmp")));
    }

    [Fact]
    public void Sync_BacksUpOriginalOnceAndRestoresOnDirect()
    {
        var config = NewConfig();
        var managerDir = Path.Combine(UserDir(config), "__manager");
        Directory.CreateDirectory(managerDir);
        var channels = Path.Combine(managerDir, "channels.list");
        File.WriteAllText(channels, Template);
        config.EnvUpdate.GithubMirror = Mirror;

        ComfyManagerChannelService.Sync(config);
        var backup = channels + ".launcher.bak";
        Assert.True(File.Exists(backup));
        Assert.Equal(Template, File.ReadAllText(backup));

        config.EnvUpdate.GithubMirror = string.Empty;
        var restored = ComfyManagerChannelService.Sync(config);

        Assert.Single(restored.ChangedFiles);
        Assert.Equal(Template, File.ReadAllText(channels));
        Assert.Contains("直连", restored.Summary);
    }

    [Fact]
    public void Sync_HonorsUserDirectoryArgument()
    {
        var config = NewConfig();
        var customUserDir = Path.Combine(_tempRoot, "custom-user");
        Directory.CreateDirectory(Path.Combine(customUserDir, "__manager"));
        config.Args.Enable("user-directory", true);
        config.Args.SetValue("user-directory", customUserDir);
        config.EnvUpdate.GithubMirror = Mirror;

        var report = ComfyManagerChannelService.Sync(config);

        Assert.Single(report.ChangedFiles);
        Assert.True(File.Exists(Path.Combine(customUserDir, "__manager", "channels.list")));
        Assert.False(Directory.Exists(Path.Combine(UserDir(config), "__manager")));
    }

    /// <summary>
    /// 关掉开关只会把参数名移出 Enabled，Values 里的旧值还在（SetValue 仅在写空时删）。
    /// 若只看「有没有值」，关掉 --user-directory 后启动器会去改写 ComfyUI 根本不用的目录。
    /// </summary>
    [Fact]
    public void ResolveUserDirectory_IgnoresValueOfDisabledArgument()
    {
        var config = NewConfig();
        config.Args.SetValue("user-directory", Path.Combine(_tempRoot, "stale-dir"));

        Assert.Equal(UserDir(config), ComfyManagerChannelService.ResolveUserDirectory(config));
    }

    [Fact]
    public void ResolveUserDirectory_ResolvesRelativePathAgainstComfyDir()
    {
        var config = NewConfig();
        config.Args.Enable("user-directory", true);
        config.Args.SetValue("user-directory", @"shared\user");

        Assert.Equal(Path.Combine(config.ComfyUiDir, "shared", "user"),
            ComfyManagerChannelService.ResolveUserDirectory(config));
    }

    [Fact]
    public void ResolveUserDirectory_UsesBaseDirectoryWhenUserDirectoryAbsent()
    {
        var config = NewConfig();
        var baseDir = Path.Combine(_tempRoot, "shared-base");
        config.Args.Enable("base-directory", true);
        config.Args.SetValue("base-directory", baseDir);

        Assert.Equal(Path.Combine(baseDir, "user"), ComfyManagerChannelService.ResolveUserDirectory(config));
    }

    [Fact]
    public void ResolveUserDirectory_UserDirectoryOverridesBaseDirectory()
    {
        var config = NewConfig();
        config.Args.Enable("base-directory", true);
        config.Args.SetValue("base-directory", Path.Combine(_tempRoot, "shared-base"));
        config.Args.Enable("user-directory", true);
        config.Args.SetValue("user-directory", Path.Combine(_tempRoot, "explicit-user"));

        Assert.Equal(Path.Combine(_tempRoot, "explicit-user"),
            ComfyManagerChannelService.ResolveUserDirectory(config));
    }

    [Fact]
    public void ResolveUserDirectory_ResolvesRelativeBaseDirectoryAgainstComfyDir()
    {
        var config = NewConfig();
        config.Args.Enable("base-directory", true);
        config.Args.SetValue("base-directory", "shared");

        Assert.Equal(Path.Combine(config.ComfyUiDir, "shared", "user"),
            ComfyManagerChannelService.ResolveUserDirectory(config));
    }

    [Fact]
    public void ResolveUserDirectory_IgnoresValueOfDisabledBaseArgument()
    {
        var config = NewConfig();
        config.Args.SetValue("base-directory", Path.Combine(_tempRoot, "stale-base"));

        Assert.Equal(UserDir(config), ComfyManagerChannelService.ResolveUserDirectory(config));
    }

    [Fact]
    public void Sync_CoversLegacyLayoutToo()
    {
        var config = NewConfig();
        var legacyDir = Path.Combine(UserDir(config), "default", "ComfyUI-Manager");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, "channels.list"), Template);
        config.EnvUpdate.GithubMirror = Mirror;

        var report = ComfyManagerChannelService.Sync(config);

        Assert.Single(report.ChangedFiles);
        Assert.Contains(Mirror, File.ReadAllText(Path.Combine(legacyDir, "channels.list")));
    }

    // ---------- 辅助 ----------

    private AppConfig NewConfig() => new()
    {
        ComfyUiDir = Path.Combine(_tempRoot, "comfy"),
        PythonExe = Path.Combine(_tempRoot, "comfy", ".venv", "Scripts", "python.exe"),
    };

    private static string UserDir(AppConfig config) => Path.Combine(config.ComfyUiDir, "user");

    private static string[] Split(string text) =>
        text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
}
