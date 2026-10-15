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

        // 单一状态机：启动流程与环境任务都汇报到这里，界面只订阅它（SRS 2.4）
        Machine = new AppStateMachine();
        Machine.StateChanged += state => StateChanged?.Invoke(state);

        Launch = new LaunchService(Log, Machine);

        Tasks = new TaskRunner();
        Tasks.BusyChanged += Machine.ReportEnvBusy;

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

    /// <summary>应用状态机（启动流程 + 环境任务忙碌位的唯一真相）。</summary>
    public AppStateMachine Machine { get; }

    /// <summary>对外展示状态：直接取状态机，不再由两处状态互相同步。</summary>
    public LaunchState State => Machine.State;

    /// <summary>转发状态机的状态变化（界面只订阅这一处）。</summary>
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

    /// <summary>读取 .last_update_hash 前 10 位作为当前版本（FR-1.3）。</summary>
    public string CurrentVersionHash() =>
        RepoService.ShortHash(RepoService.ReadLocalHash(Settings.ComfyUiDir));
}
