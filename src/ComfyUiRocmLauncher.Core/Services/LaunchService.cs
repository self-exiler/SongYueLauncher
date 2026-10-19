using AiDiagnostics.Runtime;
using ComfyUiRocmLauncher.Core.Models;

namespace ComfyUiRocmLauncher.Core.Services;

/// <summary>
/// 启动编排（SRS FR-4.1）：校验 → 合成环境 → rocm-sdk 推导 → 追踪包同步 → 拉起 ComfyUI → 就绪检测。
/// </summary>
public sealed class LaunchService
{
    private const int StopGraceMs = 5000;
    private static readonly TimeSpan ShutdownRequestTimeout = TimeSpan.FromSeconds(2);

    private readonly ProcessService _process = new();
    private readonly CliArgsCatalog _catalog = CliArgsCatalog.Instance;
    private readonly AppStateMachine _stateMachine;

    /// <summary>本次启动推导出的服务地址（FR-4.3 同源），供 /shutdown 优雅停止使用。</summary>
    private string? _launchedUrl;

    public LaunchService(LogService log, AppStateMachine stateMachine)
    {
        Log = log;
        _stateMachine = stateMachine;
        _process.OutputReceived += line =>
        {
            Log.Append(line);
            OutputReceived?.Invoke(line);
        };
        // ComfyUI 崩溃或被外部关闭时复位状态，避免 UI 停留在"运行中"
        _process.Exited += () =>
        {
            if (_stateMachine.LaunchState is LaunchState.Starting or LaunchState.Running)
            {
                Log.Append("[启动器] 子进程已退出");
                Log.EndSession();
                SetState(LaunchState.Idle);
            }
        };
    }

    public LogService Log { get; }

    /// <summary>启动流程状态只存在状态机里，这里不再存第二份（S-12）。</summary>
    private LaunchState State => _stateMachine.LaunchState;

    public event Action<string>? OutputReceived;

    private void SetState(LaunchState state) => _stateMachine.ReportLaunch(state);

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

        // FR-8.9：ComfyUI-Manager 数据通道接管（每次启动重放，幂等；失败只降级为警告）
        var channelSync = ComfyManagerChannelService.Sync(config);
        Log.Append($"[启动器] {channelSync.Summary}");
        foreach (var warning in channelSync.Warnings)
            Log.Append($"[启动器] 警告：{warning}");

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
        _launchedUrl = ArgsBuilder.DeriveBrowserUrl(config.Args);

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

        // FR-4.2：优雅停止走 ComfyUI 自身的 /shutdown 端点。绝不再用 AttachConsole + CTRL_BREAK——
        // 组 0 信号会广播给控制台内全部进程，事件异步派发晚于保护窗口撤销，实测把启动器自己打死。
        var shutdownUrl = _launchedUrl is null ? null : _launchedUrl + "/shutdown";
        Log.Append(shutdownUrl is null
            ? "[启动器] 未记录服务地址，直接强杀进程树…"
            : $"[启动器] POST {shutdownUrl} 请求优雅停止…");
        var accepted = shutdownUrl is not null && await TryPostShutdownAsync(shutdownUrl);
        Log.Append(accepted
            ? "[启动器] /shutdown 已接受，等待进程退出（最多 5 秒）…"
            : "[启动器] /shutdown 未生效，直接强杀进程树…");

        await _process.StopAsync(accepted ? StopGraceMs : 0).ConfigureAwait(false);
        Log.EndSession();
        Log.Append("[启动器] 进程已退出");
        SetState(LaunchState.Idle);
    }

    private static async Task<bool> TryPostShutdownAsync(string url)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = ShutdownRequestTimeout };
            var response = await http.PostAsync(url, content: null).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException)
        {
            // 超时（OperationCanceledException）与服务不可达同样按「未生效」处理
            return false;
        }
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
