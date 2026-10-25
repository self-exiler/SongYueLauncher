using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using CommunityToolkit.WinUI.Controls;
using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;
using ComfyUiRocmLauncher.GUI.Helpers;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>启动参数页（SRS FR-2）：数据驱动生成全部参数控件 + 实时命令预览。分组行渐进构建以避免进入页面卡顿。</summary>
public sealed partial class ArgsPage : Page
{
    private readonly CliArgsCatalog _catalog = CliArgsCatalog.Instance;
    private readonly Dictionary<string, RowUi> _rows = new(StringComparer.Ordinal);
    private readonly List<GroupUi> _groups = new();
    private bool _syncing;
    private bool _shellBuilt;
    /// <summary>搜索同步时抑制手风琴互斥：搜索需要同时展开多个命中分组。</summary>
    private bool _suppressAccordion;

    /// <summary>
    /// 一个参数分组的 UI 引用。分组外壳是 SettingsExpander（自带展开动画 / 悬停 / chevron），
    /// 行控件由 BuildAllRowsAsync 在页面载入后渐进创建，直接作为 Expander 的 Items。
    /// </summary>
    private sealed class GroupUi(CliArgGroup definition)
    {
        public readonly CliArgGroup Definition = definition;
        public SettingsExpander Expander = null!;
        /// <summary>分组头的右侧计数文本（N 启用 · M 项）。</summary>
        public TextBlock CountText = null!;
        public bool Built;
        /// <summary>构造时缓存参数名列表：计数与搜索会反复按分组取用，避免每次重新 Select().ToList()。</summary>
        public readonly IReadOnlyList<string> ArgNames = definition.Args.Select(a => a.Name).ToList();
    }

    /// <summary>
    /// 一个参数行的 UI 引用（行网格 / 文本列 / 开关 / 取值控件）。
    /// 行是 SettingsExpander 内容区里的「平底行」：无卡片边框，靠分隔线区隔（避免卡中卡）。
    /// <c>Refresh</c> 是该行「把 Settings 写回控件」的唯一途径：构建控件时一并生成，
    /// 因此不必再靠 <c>Children[0] is ComboBox</c> 反推控件类型。
    /// 行网格与文本列一并缓存：窄窗口需要把这些元素从「左右两列」重排为「上下两行」。
    /// </summary>
    private sealed record RowUi(Grid Row, StackPanel Text, ToggleSwitch Toggle,
        FrameworkElement? Control, Action Refresh);

    public ArgsPage()
    {
        InitializeComponent();
        // 窄窗口时参数行要重排为上下两行：监听内容区实际宽度，避免 300/220 固定列宽把行挤出可视区
        GroupsHost.SizeChanged += (_, _) => ApplyRowLayout();
        Loaded += async (_, _) =>
        {
            if (_shellBuilt) { SyncControlsFromSettings(); return; } // 复用实例：只就地同步，不重建
            BuildShell();
            _shellBuilt = true;
            await BuildAllRowsAsync();
        };
    }

    private AppConfig Settings => App.Services.Settings;

    /// <summary>轻量构建：只创建分组外壳（SettingsExpander 头部），行控件随后由 BuildAllRowsAsync 渐进生成。</summary>
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
            info.CountText = new TextBlock { Style = (Style)Application.Current.Resources["SecondaryTextStyle"] };

            info.Expander = new SettingsExpander
            {
                Header = group.Title,
                Content = info.CountText,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            // 用户点开某组时收起其他组（手风琴：任何时刻最多一个分组展开）；
            // 搜索走 SetExpandedRaw 并以 _suppressAccordion 旁路本互斥
            info.Expander.Expanded += (_, _) =>
            {
                if (_suppressAccordion) return;
                foreach (var other in _groups)
                    if (!ReferenceEquals(other, info) && other.Expander.IsExpanded)
                        other.Expander.IsExpanded = false;
            };

            GroupsHost.Children.Add(info.Expander);

            _groups.Add(info);
        }

        ArgCountText.Text = $"共 {_catalog.Count} 项参数（来源 ComfyUI cli_args.py 快照）";
        UpdateGroupCounts();

        // 含已启用参数的组：只展开第一个（手风琴：任何时刻最多一个分组展开，避免页面过长）
        var firstEnabled = _groups.FirstOrDefault(g => g.ArgNames.Any(Settings.Args.IsEnabled));
        if (firstEnabled is not null) firstEnabled.Expander.IsExpanded = true;

