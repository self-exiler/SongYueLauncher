using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace ComfyUiRocmLauncher.GUI.Helpers;

/// <summary>
/// 窗口最小尺寸约束（Win32 WM_GETMINMAXINFO 子类化）。
/// WindowsAppSDK 的 OverlappedPresenter 不暴露 MinWidth/MinHeight，
/// 用 AppWindow.Changed 回夹会闪烁，因此按桌面惯例子类化窗口过程，
/// 在系统调整尺寸前给出最小跟踪尺寸。
/// 尺寸单位为逻辑像素，内部按窗口 DPI 换算成物理像素（MINMAXINFO 是物理值）。
/// 实例须长期存活（窗口过程委托被原生侧引用），统一挂在静态表里防止被 GC。
/// </summary>
internal sealed class WindowMinSize
{
    private const int GWLP_WNDPROC = -4;
    private const uint WM_GETMINMAXINFO = 0x0024;

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    private readonly int _minWidth;
    private readonly int _minHeight;
    private readonly nint _originalProc;
    // 静态表同时承担「保活」与「唯一约束」：同一窗口只挂一次
    private static readonly Dictionary<nint, WindowMinSize> _attached = new();
    private readonly WndProc _proc;

    /// <summary>给窗口挂上最小尺寸约束（逻辑像素；同一窗口重复调用为无操作）。</summary>
    public static void Apply(Window window, int minWidth, int minHeight)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (_attached.ContainsKey(hwnd)) return;
        _attached[hwnd] = new WindowMinSize(hwnd, minWidth, minHeight);
    }

    private WindowMinSize(nint hwnd, int minWidth, int minHeight)
    {
        _minWidth = minWidth;
        _minHeight = minHeight;
        _proc = WndProcImpl;
        _originalProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_proc));
    }

    private nint WndProcImpl(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var scale = GetDpiForWindow(hwnd) / 96.0;
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMinTrackSize.X = (int)(_minWidth * scale);
            mmi.ptMinTrackSize.Y = (int)(_minHeight * scale);
            Marshal.StructureToPtr(mmi, lParam, true);
            return 0; // 已处理：不再走默认过程，否则会回填系统默认最小值
        }
        return CallWindowProc(_originalProc, hwnd, msg, wParam, lParam);
    }

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }
}
