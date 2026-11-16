using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarDrawer.Interop;

/// <summary>
/// Win32 互操作层分片：窗口枚举 / 样式 / 摆位、显示器与 DPI、工作区坐标换算、窗口事件。
/// </summary>
public static partial class NativeMethods
{
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    /// <summary>GetDpiForMonitor 的 MDT_EFFECTIVE_DPI：显示器实际生效的缩放比例。</summary>
    private const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>GetAncestor 的 GA_ROOT：取顶层祖先，用来判断命中窗口是不是任务栏的子窗口。</summary>
    public const uint GA_ROOT = 2;

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtrW(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtrW(IntPtr h, int idx, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLengthW(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT pl);
    [DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT pl);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFOEX mi);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int value, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static bool? _selfElevated;

    /// <summary>本进程是否以管理员身份运行。</summary>
    public static bool IsCurrentProcessElevated =>
        _selfElevated ??= IsProcessElevated((uint)Environment.ProcessId);

    /// <summary>
    /// 目标进程是否提权。隐藏 / 最小化别的进程的窗口要跨完整性级别，
    /// 低权限进程对高权限窗口调用 ShowWindow / SetWindowPos 会被 UIPI 直接拒绝（返回 false），
    /// 所以收纳前先判断，收不进去时如实提示，而不是留下一个点不开的空胶囊。
    /// </summary>
    public static bool IsProcessElevated(uint processId)
    {
        if (processId == 0) return false;
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero) return false;
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out token)) return false;
            if (!GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _)) return false;
            return elevated != 0;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
            CloseHandle(process);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowW(string className, string windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowExW")]
    public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string className, string windowName);

    [DllImport("kernel32.dll")] public static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")] public static extern bool FreeConsole();
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr h, uint msg, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);

    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_SETTEXT = 0x000C;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    public static long ExStyle(IntPtr hwnd) => GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();

    /// <summary>
    /// 取窗口标题。<b>不能用 GetWindowText</b>：它对跨进程窗口是同步 SendMessage，
    /// 目标线程挂起时会把调用线程卡到系统消息超时。这里用 SendMessageTimeout + SMTO_ABORTIFHUNG。
    /// </summary>
    public static string Title(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        int len = GetWindowTextLengthW(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 2);
        IntPtr copied = SendMessageTimeoutW(
            hwnd, WM_GETTEXT, (IntPtr)sb.Capacity, sb, SMTO_ABORTIFHUNG, 120, out _);
        return copied == IntPtr.Zero ? "" : sb.ToString();
    }

    /// <summary>
    /// 改窗口标题（清理 1.6.6 及更早版本写入的场景 emoji 前缀）。同样走 SendMessageTimeout：
    /// SetWindowText 对跨进程窗口是同步 SendMessage，目标线程挂起时会卡住调用方。
    /// </summary>
    public static bool SetTitle(IntPtr hwnd, string title)
    {
        if (hwnd == IntPtr.Zero || title == null) return false;
        var sb = new StringBuilder(title);
        IntPtr copied = SendMessageTimeoutW(
            hwnd, WM_SETTEXT, IntPtr.Zero, sb, SMTO_ABORTIFHUNG, 200, out _);
        return copied != IntPtr.Zero;
    }

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string MonitorDeviceOf(IntPtr hwnd)
    {
        IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return null;
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfoW(mon, ref mi) ? mi.szDevice : null;
    }

    /// <summary>
    /// 鼠标所在显示器的设备名。从托盘菜单 / 面板按钮触发时前台是本程序或 shell，
    /// 拿不到「用户正在操作的窗口」，就用鼠标位置判断当前屏幕。
    /// </summary>
    public static string MonitorDeviceOfCursor()
    {
        if (!GetCursorPos(out var point)) return null;
        IntPtr mon = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return null;
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfoW(mon, ref mi) ? mi.szDevice : null;
    }

    public static uint DpiOf(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 96u : dpi;
    }

    /// <summary>按设备名找显示器信息（工作区 / 全屏矩形）。显示器已拔掉时返回 false。</summary>
    public static bool TryGetMonitorOfDevice(string device, out MONITORINFOEX info)
    {
        var found = default(MONITORINFOEX);
        bool ok = false;

        if (!string.IsNullOrEmpty(device))
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr hdc, ref RECT r, IntPtr data) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfoW(mon, ref mi) && string.Equals(mi.szDevice, device, StringComparison.Ordinal))
                {
                    found = mi;
                    ok = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
        }

        info = found;
        return ok;
    }

    /// <summary>全部显示器信息（诊断日志用）。</summary>
    public static List<MONITORINFOEX> AllMonitors()
    {
        var list = new List<MONITORINFOEX>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr hdc, ref RECT r, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(mon, ref mi)) list.Add(mi);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>MONITORINFOF_PRIMARY：这块是不是主显示器。</summary>
    public static bool IsPrimary(MONITORINFOEX monitor) => (monitor.dwFlags & 1) != 0;

    /// <summary>矩形最近的显示器（MONITOR_DEFAULTTONEAREST）：目标屏被拔掉时也不会返回空。</summary>
    public static bool TryGetMonitorOfRect(RECT rect, out MONITORINFOEX info)
    {
        info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        IntPtr monitor = rect.IsEmpty ? IntPtr.Zero : MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
        return monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info);
    }

    /// <summary>设备名对应的有效 DPI（96 = 100%）。找不到时按 96 处理。</summary>
    public static uint DpiOfDevice(string device)
    {
        uint dpi = 96;
        if (string.IsNullOrEmpty(device)) return dpi;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr hdc, ref RECT r, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfoW(mon, ref mi) || !string.Equals(mi.szDevice, device, StringComparison.Ordinal))
                return true;

            if (GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out uint x, out _) == 0 && x > 0) dpi = x;
            return false;
        }, IntPtr.Zero);

        return dpi == 0 ? 96u : dpi;
    }

    /// <summary>点落在哪块显示器上；用于反馈浮层 / 面板按操作屏定位。</summary>
    public static bool TryGetMonitorOfPoint(POINT point, out MONITORINFOEX info)
    {
        info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        IntPtr monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        return monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info);
    }

    public static string MonitorDeviceOfPoint(POINT point)
        => TryGetMonitorOfPoint(point, out var info) ? info.szDevice : null;

    public static string MonitorDeviceOfRect(RECT rect)
        => TryGetMonitorOfRect(rect, out var info) ? info.szDevice : null;

    public static bool TryGetMonitorOfWindow(IntPtr hwnd, out MONITORINFOEX info)
    {
        info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        return monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info);
    }

    /// <summary>只作用于自身小条：工具窗（不进 Alt+Tab）+ 禁止激活。不能加 WS_EX_TRANSPARENT（会点不到）。</summary>
    public static void ApplyChipStyles(IntPtr hwnd)
    {
        long ex = ExStyle(hwnd);
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    public static void ShowNoActivate(IntPtr hwnd) => ShowWindow(hwnd, SW_SHOWNOACTIVATE);

    /// <summary>主面板：只加工具窗样式（不进 Alt+Tab / 任务栏），保留正常激活能力。</summary>
    public static void ApplyToolWindowStyle(IntPtr hwnd)
    {
        long ex = ExStyle(hwnd);
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
    }

    // ---------- 工作区坐标 ↔ 屏幕坐标 ----------
    // WINDOWPLACEMENT.rcNormalPosition 用的是「工作区坐标」：Windows 只扣掉本显示器上
    // 任务栏 / AppBar 占掉的那一条（rcWork - rcMonitor），<b>不会</b>扣掉显示器在虚拟桌面里的原点。
    // 早先按「绝对工作区原点」换算（加了显示器原点），副屏上的矩形会整体平移一整块屏宽，
    // 还原时表现为「窗口跑到另一块屏 / 贴边」——1.6.5 修正为任务栏偏移量。
    // 参考：JDK awt_Frame.cpp 的 SetWindowPlacement 换算 x - (rcWork.left - rcMonitor.left)。

    /// <summary>窗口所在显示器的任务栏偏移量（rcWork - rcMonitor），用来在屏幕坐标与工作区坐标间换算。</summary>
    public static POINT WorkspaceOffsetOfWindow(IntPtr hwnd)
        => TryGetMonitorOfWindow(hwnd, out var info)
            ? new POINT { X = info.rcWork.Left - info.rcMonitor.Left, Y = info.rcWork.Top - info.rcMonitor.Top }
            : default;

    /// <summary>
    /// 按设备名找显示器的任务栏偏移量（隐藏窗口的矩形是哨兵值，不能按窗口位置反查）。
    /// 显示器被拔掉 / 设备名变化时退回「目标矩形最近的显示器」，保证换算与
    /// DisplayRules.EnsureVisible 用的是同一块屏。
    /// </summary>
    public static POINT WorkspaceOffsetOfDevice(string device, RECT fallbackRect)
    {
        if (TryGetMonitorOfDevice(device, out var info))
            return new POINT { X = info.rcWork.Left - info.rcMonitor.Left, Y = info.rcWork.Top - info.rcMonitor.Top };

        if (TryGetMonitorOfRect(fallbackRect, out var nearest))
            return new POINT { X = nearest.rcWork.Left - nearest.rcMonitor.Left, Y = nearest.rcWork.Top - nearest.rcMonitor.Top };

        Diagnostics.Write("window placement", new InvalidOperationException(
            $"找不到保存的显示器 {device ?? "(未记录)"}，按 (0,0) 换算"));
        return default;
    }

    public static RECT ToScreenCoordinates(RECT rect, POINT origin)
        => RECT.FromLTRB(rect.Left + origin.X, rect.Top + origin.Y,
            rect.Right + origin.X, rect.Bottom + origin.Y);

    public static RECT ToWorkspaceCoordinates(RECT rect, POINT origin)
        => RECT.FromLTRB(rect.Left - origin.X, rect.Top - origin.Y,
            rect.Right - origin.X, rect.Bottom - origin.Y);
}
