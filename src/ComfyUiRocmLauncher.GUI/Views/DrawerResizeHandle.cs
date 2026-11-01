using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace ComfyUiRocmLauncher.GUI.Views;

/// <summary>抽屉左缘拖拽条：只负责左右拉伸光标，拖拽逻辑在 OutputDrawer。</summary>
public sealed class DrawerResizeHandle : ContentControl
{
    public DrawerResizeHandle()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
