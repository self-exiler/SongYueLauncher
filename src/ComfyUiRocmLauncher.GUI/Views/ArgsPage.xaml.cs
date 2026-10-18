using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;
using ComfyUiRocmLauncher.GUI.Helpers;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>启动参数页（SRS FR-2）：数据驱动生成全部参数控件 + 实时命令预览。分组懒加载以避免进入页面卡顿。</summary>
public sealed partial class ArgsPage : Page
{
    private readonly CliArgsCatalog _catalog = CliArgsCatalog.Instance;
    private readonly Dictionary<string, RowUi> _rows = new(StringComparer.Ordinal);
    private readonly List<GroupUi> _groups = new();
    private bool _syncing;
    private bool _shellBuilt;

    /// <summary>一个参数分组的 UI 引用；行控件在首次展开时才创建（懒加载）。</summary>
    private sealed class GroupUi(CliArgGroup definition)
    {
        public readonly CliArgGroup Definition = definition;
        public StackPanel? Body;
        public TextBlock HeaderText = null!;
        public Button Header = null!;
        public FontIcon Chevron = null!;
        public StackPanel Container = null!;
        public bool Built;
        /// <summary>构造时缓存参数名列表：计数与搜索会反复按分组取用，避免每次重新 Select().ToList()。</summary>
        public readonly IReadOnlyList<string> ArgNames = definition.Args.Select(a => a.Name).ToList();
    }

    /// <summary>
    /// 一个参数行的 UI 引用（卡片 / 开关 / 取值控件）。
    /// <c>Refresh</c> 是该行「把 Settings 写回控件」的唯一途径：构建控件时一并生成，
    /// 因此不必再靠 <c>Children[0] is ComboBox</c> 反推控件类型。
    /// </summary>
    private sealed record RowUi(Border Card, ToggleSwitch Toggle, FrameworkElement? Control, Action Refresh);

