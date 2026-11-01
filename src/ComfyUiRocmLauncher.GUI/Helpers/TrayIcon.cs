using System.Runtime.InteropServices;

namespace ComfyUiRocmLauncher.GUI.Helpers;

/// <summary>
/// 系统托盘图标（WinUI 3 无内建 API，直接走 Shell_NotifyIcon，不引第三方包）。
/// 只负责图标、右键菜单与最小化消息；收起/恢复/退出由 MainWindow 决定。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint TrayId = 1;
    private const uint WmTrayCallback = 0x8000 + 1;   // WM_APP + 1
    private const uint WmHideToTray = 0x8000 + 2;     // WM_APP + 2：WM_SIZE 里不能同步隐藏窗口，转投递
    private const UIntPtr SubclassId = (UIntPtr)0xC0DE;

    private const int MenuShow = 1;
    private const int MenuOutput = 2;
    private const int MenuExit = 3;

    private const uint NIM_ADD = 0;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;

    private const int WM_NULL = 0x0000;
    private const int WM_SIZE = 0x0005;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int SIZE_MINIMIZED = 1;

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_NONOTIFY = 0x0080;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const int SM_CXSMICON = 49;
    private const int IDI_APPLICATION = 32512;

    private readonly IntPtr _hwnd;
    private readonly string _tip;
    private readonly Action<string>? _log;
    private readonly SUBCLASSPROC _proc;
    private readonly IntPtr _menu;
    private readonly IntPtr _icon;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private bool _visible;
    private bool _disposed;

    /// <summary>用户点了最小化（WM_SIZE/SIZE_MINIMIZED 已投递到消息循环之后）。</summary>
    public event Action? MinimizeRequested;

    public event Action? ShowRequested;

    public event Action? OutputRequested;

    public event Action? ExitRequested;

    public TrayIcon(IntPtr hwnd, string iconPath, string tip, Action<string>? log = null)
    {
        _hwnd = hwnd;
        _tip = tip;
        _log = log;
        _proc = WndProc;

        _menu = CreatePopupMenu();
        AppendMenu(_menu, MF_STRING, (IntPtr)MenuShow, "显示主窗口");
        AppendMenu(_menu, MF_STRING, (IntPtr)MenuOutput, "任务输出");
        AppendMenu(_menu, MF_SEPARATOR, IntPtr.Zero, null);
        AppendMenu(_menu, MF_STRING, (IntPtr)MenuExit, "退出启动器");

        var size = GetSystemMetrics(SM_CXSMICON);
        _icon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, size, size, LR_LOADFROMFILE);
        if (_icon == IntPtr.Zero)
        {
            _log?.Invoke($"[启动器] 警告：未找到托盘图标 {iconPath}，回退为系统默认图标");
            _icon = LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
        }

        if (!SetWindowSubclass(hwnd, _proc, SubclassId, UIntPtr.Zero))
            _log?.Invoke("[启动器] 警告：托盘消息钩子安装失败，最小化收起将不可用");
    }

    public void Show()
    {
        if (_disposed || _visible) return;
        _visible = AddIcon();
    }

    private bool AddIcon()
    {
        var data = MakeData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        data.uCallbackMessage = WmTrayCallback;
        data.hIcon = _icon;
        data.szTip = _tip;
        if (Shell_NotifyIcon(NIM_ADD, ref data)) return true;
        _log?.Invoke($"[启动器] 托盘图标注册失败：{Marshal.GetLastWin32Error()}");
        return false;
    }

    private void DeleteIcon()
    {
        var data = MakeData(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
    }

    private NOTIFYICONDATA MakeData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = TrayId,
        uFlags = flags,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        // 原生回调里抛异常会直接终结进程（0xC000027B），所有分支必须兜住
        try
        {
            if (!_disposed)
            {
                if (msg == _taskbarCreated)
                {
                    // 资源管理器重启会清空托盘区，图标常驻期间需重新注册
                    if (_visible) AddIcon();
                }
                else if (msg == WmTrayCallback)
                {
                    switch ((int)(lParam.ToInt64() & 0xFFFF))
                    {
                        case WM_LBUTTONDBLCLK:
                            ShowRequested?.Invoke();
                            break;
                        case WM_RBUTTONUP:
                        case WM_CONTEXTMENU:
                            ShowMenu();
                            break;
                    }
                }
                else if (msg == WM_SIZE && (int)wParam.ToInt64() == SIZE_MINIMIZED)
                {
                    PostMessage(hWnd, WmHideToTray, IntPtr.Zero, IntPtr.Zero);
                }
                else if (msg == WmHideToTray)
                {
                    MinimizeRequested?.Invoke();
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[启动器] 托盘消息处理失败：{ex.Message}");
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        GetCursorPos(out var pt);
        // 菜单的 owner 必须先成为前台窗口，否则点菜单外部时菜单不消失
        SetForegroundWindow(_hwnd);
        var cmd = TrackPopupMenuEx(_menu,
            TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero); // 清空菜单命令队列的经典 workaround

        switch (cmd)
        {
            case MenuShow: ShowRequested?.Invoke(); break;
            case MenuOutput: OutputRequested?.Invoke(); break;
            case MenuExit: ExitRequested?.Invoke(); break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_visible) DeleteIcon();
        RemoveWindowSubclass(_hwnd, _proc, SubclassId);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        if (_menu != IntPtr.Zero) DestroyMenu(_menu);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint flags, IntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hWnd, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC proc, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
