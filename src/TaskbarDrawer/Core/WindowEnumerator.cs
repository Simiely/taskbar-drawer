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
    public bool Minimized { get; init; }
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
        uint self = (uint)Environment.ProcessId;

        NativeMethods.EnumWindows((hwnd, state) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            // 挂起 / 正在退出的窗口不再往下走：标题查询虽然带超时，但没必要为它等 120 ms。
            if (NativeMethods.IsHungAppWindow(hwnd)) return true;

            bool cloaked = NativeMethods.IsCloaked(hwnd, out int _);
            bool tool = (NativeMethods.ExStyle(hwnd) & NativeMethods.WS_EX_TOOLWINDOW) != 0;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            bool own = pid == self;
            NativeMethods.GetWindowRect(hwnd, out var rect);
            string title = own ? "" : NativeMethods.Title(hwnd);

            var facts = new WindowFacts
            {
                Visible = true,
                Cloaked = cloaked,
                ToolWindow = tool,
                OwnProcess = own,
                HasTitle = title.Length > 0,
                HasArea = !rect.IsEmpty
            };
            if (!WindowFilter.IsCandidate(in facts)) return true;

            string cls = NativeMethods.ClassName(hwnd);
            if (ShellClasses.Contains(cls)) return true;

            bool minimized = NativeMethods.IsIconic(hwnd);
            if (minimized && !includeMinimized) return true;

            list.Add(new WindowCandidate
            {
                Handle = hwnd,
                Title = title,
                ClassName = cls,
                ProcessId = pid,
                Rect = rect,
                Minimized = minimized
            });
            return true;
        }, IntPtr.Zero);

        return list;
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
