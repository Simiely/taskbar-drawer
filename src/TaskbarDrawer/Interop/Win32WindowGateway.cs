using System.Diagnostics;
using System.Runtime.InteropServices;
using TaskbarDrawer.Core;

namespace TaskbarDrawer.Interop;

/// <summary>
/// <see cref="IWindowGateway"/> 的 Win32 实现：所有平台调用都在这一层，
/// Core 里只剩状态迁移规则。<see cref="Instance"/> 是无状态的单例，可以直接共享。
/// </summary>
public sealed class Win32WindowGateway : IWindowGateway
{
    public static readonly Win32WindowGateway Instance = new();

    // 同一批收纳里同一 pid 往往有多个窗口（浏览器、IDE）：进程元数据一次运行内不会变，按 pid 缓存。
    private readonly Dictionary<uint, ProcessMeta> _processCache = new();

    private sealed class ProcessMeta
    {
        public string Name = "";
        public string Path = "";
        public DateTime? StartTime;
    }

    public bool IsWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd);

    public bool IsVisible(IntPtr hwnd) => hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(hwnd);

    public bool IsHung(IntPtr hwnd) => hwnd != IntPtr.Zero && NativeMethods.IsHungAppWindow(hwnd);

    public IntPtr Foreground() => NativeMethods.GetForegroundWindow();

    public string MonitorOf(IntPtr hwnd) => NativeMethods.MonitorDeviceOf(hwnd);

    public IReadOnlyList<WindowCandidate> Enumerate(bool includeMinimized)
        => WindowEnumerator.Enumerate(includeMinimized);

    public WindowFacts Describe(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return null;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        var meta = ReadProcess(pid);
        NativeMethods.GetWindowRect(hwnd, out var rect);
        long exStyle = NativeMethods.ExStyle(hwnd);

        return new WindowFacts
        {
            Handle = hwnd,
            Title = NativeMethods.Title(hwnd),
            ClassName = NativeMethods.ClassName(hwnd),
            ProcessId = pid,
            ProcessName = meta.Name,
            ProcessPath = meta.Path,
            ProcessStartTime = meta.StartTime,
            Visible = NativeMethods.IsWindowVisible(hwnd),
            ToolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0,
            Cloaked = NativeMethods.IsCloaked(hwnd, out _),
            // 提权判断要跨进程查令牌，代价不小：只在真的要拦的时候问（普通权限实例才需要知道）。
            Elevated = !NativeMethods.IsCurrentProcessElevated && NativeMethods.IsProcessElevated(pid),
            Rect = rect
        };
    }

    public WindowPlacement CapturePlacement(IntPtr hwnd)
    {
        var placement = new NativeMethods.WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>()
        };
        bool hasPlacement = NativeMethods.GetWindowPlacement(hwnd, ref placement);

        // rcNormalPosition 用的是「工作区坐标」，窗口矩形是屏幕坐标。任务栏位置 / DPI 变化后
        // 工作区原点会变，直接把 rcNormal 存下来再写回去会整体平移，所以统一转成屏幕坐标保存。
        // 换算只扣任务栏偏移量（rcWork - rcMonitor），不能加显示器在虚拟桌面里的原点。
        if (hasPlacement)
            placement.rcNormalPosition = NativeMethods.ToScreenCoordinates(
                placement.rcNormalPosition, NativeMethods.WorkspaceOffsetOfWindow(hwnd));

        return new WindowPlacement
        {
            Value = placement,
            Maximized = NativeMethods.IsZoomed(hwnd) ||
                        (hasPlacement && placement.showCmd == NativeMethods.SW_SHOWMAXIMIZED),
            MonitorDevice = NativeMethods.MonitorDeviceOf(hwnd)
        };
    }

    public void ApplyPlacement(IntPtr hwnd, WindowPlacement placement)
    {
        var value = placement.Value;
        value.length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>();
        value.flags = 0;
        value.showCmd = placement.Maximized ? NativeMethods.SW_SHOWMAXIMIZED : NativeMethods.SW_SHOWNORMAL;
        // 存的是屏幕坐标，SetWindowPlacement 要的是工作区坐标：按目标矩形所在显示器的
        // 任务栏偏移量换算（不是绝对工作区原点，否则副屏会整体平移一整块屏宽）。
        var target = value.rcNormalPosition;
        value.rcNormalPosition = NativeMethods.ToWorkspaceCoordinates(
            target, NativeMethods.WorkspaceOffsetOfDevice(placement.MonitorDevice, target));
        NativeMethods.SetWindowPlacement(hwnd, ref value);

        // 个别应用会在 SetWindowPlacement 后重排布局：再按目标矩形校正一次，确保「原位回去」。
        if (placement.Maximized || target.IsEmpty) return;
        NativeMethods.GetWindowRect(hwnd, out var now);
        if (Math.Abs(now.Left - target.Left) > 2 || Math.Abs(now.Top - target.Top) > 2 ||
            Math.Abs(now.Width - target.Width) > 2 || Math.Abs(now.Height - target.Height) > 2)
        {
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, target.Left, target.Top,
                target.Width, target.Height, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
    }

    public void SetVisibility(IntPtr hwnd, WindowVisibility visibility)
    {
        int command = visibility switch
        {
            WindowVisibility.Hidden => NativeMethods.SW_HIDE,
            WindowVisibility.Shown => NativeMethods.SW_SHOWNA,
            WindowVisibility.Restored => NativeMethods.SW_RESTORE,
            WindowVisibility.Minimized => NativeMethods.SW_SHOWMINNOACTIVE,
            _ => NativeMethods.SW_SHOWNA
        };
        NativeMethods.ShowWindow(hwnd, command);
    }

    public IReadOnlyList<IntPtr> OwnedPopups(IntPtr hwnd)
    {
        var list = new List<IntPtr>();
        IntPtr current = hwnd;

        for (int depth = 0; depth < 4; depth++)
        {
            IntPtr popup = NativeMethods.GetLastActivePopup(current);
            if (popup == IntPtr.Zero || popup == current) break;
            if (NativeMethods.Owner(popup) == IntPtr.Zero) break;
            if (NativeMethods.IsWindowVisible(popup) && !list.Contains(popup)) list.Add(popup);
            current = popup;
        }

        return list;
    }

    public void Activate(IntPtr hwnd) => WindowActivator.Activate(hwnd);

    public void ShowOwnedPopup(IntPtr hwnd)
    {
        if (!IsWindow(hwnd) || IsVisible(hwnd)) return;
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNA);
    }

    private ProcessMeta ReadProcess(uint pid)
    {
        if (_processCache.TryGetValue(pid, out var cached)) return cached;

        var meta = new ProcessMeta();
        try
        {
            using var process = Process.GetProcessById((int)pid);
            meta.Name = process.ProcessName;
            // 被保护 / 已退出的进程读不到 MainModule 与启动时间：元数据缺省即可。
            try { meta.Path = process.MainModule?.FileName ?? ""; } catch { }
            try { meta.StartTime = process.StartTime.ToUniversalTime(); } catch { }
        }
        catch { /* 进程已退出（句柄可能已失效），返回空元数据 */ }

        // 长时间运行时进程会不断变化：给缓存一个上限，避免按 PID 无限增长。
        if (_processCache.Count >= 256) _processCache.Clear();
        _processCache[pid] = meta;
        return meta;
    }
}
