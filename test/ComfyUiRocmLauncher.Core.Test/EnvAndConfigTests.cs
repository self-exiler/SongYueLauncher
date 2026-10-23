using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.Core.Test;

public class EnvAndConfigTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "crl-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Build_FixedInjectionLayer()
    {
        var config = new AppConfig
        {
            ComfyUiDir = @"D:\AI\comfyui-rocm",
            PythonExe = @"D:\AI\comfyui-rocm\.venv\Scripts\python.exe",
        };

        var env = LaunchEnvBuilder.Build(config, @"C:\rocm", out var conflicts);

        Assert.Equal("1", env["PYTHONUTF8"]);
        Assert.Equal("1", env["PYTHONNOUSERSITE"]);
        Assert.Equal(@"C:\rocm", env["HIP_PATH"]);
        Assert.Equal(@"C:\rocm", env["ROCM_PATH"]);
        Assert.StartsWith(@"D:\AI\comfyui-rocm\.venv;D:\AI\comfyui-rocm\.venv\Scripts;", env["PATH"]);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void Build_ReportsConflictWithFixedKeys()
    {
        var config = new AppConfig { PythonExe = @"D:\venv\Scripts\python.exe" };
        config.CustomEnvVars["HIP_PATH"] = "x";

        var env = LaunchEnvBuilder.Build(config, null, out var conflicts);

        Assert.Contains("HIP_PATH", conflicts);
        Assert.False(env.ContainsKey("HIP_PATH"));
    }

    [Fact]
    public void Build_AppliesCustomVars()
    {
        var config = new AppConfig { PythonExe = @"D:\venv\Scripts\python.exe" };
        config.CustomEnvVars["MIOPEN_FIND_MODE"] = "2";

        var env = LaunchEnvBuilder.Build(config, null, out _);

        Assert.Equal("2", env["MIOPEN_FIND_MODE"]);
    }

    [Fact]
    public void Build_InjectsGithubEndpointWhenMirrorSet()
    {
        var config = new AppConfig { PythonExe = @"D:\venv\Scripts\python.exe" };
        config.EnvUpdate.GithubMirror = "https://ghproxy.net/";

        var env = LaunchEnvBuilder.Build(config, null, out _);

        Assert.Equal("https://ghproxy.net/https://github.com", env["GITHUB_ENDPOINT"]);
    }

    [Fact]
    public void Build_NoMirrorKeepsUserGithubEndpointUntouched()
    {
        var config = new AppConfig { PythonExe = @"D:\venv\Scripts\python.exe" };
        config.CustomEnvVars["GITHUB_ENDPOINT"] = "https://self-hosted.example/https://github.com";

        var env = LaunchEnvBuilder.Build(config, null, out var conflicts);

        Assert.Equal("https://self-hosted.example/https://github.com", env["GITHUB_ENDPOINT"]);
        Assert.Empty(conflicts);
    }

    [Fact]
    public void Build_MirrorOverridesUserGithubEndpointAndReportsConflict()
    {
        var config = new AppConfig { PythonExe = @"D:\venv\Scripts\python.exe" };
        config.CustomEnvVars["GITHUB_ENDPOINT"] = "https://stale.example/https://github.com";
        config.EnvUpdate.GithubMirror = "https://gh-proxy.com/";

        var env = LaunchEnvBuilder.Build(config, null, out var conflicts);

        Assert.Equal("https://gh-proxy.com/https://github.com", env["GITHUB_ENDPOINT"]);
        Assert.Contains("GITHUB_ENDPOINT", conflicts);
    }

    [Fact]
    public void EnsureCacheDirs_CreatesDefaults()
    {
        var dir = Path.Combine(_tempRoot, "comfy");
        Directory.CreateDirectory(dir);
        var env = new Dictionary<string, string>();

        var created = LaunchEnvBuilder.EnsureCacheDirs(dir, env);

        Assert.Equal(2, created.Count);
        Assert.True(Directory.Exists(Path.Combine(dir, "triton-cache")));
        Assert.True(Directory.Exists(Path.Combine(dir, "tunableop-cache")));
        Assert.Equal(Path.Combine(dir, "triton-cache"), env["TRITON_CACHE_DIR"]);
    }

    [Fact]
    public void VenvDirFrom_DerivesVenv()
    {
        Assert.Equal(@"D:\AI\comfyui-rocm\.venv",
            LaunchEnvBuilder.VenvDirFrom(@"D:\AI\comfyui-rocm\.venv\Scripts\python.exe"));
    }

    [Fact]
    public void Config_RoundTrip()
    {
        var svc = new ConfigService(Path.Combine(_tempRoot, "cfg"));
        var config = new AppConfig
        {
            ComfyUiDir = @"D:\AI\comfyui-rocm",
            PythonExe = @"D:\AI\comfyui-rocm\.venv\Scripts\python.exe",
            ExtraArgs = "--verbose",
        };
        config.Args.Enable("enable-manager", true);
        config.CustomEnvVars["MIOPEN_FIND_MODE"] = "2";
        var customTemplate = new GpuTemplate
        {
            Id = "mine",
            Title = "我的模板",
            EnvSnapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["HACK"] = @"{comfyUiDir}\x" },
            ArgsSnapshot = new StructuredArgs(),
        };
        customTemplate.ArgsSnapshot.Enable("port", true);
        customTemplate.ArgsSnapshot.SetValue("port", "8188");
        config.CustomTemplates.Add(customTemplate);

        svc.Save(config);
        var loaded = svc.Load();

        Assert.Equal(config.ComfyUiDir, loaded.ComfyUiDir);
        Assert.Equal("--verbose", loaded.ExtraArgs);
        Assert.True(loaded.Args.IsEnabled("enable-manager"));
        Assert.Equal("2", loaded.CustomEnvVars["MIOPEN_FIND_MODE"]);

        var tpl = Assert.Single(loaded.CustomTemplates);
        Assert.Equal("我的模板", tpl.Title);
        Assert.True(tpl.IsCustom);
        Assert.Equal(@"{comfyUiDir}\x", tpl.EnvSnapshot!["HACK"]);
        Assert.True(tpl.ArgsSnapshot!.IsEnabled("port"));
        Assert.Equal("8188", tpl.ArgsSnapshot.Value("port"));
    }

    [Fact]
    public void Config_BrokenFileIsBackedUpAndDefaultsRestored()
    {
        var root = Path.Combine(_tempRoot, "broken");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "config.json"), "{ this is not json");

        var svc = new ConfigService(root);
        var loaded = svc.Load();

        Assert.Equal(string.Empty, loaded.ComfyUiDir);
        Assert.True(Directory.GetFiles(root, "config.broken.*.json").Length == 1);
    }

    [Fact]
    public void LogService_RingBufferRespectsCapacity()
    {
        var dir = Path.Combine(_tempRoot, "logs");
        var log = new LogService(dir, capacity: 5);
        for (var i = 0; i < 20; i++) log.Append($"line {i}");

        Assert.Equal(5, log.Count);
        Assert.Equal("line 15", log.Snapshot()[0]);
    }

    [Fact]
    public void LogService_RetentionDeletesOldest()
    {
        var dir = Path.Combine(_tempRoot, "logs2");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(dir, $"comfyui_2026-01-0{i + 1}_00-00-00.log"), new string('x', 1024));
        }

        var log = new LogService(dir);
        var deleted = log.ApplyRetention(retainCount: 2, retainTotalMB: 200);

        Assert.Equal(3, deleted);
        Assert.Equal(2, Directory.GetFiles(dir, "comfyui_*.log").Length);
    }

    [Fact]
    public void LaunchStates_TextMapping()
    {
        Assert.Equal("运行中", LaunchStates.Text(LaunchState.Running));
        Assert.Equal("已停止", LaunchStates.Text(LaunchState.Idle));
    }

    [Fact]
    public void AnsiParser_ParsesColors()
    {
        var runs = AnsiParser.Parse("\u001b[32mok\u001b[0m tail");
        Assert.Equal("green", runs[0].Color);
        Assert.Equal("ok", runs[0].Text);
        Assert.Equal("default", runs[1].Color);
        Assert.Equal(" tail", runs[1].Text);
    }

    [Fact]
    public void LogService_SnapshotPlainText_StripsAnsi()
    {
        var log = new LogService(Path.GetTempPath());
        log.Append("\u001b[32mok\u001b[0m tail");

        Assert.Equal("ok tail", log.SnapshotPlainText());
    }
}
