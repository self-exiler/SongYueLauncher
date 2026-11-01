using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.WinUI.Controls;
using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;
using ComfyUiRocmLauncher.GUI.Helpers;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>
/// 插件管理页（SRS FR-10 方案 A）：custom_nodes 扫描 + 启用/禁用 + 卸载备份与恢复。
/// 扫描口径与上游加载器一致（见 <see cref="PluginService"/>）；改名与移动目录都会撞上运行中的
/// ComfyUI 文件锁，因此全部写操作只在 Idle 状态放行。
/// </summary>
public sealed partial class PluginsPage : Page
{
    private List<PluginService.PluginInfo> _plugins = new();
    private readonly List<Button> _actionButtons = new();

    public PluginsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private AppConfig Settings => App.Services.Settings;

    private string BackupRoot => Path.Combine(App.Services.ConfigService.RootDirectoryPath, "plugin_backup");

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Services.StateChanged += OnStateChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => App.Services.StateChanged -= OnStateChanged;

    /// <summary>状态变化只重算按钮可用性：整表重建会打断滚动与正在进行的确认弹窗。</summary>
    private void OnStateChanged(LaunchState state) => DispatcherQueue.TryEnqueue(ApplyBusyState);

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = PluginService.CustomNodesDir(Settings);
        if (!UiHelpers.OpenFolder(dir))
            PluginOpText.Text = $"插件目录不存在：{dir}";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildRows();

    private void Refresh()
    {
        _plugins = PluginService.Scan(PluginService.CustomNodesDir(Settings)).ToList();
        RebuildRows();
        RebuildBackups();
        ApplyBusyState();
    }

    private void ApplyBusyState()
    {
        var idle = App.Services.State == LaunchState.Idle;
        InstallButton.IsEnabled = idle;
        CheckUpdatesButton.IsEnabled = idle;
        BatchUpdateButton.IsEnabled = idle;
        foreach (var button in _actionButtons) button.IsEnabled = idle;
        UpdateBatchBar();
    }

    // ---------- 安装新插件（方案 B） ----------

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (!GuardIdle()) return;

        var url = InstallUrlBox.Text.Trim();
        if (!PluginInstallService.IsClonableUrl(url))
        {
            PluginOpText.Text = "[错误] 地址无效：请粘贴 https://github.com/作者/仓库 形式的地址";
            return;
        }

        var target = Path.Combine(PluginService.CustomNodesDir(Settings), PluginInstallService.RepoName(url));
        var mirror = Settings.EnvUpdate.GithubMirror.Trim();
        var ok = await UiHelpers.ConfirmAsync(
            XamlRoot, "安装插件",
            $"将把仓库克隆到：\n{target}\n\n" +
            $"GitHub 镜像：{(mirror.Length > 0 ? mirror + "（在「环境与更新」页设置）" : "直连")}\n" +
            $"依赖：{(AutoDepsToggle.IsOn ? "自动安装（requirements.txt 或 install.py，走已设的 PyPI 镜像）" : "跳过，只克隆代码")}\n\n" +
            "克隆与安装期间请勿关闭启动器；失败会留下提示，残留目录会被自动清理。",
            "安装");
        if (!ok) return;

