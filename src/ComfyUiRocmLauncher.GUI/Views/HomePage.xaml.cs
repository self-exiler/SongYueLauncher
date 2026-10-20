using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;
using ComfyUiRocmLauncher.GUI.Helpers;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>主页（SRS FR-1）：路径设置、启动/停止/浏览器、状态显示、快捷目录。</summary>
public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private AppConfig Settings => App.Services.Settings;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ComfyDirBox.Text = Settings.ComfyUiDir;
        PythonBox.Text = Settings.PythonExe;
        AutoBrowserToggle.IsOn = Settings.AutoOpenBrowser;

        var uv = ToolLocator.FindUv();
        UvStatusText.Text = uv is not null
            ? $"uv.exe：已自动发现 {uv}"
            : "uv.exe：未找到，请在环境变量中安装 uv 或稍后手动指定（winget install astral-sh.uv）";

        // 行标签已在 XAML 中给出，代码只赋值
        GpuText.Text = GpuInfoService.GetAmdGpuName();
        VersionHashText.Text = App.Services.CurrentVersionHash();
        _ = RefreshVersionFromGitAsync();

        App.Services.StateChanged += OnStateChanged;
        RefreshState();
        RefreshQuickDirs();
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
        VersionHashText.Text = App.Services.CurrentVersionHash();

        // 状态点配色：强调状态取状态色，空闲用次级灰
        RunStateDot.Fill = ThemeHelper.Brush(ThemeHelper.StateAccentKey(state) ?? "TextFillColorDisabledBrush");
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
            var ready = await App.Services.Launch.StartAsync(Settings, TimeSpan.FromSeconds(180));
            if (ready && Settings.AutoOpenBrowser)
            {
                UiHelpers.OpenUrl(ArgsBuilder.DeriveBrowserUrl(Settings.Args));
            }
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

    private void AutoBrowserToggle_Toggled(object sender, RoutedEventArgs e)
    {
        Settings.AutoOpenBrowser = AutoBrowserToggle.IsOn;
        App.Services.Save();
    }

    private void QuickDir_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string relative) return;
        UiHelpers.OpenFolder(Path.Combine(Settings.ComfyUiDir, relative));
    }
}
