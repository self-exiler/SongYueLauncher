using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ComfyUiRocmLauncher.Core.Ai;
using ComfyUiRocmLauncher.Core.Services;
using ComfyUiRocmLauncher.GUI.Helpers;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>日志页（SRS FR-5）：ANSI 着色渲染 + 自动滚动 + 复制/清空 + 打开日志目录。</summary>
public sealed partial class LogPage : Page
{
    private const int MaxRenderedLines = 3000;
    private readonly BoundedAnsiConsole _console;

    public LogPage()
    {
        InitializeComponent();
        _console = new BoundedAnsiConsole(LogText, MaxRenderedLines);
        Loaded += OnLoaded;
        Unloaded += (_, _) => App.Services.Log.LineAppended -= OnLineAppended;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _console.Clear();

        foreach (var line in App.Services.Log.Snapshot())
        {
            _console.AppendLine(line);
        }

        App.Services.Log.LineAppended += OnLineAppended;
        RetentionText.Text = $"磁盘保护：保留最近 {App.Services.Settings.LogRetainCount} 份，总大小超 {App.Services.Settings.LogRetainTotalMB} MB 从最旧开始删除";
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

    private void ScrollToEnd() => LogScroll.ChangeView(null, LogScroll.ExtentHeight, null);

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        App.Services.Log.Clear();
        _console.Clear();
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(App.Services.Log.SnapshotPlainText());
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

    /// <summary>AI 智能分析（FR-5.6）：未装 opencode 提醒并退出；否则打开独立分析窗口（标题栏 X 关闭）。</summary>
    private AiAnalysisWindow? _aiWindow;

    private void AiAnalyze_Click(object sender, RoutedEventArgs e)
    {
        var exe = ComfyUiAiService.LocateOpenCode();
        if (exe is null)
        {
            _ = ShowOpenCodeNotInstalledAsync();
            return;
        }

        if (_aiWindow is not null)
        {
            _aiWindow.Activate();
            return;
        }

        var panel = new AiAnalysisDialog();
        void Seed() => panel.SeedErrorPreview(CaptureErrors());
        panel.RecaptureRequested += (_, _) => Seed();
        Seed();

        _aiWindow = new AiAnalysisWindow(panel, "AI 智能分析");
        _aiWindow.Closed += (_, _) => _aiWindow = null;
        _aiWindow.Activate();
    }

    private async Task ShowOpenCodeNotInstalledAsync()
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
    }

    private static ErrorLogExtraction CaptureErrors() =>
        ComfyUiAiService.ExtractErrors(App.Services.Log.Snapshot());
}
