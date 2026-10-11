using Microsoft.UI.Dispatching;
using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.GUI.Services;

/// <summary>
/// GUI 侧的服务容器：持有配置、日志、启动服务与全局状态机（SRS 2.4）。
/// 页面通过它访问 Core，不直接互相依赖。
/// </summary>
public sealed class AppServices
{
    private readonly DispatcherQueueTimer _saveTimer;
    private bool _savePending;

    public AppServices()
    {
        ConfigService = new ConfigService();
        Settings = ConfigService.Load();
        Log = new LogService(ConfigService.EnsureLogDirectory());
        Launch = new LaunchService(Log);
        Launch.StateChanged += s => SetState(s);

        Tasks = new TaskRunner();
        Tasks.BusyChanged += busy =>
        {
            // 环境任务仅在 Idle 下发起（FR-8.1 互斥）：置 BusyEnv / 完成后回 Idle
            if (busy) SetState(LaunchState.BusyEnv);
            else if (State == LaunchState.BusyEnv) SetState(LaunchState.Idle);
        };

        _saveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => FlushSave();
    }

    public ConfigService ConfigService { get; }

    public LogService Log { get; }

    public LaunchService Launch { get; }

    /// <summary>环境任务调度器（全局单任务互斥，供环境与更新页使用）。</summary>
    public TaskRunner Tasks { get; }

    public AppConfig Settings { get; }

    /// <summary>展示状态：启动流程以 LaunchService 为权威经事件同步，环境任务经 TaskRunner.BusyChanged 置位。</summary>
    public LaunchState State { get; private set; } = LaunchState.Idle;

    public event Action<LaunchState>? StateChanged;

    /// <summary>防抖保存：连续编辑（如文本框逐字符）合并为一次落盘。</summary>
    public void Save()
    {
        _savePending = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>立即落盘待写配置（窗口关闭前调用）；无待写变更时为空操作。</summary>
    public void FlushSave()
    {
        _saveTimer.Stop();
        if (!_savePending) return;
        _savePending = false;
        try
        {
            ConfigService.Save(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Append($"[启动器] 警告：配置写入失败：{ex.Message}");
        }
    }

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
