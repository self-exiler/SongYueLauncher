using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ComfyUiRocmLauncher.GUI.Helpers;

public static class UiHelpers
{
    /// <summary>在资源管理器中打开目录（目录不存在时返回 false）。</summary>
    public static bool OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = path,
            UseShellExecute = true,
        });
        return true;
    }

    public static void OpenUrl(string url)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// 危险操作确认（Windows 设置风格）：默认按钮恒为“取消”，主按钮是显式破坏性动作。
    /// 覆盖更新、卸载包、模板全量替换等不可撤销或影响面大的操作都必须先走这里。
    /// </summary>
    public static async Task<bool> ConfirmAsync(XamlRoot xamlRoot, string title, string message,
        string primaryText, string closeText = "取消")
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// 短时状态文本：复制、导出这类轻量操作需要结果反馈，但不应弹窗打断。
    /// 时间到后只清除“还是自己写入的那条”文案，避免覆盖后续新状态。
    /// </summary>
    public static async Task FlashAsync(TextBlock target, string message, int milliseconds = 2000)
    {
        var queue = target.DispatcherQueue;
        queue.TryEnqueue(() => target.Text = message);
        await Task.Delay(milliseconds).ConfigureAwait(false);
        queue.TryEnqueue(() =>
        {
            if (string.Equals(target.Text, message, StringComparison.Ordinal)) target.Text = string.Empty;
        });
    }
}
