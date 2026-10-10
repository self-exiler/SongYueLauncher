using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using ComfyUiRocmLauncher.Core.Services;
using ComfyUiRocmLauncher.GUI.Helpers;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>日志页（SRS FR-5）：ANSI 着色渲染 + 自动滚动 + 复制/清空 + 打开日志目录。</summary>
public sealed partial class LogPage : Page
{
    private const int MaxRenderedLines = 3000;
    private int _renderedLines;

    public LogPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => App.Services.Log.LineAppended -= OnLineAppended;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LogText.Inlines.Clear();
        _renderedLines = 0;

        foreach (var line in App.Services.Log.Snapshot())
        {
            AppendLine(line);
        }

        App.Services.Log.LineAppended += OnLineAppended;
        RetentionText.Text = $"磁盘保护：保留最近 {App.Services.Settings.LogRetainCount} 份，总大小超 {App.Services.Settings.LogRetainTotalMB} MB 从最旧开始删除";
        ScrollToEnd();
    }

    private void OnLineAppended(string line)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AppendLine(line);
            if (AutoScrollToggle.IsOn) ScrollToEnd();
        });
    }

    private void AppendLine(string line)
    {
        foreach (var run in AnsiParser.Parse(line))
        {
            LogText.Inlines.Add(new Run
            {
                Text = run.Text,
                Foreground = BrushFor(run.Color),
            });
        }
        LogText.Inlines.Add(new LineBreak());
        _renderedLines++;

        // 渲染行数上限保护：超出后按内存缓冲尾部重建
        if (_renderedLines > MaxRenderedLines)
        {
            LogText.Inlines.Clear();
            _renderedLines = 0;
            foreach (var tail in App.Services.Log.Snapshot().TakeLast(1000))
            {
                AppendLine(tail);
            }
        }
    }

    // 控制台配色（深底 #161616 上的调色板）
    private static Brush BrushFor(string color) => color switch
    {
        "red" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 153, 164)),
        "green" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 108, 203, 95)),
        "yellow" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 252, 225, 0)),
        "blue" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 118, 185, 237)),
        "magenta" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 209, 109, 158)),
        "cyan" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 194, 255)),
        "black" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 154, 154, 154)),
        "white" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 232, 232, 232)),
        _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 232, 232, 232)),
    };

    private void ScrollToEnd() => LogScroll.ChangeView(null, LogScroll.ExtentHeight, null);

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Log.Clear();
        LogText.Inlines.Clear();
        _renderedLines = 0;
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, App.Services.Log.Snapshot().Select(AnsiParser.Strip));
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void OpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = App.Services.Log.LogDirectory;
        if (!UiHelpers.OpenFolder(dir))
        {
            _ = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "日志目录不存在",
                Content = dir,
                CloseButtonText = "确定",
            }.ShowAsync();
        }
    }
}