        UpdatePreview();
        ApplyRowLayout();
    }

    /// <summary>
    /// 渐进式创建全部分组行：每组构建后让出 UI 线程，避免一次性创建 110 行卡顿（NFR-1）。
    /// 行不再「首次展开时懒建」——SettingsExpander 没有 Expanding 事件，展开后再建会闪空内容。
    /// </summary>
    private async Task BuildAllRowsAsync()
    {
        foreach (var group in _groups)
        {
            EnsureBodyBuilt(group);
            await Task.Yield(); // DispatcherQueue 同步上下文保证回到 UI 线程继续
        }
    }

    /// <summary>只切展开态，不参与手风琴——搜索需要同时展开多个命中分组。</summary>
    private void SetExpandedRaw(GroupUi group, bool expanded)
    {
        // 只有展开时才懒建行控件；未建过行就折叠是无操作
        if (expanded) EnsureBodyBuilt(group);
        group.Expander.IsExpanded = expanded;
    }

    /// <summary>创建该组的行控件（平底行 + 行间分隔线，直接进 Expander.Items）。</summary>
    private void EnsureBodyBuilt(GroupUi group)
    {
        if (group.Built) return;
        group.Built = true;
        var items = group.Expander.Items;
        for (var i = 0; i < group.Definition.Args.Count; i++)
        {
            if (i > 0) items.Add(new Border { Style = (Style)Application.Current.Resources["CardDividerStyle"] });
            items.Add(BuildRow(group.Definition.Args[i]));
        }
    }

    /// <summary>组头计数：N 启用 · M 项（计数放右侧 Content，不混进标题文本）。</summary>
    private void UpdateGroupCounts()
    {
        foreach (var g in _groups)
        {
            var on = g.ArgNames.Count(n => Settings.Args.IsEnabled(n));
            var text = on > 0 ? $"{on} 启用 · {g.ArgNames.Count} 项" : $"{g.ArgNames.Count} 项";
            g.CountText.Text = text;
            // Expander 的 Header 是纯标题字符串：自动化名称补上完整语义（含计数）
            AutomationProperties.SetName(g.Expander, $"{g.Definition.Title}（{text}）");
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
        // 开关本身没有可见标题：把参数名与说明写进自动化名称/帮助文本，读屏才知道在开什么
        AutomationProperties.SetName(toggle, $"启用参数 --{arg.Name}");
        AutomationProperties.SetHelpText(toggle, arg.Description);
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
        if (control is not null)
        {
            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            control.MaxWidth = 460;
            AutomationProperties.SetName(control, $"--{arg.Name} 取值");
        }

        // 设置应用行布局：宽窗口「左侧参数名 + 说明，右侧取值控件与启用开关」；
        // 窄窗口由 ApplyRowLayout 重排为上下两段，不再依赖 300/220 固定列宽。
        // 行是 Expander 内容区里的平底行（无卡片边框），行内边距与最小行高交给 SettingsRowStyle（16,12 / 48）。
        // Tag 记参数名：搜索过滤时靠它同步「行 + 前置换行分隔线」的可见性。
        var grid = new Grid
        {
            ColumnSpacing = 12,
            Style = (Style)Application.Current.Resources["SettingsRowStyle"],
            Tag = arg.Name,
        };

        grid.Children.Add(textColumn);
        if (control is not null) grid.Children.Add(control);
        grid.Children.Add(toggle);

        _rows[arg.Name] = new RowUi(grid, textColumn, toggle, control, refresh);
        ApplyRowLayout();
        SyncControlState(arg.Name);
        return grid;
    }

    /// <summary>
    /// 参数行响应式布局：内容区较窄时改为「说明一行、取值控件与启用开关一行」，
    /// 避免固定列宽把控件挤出可视区（窄窗口下也不再出现横向挤压）。
    /// </summary>
    private void ApplyRowLayout()
    {
        // GroupsHost 位于 ScrollViewer 内，其 ActualWidth 即行可用宽度（已扣除页面边距与滚动条）
        var narrow = GroupsHost.ActualWidth > 0 && GroupsHost.ActualWidth < 640;
        foreach (var row in _rows.Values) ApplyRowLayout(row, narrow);
    }

    private static void ApplyRowLayout(RowUi row, bool narrow)
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

            Grid.SetRow(row.Text, 0);
            Grid.SetColumn(row.Text, 0);
            Grid.SetColumnSpan(row.Text, 2);

            if (row.Control is not null)
            {
                Grid.SetRow(row.Control, 1);
                Grid.SetColumn(row.Control, 0);
                Grid.SetColumnSpan(row.Control, 1);
            }
            // 无取值控件（纯开关参数）时，开关单独占第二行右侧
            Grid.SetRow(row.Toggle, 1);
            Grid.SetColumn(row.Toggle, 1);
            Grid.SetColumnSpan(row.Toggle, 1);
            return;
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(2, GridUnitType.Star),
            MinWidth = 180,
        });
        if (row.Control is not null)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
                MinWidth = 160,
            });
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetRow(row.Text, 0);
        Grid.SetColumn(row.Text, 0);
        Grid.SetColumnSpan(row.Text, 1);

        if (row.Control is not null)
        {
            Grid.SetRow(row.Control, 0);
            Grid.SetColumn(row.Control, 1);
            Grid.SetColumnSpan(row.Control, 1);
        }
        Grid.SetRow(row.Toggle, 0);
        Grid.SetColumn(row.Toggle, row.Control is null ? 1 : 2);
        Grid.SetColumnSpan(row.Toggle, 1);
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

    /// <summary>
    /// 未启用行的取值控件必须真正禁用：只改透明度/命中测试会留下一个「看得见灰、键盘仍能编辑」的控件。
    /// 灰显交给原生控件模板，自动化树也会同步为 Disabled。
    /// </summary>
    private void SyncControlState(string name)
    {
        if (!_rows.TryGetValue(name, out var row)) return;
        SetControlEnabled(row.Control, row.Toggle.IsOn);
    }

    /// <summary>
    /// 真正禁用取值控件：Control 直接置 IsEnabled=false（键盘焦点与自动化状态同步）；
    /// 组合控件（如「下拉 + 路径框」的 StackPanel）递归禁用其中的控件，容器本身再灰显并停止命中。
    /// </summary>
    private static void SetControlEnabled(FrameworkElement? element, bool enabled)
    {
        if (element is null) return;

        if (element is Control control)
        {
            control.IsEnabled = enabled;
            return;
        }

        element.Opacity = enabled ? 1 : 0.35;
        element.IsHitTestVisible = enabled;
        if (element is Panel panel)
        {
            foreach (var child in panel.Children.OfType<FrameworkElement>())
                SetControlEnabled(child, enabled);
        }
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
                AutomationProperties.SetName(fileBox, $"--{arg.Name} 日志文件路径");
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
                    // 多个开关同处一行，读屏需要知道每个勾选项属于哪个参数
                    AutomationProperties.SetName(check, $"--{arg.Name} {choice}");
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

        // python.exe 与 --参数 用强调色，额外参数用警示色；其余（取值）显式给正文色——
        // 传 null 让 Run 自己继承在本机实测会渲染成与背景同色（2026-10-18 报障）。
        var accent = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        var warn = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        var normal = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        PreviewText.Inlines.Clear();
        PreviewText.Inlines.Add(new Run { Text = ArgsPreviewFormatter.QuoteInterpreter(Settings.PythonExe), Foreground = accent });
        PreviewText.Inlines.Add(new Run { Text = " main.py", Foreground = normal });

        // 与真实启动同源：同一次 Build（含额外参数）产出的 token 列表，预览不会再与 spawn 漂移；
        // 是否参数名由 Core 在加引号前判定（IsFlag），界面不再对已加引号的文本做前缀匹配
        foreach (var token in ArgsPreviewFormatter.Format(Settings.Args, _catalog, Settings.ExtraArgs))
        {
            PreviewText.Inlines.Add(new Run
            {
                Text = " " + token.Text,
                Foreground = token.IsExtra ? warn : token.IsFlag ? accent : normal,
            });
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        // 展开命中分组时旁路手风琴互斥：搜索需要同时看到多个分组
        _suppressAccordion = true;
        try
        {
            foreach (var g in _groups)
            {
                var matches = g.ArgNames.Where(n => query.Length == 0 || n.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
                var anyVisible = matches.Count > 0;
                g.Expander.Visibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;
                if (!anyVisible) continue;

                if (query.Length == 0)
                {
                    // 空查询：恢复该组全部行（未建过行的组无需处理，展开时会按其时的 Settings 创建）
                    SyncRowVisibility(g, null);
                    continue;
                }

                // 搜索命中要能同时看到多个分组的匹配项，故绕过手风琴
                SetExpandedRaw(g, true);
                SyncRowVisibility(g, matches.Contains);
            }
        }
        finally { _suppressAccordion = false; }
    }

    /// <summary>
    /// 同步一组内「行 + 前置换行分隔线」的可见性：行按 match 过滤；
    /// 分隔线仅在其前后都有可见行时保留，避免隐藏行处残留空线或双线。
    /// </summary>
    private static void SyncRowVisibility(GroupUi group, Func<string, bool>? match)
    {
        var items = group.Expander.Items.OfType<FrameworkElement>().ToList();
        var seenVisible = false;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is Grid row && row.Tag is string name)
            {
                var visible = match is null || match(name);
                row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (visible) seenVisible = true;
                continue;
            }
            if (items[i] is Border divider)
            {
                var nextVisible = i + 1 < items.Count && items[i + 1] is Grid next
                    && next.Visibility == Visibility.Visible;
                divider.Visibility = seenVisible && nextVisible ? Visibility.Visible : Visibility.Collapsed;
            }
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
