using ComfyUiRocmLauncher.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ComfyUiRocmLauncher.GUI.Helpers;

/// <summary>
/// 主题 brush 取值helper：统一从应用资源（含合并的 XamlControlsResources 主题字典）按 key 取色，
/// 避免各页散落裸色字面量与重复的类型转换。
/// </summary>
public static class ThemeHelper
{
    public static Brush Brush(string key) =>
        Application.Current.Resources[key] as Brush ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public static Windows.UI.Color Color(string key) =>
        (Application.Current.Resources[key] as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.Transparent;

    /// <summary>按主题色生成半透明底刷（用于徽章底色）。</summary>
    public static SolidColorBrush Tint(string key, double opacity) =>
        new(Color(key)) { Opacity = opacity };

    /// <summary>等宽字体（命令 / 路径 / 日志），单点取自应用资源 MonoFontFamily。</summary>
    public static FontFamily Mono =>
        Application.Current.Resources["MonoFontFamily"] as FontFamily ?? new FontFamily("Cascadia Mono, Consolas");

    /// <summary>
    /// 状态 → 强调色资源 key 的单点映射：运行=成功绿 / 启动=提示黄 / 停止=危险红 / 环境任务=强调蓝；
    /// 空闲返回 null（无强调色，调用方各自决定中性样式）。
    /// </summary>
    public static string? StateAccentKey(LaunchState state) => state switch
    {
        LaunchState.Running => "SystemFillColorSuccessBrush",
        LaunchState.Starting => "SystemFillColorCautionBrush",
        LaunchState.Stopping => "SystemFillColorCriticalBrush",
        LaunchState.BusyEnv => "AccentFillColorDefaultBrush",
        _ => null,
    };
}
