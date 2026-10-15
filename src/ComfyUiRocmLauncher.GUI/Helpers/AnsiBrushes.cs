using Microsoft.UI.Xaml.Media;

namespace ComfyUiRocmLauncher.GUI.Helpers;

/// <summary>
/// ANSI 颜色名 → 画刷的单点映射（深底控制台上的调色板）。
/// 覆盖 AnsiParser 会产出的全部颜色：default、标准 8 色（30-37）与高亮 8 色（90-97，bright-*）。
/// 画刷只创建一次并缓存，避免高频日志下每个 Run 都分配新的 SolidColorBrush。
/// </summary>
public static class AnsiBrushes
{
    private const string DefaultColor = "default";

    /// <summary>高亮色向白混合的比例（终端 90-97 的视觉表现）。</summary>
    private const double BrightenAmount = 0.32;

    private static readonly Dictionary<string, uint> Palette = new(StringComparer.Ordinal)
    {
        [DefaultColor] = 0xFFE8E8E8,
        ["black"] = 0xFF9A9A9A,
        ["red"] = 0xFFFF99A4,
        ["green"] = 0xFF6CCB5F,
        ["yellow"] = 0xFFFCE100,
        ["blue"] = 0xFF76B9ED,
        ["magenta"] = 0xFFD16D9E,
        ["cyan"] = 0xFF4CC2FF,
        ["white"] = 0xFFE8E8E8,
    };

    private static readonly Dictionary<string, SolidColorBrush> Cache = Build();

    /// <summary>按 AnsiParser 给出的颜色名取画刷；未知颜色回落默认前景色。</summary>
    public static Brush For(string color) =>
        Cache.TryGetValue(color, out var brush) ? brush : Cache[DefaultColor];

    private static Dictionary<string, SolidColorBrush> Build()
    {
        var cache = new Dictionary<string, SolidColorBrush>(StringComparer.Ordinal);
        foreach (var (name, argb) in Palette)
        {
            cache[name] = Create(argb);
            if (name == DefaultColor) continue;
            cache["bright-" + name] = Create(Brighten(argb));
        }
        return cache;
    }

    private static SolidColorBrush Create(uint argb) =>
        new(Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    private static uint Brighten(uint argb)
    {
        byte Blend(byte channel) => (byte)(channel + (255 - channel) * BrightenAmount);
        return ((uint)(byte)(argb >> 24) << 24)
            | ((uint)Blend((byte)(argb >> 16)) << 16)
            | ((uint)Blend((byte)(argb >> 8)) << 8)
            | Blend((byte)argb);
    }
}
