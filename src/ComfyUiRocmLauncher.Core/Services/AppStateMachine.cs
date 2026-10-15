using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 应用级状态机（SRS 2.4）：ComfyUI 启动流程与环境任务两条来源都向它汇报，界面只订阅这一处。
/// 取代此前 AppServices 与 LaunchService 各持一份状态、靠"互相同步"维持一致的写法。
/// </summary>
public sealed class AppStateMachine
{
    private LaunchState _launchState = LaunchState.Idle;
    private bool _envBusy;

    /// <summary>对外展示的状态：环境任务进行中恒为 BusyEnv，否则取启动流程状态。</summary>
    public LaunchState State { get; private set; } = LaunchState.Idle;

    public event Action<LaunchState>? StateChanged;

    /// <summary>
    /// 启动流程汇报（Idle / Starting / Running / Stopping）。
    /// 环境任务进行中时只记录、不改变展示状态（环境任务只在 Idle 下发起，二者不应互相冲掉）。
    /// </summary>
    public void ReportLaunch(LaunchState state)
    {
        _launchState = state;
        if (!_envBusy) Set(state);
    }

    /// <summary>环境任务忙碌位（TaskRunner 汇报）：置位进 BusyEnv，释放后回到启动流程状态。</summary>
    public void ReportEnvBusy(bool busy)
    {
        if (_envBusy == busy) return;
        _envBusy = busy;
        Set(busy ? LaunchState.BusyEnv : _launchState);
    }

    private void Set(LaunchState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }
}
