using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.GUI.Helpers;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>
/// 全局任务输出抽屉（覆盖式，右侧滑出）：渲染 <see cref="Core.Services.LogService"/> 的单一缓冲，
/// 环境任务、插件安装、ComfyUI 运行输出都汇到这里，取代原来各自页面里的定高控制台与独立日志页。
/// </summary>
public sealed partial class OutputDrawer : UserControl
{
    private const int MaxRenderedLines = 3000;

    /// <summary>抽屉宽度下限（DIP）；上限按宿主宽度的比例封顶（FR-5.1）。</summary>
    private const double MinDrawerWidth = 320;

    /// <summary>首次使用时的宽度比例：与旧版 560 DIP 在常见窗口宽度下的观感一致。</summary>
    private const double DefaultWidthRatio = 0.36;
    private const double MinWidthRatio = 0.1;
    private const double MaxWidthRatio = 0.85;

    private readonly BoundedAnsiConsole _console;
    private bool _open;
    private bool _closing;
    private double _hostWidth;
    private bool _resizing;
    private double _resizeStartX;
    private double _resizeStartWidth;

    /// <summary>滑出动画结束、Popup 可以收回时触发。</summary>
    public event EventHandler? Dismissed;

    public OutputDrawer()
    {
        InitializeComponent();
        _console = new BoundedAnsiConsole(LogText, MaxRenderedLines);
        // 开关没有可见标题，补自动化名称；旁边「自动滚动」文本对读屏不构成标签关联
        AutomationProperties.SetName(AutoScrollToggle, "自动滚动");
        RetentionText.Text =
            $"磁盘保护：保留最近 {App.Services.Settings.LogRetainCount} 份，总大小超 {App.Services.Settings.LogRetainTotalMB} MB 从最旧开始删除";
    }

    /// <summary>窗口状态变化时同步工具条上的任务状态文字。</summary>
    public void SetState(LaunchState state) => TaskHintText.Text = LaunchStates.Text(state);

    /// <summary>抽屉覆盖 NavigationView 区域：由宿主按窗口尺寸同步，宽度按记忆的比例跟随。</summary>
    public void SetHostSize(double width, double height)
    {
        if (width <= 0 || height <= 0) return;

        Host.Width = width;
        Host.Height = height;
        _hostWidth = width;

        var ratio = App.Services.Settings.TaskDrawerWidthRatio;
        if (!double.IsFinite(ratio) || ratio <= 0) ratio = DefaultWidthRatio;
        ApplyWidth(width * Math.Clamp(ratio, MinWidthRatio, MaxWidthRatio));
    }

    /// <summary>应用抽屉宽度并同步工具条方向；宽度按宿主尺寸与比例上限收敛。</summary>
    private void ApplyWidth(double width)
    {
        var lower = Math.Min(MinDrawerWidth, _hostWidth);
        var upper = Math.Min(_hostWidth, Math.Max(lower, _hostWidth * MaxWidthRatio));
        var drawerWidth = Math.Clamp(width, lower, upper);

        DrawerPanel.Width = drawerWidth;

        // 窄抽屉把工具条改为纵向，避免按钮横向溢出
        var narrow = drawerWidth < 520;
        ToolbarRow.Orientation = narrow ? Orientation.Vertical : Orientation.Horizontal;
        AutoScrollPanel.Orientation = narrow ? Orientation.Vertical : Orientation.Horizontal;
        ActionsPanel.Orientation = narrow ? Orientation.Vertical : Orientation.Horizontal;
    }

    // ---------- 左缘拖拽调整宽度 ----------

    private void ResizeHandle_PointerEntered(object sender, PointerRoutedEventArgs e) => ResizeVisual.Opacity = 0.55;

