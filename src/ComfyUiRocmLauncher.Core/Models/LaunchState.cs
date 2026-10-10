namespace ComfyUiRocmLauncher.Core.Models;

/// <summary>
/// 全局状态机（SRS 2.4）。
/// </summary>
public enum LaunchState
{
    Idle,
    Starting,
    Running,
    Stopping,
    BusyEnv,
}

public static class LaunchStates
{
    /// <summary>状态中文名，供 UI 直接显示。</summary>
    public static string Text(LaunchState state) => state switch
    {
        LaunchState.Idle => "已停止",
        LaunchState.Starting => "启动中",
        LaunchState.Running => "运行中",
        LaunchState.Stopping => "停止中",
        LaunchState.BusyEnv => "环境任务中",
        _ => "未知",
    };
}
