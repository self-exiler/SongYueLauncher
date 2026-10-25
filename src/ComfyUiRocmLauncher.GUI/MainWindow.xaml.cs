using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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

        // 窗口最小尺寸与状态记忆的 640×480 下限保持一致（此前只有记忆侧过滤，窗口本身仍可缩得更小）
        WindowMinSize.Apply(this, 640, 480);

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

        RestoreWindowState();
        NavView.SelectedItem = FindNavItem(App.Services.Settings.LastPage) ?? NavView.MenuItems[0];
        // 除显示模式事件外，尺寸变化也同步一次：最大化/还原这类路径下更稳，保证回到展开态一定重新打开面板
        NavView.SizeChanged += (_, _) => SyncPaneToggleVisibility();
        SyncPaneToggleVisibility();
        RefreshHeader();

        AppWindow.Closing += OnAppWindowClosing;
        App.Services.StateChanged += _ => DispatcherQueue.TryEnqueue(RefreshHeader);
        _ = RefreshVersionFromGitAsync();
    }

    /// <summary>标题栏版本徽章读取本地 git HEAD（FR-1.3），外部 pull 后也能显示真实版本。</summary>
    private async Task RefreshVersionFromGitAsync()
    {
        if (await App.Services.RefreshVersionFromGitAsync() is not null)
            DispatcherQueue.TryEnqueue(RefreshHeader);
    }

    // ---------- 窗口尺寸 / 位置记忆（FR-9） ----------

    /// <summary>恢复上次的窗口位置、大小与最大化状态；随后监听变化以便随时记住。</summary>
    private void RestoreWindowState()
    {
        var cfg = App.Services.Settings;
        try
        {
            if (cfg.WindowWidth >= 640 && cfg.WindowHeight >= 480)
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                    cfg.WindowX, cfg.WindowY, cfg.WindowWidth, cfg.WindowHeight));

            if (cfg.WindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.Maximize();
        }
        catch (Exception ex)
        {
            // 显示器改分辨率/拔掉外接屏时旧坐标可能非法：保持系统默认，不阻断启动
            App.Services.Log.Append($"[启动器] 警告：恢复窗口位置失败：{ex.Message}");
        }

        AppWindow.Changed += OnAppWindowChanged;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange) return;
        CaptureWindowState();
        App.Services.Save();
    }

    /// <summary>
    /// 记录窗口状态。最大化时不覆盖还原尺寸——否则取消最大化会回到全屏大小。
    /// </summary>
    private void CaptureWindowState()
    {
        var cfg = App.Services.Settings;
        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        cfg.WindowMaximized = maximized;
        if (maximized) return;

        var position = AppWindow.Position;
        var size = AppWindow.Size;
        if (size.Width < 640 || size.Height < 480) return; // 最小化/异常态不记录

        cfg.WindowX = position.X;
        cfg.WindowY = position.Y;
        cfg.WindowWidth = size.Width;
        cfg.WindowHeight = size.Height;
    }

    private NavigationViewItem? FindNavItem(string? tag) =>
        string.IsNullOrEmpty(tag)
            ? null
            : NavView.MenuItems.OfType<NavigationViewItem>()
                .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>关闭确认状态机：弹窗期间取消关闭，用户选择“结束 Python 并关闭”后放行。</summary>
    private bool _closeAfterKill;

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // 无论最终是否关闭，先立即落盘窗口状态与待写配置
        CaptureWindowState();
        App.Services.FlushSave();
        App.Services.Gateway.Shutdown();
        if (_closeAfterKill) return;

        var leftovers = FindLeftoverPython();

        if (leftovers.Length == 0 && App.Services.State == LaunchState.Idle) return;

        args.Cancel = true;
        var xamlRoot = (Content as FrameworkElement)?.XamlRoot;
        if (xamlRoot is null) return;

        _ = ShowCloseWarningAsync(xamlRoot, leftovers);
    }

    /// <summary>
    /// 扫出由本环境 venv 拉起的残留 Python 进程。
    /// 关闭流程要在「弹窗之前」和「结束后兜底」各扫一次，因此抽成一处，避免两处各自推导 venv 根目录。
    /// </summary>
    private static System.Diagnostics.Process[] FindLeftoverPython()
    {
        var pythonExe = App.Services.Settings.PythonExe;
        if (string.IsNullOrWhiteSpace(pythonExe)) return [];

        var venvRoot = LaunchEnvBuilder.VenvDirFrom(pythonExe);
        return string.IsNullOrEmpty(venvRoot) ? [] : ProcessService.FindPythonProcesses(venvRoot);
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

        // StopAsync 依赖状态机，可能因状态与进程实际状态不一致而空转：兜底再扫一次并强杀
        await ProcessService.KillProcessesAsync(FindLeftoverPython());

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
        HideBreadcrumb();

        // 记住上次停留的页面（FR-9）：存 Tag 而非序号，菜单顺序调整后仍能对上
        var navTag = item.Tag?.ToString() ?? string.Empty;
        if (!string.Equals(App.Services.Settings.LastPage, navTag, StringComparison.Ordinal))
        {
            App.Services.Settings.LastPage = navTag;
            App.Services.Save();
        }

        RefreshHeader();
    }

    private void RefreshHeader()
    {
        var state = App.Services.State;
        var text = LaunchStates.Text(state);
        StateTextBlock.Text = text;

        // 状态徽章配色：运行=成功绿 / 启动=提示黄 / 停止=危险红 / 环境任务=强调蓝。
        // 底色为状态色淡底，文字恒为「主题正文色」——此前用同状态色作前景，黄/绿在浅色下对比度不可控。
        var key = ThemeHelper.StateAccentKey(state);
        StateBadge.Background = key is null
            ? ThemeHelper.Brush("ControlFillColorSecondaryBrush")
            : ThemeHelper.Tint(key, 0.25);
        StateTextBlock.Foreground = ThemeHelper.Brush("TextFillColorPrimaryBrush");

        // 徽章对读屏只是「已停止」这样的裸文本，补上语义前缀
        AutomationProperties.SetName(StateBadge, "运行状态：" + text);
    }

    // ---------- 页头面包屑（二级页导航，Win11 设置风格） ----------
    private Action? _crumbBack;

    /// <summary>大标题切换为「父级 › 当前页」面包屑，点父级段执行 onBack；同时显示并启用导航返回按钮。</summary>
    public void ShowBreadcrumb(string parent, string current, Action onBack)
    {
        _crumbBack = onBack;
        CrumbParent.Content = parent;
        CrumbCurrent.Text = current;
        PageTitle.Visibility = Visibility.Collapsed;
        PageBreadcrumb.Visibility = Visibility.Visible;
        NavView.IsBackButtonVisible = NavigationViewBackButtonVisible.Visible;
        // 只设 Visible 不够：IsBackEnabled 默认为 false，返回按钮会是「可见但禁用」
        NavView.IsBackEnabled = true;
    }

    public void HideBreadcrumb()
    {
        if (PageBreadcrumb.Visibility != Visibility.Visible) return;
        _crumbBack = null;
        PageBreadcrumb.Visibility = Visibility.Collapsed;
        PageTitle.Visibility = Visibility.Visible;
        NavView.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        NavView.IsBackEnabled = false;
    }

    private void CrumbParent_Click(object sender, RoutedEventArgs e) => TryGoBack();

    /// <summary>返回按钮与面包屑共用同一条回退路径（二级页没有真实 Frame 历史）。</summary>
    private void TryGoBack()
    {
        if (_crumbBack is null) return;
        var back = _crumbBack;
        _crumbBack = null;
        back();
    }

    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) => TryGoBack();

    private void NavView_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args) =>
        ApplyDisplayMode(args.DisplayMode);

    /// <summary>启动时先按当前显示模式归位一次，避免窗口本来就窄却拿不到菜单按钮。</summary>
    private void SyncPaneToggleVisibility() => ApplyDisplayMode(NavView.DisplayMode);

    /// <summary>
    /// 按显示模式统一处理「菜单按钮可见性 + 面板开合」：
    /// 折叠态显示菜单按钮；展开态隐藏菜单按钮，并且必须把面板重新打开——
    /// 否则窄窗口收起过一次后，IsPaneOpen 会保持 false，最大化回到展开态时导航再也恢复不了。
    /// </summary>
    private void ApplyDisplayMode(NavigationViewDisplayMode mode)
    {
        var expanded = mode == NavigationViewDisplayMode.Expanded;
        NavView.IsPaneToggleButtonVisible = !expanded;
        if (expanded) NavView.IsPaneOpen = true;
    }
}
