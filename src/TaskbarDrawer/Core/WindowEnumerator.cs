using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>一个可被本程序接管的顶层窗口（只读快照，不修改目标窗口）。</summary>
public sealed class WindowCandidate
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; }
    public string ClassName { get; init; }
    public uint ProcessId { get; init; }
    public NativeMethods.RECT Rect { get; init; }
}

/// <summary>
/// 候选窗口枚举：一次 EnumWindows + Alt+Tab 规则过滤 + shell 类窗口黑名单。
/// 只读，不移动 / 不激活 / 不修改任何窗口。
/// </summary>
public static class WindowEnumerator
{
    /// <summary>桌面、任务栏、开始菜单等由 explorer 画的窗口：既不能最小化也不能收进收纳。</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",
        "ApplicationManager_DesktopShellWindow",
        "ForegroundStaging",
        "MultitaskingViewFrame",
        "XamlExplorerHostIslandWindow",
        "TaskListThumbnailWnd"
    };

    public static List<WindowCandidate> Enumerate(bool includeMinimized)
    {
        var list = new List<WindowCandidate>();

        NativeMethods.EnumWindows((hwnd, state) =>
        {
            // 先用「不读标题」的快速判据过滤：读标题是跨进程 SendMessageTimeout，
            // 必然被排除的窗口不值得为它等一次超时。
            if (ExclusionReason(hwnd, checkTitle: false) != null) return true;

            bool minimized = NativeMethods.IsIconic(hwnd);
            if (minimized && !includeMinimized) return true;

            string title = NativeMethods.Title(hwnd);
            if (title.Length == 0) return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            NativeMethods.GetWindowRect(hwnd, out var rect);

            list.Add(new WindowCandidate
            {
                Handle = hwnd,
                Title = title,
                ClassName = NativeMethods.ClassName(hwnd),
                ProcessId = pid,
                Rect = rect
            });
            return true;
        }, IntPtr.Zero);

        return list;
    }

    /// <summary>
    /// 这个窗口为什么不能当场景成员（null = 可以）。
    /// 判定顺序即成本顺序：便宜的样式 / 矩形判断在前，跨进程读标题放在最后，
    /// 由 <paramref name="checkTitle"/> 控制是否执行（枚举热路径先不查）。
    ///
    /// 关键一条是「被拥有的窗口」：对话框、弹出层、浏览器扩展弹窗都有 owner
    /// （Chromium 的扩展弹窗就是用 GW_OWNER 挂到浏览器窗口上的）。
    /// 它们不该成为场景成员——否则会占用条目、污染配对，关场景时把主窗口一起关掉。
    /// </summary>
    public static string ExclusionReason(IntPtr hwnd, bool checkTitle = true)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return "无效句柄";
        if (!NativeMethods.IsWindowVisible(hwnd)) return "不可见";
        if (NativeMethods.IsHungAppWindow(hwnd)) return "挂起";
        if (NativeMethods.IsCloaked(hwnd, out _)) return "已 cloaked（其它虚拟桌面 / 后台）";

        long ex = NativeMethods.ExStyle(hwnd);
        if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return "工具窗（不进 Alt+Tab）";
        if ((ex & NativeMethods.WS_EX_NOACTIVATE) != 0) return "不抢焦点窗口";
        if (IsOwnProcessWindow(hwnd)) return "本程序窗口";
        if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.IsEmpty) return "矩形为空";

        // WS_EX_APPWINDOW 表示「当作没有 owner」，显式要求出现在任务栏，
        // 少数应用把主窗口错误设成 owned 时靠它豁免。
        if ((ex & NativeMethods.WS_EX_APPWINDOW) == 0 && NativeMethods.Owner(hwnd) != IntPtr.Zero)
            return "被拥有的弹窗 / 对话框";

        if (checkTitle && NativeMethods.Title(hwnd).Length == 0) return "无标题";
        if (ShellClasses.Contains(NativeMethods.ClassName(hwnd))) return "shell 窗口";
        return null;
    }

    public static bool IsShellWindow(IntPtr hwnd)
        => hwnd != IntPtr.Zero && ShellClasses.Contains(NativeMethods.ClassName(hwnd));

    public static bool IsOwnProcessWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }
}
