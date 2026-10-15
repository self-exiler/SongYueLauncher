using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        _console = new BoundedAnsiConsole(ConsoleText, MaxConsoleLines);
        GithubMirrorBox.ItemsSource = MirrorCatalog.GithubMirrors;
        PypiMirrorBox.ItemsSource = MirrorCatalog.PypiMirrors;
        Loaded += OnLoaded;
        Unloaded += (_, _) => App.Services.StateChanged -= OnStateChanged;
    }

    private AppConfig Settings => App.Services.Settings;

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
        CatalogVersionText.Text =
            $"内置轮子清单 v{WheelCatalog.Instance.Version}（{WheelCatalog.Instance.Updated}）· 仅随软件发版更新";
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
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
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
        await RunOpAsync("检查更新", async () =>
        {
            var remote = await RepoService.QueryRemoteHashAsync(App.Services.Tasks, Settings, Append);
            var local = RepoService.ReadLocalHash(Settings.ComfyUiDir);
            DispatcherQueue.TryEnqueue(() => ProjectStatusText.Text =
                $"本地 {RepoService.ShortHash(local)} · 远端 {RepoService.ShortHash(remote)}" + (remote.Length > 0 && remote == local ? "（已最新）" : "（有更新）"));
        });
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
            if (!ok) RollbackButton.IsEnabled = File.Exists(Path.Combine(Settings.ComfyUiDir, "_pre_update_freeze.txt"));
        });
    }

    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = Path.Combine(Settings.ComfyUiDir, "_pre_update_freeze.txt");
        await RunOpAsync("回滚", async () =>
            await WheelService.RollbackAsync(App.Services.Tasks, Settings, snapshot, Append));
    }

    private async void InstallWheels_Click(object sender, RoutedEventArgs e)
    {
        var arch = ToolchainService.DetectArch();
        await RunOpAsync("安装专用轮子", () => WheelService.InstallSpecialWheelsAsync(App.Services.Tasks, Settings, arch, Append));
    }

    private async void SyncDeps_Click(object sender, RoutedEventArgs e)
    {
        await RunOpAsync("同步项目依赖", async () =>
        {
            var changes = await WheelService.SyncRequirementsAsync(App.Services.Tasks, Settings, Append);
            Append("变更包：");
            foreach (var c in changes) Append("  " + c);
        });
    }

    private async void RefreshPackages_Click(object sender, RoutedEventArgs e)
    {
        await RunOpAsync("刷新已装列表", async () =>
        {
            var lines = await WheelService.ListInstalledAsync(App.Services.Tasks, Settings);
            DispatcherQueue.TryEnqueue(() => PackageListText.Text = string.Join(Environment.NewLine, lines));
        });
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

    private static string DescribeArch(ArchInfo arch)
    {
        if (!arch.Known) return "未能识别 AMD 显卡架构（请确认驱动 / 显卡）";
        var tags = new List<string>();
        if (arch.IsLegacy) tags.Add("legacy 专用源");
        if (!arch.IsAvailable) tags.Add("暂无可用 PyTorch 构建");
        return $"{arch.Arch}" + (tags.Count > 0 ? $"（{string.Join("，", tags)}）" : string.Empty);
    }
}
