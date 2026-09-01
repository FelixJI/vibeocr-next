using System.Runtime.InteropServices;

namespace VibeOCR.App.Services;

/// <summary>
/// 强制 WinUI 3 未打包窗口的最小逻辑尺寸，通过 Win32 子类化拦截 WM_GETMINMAXINFO。
/// minWidth/minHeight 为逻辑像素（与前端 CSS 布局下限一致），实际下发前按窗口 DPI
/// 换算为物理像素，并在 WM_DPICHANGED 时跟随更新。
/// </summary>
internal static class WindowMinSizeEnforcer
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_DPICHANGED = 0x02E0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT Reserved;
        public POINT MaxSize;
        public POINT MaxPosition;
        public POINT MinTrackSize;
        public POINT MaxTrackSize;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern IntPtr SetWindowLongW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern IntPtr GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    private const int GWLP_WNDPROC = -4;

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static WndProc? _subclassProc;
    private static IntPtr _originalWndProc;
    private static int _minWidth;
    private static int _minHeight;
    private static double _scale = 1.0;

    /// <summary>对指定窗口句柄启用最小尺寸约束（minWidth/minHeight 为逻辑像素）。</summary>
    public static void Apply(IntPtr hwnd, int minWidth, int minHeight)
    {
        _minWidth = minWidth;
        _minHeight = minHeight;
        _scale = WindowGeometryPolicy.GetWindowScale(hwnd);
        _subclassProc = CustomWndProc;
        if (IntPtr.Size == 8)
        {
            _originalWndProc = GetWindowLongPtrW(hwnd, GWLP_WNDPROC);
            SetWindowLongPtrW(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_subclassProc));
        }
        else
        {
            _originalWndProc = GetWindowLongW(hwnd, GWLP_WNDPROC);
            SetWindowLongW(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_subclassProc));
        }
    }

    private static IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DPICHANGED && wParam != IntPtr.Zero)
        {
            _scale = ((uint)wParam >> 16) / 96.0;
        }
        if (msg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
        {
            MINMAXINFO mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.MinTrackSize = new POINT
            {
                X = WindowGeometryPolicy.ScaleToPhysical(_minWidth, _scale),
                Y = WindowGeometryPolicy.ScaleToPhysical(_minHeight, _scale),
            };
            Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
        }
        return CallWindowProc(hWnd, msg, wParam, lParam);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr CallWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => CallWindowProcW(_originalWndProc, hWnd, msg, wParam, lParam);
}
