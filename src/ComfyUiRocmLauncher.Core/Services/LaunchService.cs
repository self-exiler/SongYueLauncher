using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 启动编排（SRS FR-4.1）：校验 → 合成环境 → rocm-sdk 推导 → 追踪包同步 → 拉起 ComfyUI → 就绪检测。
/// </summary>
public sealed class LaunchService
{
    private readonly ProcessService _process = new();
    private readonly CliArgsCatalog _catalog = CliArgsCatalog.Instance;

    public LaunchService(LogService log)
    {
        Log = log;
        _process.OutputReceived += line =>
        {
            Log.Append(line);
            OutputReceived?.Invoke(line);
        };
        // ComfyUI 崩溃或被外部关闭时复位状态，避免 UI 停留在"运行中"
        _process.Exited += () =>
        {
            if (State is LaunchState.Starting or LaunchState.Running)
            {
                Log.Append("[启动器] 子进程已退出");
                Log.EndSession();
                SetState(LaunchState.Idle);
            }
        };
    }

    public LogService Log { get; }

    public LaunchState State { get; private set; } = LaunchState.Idle;

    public event Action<LaunchState>? StateChanged;

    public event Action<string>? OutputReceived;

    private void SetState(LaunchState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    /// <summary>启动 ComfyUI；返回是否检测到就绪。失败时状态复位为 Idle 并重抛。</summary>
    public async Task<bool> StartAsync(AppConfig config, TimeSpan readyTimeout, CancellationToken cancellationToken = default)
    {
        if (State != LaunchState.Idle) return false;
        try
        {
            return await StartCoreAsync(config, readyTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 异常（如 uv 缺失、包同步失败）必须复位，否则 State 卡在 Starting，之后永远无法启动
            await _process.StopAsync().ConfigureAwait(false);
            Log.EndSession();
            SetState(LaunchState.Idle);
            throw;
        }
    }

    private async Task<bool> StartCoreAsync(AppConfig config, TimeSpan readyTimeout, CancellationToken cancellationToken)
    {
        var comfyDir = config.ComfyUiDir;
        var mainPy = Path.Combine(comfyDir, "main.py");

        if (!File.Exists(config.PythonExe))
            throw new FileNotFoundException($"未找到 Python 解释器：{config.PythonExe}");
        if (!File.Exists(mainPy))
            throw new FileNotFoundException($"未找到 main.py：{mainPy}");

        SetState(LaunchState.Starting);
        Log.Append("[启动器] 校验 python.exe / main.py … OK");

        var rocmRoot = await RunRocmSdkStepsAsync(config).ConfigureAwait(false);

        var env = LaunchEnvBuilder.Build(config, rocmRoot, out var conflicts);
        foreach (var key in conflicts)
            Log.Append($"[启动器] 警告：自定义环境变量 {key} 与固定注入层冲突，已忽略");

        var created = LaunchEnvBuilder.EnsureCacheDirs(comfyDir, env);
        foreach (var dir in created) Log.Append($"[启动器] 已创建缓存目录：{dir}");

        var tokens = ArgsBuilder.Build(config.Args, _catalog, config.ExtraArgs);
        var arguments = "main.py " + CommandLine.Join(tokens);
        var logFile = Log.StartSession();
        // FR-5.4 磁盘保护：每次开新会话前清理超限的旧日志
        Log.ApplyRetention(config.LogRetainCount, config.LogRetainTotalMB);
        Log.Append($"[启动器] 会话日志：{logFile}");
        Log.Append($"[启动器] spawn: {config.PythonExe} {arguments}");

        _process.Start(config.PythonExe, arguments, comfyDir, env);

        var ready = await WaitForReadyAsync(config, readyTimeout, cancellationToken).ConfigureAwait(false);
        if (ready)
        {
            SetState(LaunchState.Running);
        }
        else if (_process.IsRunning)
        {
            // SRS 2.4：就绪超时但进程仍在 → Running（就绪标志可能不出现，如 --disable-auto-launch 场景）
            Log.Append("[启动器] 等待就绪超时，进程仍在运行，状态置为运行中");
            SetState(LaunchState.Running);
        }
        else
        {
            Log.Append("[启动器] 进程在等待就绪期间已退出");
            SetState(LaunchState.Idle);
        }
        return ready;
    }

    public async Task StopAsync()
    {
        if (State is not (LaunchState.Starting or LaunchState.Running)) return;
        SetState(LaunchState.Stopping);
        Log.Append("[启动器] 发送 CTRL_BREAK_EVENT 到子进程组，等待最多 5 秒…");
        await _process.StopAsync().ConfigureAwait(false);
        Log.EndSession();
        Log.Append("[启动器] 进程已退出");
        SetState(LaunchState.Idle);
    }

    private async Task<string?> RunRocmSdkStepsAsync(AppConfig config)
    {
        var sdk = LaunchEnvBuilder.RocmSdkExeFrom(config.PythonExe);
        if (!File.Exists(sdk))
        {
            Log.Append($"[启动器] 警告：未找到 {sdk}，跳过 rocm-sdk init 与 HIP_PATH 推导");
            return null;
        }

        var initCode = await ProcessService.RunCommandAsync(
            sdk, "init", config.ComfyUiDir, Log.Append).ConfigureAwait(false);
        if (initCode != 0) Log.Append("[启动器] 警告：rocm-sdk init 返回非零（不阻断启动）");

        var captured = new List<string>();
        var code = await ProcessService.RunCommandAsync(
            sdk, "path --root", config.ComfyUiDir, line =>
            {
                captured.Add(line);
                Log.Append(line);
            }).ConfigureAwait(false);

        var root = captured.Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        if (code != 0 || string.IsNullOrEmpty(root))
        {
            Log.Append("[启动器] 警告：rocm-sdk path --root 未取到结果（不阻断启动）");
            return null;
        }
        return root;
    }

    private async Task<bool> WaitForReadyAsync(AppConfig config, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var port = ArgsBuilder.ResolvePort(config.Args);
        var deadline = DateTime.UtcNow + timeout;
        var ready = false;

        void Handler(string line)
        {
            if (line.Contains("To see the GUI go to", StringComparison.OrdinalIgnoreCase)) ready = true;
        }

        // 就绪判定：输出出现 ComfyUI 的提示行，或端口可连通
        OutputReceived += Handler;
        try
        {
            while (!ready && DateTime.UtcNow < deadline)
            {
                if (!_process.IsRunning) break;
                if (await IsPortOpenAsync(port).ConfigureAwait(false))
                {
                    ready = true;
                    break;
                }
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }
            return ready;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            OutputReceived -= Handler;
        }
    }

    private static async Task<bool> IsPortOpenAsync(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var task = client.ConnectAsync("127.0.0.1", port);
            var completed = await Task.WhenAny(task, Task.Delay(300)).ConfigureAwait(false);
            return completed == task && client.Connected;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
}
