using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>
/// 前台激活：SW_RESTORE（仅最小化）→ SetForegroundWindow → AttachThreadInput 兜底 → 失败闪烁任务栏。
/// 不修改目标窗口样式、不改前台锁、不模拟 Alt。
/// </summary>
public static class WindowActivator
{
    private const bool AttachThreadInputFallback = true;
    private const bool FlashOnFailure = true;

    public static void Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;

        bool minimized = NativeMethods.IsIconic(hwnd);
        if (minimized) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

        NativeMethods.SetForegroundWindow(hwnd);

        if (NativeMethods.GetForegroundWindow() != hwnd && AttachThreadInputFallback)
        {
            uint foregroundThread = 0;
            IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
            if (foregroundWindow != IntPtr.Zero)
                foregroundThread = NativeMethods.GetWindowThreadProcessId(foregroundWindow, out _);
            uint selfThread = NativeMethods.GetCurrentThreadId();

            if (foregroundThread != 0 && foregroundThread != selfThread)
            {
                bool usedAttach = NativeMethods.AttachThreadInput(selfThread, foregroundThread, true);
                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.SetForegroundWindow(hwnd);
                if (usedAttach) NativeMethods.AttachThreadInput(selfThread, foregroundThread, false);
            }
        }

        if (NativeMethods.GetForegroundWindow() != hwnd && FlashOnFailure)
            NativeMethods.FlashTaskbarResult(hwnd, 3);
    }
}
