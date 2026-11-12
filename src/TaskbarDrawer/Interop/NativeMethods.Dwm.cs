using System.Runtime.InteropServices;

namespace TaskbarDrawer.Interop;

/// <summary>
/// Win32 互操作层分片：DWM 窗口属性（判断窗口是否被「隐藏」到其它虚拟桌面）。
/// </summary>
public static partial class NativeMethods
{
    public const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);

    public static bool IsCloaked(IntPtr hwnd, out int reason)
    {
        reason = 0;
        return hwnd != IntPtr.Zero &&
               DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out reason, sizeof(int)) == 0 && reason != 0;
    }
}
