using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;
using ComfyUiRocmLauncher.GUI.Helpers;
using ComfyUiRocmLauncher.GUI.Views;

namespace ComfyUiRocmLauncher.GUI;

public sealed partial class MainWindow : Window
{
    /// <summary>页面实例缓存：导航只复用，避免 ArgsPage 每次导航重建 110 组控件（NFR-1）。</summary>
    private readonly Dictionary<Type, Page> _pageCache = new();

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // 免打包运行时需显式设置窗口图标（标题栏 + 任务栏 + Alt-Tab；exe 内嵌的 ApplicationIcon 只覆盖
        // 资源管理器里的文件图标）。图标随程序发布（csproj 的 CopyToPublishDirectory），缺失时记日志而非静默。
        var appIcon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        try
        {
            if (File.Exists(appIcon)) AppWindow.SetIcon(appIcon);
            else App.Services.Log.Append($"[启动器] 警告：未找到窗口图标 {appIcon}，将回退为 exe 内嵌图标");
        }
        catch (Exception ex)
        {
            // 平台不支持或图标损坏时不阻断启动
            App.Services.Log.Append($"[启动器] 警告：设置窗口图标失败：{ex.Message}");
        }

        NavView.SelectedItem = NavView.MenuItems[0];
        RefreshHeader();

        AppWindow.Closing += OnAppWindowClosing;
        App.Services.StateChanged += _ => DispatcherQueue.TryEnqueue(RefreshHeader);
    }

    /// <summary>关闭确认状态机：弹窗期间取消关闭，用户选择“结束 Python 并关闭”后放行。</summary>
    private bool _closeAfterKill;

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // 无论最终是否关闭，先立即落盘待写配置
        App.Services.FlushSave();
        if (_closeAfterKill) return;

        var pythonExe = App.Services.Settings.PythonExe;
        var venvRoot = string.IsNullOrWhiteSpace(pythonExe)
            ? string.Empty
            : LaunchEnvBuilder.VenvDirFrom(pythonExe);
        var leftovers = string.IsNullOrEmpty(venvRoot)
            ? []
            : ProcessService.FindPythonProcesses(venvRoot);

        if (leftovers.Length == 0 && App.Services.State == LaunchState.Idle) return;

        args.Cancel = true;
        var xamlRoot = (Content as FrameworkElement)?.XamlRoot;
        if (xamlRoot is null) return;

        _ = ShowCloseWarningAsync(xamlRoot, leftovers);
    }

    private async Task ShowCloseWarningAsync(Microsoft.UI.Xaml.XamlRoot xamlRoot, System.Diagnostics.Process[] leftovers)
    {
        var running = App.Services.State != LaunchState.Idle;
        var message = running
            ? $"ComfyUI 仍在运行，且检测到 {leftovers.Length} 个 Python 进程。直接关闭启动器无法终止它们。"
            : $"启动器未跟踪到运行中的 ComfyUI，但仍检测到 {leftovers.Length} 个由该环境拉起的 Python 进程。";

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "关闭启动器",
            Content = message,
            PrimaryButtonText = "结束 Python 并关闭",
            CloseButtonText = "取消关闭",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _closeAfterKill = true;
        if (running)
            await App.Services.Launch.StopAsync();
        else
            await ProcessService.KillProcessesAsync(leftovers);

        // StopAsync 依赖状态机，可能因状态与进程实际状态不一致而空转：兜底再扫一次
        var pythonExe = App.Services.Settings.PythonExe;
        var venvRoot = string.IsNullOrWhiteSpace(pythonExe) ? string.Empty : LaunchEnvBuilder.VenvDirFrom(pythonExe);
        if (!string.IsNullOrEmpty(venvRoot))
            await ProcessService.KillProcessesAsync(ProcessService.FindPythonProcesses(venvRoot));

        App.Services.Log.Append("[启动器] 关闭前结束残留 Python 进程");
        Close();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected) return;
        if (args.SelectedItem is not NavigationViewItem item) return;

        var pageType = item.Tag?.ToString() switch
        {
            "EnvUpdatePage" => typeof(EnvUpdatePage),
            "ConfigPage" => typeof(ConfigPage),
            "ArgsPage" => typeof(ArgsPage),
            "EnvVarsPage" => typeof(EnvVarsPage),
            "LogPage" => typeof(LogPage),
            _ => typeof(HomePage),
        };

        if (!_pageCache.TryGetValue(pageType, out var page))
            _pageCache[pageType] = page = (Page)Activator.CreateInstance(pageType)!;

        if (!ReferenceEquals(RootFrame.Content, page))
        {
            RootFrame.Content = page;
            RootFrame.BackStack.Clear();
        }
        PageTitle.Text = item.Content?.ToString() ?? string.Empty;
        RefreshHeader();
    }

    private void RefreshHeader()
    {
        var state = App.Services.State;
        StateTextBlock.Text = LaunchStates.Text(state);

        // 状态徽章配色：运行=成功绿 / 启动=提示黄 / 停止=危险红 / 环境任务=强调蓝（浅色底 + 同色文字）
        var key = ThemeHelper.StateAccentKey(state);
        StateBadge.Background = key is null
            ? ThemeHelper.Brush("ControlFillColorSecondaryBrush")
            : ThemeHelper.Tint(key, 0.25);
        StateTextBlock.Foreground = key is null
            ? ThemeHelper.Brush("TextFillColorPrimaryBrush")
            : ThemeHelper.Brush(key);

        VersionText.Text = "版本 " + App.Services.CurrentVersionHash();
    }
}
