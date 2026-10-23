using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>
/// AI 智能分析宿主窗口（SRS FR-5.10）：独立 Window 而非 ContentDialog——
/// 关闭直接用标题栏右上角 X，不再占一整行「关闭」按钮；X 关窗触发的 Unloaded 由内容控件负责取消进行中的分析。
/// 观感与主窗口对齐：Mica 背景 + 同款 TitleBar 控件（图标居左）+ 页面级内边距。
/// </summary>
public sealed class AiAnalysisWindow : Window
{
    public AiAnalysisWindow(AiAnalysisDialog content, string title)
    {
        Title = title;
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new MicaBackdrop();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = new TitleBar { Title = title };
        var appIcon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(appIcon))
        {
            titleBar.IconSource = new ImageIconSource { ImageSource = new BitmapImage(new Uri(appIcon)) };
        }
        root.Children.Add(titleBar);

        Grid.SetRow(content, 1);
        root.Children.Add(content);
        Content = root;

        AppWindow.Resize(new Windows.Graphics.SizeInt32(980, 720));
        if (File.Exists(appIcon)) AppWindow.SetIcon(appIcon);

        // 主窗口关闭后独立窗口不会自动退出，进程会悬着；随主窗口一起关（顺带触发 Unloaded 取消分析）。
        App.Window.Closed += OnMainWindowClosed;
        Closed += OnClosed;

        CenterOverMainWindow();
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args) => Close();

    private void OnClosed(object sender, WindowEventArgs args) => App.Window.Closed -= OnMainWindowClosed;

    private void CenterOverMainWindow()
    {
        try
        {
            var main = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(App.WindowHandle));
            var x = main.Position.X + (main.Size.Width - AppWindow.Size.Width) / 2;
            var y = main.Position.Y + (main.Size.Height - AppWindow.Size.Height) / 2;
            AppWindow.Move(new Windows.Graphics.PointInt32(Math.Max(0, x), Math.Max(0, y)));
        }
        catch (Exception ex)
        {
            // 拿不到主窗口几何信息就用系统默认位置，不阻断打开
            App.Services.Log.Append($"[启动器] 警告：AI 分析窗口定位失败：{ex.Message}");
        }
    }
}
