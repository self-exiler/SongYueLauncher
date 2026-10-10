using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;
using ComfyUiRocmLauncher.GUI.Views;

namespace ComfyUiRocmLauncher.GUI;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        NavView.SelectedItem = NavView.MenuItems[0];
        RefreshHeader();

        AppWindow.Closing += (_, _) => App.Services.FlushSave();
        App.Services.StateChanged += _ => DispatcherQueue.TryEnqueue(RefreshHeader);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;

        var pageType = item.Tag?.ToString() switch
        {
            "ArgsPage" => typeof(ArgsPage),
            "EnvVarsPage" => typeof(EnvVarsPage),
            "LogPage" => typeof(LogPage),
            "EnvManagerPage" => typeof(EnvManagerPage),
            _ => typeof(HomePage),
        };

        RootFrame.Navigate(pageType);
        PageTitle.Text = item.Content?.ToString() ?? string.Empty;
        RefreshHeader();
    }

    private void RefreshHeader()
    {
        var state = App.Services.State;
        StateTextBlock.Text = LaunchStates.Text(state);

        // 状态徽章配色：运行绿 / 启动黄 / 停止红 / 环境任务蓝（浅色底 + 同色文字）
        var (color, tinted) = state switch
        {
            LaunchState.Running => (Microsoft.UI.Colors.ForestGreen, true),
            LaunchState.Starting => (Microsoft.UI.Colors.Goldenrod, true),
            LaunchState.Stopping => (Microsoft.UI.Colors.IndianRed, true),
            LaunchState.BusyEnv => (Microsoft.UI.Colors.SteelBlue, true),
            _ => (Microsoft.UI.Colors.Transparent, false),
        };
        StateBadge.Background = tinted
            ? new SolidColorBrush(color) { Opacity = 0.25 }
            : (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"];
        StateTextBlock.Foreground = tinted
            ? new SolidColorBrush(color)
            : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

        VersionText.Text = "版本 " + App.Services.CurrentVersionHash();
    }
}