    private void ResizeHandle_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing) ResizeVisual.Opacity = 0;
    }

    private void ResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_open || !e.GetCurrentPoint(ResizeHandle).Properties.IsLeftButtonPressed) return;

        _resizing = true;
        _resizeStartX = e.GetCurrentPoint(Host).Position.X;
        _resizeStartWidth = DrawerPanel.Width;
        ResizeHandle.CapturePointer(e.Pointer);
        ResizeVisual.Opacity = 0.55;
        e.Handled = true;
    }

    private void ResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing) return;

        // 抽屉右对齐：指针向左移动（X 变小）即加宽
        var x = e.GetCurrentPoint(Host).Position.X;
        ApplyWidth(_resizeStartWidth - (x - _resizeStartX));
        e.Handled = true;
    }

    private void ResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing) return;

        _resizing = false;
        ResizeHandle.ReleasePointerCapture(e.Pointer);
        ResizeVisual.Opacity = 0;
        PersistWidthRatio();
        e.Handled = true;
    }

    private void ResizeHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing) return;

        _resizing = false;
        ResizeVisual.Opacity = 0;
        PersistWidthRatio();
    }

    /// <summary>宽度以「占宿主宽度的比例」落盘，窗口大小变化或下次启动时按比例还原（FR-5.1）。</summary>
    private void PersistWidthRatio()
    {
        if (_hostWidth <= 0) return;
        App.Services.Settings.TaskDrawerWidthRatio = DrawerPanel.Width / _hostWidth;
        App.Services.Save();
    }

    /// <summary>Popup 已打开时调用：重建缓冲并滑入。重复调用为空操作。</summary>
    public void Open()
    {
        if (_open) return;
        _open = true;
        _closing = false;

        CopyStatusText.Text = string.Empty;
        SetState(App.Services.State);
        Rebuild();

        App.Services.Log.LineAppended += OnLineAppended;
        App.Services.Log.Cleared += OnLogCleared;

        Slide.X = DrawerPanel.Width;
        Animate(toX: 0, toScrimOpacity: 1, milliseconds: 220, EasingMode.EaseOut, completed: null);
    }

    /// <summary>滑出后触发 <see cref="Dismissed"/>，宿主再收回 Popup。未打开时立即完成。</summary>
    public Task CloseAsync()
    {
        if (!_open || _closing) return Task.FromResult(true);

        _closing = true;
        Unsubscribe();

        var done = new TaskCompletionSource<bool>();
        Animate(toX: DrawerPanel.Width, toScrimOpacity: 0, milliseconds: 160, EasingMode.EaseIn, () =>
        {
            _open = false;
            _closing = false;
            Dismissed?.Invoke(this, EventArgs.Empty);
            done.TrySetResult(true);
        });
        return done.Task;
    }

    private void Unsubscribe()
    {
        App.Services.Log.LineAppended -= OnLineAppended;
        App.Services.Log.Cleared -= OnLogCleared;
    }

    private void Rebuild()
    {
        _console.Clear();
        foreach (var line in App.Services.Log.Snapshot()) _console.AppendLine(line);
        ScrollToEnd();
    }

    private void OnLineAppended(string line)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _console.AppendLine(line);
            if (AutoScrollToggle.IsOn) ScrollToEnd();
        });
    }

    private void OnLogCleared() => DispatcherQueue.TryEnqueue(() =>
    {
        _console.Clear();
        CopyStatusText.Text = string.Empty;
    });

    private void ScrollToEnd() => LogScroll.ChangeView(null, LogScroll.ExtentHeight, null);

    private void Animate(
        double toX,
        double toScrimOpacity,
        int milliseconds,
        EasingMode ease,
        Action? completed)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        var easing = new CubicEase { EasingMode = ease };

        var slide = new DoubleAnimation { To = toX, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(slide, Slide);
        Storyboard.SetTargetProperty(slide, "X");

        var fade = new DoubleAnimation { To = toScrimOpacity, Duration = duration, EasingFunction = easing };
        Storyboard.SetTarget(fade, Scrim);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        if (completed is not null) storyboard.Completed += (_, _) => completed();
        storyboard.Begin();
    }

    // ---------- 工具条 ----------

    private void Close_Click(object sender, RoutedEventArgs e) => _ = CloseAsync();

    private void Scrim_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Scrim).Properties.IsLeftButtonPressed) return;
        _ = CloseAsync();
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(App.Services.Log.SnapshotPlainText());
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        _ = UiHelpers.FlashAsync(CopyStatusText, "已复制全部日志到剪贴板");
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => App.Services.Log.Clear();

    private void OpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        if (!UiHelpers.OpenFolder(App.Services.Log.LogDirectory))
            _ = UiHelpers.FlashAsync(CopyStatusText, "日志目录不存在");
    }

    private async void AiAnalyze_Click(object sender, RoutedEventArgs e)
    {
        // 先收起抽屉：网关引导用到 ContentDialog，弹窗与 Popup 同层时层级不可控
        await CloseAsync();

        AiAnalyzeButton.IsEnabled = false;
        try
        {
            await AiGatewayEntry.OpenAsync(App.Window);
        }
        finally
        {
            AiAnalyzeButton.IsEnabled = true;
        }
    }
}
