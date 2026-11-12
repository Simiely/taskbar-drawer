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

    /// <summary>GetAncestor 的 GA_ROOT：取顶层祖先，用来判断命中窗口是不是任务栏的子窗口。</summary>
    public const uint GA_ROOT = 2;

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtrW(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtrW(IntPtr h, int idx, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLengthW(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT pl);
    [DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT pl);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
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

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static bool TryGetPlacement(IntPtr hwnd, out WINDOWPLACEMENT placement)
    {
        placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        return GetWindowPlacement(hwnd, ref placement);
    }

    public static string MonitorDeviceOf(IntPtr hwnd)
    {
        IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return null;
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfoW(mon, ref mi) ? mi.szDevice : null;
    }

    public static uint DpiOf(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 96u : dpi;
    }

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
    // WINDOWPLACEMENT.rcNormalPosition 用的是「工作区坐标」（原点 = 所在显示器工作区左上角）。
    // 跨时刻保存的矩形必须先转成屏幕坐标，否则任务栏/AppBar 改变工作区原点时会整体平移。

    public static POINT WorkOriginOfWindow(IntPtr hwnd)
        => TryGetMonitorOfWindow(hwnd, out var info)
            ? new POINT { X = info.rcWork.Left, Y = info.rcWork.Top }
            : default;

    /// <summary>按设备名找显示器的工作区原点（隐藏窗口的矩形是哨兵值，不能按窗口位置反查）。</summary>
    public static POINT WorkOriginOfDevice(string device)
    {
        if (string.IsNullOrEmpty(device)) return default;
        POINT origin = default;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr hdc, ref RECT r, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(mon, ref mi) && string.Equals(mi.szDevice, device, StringComparison.Ordinal))
            {
                origin = new POINT { X = mi.rcWork.Left, Y = mi.rcWork.Top };
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return origin;
    }

    public static RECT ToScreenCoordinates(RECT rect, POINT origin)
        => RECT.FromLTRB(rect.Left + origin.X, rect.Top + origin.Y,
            rect.Right + origin.X, rect.Bottom + origin.Y);

    public static RECT ToWorkspaceCoordinates(RECT rect, POINT origin)
        => RECT.FromLTRB(rect.Left - origin.X, rect.Top - origin.Y,
            rect.Right - origin.X, rect.Bottom - origin.Y);
}
