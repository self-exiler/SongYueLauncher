namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 长任务调度（SRS FR-8.1）：全局单任务互斥（不排队、不并行）、取消、流式输出。
/// 组合流程（如 torch 更新的多步骤）用 StartAsync/Complete 包裹整段，内部多次 Run 共享同一取消令牌。
/// Core 侧不依赖 UI；状态机置位（BusyEnv）由 GUI 在 Start/Complete 回调中处理。
/// </summary>
public sealed class TaskRunner
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;

    /// <summary>是否已有任务在跑（与任务槽同源，不再维护独立标志位）。</summary>
    public bool IsBusy => _cts is not null;

    /// <summary>忙碌状态变化（供 GUI 同步状态机与按钮启用）。</summary>
    public event Action<bool>? BusyChanged;

    /// <summary>当前任务取消令牌（无任务时为 None）。</summary>
    public CancellationToken Token => _cts?.Token ?? CancellationToken.None;

    /// <summary>尝试占用全局任务槽；已被占用返回 false（调用方应放弃本次操作）。</summary>
    public bool TryStart()
    {
        // _gate.Wait(0) 本身就是原子的互斥判定，无需再看 IsBusy
        if (!_gate.Wait(0)) return false;
        _cts = new CancellationTokenSource();
        BusyChanged?.Invoke(true);
        return true;
    }

    /// <summary>释放任务槽。必须与成功的 TryStart 成对调用（放 finally）；未被占用时为空操作。</summary>
    public void Complete()
    {
        if (_cts is null) return;
        _cts.Dispose();
        _cts = null;
        _gate.Release();
        BusyChanged?.Invoke(false);
    }

    /// <summary>取消当前任务 = 终止其子进程（Run 内部据此 Kill 进程树）。</summary>
    public void Cancel()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>在当前任务槽内执行一条命令，输出流式回调，返回退出码。</summary>
    public Task<int> Run(string fileName, string arguments, string workingDirectory, Action<string>? onOutput = null) =>
        ProcessService.RunCommandAsync(fileName, arguments, workingDirectory, onOutput, Token);
}
