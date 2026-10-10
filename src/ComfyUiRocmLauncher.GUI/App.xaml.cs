using Microsoft.UI.Xaml;
using ComfyUiRocmLauncher.GUI.Services;

namespace ComfyUiRocmLauncher.GUI;

public partial class App : Application
{
    public static Window Window { get; private set; } = null!;

    public static AppServices Services { get; private set; } = null!;

    /// <summary>原生窗口句柄，文件/目录选择器与 WinRT 互操作需要。</summary>
    public static nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Services = new AppServices();
        Window = new MainWindow();
        Window.Activate();
    }
}
