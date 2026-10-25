using Microsoft.UI.Dispatching;
using ComfyUiRocmLauncher.Core.Ai;
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

        // AI 智能分析：网关安装/生命周期（SRS v2.0 FR-5.6）+ 诊断门面
        Gateway = new AiGatewayService(
            Log,
            Path.Combine(ConfigService.RootDirectoryPath, "our-free-model"),
            () => Settings.AiGatewayPort,
            key =>
            {
                Settings.AiGatewayApiKey = key;
                Save();
            });
        Ai = new ComfyUiAiService(() => new AiDiagnostics.GatewayOptions
        {
            BaseUrl = $"http://127.0.0.1:{Settings.AiGatewayPort}",
            ApiKey = Settings.AiGatewayApiKey,
        });

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

    /// <summary>AI 网关服务（dsh-our-free-model 独立服务）的安装与生命周期（SRS v2.0 FR-5.6）。</summary>
    public AiGatewayService Gateway { get; }

    /// <summary>AI 智能分析门面（SRS v2.0 FR-5.6~5.9）。</summary>
    public ComfyUiAiService Ai { get; }

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

    private Task<string?>? _gitHeadRefresh;

    /// <summary>
    /// 当前版本显示（FR-1.3）：.last_update_hash → comfyui_version.py → unknown。
    /// git 检出的 HEAD 由后台刷新回填，hash 文件在这里只是同步缓存。
    /// </summary>
    public string CurrentVersionHash()
    {
        var hash = RepoService.ReadLocalHash(Settings.ComfyUiDir);
        if (!string.IsNullOrEmpty(hash)) return RepoService.ShortHash(hash);
        var version = RepoService.ReadComfyVersion(Settings.ComfyUiDir);
        return version is null ? "unknown" : "v" + version;
    }

    /// <summary>
    /// git 检出每次读取本地 HEAD——用户可能在外部 <c>git pull</c>，只看 hash 文件会显示过期版本。
    /// 并发调用共用同一个查询；返回显示值，非 git 检出或查询失败返回 null。
    /// </summary>
    public Task<string?> RefreshVersionFromGitAsync()
    {
        if (!RepoService.IsGitCheckout(Settings.ComfyUiDir)) return Task.FromResult<string?>(null);
        if (_gitHeadRefresh is { IsCompleted: false }) return _gitHeadRefresh;
        return _gitHeadRefresh = RepoService.DetectGitHeadAsync(Settings);
    }
}
