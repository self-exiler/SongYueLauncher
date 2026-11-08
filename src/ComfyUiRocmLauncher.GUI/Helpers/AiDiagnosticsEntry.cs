using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ComfyUiRocmLauncher.Core.Ai;
using ComfyUiRocmLauncher.GUI.Views;

namespace ComfyUiRocmLauncher.GUI.Helpers;

/// <summary>
/// AI 智能分析入口（SRS v3.4 FR-5.6）：只检查本机 opencode CLI 是否存在，未安装时引导安装。
/// 不再有服务进程、端口、健康检查与 API Key：provider / 模型可用性由用户本机 opencode 配置决定，
/// 其状态在分析窗口的模型下拉与失败提示里呈现，不在入口做阻塞式探测。
/// </summary>
public static class AiDiagnosticsEntry
{
    private const string InstallCommand = "npm i -g @opencode/cli";

    private static AiAnalysisWindow? _window;

    public static async Task OpenAsync(Window owner)
    {
        var xamlRoot = (owner.Content as FrameworkElement)?.XamlRoot;
        if (xamlRoot is null) return;

        try
        {
            var exe = ComfyUiAiService.LocateOpenCode();
            if (exe is null)
            {
                await ShowInstallGuideAsync(xamlRoot);
                return;
            }

            if (_window is null)
            {
                App.Services.Log.Append($"[AI诊断] 使用本机 opencode：{exe}");
            }
            OpenWindow();
        }
        catch (Exception ex)
        {
            // 入口是 async void 事件处理器的延续，未捕获异常会直接崩溃（沿用 v2.0 审查 A1 的兜底）
            App.Services.Log.Append($"[AI诊断] 打开失败：{ex.Message}");
            await ShowProblemAsync(xamlRoot, $"准备 AI 智能分析时出错：{ex.Message}");
        }
    }

    private static void OpenWindow()
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        var panel = new AiAnalysisDialog();
        void Seed() => panel.SeedErrorPreview(CaptureErrors());
        panel.RecaptureRequested += (_, _) => Seed();
        Seed();

        _window = new AiAnalysisWindow(panel, "AI 智能分析");
        _window.Closed += (_, _) => _window = null;
        _window.Activate();
    }

    /// <summary>未检测到 opencode 时给可复制的安装命令；模型 provider 仍需用户自行配置。</summary>
    private static async Task ShowInstallGuideAsync(XamlRoot xamlRoot)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "未检测到本机 opencode",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "AI 智能分析现在直接调用本机 opencode CLI，不再依赖本地网关服务。\n\n"
                    + "当前未找到 opencode 可执行文件。可在 PowerShell 中执行：\n"
                    + $"{InstallCommand}\n\n"
                    + "安装完成后重新打开本页即可。模型与凭据由本机 opencode 自行配置，"
                    + "可执行 `opencode models` 检查可用模型。",
            },
            PrimaryButtonText = "复制安装命令",
            CloseButtonText = "知道了",
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            CopyToClipboard(InstallCommand);
            dialog.PrimaryButtonText = "已复制";
        };
        await dialog.ShowAsync();
    }

    private static async Task ShowProblemAsync(XamlRoot xamlRoot, string message) =>
        await new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "AI 智能分析未就绪",
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = message },
            CloseButtonText = "知道了",
        }.ShowAsync();

    private static void CopyToClipboard(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private static ErrorLogExtraction CaptureErrors() =>
        ComfyUiAiService.ExtractErrors(App.Services.Log.Snapshot());
}
