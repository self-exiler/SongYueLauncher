using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>启动参数页（SRS FR-2）：数据驱动生成全部参数控件 + 实时命令预览。</summary>
public sealed partial class ArgsPage : Page
{
    private readonly CliArgsCatalog _catalog = CliArgsCatalog.Instance;
    private readonly Dictionary<string, RowUi> _rows = new(StringComparer.Ordinal);
    private readonly List<GroupUi> _groups = new();

    /// <summary>一个参数分组的 UI 引用（容器 / 折叠体 / 头部 / 头部文本 / 参数名集合）。</summary>
    private sealed record GroupUi(
        StackPanel Container,
        StackPanel Body,
        Button Header,
        TextBlock HeaderText,
        string Title,
        IReadOnlyList<string> ArgNames);

    /// <summary>一个参数行的 UI 引用（卡片 / 开关 / 取值控件）。</summary>
    private sealed record RowUi(Border Card, ToggleSwitch Toggle, FrameworkElement? Control);

    public ArgsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => BuildGroups();
    }

    private AppConfig Settings => App.Services.Settings;

    private void BuildGroups()
    {
        GroupsHost.Children.Clear();
        _rows.Clear();
        _groups.Clear();
        ExtraArgsBox.Text = Settings.ExtraArgs;

        foreach (var group in _catalog.Groups)
        {
            var body = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var arg in group.Args)
            {
                body.Children.Add(BuildRow(arg));
            }

            var headerText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            var chevron = new FontIcon
            {
                Glyph = "\uE76C", // ChevronDown：展开朝下，折叠时旋转 -90° 朝右
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            };

            var headerContent = new Grid { ColumnSpacing = 12 };
            headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerContent.Children.Add(headerText);
            Grid.SetColumn(chevron, 1);
            headerContent.Children.Add(chevron);

            var header = new Button
            {
                Content = headerContent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(16, 12, 16, 12),
                CornerRadius = new CornerRadius(8),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
            };
            header.Click += (_, _) =>
            {
                var expanded = body.Visibility == Visibility.Visible;
                body.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
                chevron.RenderTransform = new Microsoft.UI.Xaml.Media.RotateTransform { Angle = expanded ? -90 : 0 };
            };

            var container = new StackPanel { Spacing = 0 };
            container.Children.Add(header);
            container.Children.Add(body);
            GroupsHost.Children.Add(container);

            _groups.Add(new GroupUi(container, body, header, headerText, group.Title, group.Args.Select(a => a.Name).ToList()));
        }

        ArgCountText.Text = $"共 {_catalog.Count} 项参数（来源 ComfyUI cli_args.py 快照）";
        UpdatePreview();
    }

    /// <summary>组头计数：N 启用 · M 项。</summary>
    private void UpdateGroupCounts()
    {
        foreach (var g in _groups)
        {
            var on = g.ArgNames.Count(n => Settings.Args.IsEnabled(n));
            var text = on > 0
                ? $"{g.Title}（{on} 启用 · {g.ArgNames.Count} 项）"
                : $"{g.Title}（{g.ArgNames.Count} 项）";
            g.HeaderText.Text = text;
            // 头部内容是 Grid，需手动给按钮一个自动化名称（屏幕阅读器 / UI 自动化）
            AutomationProperties.SetName(g.Header, text);
        }
    }

    private UIElement BuildRow(CliArgDefinition arg)
    {
        var toggle = new ToggleSwitch
        {
            VerticalAlignment = VerticalAlignment.Center,
            OnContent = string.Empty,
            OffContent = string.Empty,
        };
        toggle.IsOn = Settings.Args.IsEnabled(arg.Name);
        toggle.Toggled += (_, _) =>
        {
            Settings.Args.Enable(arg.Name, toggle.IsOn);
            if (toggle.IsOn)
            {
                var closed = _catalog.EnforceMutex(Settings.Args, arg.Name);
                foreach (var name in closed)
                {
                    if (_rows.TryGetValue(name, out var peer)) peer.Toggle.IsOn = false;
                }
            }
            SyncControlState(arg.Name);
            App.Services.Save();
            UpdatePreview();
        };

        var nameText = new TextBlock { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") };
        nameText.Inlines.Add(new Run
        {
            Text = "--" + arg.Name,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
        });
        if (arg.Bat)
        {
            nameText.Inlines.Add(new Run { Text = "  bat 默认", Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Goldenrod) });
        }

        var descText = new TextBlock
        {
            Text = arg.Description + (arg.Mutex.HasValue ? $" · 互斥组 {arg.Mutex}" : string.Empty),
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };

        var textColumn = new StackPanel { Spacing = 2 };
        textColumn.Children.Add(nameText);
        textColumn.Children.Add(descText);

        var control = BuildControl(arg);

        // 设置应用行布局：左侧「参数名 + 说明」，右侧「取值控件 + 启用开关」
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 10, 16, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (control is not null)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(textColumn);
        if (control is not null)
        {
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
        Grid.SetColumn(toggle, grid.ColumnDefinitions.Count - 1);
        grid.Children.Add(toggle);

        var card = new Border
        {
            Style = (Style)Application.Current.Resources["CardBorderStyle"],
            Child = grid,
        };

        _rows[arg.Name] = new RowUi(card, toggle, control);
        SyncControlState(arg.Name);
        return card;
    }

    /// <summary>未启用行的取值控件灰显并禁止交互。</summary>
    private void SyncControlState(string name)
    {
        if (!_rows.TryGetValue(name, out var row) || row.Control is null) return;
        var on = row.Toggle.IsOn;
        row.Control.Opacity = on ? 1 : 0.35;
        row.Control.IsHitTestVisible = on;
    }

    private FrameworkElement? BuildControl(CliArgDefinition arg)
    {
        switch (arg.Type)
        {
            case ArgType.Flag:
                return null;

            case ArgType.Enum:
            case ArgType.RepeatableVerbose:
            {
                var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var choice in arg.Choices ?? new List<string>())
                {
                    combo.Items.Add(new ComboBoxItem { Content = choice });
                }
                combo.SelectedItem = combo.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(i => (string)i.Content == Settings.Args.Value(arg.Name));
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is ComboBoxItem item)
                    {
                        Settings.Args.SetValue(arg.Name, (string)item.Content);
                        App.Services.Save();
                        UpdatePreview();
                    }
                };

                if (arg.Type == ArgType.Enum) return combo;

                var fileBox = new TextBox { PlaceholderText = "可选：日志文件路径", Margin = new Thickness(0, 4, 0, 0) };
                fileBox.Text = Settings.Args.Value(arg.Name + ":file");
                fileBox.TextChanged += (_, _) =>
                {
                    Settings.Args.SetValue(arg.Name + ":file", fileBox.Text.Trim());
                    App.Services.Save();
                    UpdatePreview();
                };

                var stack = new StackPanel();
                stack.Children.Add(combo);
                stack.Children.Add(fileBox);
                return stack;
            }

            case ArgType.MultiEnum:
            {
                var panel = new StackPanel();
                var selected = Settings.Args.MultiOf(arg.Name);
                foreach (var choice in arg.Choices ?? new List<string>())
                {
                    var check = new CheckBox { Content = choice, IsChecked = selected.Contains(choice) };
                    check.Checked += (_, _) => UpdateMulti(arg, panel);
                    check.Unchecked += (_, _) => UpdateMulti(arg, panel);
                    panel.Children.Add(check);
                }
                return panel;
            }

            case ArgType.RepeatableStringList:
            case ArgType.RepeatableKeyValue:
            {
                var box = new TextBox
                {
                    AcceptsReturn = true,
                    MinHeight = 52,
                    PlaceholderText = "每行一条",
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                };
                box.Text = string.Join(Environment.NewLine, Settings.Args.LinesOf(arg.Name));
                box.TextChanged += (_, _) =>
                {
                    Settings.Args.Lines[arg.Name] = box.Text
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0)
                        .ToList();
                    App.Services.Save();
                    UpdatePreview();
                };
                return box;
            }

            default:
            {
                var box = new TextBox
                {
                    PlaceholderText = arg.Type is ArgType.OptionalString or ArgType.OptionalInt
                        ? $"留空 = 无值传递（const {arg.Const}）"
                        : (arg.Default ?? "值"),
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                };
                box.Text = Settings.Args.Value(arg.Name);
                box.TextChanged += (_, _) =>
                {
                    Settings.Args.SetValue(arg.Name, box.Text.Trim());
                    App.Services.Save();
                    UpdatePreview();
                };
                return box;
            }
        }
    }

    private void UpdateMulti(CliArgDefinition arg, StackPanel panel)
    {
        Settings.Args.Multi[arg.Name] = panel.Children
            .OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => (string)c.Content)
            .ToList();
        App.Services.Save();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        UpdateGroupCounts();

        // python.exe 与 --参数 用强调色，额外参数用警示色
        var accent = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        var warn = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];

        PreviewText.Inlines.Clear();
        PreviewText.Inlines.Add(new Run { Text = ArgsBuilder.Quote(Settings.PythonExe), Foreground = accent });
        PreviewText.Inlines.Add(new Run { Text = " main.py" });

        foreach (var token in ArgsBuilder.Build(Settings.Args, _catalog))
        {
            PreviewText.Inlines.Add(new Run
            {
                Text = " " + ArgsBuilder.Quote(token),
                Foreground = token.StartsWith("--", StringComparison.Ordinal) ? accent : null,
            });
        }
        foreach (var token in ArgsBuilder.SplitExtra(Settings.ExtraArgs))
        {
            PreviewText.Inlines.Add(new Run { Text = " " + ArgsBuilder.Quote(token), Foreground = warn });
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        foreach (var g in _groups)
        {
            var anyVisible = false;
            foreach (var name in g.ArgNames)
            {
                if (!_rows.TryGetValue(name, out var row)) continue;
                var visible = query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase);
                row.Card.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                anyVisible |= visible;
            }
            // 无匹配的分组整体隐藏；有匹配时自动展开
            g.Container.Visibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;
            if (query.Length > 0 && anyVisible) g.Body.Visibility = Visibility.Visible;
        }
    }

    private void ExtraArgsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Settings.ExtraArgs = ExtraArgsBox.Text;
        App.Services.Save();
        UpdatePreview();
    }

    private void ApplyTemplate_Click(object sender, RoutedEventArgs e)
    {
        var tag = (TemplateCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "rdna3";
        TemplateCatalog.Instance.ApplyArgs(tag, Settings.Args, _catalog);
        App.Services.Save();
        SyncControlsFromSettings();
        UpdatePreview();
    }

    private void ApplyBat_Click(object sender, RoutedEventArgs e)
    {
        _catalog.ApplyBatDefaults(Settings.Args);
        App.Services.Save();
        SyncControlsFromSettings();
        UpdatePreview();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _catalog.ClearAll(Settings.Args);
        Settings.ExtraArgs = string.Empty;
        ExtraArgsBox.Text = string.Empty;
        App.Services.Save();
        SyncControlsFromSettings();
        UpdatePreview();
    }

    /// <summary>配置变更后重建全部控件（模板应用 / 恢复默认）。</summary>
    private void SyncControlsFromSettings() => BuildGroups();
}