    public ArgsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_shellBuilt) SyncControlsFromSettings(); // 复用实例：只就地同步，不重建
            else { BuildShell(); _shellBuilt = true; }
        };
    }

    private AppConfig Settings => App.Services.Settings;

    /// <summary>轻量构建：只创建分组外壳（头部+空折叠体），行控件延迟到首次展开时生成。</summary>
    private void BuildShell()
    {
        GroupsHost.Children.Clear();
        _rows.Clear();
        _groups.Clear();

        // 初始化控件值不是用户操作：包一层 _syncing，避免 TextChanged 触发一次多余的存盘
        _syncing = true;
        try { ExtraArgsBox.Text = Settings.ExtraArgs; }
        finally { _syncing = false; }

        foreach (var group in _catalog.Groups)
        {
            var info = new GroupUi(group);
            info.Body = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };

            info.HeaderText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            info.Chevron = new FontIcon
            {
                Glyph = "\uE70D", // ChevronRight：折叠态；展开时旋转 90°
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            };

            var headerContent = new Grid { ColumnSpacing = 12 };
            headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerContent.Children.Add(info.HeaderText);
            Grid.SetColumn(info.Chevron, 1);
            headerContent.Children.Add(info.Chevron);

            info.Header = new Button
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
            info.Header.Click += (_, _) => SetExpanded(info, info.Body!.Visibility != Visibility.Visible);

            info.Container = new StackPanel { Spacing = 0 };
            info.Container.Children.Add(info.Header);
            info.Container.Children.Add(info.Body);
            GroupsHost.Children.Add(info.Container);

            _groups.Add(info);
        }

        ArgCountText.Text = $"共 {_catalog.Count} 项参数（来源 ComfyUI cli_args.py 快照）";
        UpdateGroupCounts();

        // 含已启用参数的组：只展开第一个（手风琴：任何时刻最多一个分组展开，避免页面过长）
        var firstEnabled = _groups.FirstOrDefault(g => g.ArgNames.Any(Settings.Args.IsEnabled));
        if (firstEnabled is not null) SetExpanded(firstEnabled, true);

        UpdatePreview();
    }

    /// <summary>展开/收起分组。展开时自动收起其他分组（手风琴语义：任何时刻最多一个展开）。</summary>
    private void SetExpanded(GroupUi group, bool expanded)
    {
        if (expanded)
        {
            foreach (var other in _groups)
            {
                if (ReferenceEquals(other, group)) continue;
                SetExpandedRaw(other, false);
            }
        }
        SetExpandedRaw(group, expanded);
    }

    /// <summary>只切显隐与箭头，不参与手风琴——搜索需要同时展开多个命中分组。</summary>
    private void SetExpandedRaw(GroupUi group, bool expanded)
    {
        // 只有展开时才懒建行控件；从未展开过的分组被折叠时直接返回
        if (expanded) EnsureBodyBuilt(group);
        if (group.Body is null) return;
        group.Body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        group.Chevron.RenderTransform = new Microsoft.UI.Xaml.Media.RotateTransform { Angle = expanded ? 90 : 0 };
    }

    /// <summary>首次展开时才创建该组的行控件。</summary>
    private void EnsureBodyBuilt(GroupUi group)
    {
        if (group.Built) return;
        group.Built = true;
        foreach (var arg in group.Definition.Args)
            group.Body!.Children.Add(BuildRow(arg));
    }

    /// <summary>组头计数：N 启用 · M 项。</summary>
    private void UpdateGroupCounts()
    {
        foreach (var g in _groups)
        {
            var on = g.ArgNames.Count(n => Settings.Args.IsEnabled(n));
            var text = on > 0
                ? $"{g.Definition.Title}（{on} 启用 · {g.ArgNames.Count} 项）"
                : $"{g.Definition.Title}（{g.ArgNames.Count} 项）";
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
        toggle.Toggled += (_, _) => Commit(() =>
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
        });

        var nameText = new TextBlock { FontFamily = ThemeHelper.Mono };
        nameText.Inlines.Add(new Run
        {
            Text = "--" + arg.Name,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
        });
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

        var (control, refresh) = BuildControl(arg);

        // 设置应用行布局：左侧「参数名 + 说明」，右侧「取值控件 + 启用开关」
        // 列宽随窗体比例伸缩：文本列 2*（有下限），控件列 1*（控件本身设上下限），开关列 Auto
        // 行内边距与最小行高交给 SettingsRowStyle（16,12 / 48），不再手写 16,10
        var grid = new Grid
        {
            ColumnSpacing = 12,
            Style = (Style)Application.Current.Resources["SettingsRowStyle"],
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 300 });
        if (control is not null)
        {
            control.MinWidth = 220;
            control.MaxWidth = 460;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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

        _rows[arg.Name] = new RowUi(card, toggle, control, refresh);
        SyncControlState(arg.Name);
        return card;
    }

    /// <summary>
    /// 统一的「用户改动 → 落盘 → 刷新界面」路径：六处事件处理器只写各自的 apply 逻辑，
    /// 抑制回写 / 存盘 / 预览刷新都在这里做，避免各写一遍时漏掉其中一步。
    /// </summary>
    private void Commit(Action apply)
    {
        if (_syncing) return;
        apply();
        App.Services.Save();
        UpdatePreview();
    }

    /// <summary>未启用行的取值控件灰显并禁止交互。</summary>
    private void SyncControlState(string name)
    {
        if (!_rows.TryGetValue(name, out var row) || row.Control is null) return;
        var on = row.Toggle.IsOn;
        row.Control.Opacity = on ? 1 : 0.35;
        row.Control.IsHitTestVisible = on;
    }

    /// <summary>
    /// 按参数类型构建取值控件，并**一并返回该行的 Refresh 闭包**（把 Settings 写回控件）。
    /// 两个闭包在构建时就知道自己操作的是哪个控件，外部同步时无需再判断控件类型。
    /// </summary>
    private (FrameworkElement? Control, Action Refresh) BuildControl(CliArgDefinition arg)
    {
        switch (arg.Type)
        {
            case ArgType.Flag:
                return (null, static () => { });

            case ArgType.Enum:
            case ArgType.RepeatableVerbose:
            {
                var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var choice in arg.Choices ?? new List<string>())
                {
                    combo.Items.Add(new ComboBoxItem { Content = choice });
                }
                void RefreshCombo() => combo.SelectedItem = combo.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(i => (string)i.Content == Settings.Args.Value(arg.Name));
                RefreshCombo();
                combo.SelectionChanged += (_, _) => Commit(() =>
                {
                    if (combo.SelectedItem is ComboBoxItem item)
                        Settings.Args.SetValue(arg.Name, (string)item.Content);
                });

                if (arg.Type == ArgType.Enum) return (combo, RefreshCombo);

                var fileBox = new TextBox { PlaceholderText = "可选：日志文件路径", Margin = new Thickness(0, 4, 0, 0) };
                fileBox.Text = Settings.Args.Value(arg.Name + ":file");
                fileBox.TextChanged += (_, _) =>
                    Commit(() => Settings.Args.SetValue(arg.Name + ":file", fileBox.Text.Trim()));

                var stack = new StackPanel();
                stack.Children.Add(combo);
                stack.Children.Add(fileBox);

                void RefreshVerbose()
                {
                    RefreshCombo();
                    fileBox.Text = Settings.Args.Value(arg.Name + ":file");
                }
                return (stack, RefreshVerbose);
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
                void RefreshChecks()
                {
                    var now = Settings.Args.MultiOf(arg.Name);
                    foreach (var child in panel.Children.OfType<CheckBox>())
                        child.IsChecked = now.Contains((string)child.Content);
                }
                return (panel, RefreshChecks);
            }

            case ArgType.RepeatableStringList:
            case ArgType.RepeatableKeyValue:
            {
                var box = new TextBox
                {
                    AcceptsReturn = true,
                    MinHeight = 52,
                    PlaceholderText = "每行一条",
                    FontFamily = ThemeHelper.Mono,
                };
                box.Text = string.Join(Environment.NewLine, Settings.Args.LinesOf(arg.Name));
                box.TextChanged += (_, _) => Commit(() => Settings.Args.SetLines(arg.Name, box.Text
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)));
                return (box, () => box.Text = string.Join(Environment.NewLine, Settings.Args.LinesOf(arg.Name)));
            }

            default:
            {
                var box = new TextBox
                {
                    PlaceholderText = arg.Type is ArgType.OptionalString or ArgType.OptionalInt
                        ? $"留空 = 无值传递（const {arg.Const}）"
                        : (arg.Default ?? "值"),
                    FontFamily = ThemeHelper.Mono,
                };
                box.Text = Settings.Args.Value(arg.Name);
                box.TextChanged += (_, _) =>
                    Commit(() => Settings.Args.SetValue(arg.Name, box.Text.Trim()));
                return (box, () => box.Text = Settings.Args.Value(arg.Name));
            }
        }
    }

    private void UpdateMulti(CliArgDefinition arg, StackPanel panel) =>
        Commit(() => Settings.Args.SetMulti(arg.Name, panel.Children
            .OfType<CheckBox>()
            .Where(c => c.IsChecked == true)
            .Select(c => (string)c.Content)));

    private void UpdatePreview()
    {
        UpdateGroupCounts();

        // python.exe 与 --参数 用强调色，额外参数用警示色
        var accent = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        var warn = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];

        PreviewText.Inlines.Clear();
        PreviewText.Inlines.Add(new Run { Text = ArgsPreviewFormatter.QuoteInterpreter(Settings.PythonExe), Foreground = accent });
        PreviewText.Inlines.Add(new Run { Text = " main.py" });

        // 与真实启动同源：同一次 Build（含额外参数）产出的 token 列表，预览不会再与 spawn 漂移；
        // 仅额外参数段用警示色区分
        foreach (var token in ArgsPreviewFormatter.Format(Settings.Args, _catalog, Settings.ExtraArgs))
        {
            PreviewText.Inlines.Add(new Run
            {
                Text = " " + token.Text,
                Foreground = token.IsExtra ? warn
                    : token.Text.StartsWith("--", StringComparison.Ordinal) ? accent
                    : null,
            });
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        foreach (var g in _groups)
        {
            var matches = g.ArgNames.Where(n => query.Length == 0 || n.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            var anyVisible = matches.Count > 0;
            g.Container.Visibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;
            if (!anyVisible) continue;

            // 搜索命中要能同时看到多个分组的匹配项，故绕过手风琴
            if (query.Length > 0) SetExpandedRaw(g, true);
            else
            {
                // 空查询：恢复各行可见
                foreach (var name in g.ArgNames)
                    if (_rows.TryGetValue(name, out var row)) row.Card.Visibility = Visibility.Visible;
                continue;
            }

            foreach (var name in g.ArgNames)
                if (_rows.TryGetValue(name, out var row))
                    row.Card.Visibility = matches.Contains(name) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ExtraArgsBox_TextChanged(object sender, TextChangedEventArgs e) =>
        Commit(() => Settings.ExtraArgs = ExtraArgsBox.Text);

    /// <summary>
    /// 配置被外部整体变更（恢复默认 / 模板应用）后，就地同步控件状态；
    /// 全程 _syncing 抑制写回，不重建控件树。
    /// </summary>
    /// <remarks>
    /// 所有控件的赋值都必须在这层 <c>_syncing</c> 里完成——否则 TextChanged / SelectionChanged
    /// 会把「读出来的值」当成用户输入再存一次盘，进页面就多写盘。
    /// </remarks>
    private void SyncControlsFromSettings()
    {
        _syncing = true;
        try
        {
            foreach (var name in Settings.Args.Enabled.ToList())
                if (_rows.TryGetValue(name, out var row) && row.Toggle.IsOn)
                    row.Toggle.IsOn = false; // 事件被抑制，Enabled 稍后整体覆盖
            foreach (var name in Settings.Args.Enabled)
                if (_rows.TryGetValue(name, out var row) && !row.Toggle.IsOn)
                    row.Toggle.IsOn = true;

            // 未展开的组无需处理：展开时按其时的 Settings 创建
            foreach (var row in _rows.Values) row.Refresh();
            ExtraArgsBox.Text = Settings.ExtraArgs;
        }
        finally { _syncing = false; }

        UpdatePreview();
    }
}
