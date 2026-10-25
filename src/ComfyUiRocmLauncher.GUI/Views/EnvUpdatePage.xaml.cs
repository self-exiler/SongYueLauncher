using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Text.RegularExpressions;
using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;
using ComfyUiRocmLauncher.GUI.Helpers;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>环境与更新页（SRS FR-8）：工具链 / ComfyUI 项目 / Python 轮子 三标签 + 共享任务控制台。全局单任务互斥。</summary>
public sealed partial class EnvUpdatePage : Page
{
    private bool _toolsDetected;
    private bool _mirrorsReady;
    private IReadOnlyList<ToolStatus> _tools = Array.Empty<ToolStatus>();
    private readonly BoundedAnsiConsole _console;

    public EnvUpdatePage()
    {
        InitializeComponent();
        // 按钮栏自适应：窄窗口把每张卡里的按钮组改为纵向排列，避免横向溢出
        EnvRoot.SizeChanged += (_, _) => ApplyButtonBars();
        _console = new BoundedAnsiConsole(ConsoleText, MaxConsoleLines);
        GithubMirrorBox.ItemsSource = MirrorCatalog.GithubMirrors;
        PypiMirrorBox.ItemsSource = MirrorCatalog.PypiMirrors;
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            App.Services.StateChanged -= OnStateChanged;
            // 离开页面时退回主视图，下次进入不从二级页中间状态续起
            ShowPackages(false);
        };
    }

    private AppConfig Settings => App.Services.Settings;

    /// <summary>按钮栏响应式：同一套按钮组在窄窗口转为纵向，保持设置页的留白节奏。</summary>
    private void ApplyButtonBars()
    {
        var narrow = EnvRoot.ActualWidth > 0 && EnvRoot.ActualWidth < 760;
        foreach (var panel in new[] { RepoButtons, VenvButtons, TorchButtons, PackageButtons })
            panel.Orientation = narrow ? Orientation.Vertical : Orientation.Horizontal;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Services.StateChanged += OnStateChanged;
        RepoUrlBox.Text = Settings.EnvUpdate.RepoUrl;

        // 清单外的历史自定义镜像地址回落首项（直连 / 官方），并把归一化结果写回配置
        GithubMirrorBox.SelectedIndex = MirrorCatalog.IndexOf(MirrorCatalog.GithubMirrors, Settings.EnvUpdate.GithubMirror);
        PypiMirrorBox.SelectedIndex = MirrorCatalog.IndexOf(MirrorCatalog.PypiMirrors, Settings.EnvUpdate.PypiMirror);
        var gh = SelectedMirrorUrl(GithubMirrorBox);
        var py = SelectedMirrorUrl(PypiMirrorBox);
        if (gh != Settings.EnvUpdate.GithubMirror || py != Settings.EnvUpdate.PypiMirror)
        {
            Settings.EnvUpdate.GithubMirror = gh;
            Settings.EnvUpdate.PypiMirror = py;
            App.Services.Save();
        }
        _mirrorsReady = true;

        var arch = ToolchainService.DetectArch();
        ArchText.Text = DescribeArch(arch);
        CatalogVersionText.Text = DescribeWheelCatalog();
        WheelListText.Text = string.Join("   ",
            WheelCatalog.Instance.Wheels.Select(w => w.DisplayName + (w.Optional ? "（可选）" : string.Empty)));

        if (!_toolsDetected) { await DetectToolsAsync(); _toolsDetected = true; }
        RefreshStatus();
        ApplyGuards();
    }

    private void OnStateChanged(LaunchState state) =>
        DispatcherQueue.TryEnqueue(ApplyGuards);

    // ---------- 输出控制台 ----------
    /// <summary>控制台渲染行数上限（与日志页一致），避免长任务下 Inlines 无限增长。</summary>
    private const int MaxConsoleLines = 3000;

    /// <summary>任务输出区展开时的固定高度（行高与折叠置 0 对应）。</summary>
    private const int ConsoleFixedHeight = 200;

    private void Append(string line)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _console.AppendLine(line);
            ConsoleScroll.ChangeView(null, ConsoleScroll.ExtentHeight, null);
        });
        App.Services.Log.Append(line);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => App.Services.Tasks.Cancel();

    private void ClearConsole_Click(object sender, RoutedEventArgs e) => _console.Clear();

    /// <summary>折叠任务输出区：只留标题行，为上方内容腾出空间；页面实例被缓存，状态在会话内保持。</summary>
    private bool _consoleCollapsed;

    private void ToggleConsole_Click(object sender, RoutedEventArgs e)
    {
        _consoleCollapsed = !_consoleCollapsed;
        ConsoleScroll.Visibility = _consoleCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ConsoleRow.Height = new GridLength(_consoleCollapsed ? 0 : ConsoleFixedHeight);
        ConsoleToggleLabel.Text = _consoleCollapsed ? "展开" : "收起";
        ConsoleToggleGlyph.Glyph = _consoleCollapsed ? "\uE70E" : "\uE70D";
    }

    // ---------- 通用任务执行 ----------
    private async Task RunOpAsync(string label, Func<Task> body)
    {
        if (App.Services.State != LaunchState.Idle)
        {
            Append("[提示] 当前状态不可发起环境任务（ComfyUI 运行中或已有任务）");
            return;
        }
        if (!App.Services.Tasks.TryStart())
        {
            Append("[提示] 已有任务在运行");
            return;
        }

        CancelButton.IsEnabled = true;
        ConsoleHintText.Text = label + " 进行中…";
        try
        {
            await body();
        }
        catch (OperationCanceledException)
        {
            Append("[已取消] " + label);
        }
        catch (Exception ex)
        {
            Append("[错误] " + ex.Message);
        }
        finally
        {
            App.Services.Tasks.Complete();
            CancelButton.IsEnabled = false;
            ConsoleHintText.Text = label + " 结束";
            RefreshStatus();
            ApplyGuards();
        }
    }

    // ---------- 标签一：工具链 ----------
    private async Task DetectToolsAsync()
    {
        ConsoleHintText.Text = "检测工具链…";
        _tools = await ToolchainService.DetectAsync();
        BuildToolList();
        ConsoleHintText.Text = string.Empty;
    }

    private void BuildToolList()
    {
        ToolList.Children.Clear();
        foreach (var tool in _tools)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = tool.Name,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                VerticalAlignment = VerticalAlignment.Center,
            });

            // 版本与路径分行；路径用省略号裁剪，避免长路径把行撑破窗体
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 0 };
            text.Children.Add(new TextBlock
            {
                Text = tool.Installed ? (tool.Version ?? "已安装") : "未安装",
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (tool.Installed && !string.IsNullOrEmpty(tool.Path))
                text.Children.Add(new TextBlock
                {
                    Text = tool.Path,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
                });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            if (!tool.Installed && tool.Name != "winget")
            {
                var btn = new Button { Content = "安装", Tag = tool.Name, VerticalAlignment = VerticalAlignment.Center };
                btn.Click += InstallTool_Click;
                Grid.SetColumn(btn, 2);
                row.Children.Add(btn);
            }
            ToolList.Children.Add(row);
        }
    }

    private async void Redetect_Click(object sender, RoutedEventArgs e) => await DetectToolsAsync();

    private async void InstallTool_Click(object sender, RoutedEventArgs e)
    {
        var name = (sender as Button)?.Tag as string;
        if (name is null) return;
        var winget = ToolLocator.FindWinget();
        if (winget is null)
        {
            Append($"[引导] winget 不可用：{name} 请手动安装（uv 可用 PowerShell 用户级脚本免 UAC，git 见官网）");
            return;
        }

        var id = name == "git" ? ToolchainService.GitWingetId : ToolchainService.UvWingetId;
        await RunOpAsync($"安装 {name}", async () =>
        {
            // winget 机器级安装会自行弹 UAC；完成后回到普通权限
            await App.Services.Tasks.Run(winget, ToolchainService.WingetInstallArgs(id),
                Environment.CurrentDirectory, Append).ConfigureAwait(false);
            _tools = await ToolchainService.DetectAsync();
            BuildToolList();
        });
    }

    private void GithubMirrorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_mirrorsReady) return;
        Settings.EnvUpdate.GithubMirror = SelectedMirrorUrl(GithubMirrorBox);
        App.Services.Save();
    }

    private void PypiMirrorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_mirrorsReady) return;
        Settings.EnvUpdate.PypiMirror = SelectedMirrorUrl(PypiMirrorBox);
        App.Services.Save();
    }

    private static string SelectedMirrorUrl(ComboBox box) => (box.SelectedItem as MirrorOption)?.Url ?? string.Empty;

    // ---------- 标签二：ComfyUI 项目 ----------
    private void RepoUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Settings.EnvUpdate.RepoUrl = RepoUrlBox.Text.Trim();
        App.Services.Save();
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        string? result = null;
        await RunOpAsync("检查更新", async () =>
        {
            var remote = await RepoService.QueryRemoteHashAsync(App.Services.Tasks, Settings, Append);
            var local = RepoService.ReadLocalHash(Settings.ComfyUiDir);
            // remote 为 null = 查询失败（镜像不通 / 仓库私有 / 网络中断），不能当成「有更新」
            result = remote is null
                ? $"本地 {RepoService.ShortHash(local)} · 远端版本未知（检查失败）"
                : $"本地 {RepoService.ShortHash(local)} · 远端 {RepoService.ShortHash(remote)}"
                  + (remote == local ? "（已最新）" : "（有更新）");
        });
        // RunOpAsync 的 finally 会用 RefreshStatus 重写状态行，比较结果必须等它结束后再写。
        if (result is not null) ProjectStatusText.Text = result;
    }

    private async void Clone_Click(object sender, RoutedEventArgs e)
    {
        await RunOpAsync("克隆安装", () => RepoService.CloneAsync(App.Services.Tasks, Settings, Append));
    }

    private async void Pull_Click(object sender, RoutedEventArgs e)
    {
        await RunOpAsync("拉取更新", () => RepoService.UpdateAsync(App.Services.Tasks, Settings, false, Append));
    }

    private async void Force_Click(object sender, RoutedEventArgs e)
    {
        // 强制覆盖会丢弃本地改动，属于不可撤销的环境操作：弹确认，默认按钮为「取消」
        var ok = await UiHelpers.ConfirmAsync(
            XamlRoot,
            "强制覆盖更新",
            "将用远端仓库内容强制覆盖本地文件，本地未提交的修改与新增文件可能被丢弃。确定继续？",
            "强制覆盖更新");
        if (!ok) return;

        await RunOpAsync("强制覆盖更新", () => RepoService.UpdateAsync(App.Services.Tasks, Settings, true, Append));
    }

    // ---------- 标签三：Python 轮子 ----------
    private async void CreateVenv_Click(object sender, RoutedEventArgs e)
    {
        await RunOpAsync("创建 venv", async () =>
        {
            if (await VenvService.CreateAsync(App.Services.Tasks, Settings, Append))
                Append($"解释器已写入配置：{Settings.PythonExe}");
        });
    }

    private void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        TerminalService.OpenVenvTerminal(Settings.ComfyUiDir, Append);
    }

    private async void TorchUpdate_Click(object sender, RoutedEventArgs e)
    {
        var arch = ToolchainService.DetectArch();
        await RunOpAsync("torch 更新", async () =>
        {
            RollbackButton.IsEnabled = false;
            var ok = await WheelService.TorchUpdateAsync(App.Services.Tasks, Settings, arch, Append);
            if (!ok) RollbackButton.IsEnabled = File.Exists(WheelService.SnapshotPath(Settings));
        });
    }

    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = WheelService.SnapshotPath(Settings);
        var arch = ToolchainService.DetectArch();
        await RunOpAsync("回滚", async () =>
            await WheelService.RollbackAsync(App.Services.Tasks, Settings, snapshot, arch, Append));
    }

    private async void InstallWheels_Click(object sender, RoutedEventArgs e)
    {
        var arch = ToolchainService.DetectArch();
        await RunOpAsync("安装专用轮子", () => WheelService.InstallSpecialWheelsAsync(App.Services.Tasks, Settings, arch, Append));
    }

    private async void SyncDeps_Click(object sender, RoutedEventArgs e)
    {
        await RunOpAsync("安装项目依赖", async () =>
        {
            var changes = await WheelService.SyncRequirementsAsync(App.Services.Tasks, Settings, Append);
            Append("变更包：");
            foreach (var c in changes) Append("  " + c);
        });
    }

    // ---------- 二级页：通用 PyPI 包 ----------
    // 返回入口在主窗口页头：大标题切换为「环境与更新 › 通用 PyPI 包」面包屑（Win11 设置风格）
    private void ShowPackages(bool on)
    {
        PackagesPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        MainScroll.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        var header = (MainWindow)App.Window;
        if (on) header.ShowBreadcrumb("环境与更新", "通用 PyPI 包", () => ShowPackages(false));
        else header.HideBreadcrumb();
    }

    private async void PackagesOpen_Click(object sender, RoutedEventArgs e)
    {
        ShowPackages(true);
        // 进入即刷新一次已装列表（前置不满足时按钮本就禁用）
        if (RefreshListButton.IsEnabled) await RefreshPackagesAsync();
    }

    private async void RefreshPackages_Click(object sender, RoutedEventArgs e) => await RefreshPackagesAsync();

    private async Task RefreshPackagesAsync()
    {
        await RunOpAsync("刷新已装列表", async () =>
        {
            var lines = await WheelService.ListInstalledAsync(App.Services.Tasks, Settings);
            DispatcherQueue.TryEnqueue(() => ShowPackageTable(lines));
        });
    }

    /// <summary>已装包表格的一行。</summary>
    private sealed record PackageRow(string Name, string Version);

    // ---------- 行内操作（更新 / 卸载） ----------

    /// <summary>行操作前置校验：空闲 + 有 .venv（与底部安装/卸载按钮同一约束）。</summary>
    private bool RowOpsAllowed()
    {
        if (App.Services.State != LaunchState.Idle) return false;
        if (!VenvService.VenvExists(Settings.ComfyUiDir))
        {
            PackageListHint.Text = "未创建 .venv，无法执行包操作。";
            PackageListHint.Visibility = Visibility.Visible;
            return false;
        }
        return true;
    }

    // ---------- 行内更新悬浮框（XAML 声明式 Flyout，行模板内每行各一份实例） ----------

    /// <summary>当前打开的更新悬浮框：确认执行后用它收起；只有一份打开，后被打开者覆盖。</summary>
    private Flyout? _updateFlyout;

    /// <summary>悬浮框每次打开时清空版本框（该行 Flyout 实例会复用上次的输入）。</summary>
    private void PackageUpdateFlyout_Opening(object sender, object e)
    {
        _updateFlyout = sender as Flyout;
        if (_updateFlyout?.Content is StackPanel panel)
            foreach (var box in panel.Children.OfType<TextBox>())
                box.Text = string.Empty;
    }

    /// <summary>行内「更新」确认：文本框右侧按钮，可指定版本号（留空 = 最新），确认后安装并刷新列表。</summary>
    private async void PackageUpdateConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PackageRow row } confirm) return;
        var versionBox = (confirm.Parent as StackPanel)?.Children.OfType<TextBox>().FirstOrDefault();
        if (versionBox is null) return;

        _updateFlyout?.Hide();
        if (!RowOpsAllowed()) return;

        var spec = string.IsNullOrWhiteSpace(versionBox.Text) ? row.Name : $"{row.Name}=={versionBox.Text.Trim()}";
        await RunOpAsync($"更新 {row.Name}", async () =>
        {
            await WheelService.InstallGenericAsync(App.Services.Tasks, Settings, spec, Append);
            var lines = await WheelService.ListInstalledAsync(App.Services.Tasks, Settings);
            DispatcherQueue.TryEnqueue(() => ShowPackageTable(lines));
        });
    }

    /// <summary>行内「卸载」：危险操作确认后卸载该包。</summary>
    private async void PackageRowUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PackageRow row }) return;
        if (!RowOpsAllowed()) return;

        var ok = await UiHelpers.ConfirmAsync(
            XamlRoot,
            "卸载 PyPI 包",
            $"将从当前 .venv 卸载「{row.Name} {row.Version}」。依赖它的组件可能因此无法运行，卸载不可撤销。",
            "卸载");
        if (!ok) return;

        await RunOpAsync($"卸载 {row.Name}", async () =>
        {
            await WheelService.UninstallAsync(App.Services.Tasks, Settings, row.Name, Append);
            var lines = await WheelService.ListInstalledAsync(App.Services.Tasks, Settings);
            DispatcherQueue.TryEnqueue(() => ShowPackageTable(lines));
        });
    }

    /// <summary>
    /// 把 uv pip list 的表格输出解析为行记录并填充 ListView。
    /// 跳过表头（Package / Version）与分隔线；解析不出两列的行（异常输出）原样进「包名」列并给出提示。
    /// </summary>
    private static IReadOnlyList<PackageRow> ParsePackageList(IEnumerable<string> lines)
    {
        var rows = new List<PackageRow>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith("Package", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.All(c => c == '-' || c == ' ')) continue; // ---- 分隔线

            var m = Regex.Match(line, @"^(\S+)\s{2,}(\S.*)$");
            if (m.Success) rows.Add(new PackageRow(m.Groups[1].Value, m.Groups[2].Value.Trim()));
            else rows.Add(new PackageRow(line.Trim(), string.Empty)); // 单列行兜底，不丢数据
        }
        return rows;
    }

    private void ShowPackageTable(IReadOnlyList<string> lines)
    {
        var rows = ParsePackageList(lines);
        PackageList.ItemsSource = rows;
        PackageCountText.Text = rows.Count > 0 ? $"共 {rows.Count} 个包" : string.Empty;
        var unparseable = rows.Count(r => r.Version.Length == 0);
        PackageListHint.Text = rows.Count == 0
            ? "未获取到已装包（环境未创建或 pip list 无输出）。"
            : unparseable > 0 ? $"{unparseable} 行未能解析出版本列，已原样显示在包名列。" : string.Empty;
        PackageListHint.Visibility = string.IsNullOrEmpty(PackageListHint.Text)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void InstallPackage_Click(object sender, RoutedEventArgs e)
    {
        var spec = PackageInputBox.Text.Trim();
        if (spec.Length == 0) return;
        await RunOpAsync("安装包", () => WheelService.InstallGenericAsync(App.Services.Tasks, Settings, spec, Append));
    }

    private async void UninstallPackage_Click(object sender, RoutedEventArgs e)
    {
        var pkg = PackageInputBox.Text.Trim();
        if (pkg.Length == 0) return;

        var ok = await UiHelpers.ConfirmAsync(
            XamlRoot,
            "卸载 PyPI 包",
            $"将从当前 .venv 卸载「{pkg}」。依赖它的组件可能因此无法运行，卸载不可撤销。",
            "卸载");
        if (!ok) return;

        await RunOpAsync("卸载包", () => WheelService.UninstallAsync(App.Services.Tasks, Settings, pkg, Append));
    }

    // ---------- 状态 / 前置校验 ----------
    private void RefreshStatus()
    {
        var dir = Settings.ComfyUiDir;
        var installed = RepoService.IsInstalled(dir);
        var local = RepoService.ReadLocalHash(dir);
        var venv = VenvService.VenvExists(dir);
        ProjectStatusText.Text = installed
            ? $"已安装于 {dir}（本地版本 {RepoService.ShortHash(local)}）"
            : "未检测到 main.py，尚未安装";
        VenvStatusText.Text = venv
            ? $"已就绪：{VenvService.VenvPython(dir)}"
            : "未创建（所有轮子操作前需先创建 .venv）";
    }

    private void ApplyGuards()
    {
        var idle = App.Services.State == LaunchState.Idle;
        var hasGit = _tools.Any(t => t.Name == "git" && t.Installed);
        var hasUv = _tools.Any(t => t.Name == "uv" && t.Installed);
        var installed = RepoService.IsInstalled(Settings.ComfyUiDir);
        var venv = VenvService.VenvExists(Settings.ComfyUiDir);
        var arch = ToolchainService.DetectArch();

        RedetectButton.IsEnabled = idle;
        CloneButton.IsEnabled = idle && hasGit && !installed && !string.IsNullOrWhiteSpace(Settings.EnvUpdate.RepoUrl);
        CheckButton.IsEnabled = idle && hasGit && installed && !string.IsNullOrWhiteSpace(Settings.EnvUpdate.RepoUrl);
        PullButton.IsEnabled = idle && hasGit && installed;
        ForceButton.IsEnabled = idle && hasGit && installed;
        CreateVenvButton.IsEnabled = idle && hasUv && installed;
        OpenTerminalButton.IsEnabled = venv;

        TorchUpdateButton.IsEnabled = idle && venv && arch.Known && arch.IsAvailable;
        WheelsButton.IsEnabled = idle && venv;
        DepsButton.IsEnabled = idle && venv && installed;
        RefreshListButton.IsEnabled = idle && venv;
        PkgInstallButton.IsEnabled = idle && venv;
        PkgUninstallButton.IsEnabled = idle && venv;

        if (idle && !venv) ConsoleHintText.Text = "提示：请先创建 .venv";
        else if (idle && !arch.IsAvailable && arch.Known) ConsoleHintText.Text = "当前架构暂无可用 torch 构建";
    }

    /// <summary>轮子清单来源提示：外部文件优先（可替换更新），解析失败回落内嵌时附上原因。</summary>
    private static string DescribeWheelCatalog()
    {
        var catalog = WheelCatalog.Instance;
        var source = catalog.FromExternalFile
            ? "外部文件 wheel-catalog.json（编辑后重启生效）"
            : "程序内置";
        var text = $"轮子清单 v{catalog.Version}（{catalog.Updated}）· 来源：{source}";
        return catalog.ExternalError is null ? text : $"{text} ⚠ 外部清单解析失败已回落内置：{catalog.ExternalError}";
    }

    private static string DescribeArch(ArchInfo arch)
    {
        if (!arch.Known) return "未能识别 AMD 显卡架构（请确认驱动 / 显卡）";
        var tags = new List<string>();
        if (arch.IsLegacy) tags.Add("legacy 专用源");
        if (!arch.IsAvailable) tags.Add("暂无可用 PyTorch 构建");
        return $"{arch.Arch}" + (tags.Count > 0 ? $"（{string.Join("，", tags)}）" : string.Empty);
    }
}
