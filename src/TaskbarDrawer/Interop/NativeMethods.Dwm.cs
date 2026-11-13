using System.Runtime.InteropServices;

namespace TaskbarDrawer.Interop;

/// <summary>
/// Win32 互操作层分片：DWM 窗口属性（判断窗口是否被「隐藏」到其它虚拟桌面、深色标题栏）。
/// </summary>
public static partial class NativeMethods
{
    public const int DWMWA_CLOAKED = 14;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

    public static bool IsCloaked(IntPtr hwnd, out int reason)
    {
        reason = 0;
        return hwnd != IntPtr.Zero &&
               DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out reason, sizeof(int)) == 0 && reason != 0;
    }

    /// <summary>
    /// 让原生标题栏跟随深色主题；旧系统不支持时静默失败，不影响窗口功能。
    /// </summary>
    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        int enabled = 1;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
    }
}
