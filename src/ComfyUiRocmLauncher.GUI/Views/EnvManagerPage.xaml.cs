using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>环境管理页（SRS FR-6）：三个更新任务 + 内嵌输出 + 取消 + 运行中保护。</summary>
public sealed partial class EnvManagerPage : Page
{
    private CancellationTokenSource? _cts;

    public EnvManagerPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private AppConfig Settings => App.Services.Settings;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Services.StateChanged += OnStateChanged;
        RefreshGuard();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Services.StateChanged -= OnStateChanged;
    }

    private void OnStateChanged(LaunchState state) => DispatcherQueue.TryEnqueue(RefreshGuard);

    private void RefreshGuard()
    {
        var busy = App.Services.State != LaunchState.Idle;
        GuardBanner.IsOpen = busy;
        RocmButton.IsEnabled = !busy;
        DepsButton.IsEnabled = !busy;
        UpdateButton.IsEnabled = !busy;
    }

    private void Append(string line)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TaskOutputText.Text += line + Environment.NewLine;
            TaskScroll.ChangeView(null, TaskScroll.ExtentHeight, null);
        });
    }

    private async Task RunAsync(string title, Func<CancellationToken, Task> work)
    {
        _cts = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        TaskStatusText.Text = title + " 进行中…";
        App.Services.SetState(LaunchState.BusyEnv);
        RefreshGuard();

        try
        {
            await work(_cts.Token);
            TaskStatusText.Text = title + " 完成";
        }
        catch (OperationCanceledException)
        {
            Append("x 已取消：当前子进程已终止");
            TaskStatusText.Text = title + " 已取消";
        }
        catch (Exception ex)
        {
            Append("x 失败：" + ex.Message);
            TaskStatusText.Text = title + " 失败";
        }
        finally
        {
            CancelButton.IsEnabled = false;
            _cts = null;
            App.Services.SetState(LaunchState.Idle);
            RefreshGuard();
            App.Services.Log.ApplyRetention(Settings.LogRetainCount, Settings.LogRetainTotalMB);
        }
    }

    private async void RocmUpdate_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("ROCm / PyTorch 更新", ct => UpdateService.UpdateRocmAsync(Settings, Append, ct));

    private async void Deps_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("依赖安装", async ct =>
        {
            var changes = await UpdateService.InstallDependenciesAsync(Settings, MirrorCheck.IsChecked == true, Append, ct);
            Append("变更包列表：");
            foreach (var change in changes) Append("  " + change);
        });

    private async void ProgramUpdate_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("程序更新",
            ct => UpdateService.UpdateProgramAsync(Settings, ModeFull.IsChecked == true, Append, ct));

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void ClearOutput_Click(object sender, RoutedEventArgs e) => TaskOutputText.Text = string.Empty;
}
