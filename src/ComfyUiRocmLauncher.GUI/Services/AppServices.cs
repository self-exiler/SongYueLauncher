using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.GUI.Services;

/// <summary>
/// GUI 侧的服务容器：持有配置、日志、启动服务与全局状态机（SRS 2.4）。
/// 页面通过它访问 Core，不直接互相依赖。
/// </summary>
public sealed class AppServices
{
    public AppServices()
    {
        ConfigService = new ConfigService();
        Settings = ConfigService.Load();
        Log = new LogService(ConfigService.EnsureLogDirectory());
        Launch = new LaunchService(Log);
        Launch.StateChanged += s => SetState(s);
    }

    public ConfigService ConfigService { get; }

    public LogService Log { get; }

    public LaunchService Launch { get; }

    public AppConfig Settings { get; }

    public LaunchState State { get; private set; } = LaunchState.Idle;

    public event Action<LaunchState>? StateChanged;

    public void Save() => ConfigService.Save(Settings);

    public void SetState(LaunchState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    /// <summary>读取 .last_update_hash 前 10 位作为当前版本（FR-1.3）。</summary>
    public string CurrentVersionHash()
    {
        try
        {
            var file = Path.Combine(Settings.ComfyUiDir, ".last_update_hash");
            if (!File.Exists(file)) return "unknown";
            var text = File.ReadAllText(file).Trim();
            return text.Length <= 10 ? text : text[..10];
        }
        catch (IOException)
        {
            return "unknown";
        }
    }
}
