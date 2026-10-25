using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;
using ComfyUiRocmLauncher.GUI.Helpers;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>环境变量页（SRS FR-3）：固定注入层只读 + 可编辑自定义层。GPU 模板应用见主页启动控制区（FR-7）。</summary>
public sealed partial class EnvVarsPage : Page
{
    /// <summary>自定义层每一行的 UI 引用；窄窗口需要按它重排为上下两行。</summary>
    private sealed record EnvRowUi(Grid Row, TextBox KeyBox, TextBox ValueBox, Button Delete);

    private readonly List<EnvRowUi> _rows = new();

    public EnvVarsPage()
    {
        InitializeComponent();
        // 窄窗口时「变量名 / 值 / 删除」要换行排列，避免 260px 固定宽度把行挤出卡片
        CustomRows.SizeChanged += (_, _) => ApplyRowLayout();
        Loaded += (_, _) => RebuildRows();
    }

    private AppConfig Settings => App.Services.Settings;

    private void RebuildRows()
    {
        CustomRows.Children.Clear();
        _rows.Clear();
        foreach (var kv in Settings.CustomEnvVars)
        {
            CustomRows.Children.Add(BuildRow(kv.Key, kv.Value));
        }
        ApplyRowLayout();
        CheckConflicts();
    }

    private UIElement BuildRow(string key, string value)
    {
        var keyBox = new TextBox
        {
            Text = key,
            PlaceholderText = "VAR_NAME",
            FontFamily = ThemeHelper.Mono,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(keyBox, "环境变量名");

        var valueBox = new TextBox
        {
            Text = value,
            PlaceholderText = "值",
            FontFamily = ThemeHelper.Mono,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(valueBox, "环境变量值");

        var delete = new Button { Content = "删除" };
        // 多行删除按钮同名会让读屏无法区分目标，名称带上变量 key
        AutomationProperties.SetName(delete, $"删除环境变量 {key}");

        void Commit()
        {
            var newKey = keyBox.Text.Trim();
            if (string.IsNullOrEmpty(newKey)) return;

            // FR-3.3：key 与固定注入层冲突时禁止保存并提示
            if (LaunchEnvBuilder.FixedKeys.Contains(newKey, StringComparer.OrdinalIgnoreCase))
            {
                ShowConflict($"已阻止保存：{newKey} 属于固定注入层，由启动器统一管理");
                return;
            }

            // 与另一条自定义变量重名时阻止保存：索引器赋值会静默覆盖对方的值（审查 GUI-5）
            if (!string.Equals(newKey, key, StringComparison.OrdinalIgnoreCase)
                && Settings.CustomEnvVars.ContainsKey(newKey))
            {
                ShowConflict($"已阻止保存：{newKey} 与另一条自定义变量重名");
                return;
            }

            if (string.Equals(newKey, key, StringComparison.OrdinalIgnoreCase))
            {
                // key 未变：就地更新，避免 Remove+重加把该键挪到字典末尾（行序跳动）
                Settings.CustomEnvVars[newKey] = valueBox.Text;
            }
            else
            {
                Settings.CustomEnvVars.Remove(key);
                Settings.CustomEnvVars[newKey] = valueBox.Text;
                key = newKey;
                AutomationProperties.SetName(delete, $"删除环境变量 {key}");
            }
            App.Services.Save();
            CheckConflicts();
        }

        keyBox.TextChanged += (_, _) => Commit();
        valueBox.TextChanged += (_, _) => Commit();
        delete.Click += (_, _) =>
        {
            Settings.CustomEnvVars.Remove(key);
            App.Services.Save();
            RebuildRows();
        };

        var grid = new Grid { ColumnSpacing = 8 };
        grid.Children.Add(keyBox);
        grid.Children.Add(valueBox);
        grid.Children.Add(delete);
        _rows.Add(new EnvRowUi(grid, keyBox, valueBox, delete));
        return grid;
    }

    /// <summary>
    /// 自定义层行响应式布局：宽窗口「变量名 | 值 | 删除」，窄窗口「变量名一行、值与删除一行」。
    /// 不再用固定 260px 宽度，避免无横向滚动的情况下把删除按钮挤出可视区。
    /// </summary>
    private void ApplyRowLayout()
    {
        var narrow = CustomRows.ActualWidth > 0 && CustomRows.ActualWidth < 640;
        foreach (var row in _rows) ApplyRowLayout(row, narrow);
    }

    private static void ApplyRowLayout(EnvRowUi row, bool narrow)
    {
        var grid = row.Row;
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowSpacing = 0;

        if (narrow)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowSpacing = 8;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetRow(row.KeyBox, 0);
            Grid.SetColumn(row.KeyBox, 0);
            Grid.SetColumnSpan(row.KeyBox, 2);
            Grid.SetRow(row.ValueBox, 1);
            Grid.SetColumn(row.ValueBox, 0);
            Grid.SetColumnSpan(row.ValueBox, 1);
            Grid.SetRow(row.Delete, 1);
            Grid.SetColumn(row.Delete, 1);
            Grid.SetColumnSpan(row.Delete, 1);
            return;
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 260 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetRow(row.KeyBox, 0);
        Grid.SetColumn(row.KeyBox, 0);
        Grid.SetColumnSpan(row.KeyBox, 1);
        Grid.SetRow(row.ValueBox, 0);
        Grid.SetColumn(row.ValueBox, 1);
        Grid.SetColumnSpan(row.ValueBox, 1);
        Grid.SetRow(row.Delete, 0);
        Grid.SetColumn(row.Delete, 2);
        Grid.SetColumnSpan(row.Delete, 1);
    }

    private void CheckConflicts()
    {
        var conflicts = Settings.CustomEnvVars.Keys
            .Where(k => LaunchEnvBuilder.FixedKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();

        ShowConflict(conflicts.Count == 0
            ? null
            : "与固定注入层冲突（需删除或改名后才能保存新值）：" + string.Join(", ", conflicts));
    }

    /// <summary>冲突提示：有内容时展开 InfoBar，无内容时收起（取代原先的裸 TextBlock）。</summary>
    private void ShowConflict(string? message)
    {
        ConflictBar.Message = message ?? string.Empty;
        ConflictBar.IsOpen = !string.IsNullOrEmpty(message);
    }

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        var key = "NEW_VAR";
        for (var i = 1; Settings.CustomEnvVars.ContainsKey(key); i++) key = "NEW_VAR_" + i;
        Settings.CustomEnvVars[key] = string.Empty;
        App.Services.Save();
        RebuildRows();
    }
}
