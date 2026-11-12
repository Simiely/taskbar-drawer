using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

public sealed class ActivationResult
{
    public long Hwnd { get; init; }
    public bool Foreground { get; init; }
    public bool UsedAttachThreadInput { get; init; }
    public bool FlashedTaskbar { get; init; }
    public bool WindowGone { get; init; }
}

/// <summary>
/// 前台激活：SW_RESTORE（仅最小化）→ SetForegroundWindow → AttachThreadInput 兜底 → 失败闪烁任务栏。
/// 不修改目标窗口样式、不改前台锁、不模拟 Alt。
/// </summary>
public static class WindowActivator
{
    public static bool AttachThreadInputFallback = true;
    public static bool FlashOnFailure = true;

    public static ActivationResult Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            return new ActivationResult { Hwnd = hwnd.ToInt64(), WindowGone = true };

        bool minimized = NativeMethods.IsIconic(hwnd);
        if (minimized) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

        bool usedAttach = false;
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
                usedAttach = NativeMethods.AttachThreadInput(selfThread, foregroundThread, true);
                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.SetForegroundWindow(hwnd);
                if (usedAttach) NativeMethods.AttachThreadInput(selfThread, foregroundThread, false);
            }
        }

        bool foreground = NativeMethods.GetForegroundWindow() == hwnd;
        bool flashed = false;
        if (!foreground && FlashOnFailure)
            flashed = NativeMethods.FlashTaskbarResult(hwnd, 3);

        return new ActivationResult
        {
            Hwnd = hwnd.ToInt64(),
            Foreground = foreground,
            UsedAttachThreadInput = usedAttach,
            FlashedTaskbar = flashed
        };
    }
}