        await RunInstallAsync(url);
    }

    private async Task RunInstallAsync(string url)
    {
        var installed = false;
        var ran = await RunExclusiveAsync("安装", async () =>
            installed = await PluginInstallService.InstallAsync(
                App.Services.Tasks, Settings, url, AutoDepsToggle.IsOn, Append));
        if (!ran) return;

        PluginOpText.Text = installed ? "√ 安装流程结束，重启 ComfyUI 后生效" : "× 安装未完成，详见任务输出";
        if (installed) InstallUrlBox.Text = string.Empty;
        Refresh();
    }

    /// <summary>安装/检测/更新/批量卸载共用外壳：占用全局单任务槽（与「环境与更新」页同一互斥契约），异常与取消都落到输出区。</summary>
    /// <returns>false 表示任务槽被占用、本次根本没跑（调用方据此不要再报「未完成」）。</returns>
    private async Task<bool> RunExclusiveAsync(string label, Func<Task> body)
    {
        if (!App.Services.Tasks.TryStart())
        {
            PluginOpText.Text = "[提示] 已有任务在运行，请稍后再试。";
            return false;
        }

        CancelButton.IsEnabled = true;
        ConsoleHintText.Text = label + "进行中…";
        try
        {
            // 不 ConfigureAwait(false)：body 里的 Append 与末尾刷新都要回到 UI 线程碰控件
            await body();
        }
        catch (OperationCanceledException)
        {
            Append("[已取消] " + label + "（已完成的步骤保持原样，可重新执行）");
        }
        catch (Exception ex)
        {
            Append("[错误] " + ex.Message);
        }
        finally
        {
            App.Services.Tasks.Complete();
            CancelButton.IsEnabled = false;
            ConsoleHintText.Text = string.Empty;
        }
        return true;
    }

    private static string? Short(string? hash) => hash is { Length: > 7 } h ? h[..7] : hash;

    private void Append(string line) => App.Services.Log.Append(line);

    private void Cancel_Click(object sender, RoutedEventArgs e) => App.Services.Tasks.Cancel();

    // ---------- 插件列表 ----------

    /// <summary>检测结论按插件路径缓存于 Core（带 TTL），页面只留最近一次的展示副本。</summary>
    private readonly Dictionary<string, PluginUpdateService.CheckResult> _updates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _checks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 选择状态按**插件名**记忆：批量禁用/卸载会改变路径（加 .disabled 或整个移走），
    /// 以路径为键的话刷新一次勾选就全丢了，连做「禁用 → 卸载」得重新勾一遍。
    /// </summary>
    private readonly HashSet<string> _selectedNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _selectionMode;

    private void RebuildRows()
    {
        var query = SearchBox?.Text.Trim() ?? string.Empty;
        PluginListHost.Children.Clear();
        _actionButtons.Clear();
        _checks.Clear();
        foreach (var stale in _updates.Keys.Where(k => !Directory.Exists(k)).ToList()) _updates.Remove(stale);

        if (!Directory.Exists(PluginService.CustomNodesDir(Settings)))
        {
            PluginListHost.Children.Add(HintText($"插件目录不存在：{PluginService.CustomNodesDir(Settings)}"));
            PluginCountText.Text = "0 个插件";
            return;
        }

        var shown = _plugins
            .Where(p => query.Length == 0
                || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (p.RemoteUrl?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        var updatable = _plugins.Count(p => _updates.TryGetValue(p.Path, out var u) && u.HasUpdate);
        var baseCount = shown.Count == _plugins.Count
            ? $"共 {_plugins.Count} 个插件（{_plugins.Count(p => p.IsDisabled)} 已禁用）"
            : $"命中 {shown.Count} / {_plugins.Count} 个插件";
        PluginCountText.Text = updatable > 0 ? $"{baseCount} · {updatable} 可更新" : baseCount;

        if (shown.Count == 0)
            PluginListHost.Children.Add(HintText(query.Length > 0 ? "没有匹配的插件。" : "还没有安装任何插件。"));

        foreach (var plugin in shown) PluginListHost.Children.Add(BuildRow(plugin));
        UpdateBatchBar();
    }

    private SettingsCard BuildRow(PluginService.PluginInfo p)
    {
        var nameLine = new TextBlock { Text = p.Name, FontWeight = FontWeights.SemiBold };
        var repoLine = new TextBlock
        {
            Text = p.RemoteUrl ?? (p.IsDirectory ? "本地插件（无 .git 仓库）" : "单文件插件（.py）"),
            FontSize = 12,
            Foreground = ThemeHelper.Brush("TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        if (p.RemoteUrl is not null) ToolTipService.SetToolTip(repoLine, p.RemoteUrl);

        var textColumn = new StackPanel { Children = { nameLine, repoLine }, VerticalAlignment = VerticalAlignment.Center };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        if (_selectionMode)
        {
            var box = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
            // 勾选状态只记在页面上：自动化名称带上插件名，读屏能听懂勾的是哪一个
            AutomationProperties.SetName(box, $"选择插件 {p.Name}");
            box.Checked += (_, _) => { _selectedNames.Add(p.Name); UpdateBatchBar(); };
            box.Unchecked += (_, _) => { _selectedNames.Remove(p.Name); UpdateBatchBar(); };
            if (_selectedNames.Contains(p.Name)) box.IsChecked = true;
            _checks[p.Path] = box;
            header.Children.Add(box);
        }
        header.Children.Add(textColumn);

        var card = new SettingsCard
        {
            Header = header,
            Description = Describe(p),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HeaderIcon = new FontIcon
            {
                Glyph = p.IsDirectory ? "\uE8B7" : "\uE943",
                Foreground = ThemeHelper.Brush(p.IsDisabled ? "TextFillColorSecondaryBrush" : "AccentTextFillColorPrimaryBrush"),
            },
        };

        var toggle = new Button
        {
            Content = p.IsDisabled ? "启用" : "禁用",
            MinWidth = 76,
            Tag = p,
        };
        toggle.Click += Toggle_Click;

        var uninstall = new Button { Content = "卸载", MinWidth = 68, Tag = p };
        uninstall.Click += Uninstall_Click;

        _actionButtons.Add(toggle);
        _actionButtons.Add(uninstall);

        var hasUpdate = _updates.TryGetValue(p.Path, out var check) && check.HasUpdate;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (p.IsDisabled) actions.Children.Add(Badge("已禁用", "SystemFillColorCautionBrush"));
        if (hasUpdate)
        {
            // 「可更新」优先占位：这时「含依赖清单」只是次要信息，挤在一起反而看不清
            actions.Children.Add(Badge("可更新", "SystemFillColorSuccessBrush"));
            var update = new Button { Content = "更新", MinWidth = 68, Tag = p, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            update.Click += RowUpdate_Click;
            _actionButtons.Add(update);
            actions.Children.Add(update);
        }
        else if (p.HasGit && p.HasRequirements) actions.Children.Add(Badge("含依赖清单", "TextFillColorSecondaryBrush"));
        actions.Children.Add(toggle);
        actions.Children.Add(uninstall);

        card.Content = actions;
        return card;
    }

    private static string Describe(PluginService.PluginInfo p)
    {
        var bits = new List<string>();
        if (p.HasGit) bits.Add(p.Branch is null ? "git 仓库" : $"{p.Branch} {(p.Commit is null ? "" : p.Commit)}".TrimEnd());
        if (p.UpdatedAt != DateTimeOffset.MinValue)
            bits.Add("更新于 " + p.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        return bits.Count > 0 ? string.Join(" · ", bits) : "—";
    }

    private static TextBlock HintText(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(4, 8, 4, 8),
    };

    private static Border Badge(string text, string colorKey) => new()
    {
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(9, 2, 9, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Background = ThemeHelper.Tint(colorKey, 0.22),
        Child = new TextBlock { Text = text, FontSize = 12, Foreground = ThemeHelper.Brush("TextFillColorPrimaryBrush") },
    };

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (!GuardIdle() || sender is not Button { Tag: PluginService.PluginInfo p }) return;
        try
        {
            PluginService.Toggle(p);
            PluginOpText.Text = $"√ 「{p.Name}」已{(p.IsDisabled ? "启用" : "禁用")}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PluginOpText.Text = "[错误] " + ex.Message;
        }
        Refresh();
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (!GuardIdle() || sender is not Button { Tag: PluginService.PluginInfo p }) return;

        var ok = await UiHelpers.ConfirmAsync(
            XamlRoot, "卸载插件",
            $"将把「{p.Name}」整体移入卸载备份目录：\n{BackupRoot}\n\n" +
            "移入后 ComfyUI 不再加载该插件，可在页面底部「卸载备份」一键恢复；" +
            "要彻底释放磁盘空间，请在备份列表里自行到资源管理器删除。\n\n" +
            "卸载期间请勿启动 ComfyUI、勿关闭启动器。",
            "卸载");
        if (!ok) return;

        try
        {
            var dir = PluginService.Uninstall(p, BackupRoot);
            PluginOpText.Text = $"√ 已卸载「{p.Name}」，备份在 {Path.GetFileName(dir)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PluginOpText.Text = "[错误] " + ex.Message;
        }
        Refresh();
    }

    // ---------- 更新检测与批量操作（方案 C） ----------

    private enum UpdateChoice { Cancel, FastForward, Force }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (!GuardIdle()) return;
        var gitPlugins = _plugins.Count(p => p.IsDirectory && p.HasGit && p.RemoteUrl is not null);
        if (gitPlugins == 0)
        {
            PluginOpText.Text = "没有可检测的 git 插件（本地插件与单文件插件不参与更新检测）。";
            return;
        }

        await RunExclusiveAsync("检测更新", async () =>
        {
            _updates.Clear();
            Append($"[检测] git ls-remote 询问 {gitPlugins} 个插件的远端最新提交（只问不下载，4 路并发）…");
            var results = await PluginUpdateService.CheckAllAsync(App.Services.Tasks, Settings, _plugins, (name, r) =>
            {
                Append(r.Error is not null
                    ? $"      {name}：检测失败 — {r.Error}"
                    : $"      {name}：{(r.HasUpdate ? "可更新 → " + Short(r.RemoteHash) : "已是最新")}");
                return Task.CompletedTask;
            });
            foreach (var kv in results) _updates[kv.Key] = kv.Value;
            var updatable = results.Values.Count(r => r.HasUpdate);
            var failed = results.Values.Count(r => r.Error is not null);
            Append($"[检测] 完成：{updatable} 个可更新" + (failed > 0 ? $"，{failed} 个询问失败（网络或镜像不支持 info/refs）" : ""));
            UpdateSummaryText.Text = $"上次检测 {DateTime.Now:HH:mm}";
        });
        Refresh();
    }

    private async void BatchUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!GuardIdle()) return;
        var targets = _plugins.Where(p => _updates.TryGetValue(p.Path, out var u) && u.HasUpdate).ToList();
        if (targets.Count == 0)
        {
            PluginOpText.Text = "没有可更新的插件——先点「检测更新」。";
            return;
        }
        await UpdatePluginsAsync(targets, batch: true);
    }

    private async void RowUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!GuardIdle() || sender is not Button { Tag: PluginService.PluginInfo p }) return;
        await UpdatePluginsAsync([p], batch: false);
    }

    /// <summary>
    /// 更新流程：确认弹窗区分「快进」与「强制覆盖」——快进失败说明插件目录被改过或分支分叉，
    /// 此时只有用户显式选择强制覆盖才会丢弃本地改动（KK 的「最新」按钮是无条件 checkout -f，我们不照抄）。
    /// </summary>
    private async Task UpdatePluginsAsync(IReadOnlyList<PluginService.PluginInfo> targets, bool batch)
    {
        var choice = await ConfirmUpdateAsync(targets, batch);
        if (choice == UpdateChoice.Cancel) return;

        var force = choice == UpdateChoice.Force;
        var okCount = 0;
        await RunExclusiveAsync(batch ? "批量更新" : "更新插件", async () =>
        {
            foreach (var plugin in targets)
            {
                var ok = await PluginUpdateService.UpdateAsync(
                    App.Services.Tasks, Settings, plugin, force, reinstallDeps: AutoDepsToggle.IsOn, Append);
                if (ok) okCount++;
            }
            Append($"{(batch ? "[批量更新]" : "[更新]")} 完成：成功 {okCount} / {targets.Count}");
            PluginUpdateService.ForgetAll();
        });
        Refresh();
    }

    private async Task<UpdateChoice> ConfirmUpdateAsync(IReadOnlyList<PluginService.PluginInfo> targets, bool batch)
    {
        var names = targets.Count <= 4
            ? string.Join("、", targets.Select(t => t.Name))
            : string.Join("、", targets.Take(4).Select(t => t.Name)) + $" 等 {targets.Count} 个";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = batch ? "批量更新插件" : "更新插件",
            Content = new TextBlock
            {
                Text = $"将更新：{names}\n\n" +
                    "「更新（快进）」只接受能直接前进的仓库，插件目录被本地改过或分支已分叉时会失败并保留原样；\n" +
                    "「强制覆盖」会 fetch 后 checkout -f，插件目录里的本地改动将被丢弃、不可恢复。\n\n" +
                    (AutoDepsToggle.IsOn ? "更新后若 requirements.txt 有变化会自动重装依赖（走已设的 PyPI 镜像）。\n\n" : "已关闭自动安装依赖：依赖变化不会自动处理。\n\n") +
                    "更新期间请勿启动 ComfyUI、勿关闭启动器。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "更新（快进）",
            SecondaryButtonText = "强制覆盖",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => UpdateChoice.FastForward,
            ContentDialogResult.Secondary => UpdateChoice.Force,
            _ => UpdateChoice.Cancel,
        };
    }

    // ---------- 多选与批量启用 / 禁用 / 卸载 ----------

    private void SelectMode_Click(object sender, RoutedEventArgs e)
    {
        _selectionMode = !_selectionMode;
        if (!_selectionMode) _selectedNames.Clear();
        SelectModeButton.Content = _selectionMode ? "退出多选" : "多选";
        RebuildRows();
    }

    private List<PluginService.PluginInfo> SelectedPlugins() =>
        _checks.Where(kv => kv.Value.IsChecked == true)
            .Select(kv => _plugins.FirstOrDefault(p => p.Path == kv.Key))
            .OfType<PluginService.PluginInfo>()
            .ToList();

    private void UpdateBatchBar()
    {
        BatchBar.Visibility = _selectionMode ? Visibility.Visible : Visibility.Collapsed;
        if (!_selectionMode) return;
        var n = SelectedPlugins().Count;
        SelectedCountText.Text = $"已选 {n} 项";
        var usable = n > 0 && App.Services.State == LaunchState.Idle;
        BatchEnableButton.IsEnabled = usable;
        BatchDisableButton.IsEnabled = usable;
        BatchUninstallButton.IsEnabled = usable;
    }

    private void BatchEnable_Click(object sender, RoutedEventArgs e) => BatchToggle(toDisabled: false);

    private void BatchDisable_Click(object sender, RoutedEventArgs e) => BatchToggle(toDisabled: true);

    private void BatchToggle(bool toDisabled)
    {
        if (!GuardIdle()) return;
        var targets = SelectedPlugins()
            .Where(p => p.IsDisabled != toDisabled)
            .ToList();
        if (targets.Count == 0)
        {
            PluginOpText.Text = toDisabled ? "所选插件都已处于禁用状态。" : "所选插件都已处于启用状态。";
            return;
        }

        var failed = new List<string>();
        foreach (var plugin in targets)
        {
            try { PluginService.Toggle(plugin); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failed.Add($"{plugin.Name}（{ex.Message}）");
            }
        }
        PluginOpText.Text = failed.Count == 0
            ? $"√ 已{(toDisabled ? "禁用" : "启用")} {targets.Count} 个插件"
            : $"√ 已{(toDisabled ? "禁用" : "启用")} {targets.Count - failed.Count} 个，失败 {failed.Count} 个：{string.Join("；", failed)}";
        Refresh();
    }

    private async void BatchUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (!GuardIdle()) return;
        var targets = SelectedPlugins();
        if (targets.Count == 0) return;

        var ok = await UiHelpers.ConfirmAsync(
            XamlRoot, "批量卸载插件",
            $"将把 {targets.Count} 个插件整体移入卸载备份目录：\n{BackupRoot}\n\n" +
            "移入后 ComfyUI 不再加载它们，可在「卸载备份」逐项恢复；跨磁盘的插件会先复制再删源，体积大时会慢一些。\n\n" +
            "卸载期间请勿启动 ComfyUI、勿关闭启动器。",
            "卸载所选");
        if (!ok) return;

        var moved = 0;
        await RunExclusiveAsync("批量卸载", async () =>
        {
            foreach (var plugin in targets)
            {
                Append($"[卸载] {plugin.Name} …");
                try
                {
                    PluginService.Uninstall(plugin, BackupRoot);
                    moved++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    Append($"[错误] {plugin.Name}：{ex.Message}");
                }
                await Task.CompletedTask;
            }
            Append($"[批量卸载] 完成：移入备份 {moved} / {targets.Count}");
        });
        _selectionMode = false;
        _selectedNames.Clear();
        SelectModeButton.Content = "多选";
        PluginOpText.Text = $"√ 批量卸载完成：{moved} / {targets.Count} 已移入备份";
        Refresh();
    }

    // ---------- 卸载备份 ----------

    private void RebuildBackups()
    {
        // SettingsExpander 的 Items 只在整批替换时重挂数据源，逐项 Add 不生效（与 ArgsPage 同一坑）
        var backups = PluginService.ListBackups(BackupRoot);
        BackupCountText.Text = backups.Count > 0 ? $"{backups.Count} 项" : "无";

        var items = new List<object>
        {
            new SettingsCard
            {
                Header = "备份目录",
                Description = BackupRoot,
                HeaderIcon = new FontIcon { Glyph = "\uE71B" },
                Content = new Button { Content = "打开", MinWidth = 76, Tag = "open" },
            },
        };
        var open = ((SettingsCard)items[0]).Content as Button;
        if (open is not null)
        {
            open.Click += (_, _) =>
            {
                if (!UiHelpers.OpenFolder(BackupRoot)) PluginOpText.Text = $"备份目录还不存在：{BackupRoot}";
            };
            _actionButtons.Add(open);
        }

        foreach (var backup in backups)
        {
            var restore = new Button { Content = "恢复", MinWidth = 76, Tag = backup };
            restore.Click += Restore_Click;
            _actionButtons.Add(restore);

            items.Add(new SettingsCard
            {
                Header = backup.PluginName,
                Description = "卸载于 " + backup.UninstalledAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                Content = restore,
            });
        }

        BackupExpander.Items = items;
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (!GuardIdle() || sender is not Button { Tag: PluginService.BackupInfo backup }) return;

        var ok = await UiHelpers.ConfirmAsync(
            XamlRoot, "恢复插件",
            $"将把「{backup.PluginName}」从备份移回插件目录。若同名插件已存在，会以 _restored 后缀落回，不覆盖现有目录。",
            "恢复");
        if (!ok) return;

        try
        {
            PluginService.Restore(backup, PluginService.CustomNodesDir(Settings));
            PluginOpText.Text = $"√ 已恢复「{backup.PluginName}」";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PluginOpText.Text = "[错误] " + ex.Message;
        }
        Refresh();
    }

    private bool GuardIdle()
    {
        if (App.Services.State == LaunchState.Idle) return true;
        PluginOpText.Text = "ComfyUI 运行中或环境任务进行时不可操作。";
        return false;
    }
}
