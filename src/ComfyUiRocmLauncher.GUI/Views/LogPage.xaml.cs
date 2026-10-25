using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
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
        // 开关没有可见标题，补自动化名称；旁边「自动滚动」文本对读屏不构成标签关联
        AutomationProperties.SetName(AutoScrollToggle, "自动滚动");
        // 工具条自适应：窄窗口把按钮组与自动滚动改为纵向，避免横向溢出
        LogRoot.SizeChanged += (_, _) =>
        {
            var narrow = LogRoot.ActualWidth < 760;
            LogButtonsPanel.Orientation = narrow ? Orientation.Vertical : Orientation.Horizontal;
            AutoScrollPanel.Orientation = narrow ? Orientation.Vertical : Orientation.Horizontal;
        };
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

        // 复制是轻量操作：失败由系统剪贴板兜底提示，成功必须给出一个明确的短时反馈
        _ = UiHelpers.FlashAsync(CopyStatusText, "已复制全部日志到剪贴板");
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

    /// <summary>
    /// AI 智能分析（SRS v2.0 FR-5.6）：网关未就绪时先走引导流程（缺 Node 提示安装、
    /// 未安装则弹下载链接到浏览器并引导放置 zip、未运行则拉起服务），全部通过后打开独立分析窗口。
    /// </summary>
    private AiAnalysisWindow? _aiWindow;

    private async void AiAnalyze_Click(object sender, RoutedEventArgs e)
    {
        AiAnalyzeButton.IsEnabled = false;
        try
        {
            var gateway = App.Services.Gateway;
            if (gateway.Current == AiGatewayState.NodeMissing)
            {
                await ShowNodeMissingAsync();
                return;
            }

            // 未安装且安装目录里没有待解压的 zip：引导浏览器下载并放置（取消则中止本次进入）
            if (gateway.Current == AiGatewayState.NotInstalled && !gateway.HasPendingArchive)
            {
                if (!await ShowManualInstallGuideAsync()) return;
            }

            var alreadyRunning = gateway.Current == AiGatewayState.Running
                                 && App.Services.Settings.AiGatewayApiKey.Length > 0;
            var (ok, message) = alreadyRunning
                ? (true, string.Empty)
                : await EnsureWithProgressAsync();
            if (!ok)
            {
                // message 为空 = 用户主动取消准备流程：不再弹“服务不可用”误导用户
                if (message.Length > 0) await ShowGatewayProblemAsync(message);
                return;
            }

            OpenAnalysisWindow();
        }
        catch (OperationCanceledException)
        {
            // 用户取消准备流程：与 EnsureWithProgressAsync 一致，静默返回
        }
        catch (Exception ex)
        {
            // async void 没有 catch 就是崩溃（审查 A1：安装目录不可写等意外必须折算成弹窗）
            App.Services.Log.Append($"[AI网关] 准备失败：{ex.Message}");
            await ShowGatewayProblemAsync($"准备 AI 分析服务时出错：{ex.Message}");
        }
        finally
        {
            AiAnalyzeButton.IsEnabled = true;
        }
    }

    private void OpenAnalysisWindow()
    {
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

    /// <summary>准备期间显示进度弹窗（首次会经历解压 → 启动 → 就绪数个阶段）。取消按钮真正取消准备流程。</summary>
    private async Task<(bool Ok, string Message)> EnsureWithProgressAsync()
    {
        var progressText = new TextBlock { Text = "正在准备 AI 分析服务…", TextWrapping = TextWrapping.Wrap };
        using var cts = new CancellationTokenSource();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "AI 智能分析",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new ProgressRing { IsActive = true, Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Center },
                    progressText,
                },
            },
            CloseButtonText = "取消",
        };
        // 「取消」要真正中断后台的解压/健康等待，而不是只关弹窗放任其跑完
        dialog.CloseButtonClick += (_, _) => cts.Cancel();
        _ = dialog.ShowAsync();
        try
        {
            var result = await App.Services.Gateway.EnsureReadyAsync(
                new Progress<string>(s => DispatcherQueue.TryEnqueue(() => progressText.Text = s)),
                cts.Token);
            return (result.Ok, result.Message);
        }
        catch (OperationCanceledException)
        {
            return (false, string.Empty);
        }
        finally
        {
            dialog.Hide();
        }
    }

    /// <summary>
    /// 手动安装引导（SRS v2.0 FR-5.6）：启动器不内置下载——镜像通道慢且超时不可控，
    /// 改为把下载链接弹到默认浏览器，并用文字引导用户把 zip 放进安装目录，由启动器解压。
    /// 返回 true 表示用户点了「我已放置好」，继续走准备流程。
    /// </summary>
    private async Task<bool> ShowManualInstallGuideAsync()
    {
        var gateway = App.Services.Gateway;
        var mirror = App.Services.Settings.EnvUpdate.GithubMirror?.Trim();
        var directUrl = AiGatewayService.ArchiveUrl();

        // 备选镜像取自镜像清单（去掉直连项与配置重复项）；自动弹出优先配置镜像，其次第一个备选
        var mirrors = MirrorCatalog.GithubMirrors
            .Where(m => !string.IsNullOrEmpty(m.Url) && !string.Equals(m.Url, mirror, StringComparison.OrdinalIgnoreCase))
            .ToList();
        string launchUrl;
        if (mirror is { Length: > 0 }) launchUrl = AiGatewayService.ArchiveUrl(mirror);
        else if (mirrors.Count > 0) launchUrl = AiGatewayService.ArchiveUrl(mirrors[0].Url);
        else launchUrl = directUrl;

        Directory.CreateDirectory(gateway.InstallRoot);
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(launchUrl));

        var step1 = new TextBlock { TextWrapping = TextWrapping.Wrap };
        step1.Inlines.Add(new Run
        {
            Text = $"1. 下载源码包 v{AiGatewayService.UpstreamVersion}（约 12 MB）。已在浏览器打开下载，若没有打开请点：",
        });
        AppendLink(step1, "配置镜像", mirror is { Length: > 0 } ? AiGatewayService.ArchiveUrl(mirror) : null);
        foreach (var m in mirrors)
        {
            AppendLink(step1, m.Label, AiGatewayService.ArchiveUrl(m.Url));
        }
        AppendLink(step1, "GitHub 直连", directUrl, tail: true);

        var step2 = new TextBlock { TextWrapping = TextWrapping.Wrap };
        step2.Inlines.Add(new Run { Text = "2. 下载完成后，把 zip 文件原样放入下面的目录（无需解压）：" });
        var pathText = new TextBlock
        {
            Text = gateway.InstallRoot,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = (FontFamily)Application.Current.Resources["MonoFontFamily"],
        };
        var step3 = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "3. 点击「我已放置好，继续」，启动器会自动解压并启动服务（安装只需这一次）。",
        };
        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            Text = $"也可以自行解压 zip，把解压出的 dsh-our-free-model-{AiGatewayService.UpstreamVersion} 文件夹放入同一目录。",
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "安装 AI 分析服务（仅首次）",
            Content = new StackPanel
            {
                Spacing = 12,
                Children = { step1, step2, pathText, step3, hint },
            },
            PrimaryButtonText = "我已放置好，继续",
            SecondaryButtonText = "打开安装目录",
            CloseButtonText = "稍后再说",
        };
        // 「打开安装目录」不关弹窗：用户放好文件后直接点「继续」
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            UiHelpers.OpenFolder(gateway.InstallRoot);
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>向步骤文本追加一个「标签 ·」形式的超链接；url 为空跳过，tail 为真时去掉尾部分隔符。</summary>
    private static void AppendLink(TextBlock host, string label, string? url, bool tail = false)
    {
        if (string.IsNullOrEmpty(url)) return;
        var link = new Hyperlink { NavigateUri = new Uri(url) };
        link.Inlines.Add(new Run { Text = label });
        host.Inlines.Add(link);
        if (!tail) host.Inlines.Add(new Run { Text = " · " });
    }

    private async Task ShowNodeMissingAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "缺少 Node.js 运行时",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "AI 智能分析依赖本机 Node.js（需要 22.19+ 或 24+）来运行分析服务，当前未检测到。\n\n"
                    + "可在 PowerShell 中执行：\nwinget install OpenJS.NodeJS.LTS\n\n"
                    + "安装完成后重新打开本页面即可。",
            },
            PrimaryButtonText = "复制安装命令",
            CloseButtonText = "知道了",
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            CopyToClipboard("winget install OpenJS.NodeJS.LTS");
            dialog.PrimaryButtonText = "已复制";
        };
        await dialog.ShowAsync();
    }

    private async Task ShowGatewayProblemAsync(string message)
    {
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "AI 分析服务未就绪",
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = message },
            CloseButtonText = "知道了",
        }.ShowAsync();
    }

    private static void CopyToClipboard(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private static ErrorLogExtraction CaptureErrors() =>
        ComfyUiAiService.ExtractErrors(App.Services.Log.Snapshot());
}
