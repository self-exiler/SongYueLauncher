using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

        // 免打包运行时显式设置窗口/任务栏图标（打包模式由 exe 的 ApplicationIcon 覆盖）
        try
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        }
        catch
        {
            // 图标文件缺失或平台不支持时静默忽略，不影响启动
        }

        NavView.SelectedItem = NavView.MenuItems[0];
        RefreshHeader();

        AppWindow.Closing += (_, _) => App.Services.FlushSave();
        App.Services.StateChanged += _ => DispatcherQueue.TryEnqueue(RefreshHeader);
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
        var (key, tinted) = state switch
        {
            LaunchState.Running => ("SystemFillColorSuccessBrush", true),
            LaunchState.Starting => ("SystemFillColorCautionBrush", true),
            LaunchState.Stopping => ("SystemFillColorCriticalBrush", true),
            LaunchState.BusyEnv => ("AccentFillColorDefaultBrush", true),
            _ => (null, false),
        };
        StateBadge.Background = tinted
            ? ThemeHelper.Tint(key!, 0.25)
            : ThemeHelper.Brush("ControlFillColorSecondaryBrush");
        StateTextBlock.Foreground = tinted
            ? ThemeHelper.Brush(key!)
            : ThemeHelper.Brush("TextFillColorPrimaryBrush");

        VersionText.Text = "版本 " + App.Services.CurrentVersionHash();
    }
}
