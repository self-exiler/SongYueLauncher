using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;
using ComfyUiRocmLauncher.GUI.Helpers;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>环境变量页（SRS FR-3）：固定注入层只读 + 可编辑自定义层。GPU 模板应用见主页启动控制区（FR-7）。</summary>
public sealed partial class EnvVarsPage : Page
{
    public EnvVarsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RebuildRows();
    }

    private AppConfig Settings => App.Services.Settings;

    private void RebuildRows()
    {
        CustomRows.Children.Clear();
        foreach (var kv in Settings.CustomEnvVars)
        {
            CustomRows.Children.Add(BuildRow(kv.Key, kv.Value));
        }
        CheckConflicts();
    }

    private UIElement BuildRow(string key, string value)
    {
        var keyBox = new TextBox
        {
            Text = key,
            Width = 260,
            PlaceholderText = "VAR_NAME",
            FontFamily = ThemeHelper.Mono,
        };
        var valueBox = new TextBox
        {
            Text = value,
            MinWidth = 260,
            PlaceholderText = "值",
            FontFamily = ThemeHelper.Mono,
        };
        var delete = new Button { Content = "删除" };

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

            Settings.CustomEnvVars.Remove(key);
            Settings.CustomEnvVars[newKey] = valueBox.Text;
            key = newKey;
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

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(keyBox);
        panel.Children.Add(valueBox);
        panel.Children.Add(delete);
        return panel;
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
