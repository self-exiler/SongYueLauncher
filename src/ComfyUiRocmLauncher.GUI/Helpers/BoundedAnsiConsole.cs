using ComfyUiRocmLauncher.Core.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace ComfyUiRocmLauncher.GUI.Helpers;

/// <summary>
/// 带上限的 ANSI 着色控制台渲染器：按行追加着色 Run，行数超限时从最前面整行移除。
/// 取代调用方各自实现的"清空 + 重建"做法，避免长任务（torch 更新等）下 Inlines 无限增长。
/// </summary>
public sealed class BoundedAnsiConsole(TextBlock target, int maxLines)
{
    /// <summary>每行占用的 Inline 数（着色 Run 数 + 1 个 LineBreak），用于整行回收。</summary>
    private readonly Queue<int> _inlinesPerLine = new();

    /// <summary>渲染行数上限。</summary>
    public int MaxLines { get; } = maxLines;

    public void Clear()
    {
        target.Inlines.Clear();
        _inlinesPerLine.Clear();
    }

    /// <summary>按 ANSI 着色追加一行（含行尾换行）。</summary>
    public void AppendLine(string line)
    {
        var runs = AnsiParser.Parse(line);
        foreach (var run in runs)
        {
            target.Inlines.Add(new Run { Text = run.Text, Foreground = AnsiBrushes.For(run.Color) });
        }
        target.Inlines.Add(new LineBreak());

        _inlinesPerLine.Enqueue(runs.Count + 1);
        while (_inlinesPerLine.Count > MaxLines) RemoveOldestLine();
    }

    private void RemoveOldestLine()
    {
        var count = _inlinesPerLine.Dequeue();
        for (var i = 0; i < count; i++) target.Inlines.RemoveAt(0);
    }
}
