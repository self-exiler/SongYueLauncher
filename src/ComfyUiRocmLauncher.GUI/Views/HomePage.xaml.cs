using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;
using ComfyUiRocmLauncher.GUI.Helpers;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>主页（SRS FR-1 + FR-7）：路径设置、启动控制（含 GPU 模板与导出脚本）、状态显示、快捷目录。</summary>
public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        // 按钮栏自适应：窄窗口放不下五个快捷目录时改为纵向排列，而不是横向溢出。
        // SettingsCard 的内容列是按内容自适配宽度的，量不到「可用宽度」，因此改按页面宽度判断；
        // 阈值 ≈ 按钮组所需 560 + 页面边距 72 + 卡片内边距/图标/标题 ≈ 720
        SizeChanged += (_, _) =>
            QuickDirsPanel.Orientation = ActualWidth < 720 ? Orientation.Vertical : Orientation.Horizontal;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private AppConfig Settings => App.Services.Settings;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ComfyDirBox.Text = Settings.ComfyUiDir;
        PythonBox.Text = Settings.PythonExe;

        var uv = ToolLocator.FindUv();
        // 「uv.exe」标签由卡片 Header 承担，这里只写状态本身
        UvStatusText.Text = uv is not null
            ? $"已自动发现 {uv}"
            : "未找到，请在环境变量中安装 uv 或稍后手动指定（winget install astral-sh.uv）";

        // 行标签已在 XAML 中给出，代码只赋值
        GpuText.Text = GpuInfoService.GetAmdGpuName();
        VersionHashText.Text = App.Services.CurrentVersionHash();
        _ = RefreshVersionFromGitAsync();

        App.Services.StateChanged += OnStateChanged;
        RefreshState();
        RefreshQuickDirs();
        RefreshTemplates();
    }

    /// <summary>git 检出每次读取本地 HEAD（FR-1.3），外部 pull 后也能刷新显示。</summary>
    private async Task RefreshVersionFromGitAsync()
    {
        var version = await App.Services.RefreshVersionFromGitAsync();
        if (version is not null) DispatcherQueue.TryEnqueue(() => VersionHashText.Text = version);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Services.StateChanged -= OnStateChanged;
    }

    private void OnStateChanged(LaunchState state) => DispatcherQueue.TryEnqueue(RefreshState);

    private void RefreshState()
    {
        var state = App.Services.State;
        RunStateText.Text = LaunchStates.Text(state);
        StartButton.IsEnabled = state == LaunchState.Idle;
        StopButton.IsEnabled = state is LaunchState.Starting or LaunchState.Running;
        BrowserButton.IsEnabled = state == LaunchState.Running;
        ApplyButton.IsEnabled = state == LaunchState.Idle;
        SaveAsButton.IsEnabled = state == LaunchState.Idle;
        VersionHashText.Text = App.Services.CurrentVersionHash();

        // 状态点配色：强调状态取状态色，空闲用次级前景（disabled 刷子语义上是「控件禁用」）
        RunStateDot.Fill = ThemeHelper.Brush(ThemeHelper.StateAccentKey(state) ?? "TextFillColorSecondaryBrush");

        // 外置链接操作会搬移文件，ComfyUI 运行中或环境任务进行时必须禁入（含 BusyEnv）
        RefreshDirLinks();
    }

    /// <summary>FR-1.4：快捷目录不存在时对应按钮禁用。</summary>
    private void RefreshQuickDirs()
    {
        foreach (var button in new[] { QdOutput, QdModels, QdWorkflow, QdCustomNodes, QdInput })
        {
            var target = Path.Combine(Settings.ComfyUiDir, (string)button.Tag);
            button.IsEnabled = !string.IsNullOrWhiteSpace(Settings.ComfyUiDir) && Directory.Exists(target);
        }
    }

    private void ComfyDirBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Settings.ComfyUiDir = ComfyDirBox.Text.Trim();
        App.Services.Save();
        RefreshQuickDirs();
        RefreshDirLinks();

        // ComfyUI 目录变更时，若用户未手动改过解释器则联动刷新默认值（FR-1.1）
        if (!string.IsNullOrWhiteSpace(Settings.ComfyUiDir) && string.IsNullOrWhiteSpace(PythonBox.Text))
        {
            PythonBox.Text = VenvService.VenvPython(Settings.ComfyUiDir);
        }
    }

    private void PythonBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Settings.PythonExe = PythonBox.Text.Trim();
        App.Services.Save();
    }

    private async void BrowseComfyDir_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ComfyDirBox.Text = folder.Path;
    }

    private async void BrowsePython_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var file = await picker.PickSingleFileAsync();
        if (file is not null) PythonBox.Text = file.Path;
    }

    private async void VerifyPython_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(Settings.PythonExe))
        {
            PythonVerifyText.Text = "× 文件不存在";
            return;
        }

        PythonVerifyText.Text = "验证中…";
        try
        {
            var captured = new List<string>();
            // 工作目录无效（如 ComfyUI 目录未填/已删）时退回当前目录，避免 Process.Start 抛异常
            var workDir = PathHelpers.ExistingDirOrCurrent(Settings.ComfyUiDir);
            var code = await ProcessService.RunCommandAsync(
                Settings.PythonExe, "--version", workDir,
                line => captured.Add(line));

            PythonVerifyText.Text = code == 0
                ? "✓ " + string.Join(' ', captured).Trim()
                : "× 执行失败（退出码 " + code + "）";
        }
        catch (Exception ex)
        {
            PythonVerifyText.Text = "× 执行失败：" + ex.Message;
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (App.Services.State != LaunchState.Idle) return;

        try
        {
            // 就绪后不代开浏览器：自动打开只由启动参数 --auto-launch 决定（SRS v1.17）
            await App.Services.Launch.StartAsync(Settings, TimeSpan.FromSeconds(180));
        }
        catch (Exception ex)
        {
            // LaunchService 失败时已自行复位为 Idle，这里只提示
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "启动失败",
                Content = ex.Message,
                CloseButtonText = "确定",
            }.ShowAsync();
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await App.Services.Launch.StopAsync();

    private void OpenBrowser_Click(object sender, RoutedEventArgs e) =>
        UiHelpers.OpenUrl(ArgsBuilder.DeriveBrowserUrl(Settings.Args));

    private void QuickDir_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string relative) return;
        UiHelpers.OpenFolder(Path.Combine(Settings.ComfyUiDir, relative));
    }

    // ---------- 目录外置链接（FR-1.5） ----------

    /// <summary>四个链接控件按目录名成组，避免 models / output 两份重复代码。</summary>
    private (string Name, TextBlock Status, Button Link, Button Unlink)[] DirLinkRows() =>
    [
        ("models", ModelsLinkStatus, ModelsLinkButton, ModelsUnlinkButton),
        ("output", OutputLinkStatus, OutputLinkButton, OutputUnlinkButton),
    ];

    /// <summary>刷新外置链接状态；文件系统是真相，config 记录只用于回显。非 Idle 状态禁入（搬文件不能与运行/任务并发）。</summary>
    private void RefreshDirLinks()
    {
        var idle = App.Services.State == LaunchState.Idle;
        var comfyReady = !string.IsNullOrWhiteSpace(Settings.ComfyUiDir) && Directory.Exists(Settings.ComfyUiDir);
        foreach (var (name, status, link, unlink) in DirLinkRows())
        {
            var info = comfyReady ? DirLinkService.Inspect(Settings.ComfyUiDir, name) : null;
            status.Text = info switch
            {
                null => "请先设置有效的 ComfyUI 目录",
                { IsLink: true, TargetPath: not null } =>
                    info.TargetExists ? $"已外置 → {info.TargetPath}" : $"链接失效（外部目录不存在）：{info.TargetPath}",
                { IsLink: true } => "已链接（无法解析目标目录）",
                { Exists: true, HasContent: true } => "未链接（普通目录；外置时会先把其中内容迁往外部目录）",
                { Exists: true } => "未链接（空目录）",
                _ => "目录不存在（首次启动 ComfyUI 后生成，也可直接外置）",
            };
            link.IsEnabled = idle && comfyReady;
            unlink.IsEnabled = idle && comfyReady && info is { IsLink: true };
        }
    }

    private async void LinkDir_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string name) return;
        if (App.Services.State != LaunchState.Idle)
        {
            DirLinkOpText.Text = "ComfyUI 运行中或环境任务进行时不可操作。";
            return;
        }

        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        var target = DirLinkService.TargetPath(folder.Path, name);
        var ok = await UiHelpers.ConfirmAsync(
            XamlRoot, "外置目录链接",
            $"将把「{name}」链接到外部目录：\n{target}\n\n" +
            "目录中的现有内容会先迁移过去（同一磁盘瞬时完成，跨磁盘复制、耗时与体积成正比；同名文件会被覆盖），" +
            "随后原位置变为目录联接。此后模型与输出都存放在整合包之外，删除或重装整合包不会影响它们。\n\n" +
            "迁移期间请勿启动 ComfyUI、勿关闭启动器。",
            "外置");
        if (!ok) return;

        await RunDirLinkOpAsync($"外置 {name}", async () =>
        {
            var warning = await DirLinkService.LinkAsync(App.Services.Tasks, Settings.ComfyUiDir, name, folder.Path,
                msg => DispatcherQueue.TryEnqueue(() => DirLinkOpText.Text = msg));
            DirLinkOpText.Text = warning is null
                ? $"√ 「{name}」已外置到 {target}"
                : $"√ 「{name}」已外置到 {target}（{warning}）";
            Settings.ExternalDirLinks[name] = target;
            App.Services.Save();
        });
    }

    private async void UnlinkDir_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string name) return;
        if (App.Services.State != LaunchState.Idle)
        {
            DirLinkOpText.Text = "ComfyUI 运行中或环境任务进行时不可操作。";
            return;
        }

        var info = DirLinkService.Inspect(Settings.ComfyUiDir, name);
        if (!info.IsLink)
        {
            DirLinkOpText.Text = $"「{name}」目前不是目录链接。";
            return;
        }

        var ok = await UiHelpers.ConfirmAsync(
            XamlRoot, "还原目录链接",
            $"将解除「{name}」的外置链接：删除目录联接（外部目录中的数据不受影响），把其中内容搬回整合包目录" +
            "（同一磁盘瞬时完成，跨磁盘复制、耗时与体积成正比），恢复为普通目录。搬不动的文件会保留在外部目录。\n\n" +
            "迁移期间请勿启动 ComfyUI、勿关闭启动器。",
            "还原");
        if (!ok) return;

        await RunDirLinkOpAsync($"还原 {name}", async () =>
        {
            var warning = await DirLinkService.UnlinkAsync(App.Services.Tasks, Settings.ComfyUiDir, name,
                msg => DispatcherQueue.TryEnqueue(() => DirLinkOpText.Text = msg));
            DirLinkOpText.Text = warning is null
                ? $"√ 「{name}」已还原为整合包内普通目录"
                : $"√ 「{name}」已还原（{warning}）";
            Settings.ExternalDirLinks.Remove(name);
            App.Services.Save();
        });
    }

    /// <summary>外置/还原共用外壳：占用全局任务槽（BusyEnv 期间禁启动）、异常落到状态行、结束刷新两块目录区。</summary>
    private async Task RunDirLinkOpAsync(string label, Func<Task> body)
    {
        if (!App.Services.Tasks.TryStart())
        {
            DirLinkOpText.Text = "已有任务在运行，请稍后再试。";
            return;
        }

        DirLinkOpText.Text = label + "进行中…";
        try
        {
            await body();
        }
        catch (OperationCanceledException)
        {
            DirLinkOpText.Text = "[已取消] " + label + "（已迁移的文件保持现状，可重新执行）";
        }
        catch (Exception ex)
        {
            DirLinkOpText.Text = "[错误] " + ex.Message;
        }
        finally
        {
            App.Services.Tasks.Complete();
            RefreshDirLinks();
            RefreshQuickDirs();
        }
    }

    // ---------- GPU 架构模板（原配置管理页，SRS FR-7） ----------

    private static TemplateCatalog Catalog => TemplateCatalog.Instance;

    private GpuTemplate? SelectedTemplate => TemplateCombo.SelectedItem as GpuTemplate;

    private void RefreshTemplates(string? keepId = null)
    {
        var list = Catalog.AllTemplates(Settings);
        var keep = keepId ?? SelectedTemplate?.Id;
        // 默认预选：按检测到的显卡架构映射模板（ArchInfo.TemplateId），识别不到退回 rdna3
        var fallback = ToolchainService.DetectArch().TemplateId ?? "rdna3";
        TemplateCombo.ItemsSource = list;
        TemplateCombo.SelectedItem =
            list.FirstOrDefault(t => string.Equals(t.Id, keep, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(t => string.Equals(t.Id, fallback, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault();
    }

    private void TemplateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        DeleteButton.IsEnabled = SelectedTemplate?.IsCustom == true;

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is not { } tpl) return;
        if (App.Services.State != LaunchState.Idle) return;

        // 应用模板会整体替换环境变量与启动参数：这是不可撤销的面状修改，必须先确认
        var ok = await UiHelpers.ConfirmAsync(
            XamlRoot,
            "应用 GPU 架构模板",
            $"将用「{tpl.Title}」整体替换当前的环境变量与启动参数。替换后仍可在「启动参数 / 环境变量」页继续微调，但现有取值会被覆盖。",
            "应用模板");
        if (ok) ApplyTemplate(tpl);
    }

    private void ApplyTemplate(GpuTemplate tpl)
    {
        if (App.Services.State != LaunchState.Idle) return;

        Catalog.ApplyTo(tpl, Settings);
        App.Services.Save();
        RefreshTemplates(tpl.Id);
        TemplateStatusText.Text = $"已应用「{tpl.Title}」：环境变量与启动参数已整体替换";
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (App.Services.State != LaunchState.Idle) return;

        var nameBox = new TextBox { PlaceholderText = "例如：我的 RDNA3 调优" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "另存为自定义模板",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "把当前的环境变量与启动参数保存为可复用的自定义模板。",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    nameBox,
                },
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var title = nameBox.Text.Trim();
        if (string.IsNullOrEmpty(title))
        {
            TemplateStatusText.Text = "名称为空，未保存";
            return;
        }

        var id = TemplateCatalog.CaptureId(title, Settings);
        Settings.CustomTemplates.Add(Catalog.CaptureAsTemplate(Settings, title, id));
        App.Services.Save();
        RefreshTemplates(id);
        TemplateStatusText.Text = $"已保存自定义模板「{title}」";
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTemplate is { IsCustom: true } tpl) await DeleteTemplateAsync(tpl);
    }

    private async Task DeleteTemplateAsync(GpuTemplate tpl)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除自定义模板",
            Content = $"确定删除「{tpl.Title}」？该操作不影响已应用的环境变量与参数。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        Settings.CustomTemplates.RemoveAll(t => string.Equals(t.Id, tpl.Id, StringComparison.OrdinalIgnoreCase));
        App.Services.Save();
        RefreshTemplates();
        TemplateStatusText.Text = "已删除自定义模板";
    }

    // ---------- 导出启动脚本 ----------

    private async void ExportPs1_Click(object sender, RoutedEventArgs e)
    {
        var script = Ps1ScriptBuilder.Build(Settings, DateTime.Now);

        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "comfyui-launch",
        };
        picker.FileTypeChoices.Add("PowerShell 脚本", new List<string> { ".ps1" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            // 必须带 BOM：Windows PowerShell 5.1 读无 BOM 文件时按 ANSI 解码，中文注释与警告会乱码
            File.WriteAllText(file.Path, script, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            ExportStatusText.Text = $"已导出：{file.Path}（右键「使用 PowerShell 运行」即可启动）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ExportStatusText.Text = $"导出失败：{ex.Message}";
        }
    }
}
