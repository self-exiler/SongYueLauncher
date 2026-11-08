using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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

        // 窗口最小尺寸与状态记忆的 640×480 下限保持一致（此前只有记忆侧过滤，窗口本身仍可缩得更小）。
        // WinAppSDK 1.7+ 内建最小尺寸属性，取代 75 行 P/Invoke 子类化的 WindowMinSize（审查 A9）
        if (AppWindow.Presenter is OverlappedPresenter windowPresenter)
        {
            windowPresenter.PreferredMinimumWidth = 640;
            windowPresenter.PreferredMinimumHeight = 480;
        }

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
        // 启动恒为主页（FR-9.3 改版）：不记忆上次页面。构造期给 SelectedItem 赋值不保证触发
        // SelectionChanged（ItemsRepeater 未就绪），因此高亮与导航都显式做一遍
        NavView.SelectedItem = NavView.MenuItems[0];
        NavigateToTag("HomePage");
        // 除显示模式事件外，尺寸变化也同步一次：最大化/还原这类路径下更稳，保证回到展开态一定重新打开面板
        NavView.SizeChanged += (_, _) =>
        {
            SyncPaneToggleVisibility();
            if (_drawerOpen) PositionOutputDrawer();
        };
        SyncPaneToggleVisibility();
        RefreshHeader();

        AppWindow.Closing += OnAppWindowClosing;
        App.Services.StateChanged += OnAppStateChanged;
        App.Services.Log.LineAppended += OnLogLineAppended;
        OutputDrawer.Dismissed += OnOutputDrawerDismissed;
        RegisterDrawerAccelerators();
        InitTray();
        _ = RefreshVersionFromGitAsync();
    }

    /// <summary>Esc 只收抽屉（Ctrl+` 走 RootLayout.KeyDown：反引号不是 VirtualKey 枚举值）。</summary>
    private void RegisterDrawerAccelerators()
    {
        var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += EscapeAccelerator_Invoked;
        RootLayout.KeyboardAccelerators.Add(escape);

        // KeyboardAccelerator 只接受 VirtualKey 枚举能映射的键，(VirtualKey)0xC0（VK_OEM_3）会让
        // XAML 加速器注册在首帧渲染时直接 fail-fast（0xC000027B），因此反引号改走路由事件。
        RootLayout.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), handledEventsToo: true);
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if ((int)e.Key != 0xC0) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        if ((ctrl & Windows.UI.Core.CoreVirtualKeyStates.Down) == 0) return;

        e.Handled = true;
        ToggleOutputDrawer();
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

    /// <summary>关闭确认状态机：弹窗期间取消关闭，用户选择“结束 Python 并关闭”后放行。</summary>
    private bool _closeAfterKill;

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // 无论最终是否关闭，先立即落盘窗口状态与待写配置。
        CaptureWindowState();
        App.Services.FlushSave();
        if (_closeAfterKill) return;

        var leftovers = FindLeftoverPython();
        if (leftovers.Length == 0 && App.Services.State == LaunchState.Idle) return;

        var xamlRoot = (Content as FrameworkElement)?.XamlRoot;
        if (xamlRoot is null)
        {
            // 拿不到 XamlRoot（理论上不发生）就无法弹窗：按直接关闭处理，不再拦截
            return;
        }

        args.Cancel = true;
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
        // venv 布局守卫：解释器不在 <root>\Scripts\python.exe 时两级父目录不是 venv 根
        // （系统 Python 会推出盘根），按前缀匹配会误杀整个盘符下的 python 进程
        if (!File.Exists(Path.Combine(venvRoot, "Scripts", "python.exe"))) return [];
        return ProcessService.FindPythonProcesses(venvRoot);
    }

    private async Task ShowCloseWarningAsync(Microsoft.UI.Xaml.XamlRoot xamlRoot, System.Diagnostics.Process[] leftovers)
    {
        // BusyEnv 期间 LaunchState == Idle：文案必须区分「ComfyUI 运行中」与「环境任务进行中」
        var busyEnv = App.Services.State == LaunchState.BusyEnv;
        var running = App.Services.State is LaunchState.Starting or LaunchState.Running;
        var message = running
            ? $"ComfyUI 仍在运行，且检测到 {leftovers.Length} 个 Python 进程。直接关闭启动器无法终止它们。"
            : busyEnv
                ? "环境任务（安装/更新）仍在进行，直接关闭会中断任务并可能留下半完成的环境。"
                : $"启动器未跟踪到运行中的 ComfyUI，但仍检测到 {leftovers.Length} 个由该环境拉起的 Python 进程。";

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "关闭启动器",
            Content = message,
            PrimaryButtonText = "结束并关闭",
            CloseButtonText = "取消关闭",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _closeAfterKill = true;
        if (busyEnv)
        {
            // 取消环境任务：TaskRunner 的令牌会终止其 uv/git 等子进程树（它们不叫 python，残留扫描扫不到）
            App.Services.Tasks.Cancel();
            App.Services.Log.Append("[启动器] 关闭前取消进行中的环境任务");
        }
        if (running)
            await App.Services.Launch.StopAsync();
        else if (!busyEnv)
            await ProcessService.KillProcessesAsync(leftovers);

        // StopAsync 依赖状态机，可能因状态与进程实际状态不一致而空转：兜底再扫一次并强杀
        await ProcessService.KillProcessesAsync(FindLeftoverPython());

        App.Services.Log.Append("[启动器] 关闭前结束残留 Python 进程");
        Close();
    }

    // ---------- 系统托盘（最小化收起，FR-9.6） ----------

    private TrayIcon? _tray;

    private void InitTray()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        _tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this), iconPath, "松月启动器", App.Services.Log.Append);
        _tray.MinimizeRequested += HideToTray;
        _tray.ShowRequested += RestoreFromTray;
        _tray.OutputRequested += OnTrayOutputRequested;
        _tray.ExitRequested += OnTrayExitRequested;
        Closed += (_, _) => _tray?.Dispose();
        // 托盘图标常驻：与窗口是否隐藏无关，仅在进程退出时随 Closed 注销
        _tray.Show();
    }

    /// <summary>最小化即收起：窗口与任务栏按钮一起隐藏，托盘图标始终保留。</summary>
    private void HideToTray()
    {
        // 投递的收起消息可能对上已经还原的窗口，状态不是最小化就不处理
        if (AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;

        AppWindow.Hide();
        _tray?.Show(); // 幂等：兜住启动时图标注册失败的情况，避免收起后无图标可恢复
    }

    private void RestoreFromTray()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        AppWindow.Show();
        Activate();
    }

    private void OnTrayOutputRequested()
    {
        RestoreFromTray();
        ShowOutputDrawer();
    }

    /// <summary>退出走正常关闭流程：窗口先恢复，运行中任务仍会弹既有确认框。</summary>
    private void OnTrayExitRequested()
    {
        RestoreFromTray();
        Close();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected) return;
        if (args.SelectedItem is not NavigationViewItem item) return;

        NavigateToTag(item.Tag?.ToString());
    }

    /// <summary>按导航项 Tag 切换页面实例（缓存复用）并同步页头标题；启动与点选共用。</summary>
    private void NavigateToTag(string? tag)
    {
        var pageType = tag switch
        {
            "EnvUpdatePage" => typeof(EnvUpdatePage),
            "PluginsPage" => typeof(PluginsPage),
            "ArgsPage" => typeof(ArgsPage),
            "EnvVarsPage" => typeof(EnvVarsPage),
            _ => typeof(HomePage),
        };

        if (!_pageCache.TryGetValue(pageType, out var page))
            _pageCache[pageType] = page = (Page)Activator.CreateInstance(pageType)!;

        if (!ReferenceEquals(RootFrame.Content, page))
        {
            RootFrame.Content = page;
            RootFrame.BackStack.Clear();
        }
        // 标题取菜单项 Content，不在这里重复一份映射
        PageTitle.Text = NavView.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), tag, StringComparison.Ordinal))?.Content?.ToString()
            ?? "主页";
        HideBreadcrumb();

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

        OutputDrawer.SetState(state);
        RefreshOutputIndicator();
    }

    // ---------- 任务输出抽屉（覆盖式，右侧滑出） ----------

    private bool _drawerOpen;
    private bool _hasUnseenTaskError;
    private DispatcherQueueTimer? _drawerAutoOpenTimer;

    private void OnAppStateChanged(LaunchState state) => DispatcherQueue.TryEnqueue(() =>
    {
        RefreshHeader();
        ScheduleDrawerAutoOpen(state);
    });

    /// <summary>环境任务开始后延迟片刻再弹出：包列表刷新这类亚秒级任务不为它闪一次抽屉。</summary>
    private void ScheduleDrawerAutoOpen(LaunchState state)
    {
        if (state != LaunchState.BusyEnv)
        {
            _drawerAutoOpenTimer?.Stop();
            return;
        }
        if (_drawerOpen) return;

        _drawerAutoOpenTimer ??= CreateDrawerAutoOpenTimer();
        _drawerAutoOpenTimer.Stop();
        _drawerAutoOpenTimer.Start();
    }

    private DispatcherQueueTimer CreateDrawerAutoOpenTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(700);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (App.Services.State == LaunchState.BusyEnv) ShowOutputDrawer();
        };
        return timer;
    }

    /// <summary>失败/取消只在入口按钮上标红点，不强制弹出打断用户。</summary>
    private void OnLogLineAppended(string line)
    {
        if (!line.StartsWith("[错误]", StringComparison.Ordinal)
            && !line.StartsWith("[已取消]", StringComparison.Ordinal)) return;

        _hasUnseenTaskError = true;
        if (!_drawerOpen) DispatcherQueue.TryEnqueue(RefreshOutputIndicator);
    }

    private void RefreshOutputIndicator()
    {
        if (App.Services.State == LaunchState.BusyEnv)
        {
            OutputIndicator.Fill = ThemeHelper.Brush("AccentFillColorDefaultBrush");
            OutputIndicator.Visibility = Visibility.Visible;
        }
        else if (_hasUnseenTaskError)
        {
            OutputIndicator.Fill = ThemeHelper.Brush("SystemFillColorCriticalBrush");
            OutputIndicator.Visibility = Visibility.Visible;
        }
        else
        {
            OutputIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void OutputDrawerButton_Click(object sender, RoutedEventArgs e) => ToggleOutputDrawer();

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_drawerOpen) return;
        args.Handled = true;
        CloseOutputDrawer();
    }

    private void ToggleOutputDrawer()
    {
        if (_drawerOpen) CloseOutputDrawer();
        else ShowOutputDrawer();
    }

    public void ShowOutputDrawer()
    {
        if (_drawerOpen) return;
        _hasUnseenTaskError = false;
        PositionOutputDrawer();
        OutputDrawerPopup.IsOpen = true;
    }

    public void CloseOutputDrawer()
    {
        if (_drawerOpen) _ = OutputDrawer.CloseAsync();
    }

    private void OutputDrawerPopup_Opened(object? sender, object e)
    {
        _drawerOpen = true;
        OutputDrawer.Open();
        RefreshOutputIndicator();
    }

    private void OnOutputDrawerDismissed(object? sender, EventArgs e)
    {
        _drawerOpen = false;
        OutputDrawerPopup.IsOpen = false;
        RefreshOutputIndicator();
    }

    /// <summary>抽屉只覆盖 NavigationView 区域，标题栏保持可拖动；位置按 NavView 在根布局中的原点换算。</summary>
    private void PositionOutputDrawer()
    {
        if (Content is not FrameworkElement root || NavView.ActualWidth <= 0) return;
        var origin = NavView.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
        OutputDrawerPopup.HorizontalOffset = origin.X;
        OutputDrawerPopup.VerticalOffset = origin.Y;
        OutputDrawer.SetHostSize(NavView.ActualWidth, NavView.ActualHeight);
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
