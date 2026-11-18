using System.Diagnostics;
using System.IO;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>
/// 快照：把运行时组「抓」成可持久化的记录（软件 + 位置 + 两层身份），以及枚举候选窗口。
/// 只读窗口、不改窗口——隐藏 / 还原在 <see cref="StashAllService"/>，这一片只负责「记下来」。
/// </summary>
public sealed partial class WorkspaceService
{
    private sealed class TopWindow
    {
        public IntPtr Handle;
        public string Path;
        public string Title;
        public string ClassName;
        public bool Visible;
        public bool ToolWindow;
        public bool Cloaked;
        public bool Minimized;

        /// <summary>被拥有的窗口（对话框 / 弹出层）：不能当配对候选，否则会抢走主窗口的位置。</summary>
        public bool Owned;

        /// <summary>枚举时读好的矩形（位次分配与配对都用它，省得重复 GetWindowRect）。</summary>
        public NativeMethods.RECT Rect;
    }

    /// <summary>不是「用户可见的应用窗口」但会被 EnumWindows 枚举到的内部类：追加列表里不列。</summary>
    private static readonly HashSet<string> InternalWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "IME",
        "MSCTFIME UI",
        "OleDdeWndClass",
        "OperationStatusWindow",
        "GDI+ Hook Window Class",
        "MS_WebcheckMonitor",
        "BluetoothNotificationAreaIconWindowClass"
    };

    /// <summary>只读抓取一个窗口的保存数据（不隐藏、不改动窗口），供「追加窗口」使用。</summary>
    public SavedWindow CaptureWindow(StashAllService stash, IntPtr hwnd)
    {
        var item = stash?.CaptureSnapshot(hwnd);
        if (item == null) return null;
        stash.RefreshPlacement(item);
        return ToSaved(item);
    }

    /// <summary>枚举当前屏幕 / 全部屏幕上的可见窗口，供场景详情列出所有可追加的窗口。</summary>
    public IReadOnlyList<WindowSnapshot> EnumerateWindows()
    {
        return TopLevelWindows()
            .Where(w => w.Visible && w.Title.Length > 0 && !w.ToolWindow && !w.Cloaked &&
                        !w.Owned && !InternalWindowClasses.Contains(w.ClassName))
            .Select(w => new WindowSnapshot
            {
                Handle = w.Handle,
                // 追加列表里显示干净标题：控制台 / 终端标题常带尾随空格。
                Title = w.Title.Trim(),
                ProcessName = string.IsNullOrWhiteSpace(w.Path)
                    ? ""
                    : Path.GetFileNameWithoutExtension(w.Path),
                ExePath = w.Path,
                ClassName = w.ClassName,
                MonitorDevice = NativeMethods.MonitorDeviceOf(w.Handle),
                Minimized = w.Minimized
            })
            .ToArray();
    }

    /// <summary>
    /// 把运行时的一组窗口存成场景。位置按此刻的窗口刷新，
    /// 展开着的组也能拿到当前矩形，而不是上一次收起时的旧位置。
    /// </summary>
    public SavedGroup SaveFrom(StashAllService stash, int groupId, string name)
    {
        var windows = CaptureWindows(stash, groupId);
        if (windows.Count == 0) return null;

        var group = new SavedGroup
        {
            Id = Guid.NewGuid().ToString("N")[..6],
            Name = string.IsNullOrWhiteSpace(name) ? NextSceneName() : name.Trim(),
            Badge = SceneBadge.Default,
            CreatedAt = DateTime.Now,
            Windows = windows
        };
        _groups.Add(group);
        if (!Persist())
        {
            // 落不了盘就不是一个真场景：撤掉内存里这条，让调用方把窗口放回桌面。
            // 否则用户会看到一个「重启就消失」的胶囊，而窗口其实已经被藏进收纳区了。
            _groups.Remove(group);
            return null;
        }
        return group;
    }

    /// <summary>
    /// 关闭前按当前实际位置再存一次，下次还原才是用户最后摆放的样子。
    /// <b>只刷新位置，不改成员集合</b>：移除是编辑器的显式动作，不能因为关闭场景时
    /// 从运行时组整份重抓又写回来（旧快照的 Suppressed 是 exe + 标题键，标题一变就失配）。
    /// 已经关掉的程序项保留在快照里，详情窗会标「未打开」，由用户决定要不要移除。
    /// </summary>
    public void UpdateSnapshot(SavedGroup group, StashAllService stash, int groupId)
    {
        if (group == null || group.Windows.Count == 0) return;
        var captured = CaptureWindows(stash, groupId);
        if (captured.Count == 0) return;

        foreach (var saved in group.Windows)
        {
            // 同程序里按位置最近找运行中的对应窗口；标题会变，不能拿它当身份。
            var match = captured
                .Where(w => string.Equals(Normalize(w.ExePath), Normalize(saved.ExePath),
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(w => RectDistance(w, saved))
                .FirstOrDefault();
            if (match == null) continue;

            saved.Left = match.Left;
            saved.Top = match.Top;
            saved.Width = match.Width;
            saved.Height = match.Height;
            saved.Maximized = match.Maximized;
            if (!string.IsNullOrEmpty(match.MonitorDevice)) saved.MonitorDevice = match.MonitorDevice;
        }
        Persist();
    }

    private static long RectDistance(SavedWindow a, SavedWindow b)
        => Math.Abs((long)a.Left - b.Left) + Math.Abs((long)a.Top - b.Top) +
           Math.Abs((long)a.Width - b.Width) + Math.Abs((long)a.Height - b.Height);

    private static List<SavedWindow> CaptureWindows(StashAllService stash, int groupId)
    {
        var windows = new List<SavedWindow>();
        foreach (var item in stash.Items.Where(i => i.GroupId == groupId).ToArray())
        {
            // 读不到 exe 路径的窗口（受保护进程）无法再启动，存进去只会制造点不开的条目。
            if (string.IsNullOrWhiteSpace(item.ProcessPath)) continue;

            // 被拥有的弹窗不落盘：它随簇主一起隐藏 / 恢复 / 关闭，没有独立记录的价值。
            // 旧版本可能把它收进来过，这一道同时兼作历史数据清洗。
            if (NativeMethods.Owner(item.Handle) != IntPtr.Zero) continue;

            stash.RefreshPlacement(item);
            var saved = ToSaved(item);
            // 两层身份：L1 由 exe 决定（绝大多数桌面应用没有显式 AUMID，读取端暂不填，
            // 且当前候选本来就按 exe 过滤，填了也不改变结果）；L2 还要窗口类名。
            // 位次是相对量，等整组抓完再统一分配。
            saved.AppId = WindowIdentity.AppId(null, item.ProcessPath);
            saved.ClassName = NativeMethods.ClassName(item.Handle);
            windows.Add(saved);
            Diagnostics.Info("scene",
                $"保存窗口 hwnd=0x{item.Handle.ToInt64():X} exe={Path.GetFileName(item.ProcessPath)} " +
                $"「{Brief(item.Title)}」 device={item.MonitorDevice} rect={saved.Left},{saved.Top} " +
                $"{saved.Width}x{saved.Height} max={item.Maximized} appId={saved.AppId} " +
                $"class={saved.ClassName}");
        }

        // 位次在这里落定：同 (AppId, 类名) 的窗口按保存位置排序，0 是左上角那个。
        WindowIdentity.AssignOrdinals(windows);
        foreach (var window in windows)
        {
            Diagnostics.Info("scene", $"  位次 appId={window.AppId} class={window.ClassName} " +
                $"ordinal={window.Ordinal} 「{Brief(window.Title)}」");
        }
        return windows;
    }

    /// <summary>把运行时记录转成快照项；位置统一取 rcNormalPosition（工作区坐标已换算成屏幕坐标）。</summary>
    private static SavedWindow ToSaved(StashedWindow item)
    {
        var rect = item.Placement.rcNormalPosition;
        if (rect.IsEmpty) NativeMethods.GetWindowRect(item.Handle, out rect);

        return new SavedWindow
        {
            ExePath = item.ProcessPath,
            ProcessName = item.ProcessName,
            // 快照存干净标题：旧版 emoji 前缀只在场景展开期间挂在窗口上，不属于窗口自己的名字。
            Title = SceneTitle.Strip(item.Title).Trim(),
            Left = rect.Left,
            Top = rect.Top,
            Width = rect.Width,
            Height = rect.Height,
            MonitorDevice = item.MonitorDevice,
            Maximized = item.Maximized
        };
    }

    /// <summary>全部顶层窗口（含隐藏的，关闭组里收起来的窗口要用到）。</summary>
    private static List<TopWindow> TopLevelWindows()
    {
        var list = new List<TopWindow>();
        var paths = new Dictionary<uint, string>();

        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            if (WindowEnumerator.IsOwnProcessWindow(hwnd)) return true;
            if (WindowEnumerator.IsShellWindow(hwnd)) return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!paths.TryGetValue(pid, out string path))
            {
                path = ProcessPathOf(pid);
                paths[pid] = path;
            }

            NativeMethods.GetWindowRect(hwnd, out var rect);
            list.Add(new TopWindow
            {
                Handle = hwnd,
                Path = path,
                Rect = rect,
                // 缩掉场景前缀再比较标题，否则「同标题优先」会因为我们自己加的前缀失配。
                Title = SceneTitle.Strip(NativeMethods.Title(hwnd)),
                ClassName = NativeMethods.ClassName(hwnd),
                Visible = NativeMethods.IsWindowVisible(hwnd),
                ToolWindow = (NativeMethods.ExStyle(hwnd) & NativeMethods.WS_EX_TOOLWINDOW) != 0,
                Cloaked = NativeMethods.IsCloaked(hwnd, out _),
                Minimized = NativeMethods.IsIconic(hwnd),
                Owned = NativeMethods.Owner(hwnd) != IntPtr.Zero
            });
            return true;
        }, IntPtr.Zero);

        return list;
    }

    /// <summary>
    /// 参与位次分配的窗口：用户眼里的应用窗口（可见、有标题、非工具窗、非弹窗）。
    /// 与保存侧的口径一致，位次才对得上。最小化窗口保留——它的位置仍在原位，
    /// 排掉会让后面的位次全体前移。
    /// </summary>
    private static List<DisplayRules.RestoreCandidate> OrdinalCandidates(List<TopWindow> windows)
    {
        var list = new List<DisplayRules.RestoreCandidate>(windows.Count);
        foreach (var window in windows)
        {
            if (!window.Visible || window.Title.Length == 0) continue;
            if (window.ToolWindow || window.Owned || window.Cloaked) continue;
            list.Add(new DisplayRules.RestoreCandidate
            {
                Handle = window.Handle.ToInt64(),
                Title = window.Title,
                // 位次排序要先按显示器分组，所以这里必须带上设备名（多一次查询换跨屏稳定）。
                MonitorDevice = NativeMethods.MonitorDeviceOf(window.Handle),
                Rect = window.Rect,
                AppId = WindowIdentity.AppId(null, window.Path),
                ClassName = window.ClassName
            });
        }
        return list;
    }

    private static string ProcessPathOf(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}
