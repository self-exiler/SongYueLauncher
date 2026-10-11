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

    public bool IsBusy { get; private set; }

    /// <summary>忙碌状态变化（供 GUI 同步状态机与按钮启用）。</summary>
    public event Action<bool>? BusyChanged;

    /// <summary>当前任务取消令牌（无任务时为 None）。</summary>
    public CancellationToken Token => _cts?.Token ?? CancellationToken.None;

    /// <summary>尝试占用全局任务槽；已被占用返回 false（调用方应放弃本次操作）。</summary>
    public async Task<bool> StartAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return false;
        _cts = new CancellationTokenSource();
        SetBusy(true);
        return true;
    }

    /// <summary>释放任务槽。必须与成功的 StartAsync 成对调用（放 finally）。</summary>
    public void Complete()
    {
        _cts?.Dispose();
        _cts = null;
        SetBusy(false);
        _gate.Release();
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

    private void SetBusy(bool busy)
    {
        if (IsBusy == busy) return;
        IsBusy = busy;
        BusyChanged?.Invoke(busy);
    }
}
