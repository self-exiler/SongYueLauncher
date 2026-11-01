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
        // 每次进入页面都按其时配置重同步：base-directory 可能在启动参数页被改动，快捷目录要跟着换目标
        RefreshQuickDirs();
        RefreshBaseDirCard();
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
    }

    /// <summary>FR-1.4：快捷目录按生效路径解析（base-directory / 各 *-directory 参数改写后指向外置数据根）；目录不存在时对应按钮禁用。</summary>
    private void RefreshQuickDirs()
    {
        foreach (var button in new[] { QdOutput, QdModels, QdWorkflow, QdCustomNodes, QdInput })
        {
            var target = ComfyDirResolver.Resolve(Settings, (string)button.Tag);
            button.IsEnabled = Directory.Exists(target);
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, target);
        }
    }

    private void ComfyDirBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Settings.ComfyUiDir = ComfyDirBox.Text.Trim();
        App.Services.Save();
        RefreshQuickDirs();
        RefreshBaseDirCard();

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
        UiHelpers.OpenFolder(ComfyDirResolver.Resolve(Settings, relative));
    }

    // ---------- 目录外置（FR-1.5，纯启动参数） ----------

    private const string BaseDirArg = "base-directory";

    private void BaseDirBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var value = BaseDirBox.Text.Trim().Trim('"');
        if (value.Length == 0)
        {
            // 清空文本框不算「清除」：值留空但开关保持，真正移除参数走「清除」按钮
            Settings.Args.SetValue(BaseDirArg, string.Empty);
        }
        else
        {
            Settings.Args.Enable(BaseDirArg, true);
            Settings.Args.SetValue(BaseDirArg, value);
        }
        App.Services.Save();
        RefreshBaseDirCard();
    }

    private async void BrowseBaseDir_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) BaseDirBox.Text = folder.Path;
    }

    private void ClearBaseDir_Click(object sender, RoutedEventArgs e)
    {
        Settings.Args.Enable(BaseDirArg, false);
        Settings.Args.SetValue(BaseDirArg, string.Empty);
        App.Services.Save();
        RefreshBaseDirCard();
    }

    /// <summary>回显 base-directory 参数状态与生效推导；只读参数，不做任何文件系统操作。</summary>
    private void RefreshBaseDirCard()
    {
        var arg = Settings.Args;
        var value = arg.Value(BaseDirArg).Trim().Trim('"');
        BaseDirBox.Text = value;

        if (!arg.IsEnabled(BaseDirArg) || value.Length == 0)
        {
            BaseDirStatusText.Text = "未启用：ComfyUI 使用整合包内的 models / input / output / temp 目录。";
            return;
        }

        var warning = string.Empty;
        try
        {
            var full = NormPath(value);
            if (!Path.IsPathRooted(full))
                warning = "；相对路径按 ComfyUI 目录解析";
            else if (!string.IsNullOrWhiteSpace(Settings.ComfyUiDir) &&
                     full.StartsWith(NormPath(Settings.ComfyUiDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                warning = "；警告：该目录位于整合包内，删除整合包时数据仍会丢失";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            warning = "；路径格式无效，ComfyUI 可能启动失败";
        }

        BaseDirStatusText.Text =
            $"已启用：模型/输入/输出/临时目录按 {value} 解析（其下 models、output 等子目录）{warning}";
    }

    private static string NormPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

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
            SuggestedFileName = Ps1ScriptBuilder.SuggestedFileName,
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
