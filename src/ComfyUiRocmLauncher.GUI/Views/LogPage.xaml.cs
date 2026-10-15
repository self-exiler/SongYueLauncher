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

    /// <summary>AI 智能分析（FR-5.6）：未装 opencode 提醒并退出；否则弹出预览 + 模型下拉 + 发送。</summary>
    private async void AiAnalyze_Click(object sender, RoutedEventArgs e)
    {
        var exe = OpenCodeClient.Locate();
        if (exe is null)
        {
            var notInstalled = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "未检测到 opencode",
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "AI 智能分析依赖本机已安装的 opencode CLI，当前未在 PATH 与常见安装位置找到它。\n\n"
                        + "安装方式（任选其一）：\n"
                        + "• npm install -g @opencode/cli\n"
                        + "• curl -fsSL https://opencode.ai/install | bash\n\n"
                        + "安装完成后重新打开本页面即可使用。",
                },
                PrimaryButtonText = "复制安装命令",
                CloseButtonText = "知道了",
            };
            notInstalled.PrimaryButtonClick += (_, args) =>
            {
                args.Cancel = true;
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText("npm install -g @opencode/cli");
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                notInstalled.PrimaryButtonText = "已复制";
            };
            await notInstalled.ShowAsync();
            return;
        }

        var panel = new AiAnalysisDialog();
        void Seed()
        {
            var p = CaptureErrors();
            panel.SeedErrorPreview(p.Text, p.TotalLines, p.ErrorLineCount, p.Truncated);
        }
        panel.RecaptureRequested += (_, _) => Seed();
        Seed();

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"AI 智能分析 · {exe}",
            Content = panel,
            PrimaryButtonText = panel.SendLabel,
            SecondaryButtonText = "复制回答",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
        };

        panel.StateChanged += (_, _) => dialog.PrimaryButtonText = panel.SendLabel;

        // Primary 按钮不自动关闭对话框：分析进行中允许取消，结束后由用户自行关闭
        dialog.PrimaryButtonClick += (s, args) =>
        {
            args.Cancel = true;
            _ = panel.HandleSendAsync();
        };

        dialog.SecondaryButtonClick += (s, args) =>
        {
            args.Cancel = !panel.TryCopyAnswer();
        };

        await dialog.ShowAsync();
    }

    private (string Text, int TotalLines, int ErrorLineCount, bool Truncated) CaptureErrors()
    {
        var extraction = ErrorLogExtractor.Extract(App.Services.Log.Snapshot());
        return (extraction.ToPreviewText(), extraction.TotalLines, extraction.ErrorLineCount, extraction.Truncated);
    }
}
