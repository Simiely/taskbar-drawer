using System.Diagnostics;
using System.Runtime.InteropServices;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>「收进任务栏」的一条记录：窗口句柄 + 隐藏前的位置。</summary>
public sealed class StashedWindow
{
    public IntPtr Handle { get; init; }
    public int GroupId { get; init; }
    public string Title { get; init; }
    public string ClassName { get; init; }
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; }
    public string ProcessPath { get; init; }
    public DateTime? ProcessStartTime { get; init; }
    public string MonitorDevice { get; set; }
    public bool Maximized { get; set; }
    public NativeMethods.WINDOWPLACEMENT Placement { get; set; }

    /// <summary>true = 正被收在收纳区里；false = 组已展开、窗口在屏幕上。</summary>
    public bool Hidden { get; set; }

    /// <summary>展开期间挂在窗口图标上的场景标识；null = 不挂。</summary>
    public SceneBadge Badge { get; set; }

    /// <summary>窗口自己的原图标（换成场景图标前记下，摘的时候原样放回）。</summary>
    public IntPtr OriginalIconBig { get; set; }

    public IntPtr OriginalIconSmall { get; set; }

    /// <summary>合成出来的场景图标（本进程创建 / 持有，换图或摘图时必须销毁）。</summary>
    public IntPtr SceneIconBig { get; set; }

    public IntPtr SceneIconSmall { get; set; }

    /// <summary>
    /// 最近一次发出还原请求的时间。Prune 用它异步确认「还原是否真的成功」——
    /// SW_RESTORE / SW_HIDE 都是跨线程投递，调用返回瞬间的可见性不可信。
    /// </summary>
    public DateTime RestoreRequestedAt { get; set; }

    public DateTime StashedAt { get; set; }
}

/// <summary>
/// 收纳落点：记下 WINDOWPLACEMENT 后把窗口隐藏；展开时按记录原位还原。
///
/// 与 v3 的区别：记录<b>常驻</b>——展开不删记录，只把 <see cref="StashedWindow.Hidden"/> 置 false，
/// 这样一组窗口可以反复「展开 → 再收起」，位置也按当前状态刷新。
///
/// 兜底原则：只有确认窗口已隐藏才标记为收起；还原后若仍不可见，退回系统最小化（任务栏可见），
/// 绝不让窗口既不在屏幕也不在任务栏。
/// </summary>
public sealed class WindowStash
{
    private readonly Dictionary<long, StashedWindow> _items = new();

    /// <summary>合成场景图标；为 null 时退化为「只收窗口、不换图标」。</summary>
    private readonly ISceneIconPainter _icons;

    public WindowStash(ISceneIconPainter icons = null)
    {
        _icons = icons;
    }

    public int Count => _items.Count;

    public IReadOnlyCollection<StashedWindow> Items => _items.Values;

    public bool Contains(long hwnd) => _items.ContainsKey(hwnd);

    /// <summary>窗口所属的组号；不在任何组里返回 0。单窗口模式用它把一组当成一个整体。</summary>
    public int GroupIdOf(IntPtr hwnd)
        => _items.TryGetValue(hwnd.ToInt64(), out var item) ? item.GroupId : 0;

    /// <summary>记录隐藏前的位置（不隐藏）；不合格的窗口返回 null。</summary>
    public StashedWindow Capture(IntPtr hwnd, int groupId)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return null;
        if (!NativeMethods.IsWindowVisible(hwnd)) return null;

        long exStyle = NativeMethods.ExStyle(hwnd);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return null;

        string title = NativeMethods.Title(hwnd);
        if (title.Length == 0) return null;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return null;
        if (NativeMethods.IsCloaked(hwnd, out _)) return null;

        NativeMethods.GetWindowRect(hwnd, out var rect);
        if (rect.IsEmpty) return null;

        var meta = ReadProcess(pid);
        var item = new StashedWindow
        {
            Handle = hwnd,
            GroupId = groupId,
            // 记录干净标题：旧版 emoji 前缀是我们临时挂的，不属于窗口自己的名字。
            Title = SceneTitle.Strip(title),
            ClassName = NativeMethods.ClassName(hwnd),
            ProcessId = pid,
            ProcessName = meta.Name,
            ProcessPath = meta.Path,
            ProcessStartTime = meta.StartTime,
            Hidden = false,
            StashedAt = DateTime.UtcNow
        };
        RefreshPlacement(item);
        return item;
    }

    /// <summary>
    /// 隐藏窗口并记入列表。
    /// ShowWindow 是跨线程「投递」，调用返回时窗口往往还是可见的，所以这里先乐观记录，
    /// 真实可见性交给 <see cref="Prune"/> 的宽限期纠正。
    /// 早先「当场校验可见性、失败就不记录」会造成窗口最终被隐藏却无人认领（孤儿窗口）。
    /// </summary>
    public bool Hide(StashedWindow item)
    {
        if (item == null || !NativeMethods.IsWindow(item.Handle)) return false;

        ClearSceneDecoration(item);
        NativeMethods.ShowWindow(item.Handle, NativeMethods.SW_HIDE);

        item.Hidden = true;
        item.RestoreRequestedAt = default;
        item.StashedAt = DateTime.UtcNow;
        _items[item.Handle.ToInt64()] = item;
        Diagnostics.Info("stash",
            $"收起 hwnd=0x{item.Handle.ToInt64():X} 「{Brief(item.Title)}」 device={item.MonitorDevice} " +
            $"rect={item.Placement.rcNormalPosition} max={item.Maximized}");
        return true;
    }

    /// <summary>再次收起：先按此刻的位置刷新记录，再 SW_HIDE（组记录保持不变）。</summary>
    public bool HideAgain(long hwnd)
    {
        if (!_items.TryGetValue(hwnd, out var item)) return false;
        if (!NativeMethods.IsWindow(item.Handle)) return false;

        // 展开期间用户可能挪过窗口 / 换过显示器，收起前重新记一次。
        RefreshPlacement(item);

        ClearSceneDecoration(item);
        NativeMethods.ShowWindow(item.Handle, NativeMethods.SW_HIDE);

        item.Hidden = true;
        item.RestoreRequestedAt = default;
        item.StashedAt = DateTime.UtcNow;
        Diagnostics.Info("stash",
            $"再收起 hwnd=0x{item.Handle.ToInt64():X} 「{Brief(item.Title)}」 device={item.MonitorDevice} " +
            $"rect={item.Placement.rcNormalPosition} max={item.Maximized}");
        return true;
    }

    /// <summary>原位还原（保留记录，标记为已展开）：SW_RESTORE → 写回位置 / 最大化状态。</summary>
    public bool Restore(long hwnd)
    {
        if (!_items.TryGetValue(hwnd, out var item)) return false;

        if (!NativeMethods.IsWindow(item.Handle))
        {
            _items.Remove(hwnd);
            return false;
        }

        NativeMethods.ShowWindow(item.Handle, NativeMethods.SW_RESTORE);

        // 目标屏可能已经拔掉 / 分辨率变了：先把保存的矩形夹回最近的工作区，
        // 否则窗口会「可见但落在桌面外」——任务栏有按钮，屏幕上却找不到。
        var target = DisplayRules.EnsureVisible(item.Placement.rcNormalPosition);
        if (!target.IsEmpty)
        {
            var placement = item.Placement;
            placement.length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>();
            placement.flags = 0;
            placement.showCmd = item.Maximized
                ? NativeMethods.SW_SHOWMAXIMIZED
                : NativeMethods.SW_SHOWNORMAL;
            // 保存的是屏幕坐标，SetWindowPlacement 要的是工作区坐标：按目标矩形所在显示器的
            // 任务栏偏移量换算（不是绝对工作区原点，否则副屏会整体平移一整块屏宽）。
            placement.rcNormalPosition = NativeMethods.ToWorkspaceCoordinates(
                target, NativeMethods.WorkspaceOffsetOfDevice(item.MonitorDevice, target));
            NativeMethods.SetWindowPlacement(item.Handle, ref placement);
        }

        // SW_RESTORE 同样是跨线程投递：此刻 IsWindowVisible 可能还是 false，不能当场判定失败。
        // 先乐观标记为已展开，由 Prune 在宽限期后确认；真没起来再退回任务栏（记录保留）。
        item.RestoreRequestedAt = DateTime.UtcNow;

        // 个别应用会在 ShowWindow 后重排布局；再按保存的窗口矩形校正一次，确保「原位回去」。
        bool corrected = false;
        if (!item.Maximized && !target.IsEmpty)
        {
            NativeMethods.GetWindowRect(item.Handle, out var now);
            if (Math.Abs(now.Left - target.Left) > 2 || Math.Abs(now.Top - target.Top) > 2 ||
                Math.Abs(now.Width - target.Width) > 2 || Math.Abs(now.Height - target.Height) > 2)
            {
                NativeMethods.SetWindowPos(item.Handle, IntPtr.Zero, target.Left, target.Top,
                    target.Width, target.Height, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                corrected = true;
            }
        }

        NativeMethods.GetWindowRect(item.Handle, out var actual);
        Diagnostics.Info("stash",
            $"展开 hwnd=0x{item.Handle.ToInt64():X} 「{Brief(item.Title)}」 device={item.MonitorDevice} " +
            $"target={target} actual={actual} max={item.Maximized} corrected={corrected}");

        item.Hidden = false;
        // 回到屏幕后才换窗口图标：任务栏按钮会显示「场景标识 + 程序图标」。
        ApplySceneIcon(item);
        return true;
    }

    public void RemoveGroup(int groupId)
    {
        foreach (var item in _items.Values.Where(i => i.GroupId == groupId).ToArray())
        {
            ClearSceneDecoration(item);
            item.Badge = null;
            _items.Remove(item.Handle.ToInt64());
        }
    }

    /// <summary>给成员窗口挂场景标识；收起状态下只记录，等展开时再换图标。</summary>
    public void SetSceneIcon(StashedWindow item, SceneBadge badge)
    {
        if (item == null) return;
        item.Badge = SceneBadge.Coerce(badge);
        if (!item.Hidden) ApplySceneIcon(item);
    }

    /// <summary>摘掉场景标识（窗口恢复成应用自己的图标与标题）。</summary>
    public void ClearSceneIcon(StashedWindow item)
    {
        if (item == null) return;
        item.Badge = null;
        if (!item.Hidden) ClearSceneDecoration(item);
    }

    /// <summary>外观参数（图标间距）改了：已展开的窗口重新合成一次图标。</summary>
    public void RefreshSceneIcons()
    {
        foreach (var item in _items.Values)
        {
            if (item.Badge == null || item.Hidden) continue;
            if (!NativeMethods.IsWindow(item.Handle)) continue;
            // 先放回应用自己的图标，下次 Apply 会按新参数重新取原图标、重新合成。
            RemoveSceneIcon(item);
            ApplySceneIcon(item);
        }
    }

    /// <summary>按窗口此刻的实际位置刷新记录（保存场景前调用）。</summary>
    public void Refresh(StashedWindow item)
    {
        if (item != null && NativeMethods.IsWindow(item.Handle)) RefreshPlacement(item);
    }

    /// <summary>记录一个仍然可见的窗口（不隐藏）：从场景拉起的窗口接着按普通组管理。</summary>
    public bool Track(StashedWindow item)
    {
        if (item == null || !NativeMethods.IsWindow(item.Handle)) return false;
        item.Hidden = false;
        item.RestoreRequestedAt = default;
        item.StashedAt = DateTime.UtcNow;
        ApplySceneIcon(item);
        _items[item.Handle.ToInt64()] = item;
        return true;
    }

    /// <summary>
    /// 兜底：把还收着的窗口交回任务栏（最小化、可见）。
    /// 程序退出 / 崩溃时调用，避免窗口停在「不可见且无任务栏按钮」。
    /// </summary>
    public int ReleaseAll()
    {
        int released = 0;
        foreach (var item in _items.Values.ToArray())
        {
            // 场景没了：所有窗口（含仍在屏幕上的）都摘掉场景标识。
            ClearSceneDecoration(item);
            item.Badge = null;
            if (!item.Hidden) continue;
            if (ReleaseFallback(item.Handle)) released++;
        }
        _items.Clear();
        return released;
    }

    /// <summary>
    /// 清理已经销毁的记录。被外部还原（任务栏 / 应用自己 ShowWindow）的窗口不删除记录，
    /// 只把状态改成「已展开」，组仍然保留，随时可以再收起。
    /// 返回状态变化的条数（删除 + 被外部还原），调用方据此决定要不要刷新界面。
    /// </summary>
    public int Prune()
    {
        // ShowWindow(SW_HIDE) 是「投递到目标线程」的异步调用：刚收进的那一瞬间窗口往往还是
        // 可见的。给一个短暂的宽限期，避免把刚收起的窗口误判成「已展开」。
        long now = DateTime.UtcNow.Ticks;
        int changed = 0;
        foreach (long key in _items.Keys.ToArray())
        {
            var item = _items[key];
            if (!NativeMethods.IsWindow(item.Handle))
            {
                _items.Remove(key);
                changed++;
                continue;
            }
            if (item.Hidden)
            {
                if (NativeMethods.IsWindowVisible(item.Handle) && now - item.StashedAt.Ticks > PruneGraceTicks)
                {
                    item.Hidden = false;
                    changed++;
                }
                continue;
            }

            // 发出过还原请求却一直没真的回到屏幕：强制交回任务栏，避免卡在「不可见且无任务栏按钮」。
            if (item.RestoreRequestedAt != default &&
                now - item.RestoreRequestedAt.Ticks > PruneGraceTicks &&
                !NativeMethods.IsWindowVisible(item.Handle))
            {
                ReleaseFallback(item.Handle);
                item.RestoreRequestedAt = default;
            }

            // 应用会自己改图标（浏览器切页面换 favicon 等）：图标丢了就补回来，
            // 保证展开期间任务栏按钮上一直带着场景标识。
            // 挂起的窗口跳过：读写图标要跨进程发消息，别让一个卡死的程序拖慢定时器。
            if (item.Badge != null &&
                NativeMethods.IsWindowVisible(item.Handle) &&
                !NativeMethods.IsHungAppWindow(item.Handle))
            {
                ApplySceneIcon(item);
            }
        }
        return changed;
    }

    /// <summary>收进之后多久才承认「窗口又回到屏幕上了」（留给异步 ShowWindow 落地）。</summary>
    private static readonly long PruneGraceTicks = TimeSpan.FromMilliseconds(700).Ticks;

    /// <summary>日志里的标题截断：完整标题可能很长，前 40 字足够认出窗口。</summary>
    private static string Brief(string title)
        => string.IsNullOrEmpty(title) ? "" : title.Length <= 40 ? title : title[..40] + "…";

    /// <summary>
    /// 给窗口换上「场景标识 + 程序图标」的合成图标（标识叠在程序图标角上）。
    /// 图标句柄是系统级 USER 对象，可以跨进程设置；本进程负责在换图 / 摘图时销毁旧句柄。
    /// </summary>
    private void ApplySceneIcon(StashedWindow item)
    {
        if (_icons == null || item.Badge == null) return;
        if (!NativeMethods.IsWindow(item.Handle) || item.Hidden) return;

        IntPtr currentBig = NativeMethods.WindowIcon(item.Handle, big: true);
        IntPtr currentSmall = NativeMethods.WindowIcon(item.Handle, big: false);
        if (item.SceneIconBig != IntPtr.Zero &&
            currentBig == item.SceneIconBig && currentSmall == item.SceneIconSmall)
            return; // 已经是我们的图标：Prune 每 2.5 s 走到这里时直接跳过，不重画。

        if (item.SceneIconBig == IntPtr.Zero)
        {
            // 第一次挂：记下窗口自己的图标，摘的时候原样放回去。
            // 上一进程被强杀时窗口上会残留一张已销毁的 HICON：WM_GETICON 照样返回非零，
            // 直接拿去画只会得到「原图标空白」。无效就先清掉，让系统回落到窗口类图标。
            item.OriginalIconBig = CaptureOriginalIcon(item, currentBig, big: true);
            item.OriginalIconSmall = CaptureOriginalIcon(item, currentSmall, big: false);
            // 1.6.6 及更早版本把 emoji 写在标题里：升级后第一次挂图标时顺手清掉。
            RemoveLegacyTitlePrefix(item);
        }

        // 画图标用的原图可以比「记下来用于还原的句柄」更宽容：类图标也没有时按 exe 抽一张。
        // exe 抽出来的图标归本进程，画完立刻销毁，不留在窗口上。
        IntPtr paintBig = PaintIcon(item, item.OriginalIconBig, big: true, out bool ownedBig);
        IntPtr paintSmall = PaintIcon(item, item.OriginalIconSmall, big: false, out bool ownedSmall);
        bool created;
        try
        {
            created = _icons.TryCreate(item.Badge, paintBig, paintSmall,
                NativeMethods.DpiOf(item.Handle), out IntPtr big, out IntPtr small);
            if (created)
            {
                NativeMethods.SetWindowIcon(item.Handle, big: true, big);
                NativeMethods.SetWindowIcon(item.Handle, big: false, small);

                IntPtr oldBig = item.SceneIconBig;
                IntPtr oldSmall = item.SceneIconSmall;
                item.SceneIconBig = big;
                item.SceneIconSmall = small;
                if (oldBig != IntPtr.Zero) _icons.Release(oldBig);
                if (oldSmall != IntPtr.Zero) _icons.Release(oldSmall);
            }
        }
        finally
        {
            if (ownedBig) NativeMethods.DestroyIcon(paintBig);
            if (ownedSmall) NativeMethods.DestroyIcon(paintSmall);
        }
    }

    /// <summary>
    /// 拿一张能用来画的原图标：先用记下的原图标，再退窗口类图标，
    /// 最后按进程 exe 抽一张（资源管理器这类窗口的图标由 shell 单独给，类图标是空的）。
    /// 只有 exe 抽出来的归本进程所有，调用方画完要销毁。
    /// </summary>
    private static IntPtr PaintIcon(StashedWindow item, IntPtr original, bool big, out bool owned)
    {
        owned = false;
        if (NativeMethods.IsIconValid(original)) return original;

        IntPtr classIcon = NativeMethods.ClassIcon(item.Handle, big);
        if (NativeMethods.IsIconValid(classIcon)) return classIcon;

        IntPtr exeIcon = NativeMethods.IconFromExecutable(item.ProcessPath, big);
        if (NativeMethods.IsIconValid(exeIcon))
        {
            owned = true;
            return exeIcon;
        }
        if (exeIcon != IntPtr.Zero) NativeMethods.DestroyIcon(exeIcon);
        return IntPtr.Zero;
    }

    /// <summary>
    /// 记下「还原时放回去」的图标句柄：有效就用；无效（多半是上一进程强杀后残留的
    /// 已销毁 HICON）先清掉，让 WM_GETICON 回落到窗口类图标；类图标也没有（资源管理器
    /// 这类窗口的图标由 shell 单独给）就按 exe 抽一张并设回窗口，免得摘图标后空着。
    /// exe 这张不销毁：窗口还引用着它，销毁会立刻变成空图标。
    /// </summary>
    private static IntPtr CaptureOriginalIcon(StashedWindow item, IntPtr current, bool big)
    {
        if (NativeMethods.IsIconValid(current)) return current;

        NativeMethods.SetWindowIcon(item.Handle, big, IntPtr.Zero);
        IntPtr fallback = NativeMethods.WindowIcon(item.Handle, big);
        if (NativeMethods.IsIconValid(fallback)) return fallback;

        IntPtr exeIcon = NativeMethods.IconFromExecutable(item.ProcessPath, big);
        if (NativeMethods.IsIconValid(exeIcon))
        {
            NativeMethods.SetWindowIcon(item.Handle, big, exeIcon);
            return exeIcon;
        }
        if (exeIcon != IntPtr.Zero) NativeMethods.DestroyIcon(exeIcon);
        return IntPtr.Zero;
    }

    /// <summary>把窗口图标放回它自己的样子，并销毁我们创建的句柄。</summary>
    private void RemoveSceneIcon(StashedWindow item)
    {
        if (item.SceneIconBig == IntPtr.Zero && item.SceneIconSmall == IntPtr.Zero) return;

        if (NativeMethods.IsWindow(item.Handle))
        {
            // 只把自己换上去的图标放回去：应用中途自己改过图标（浏览器换 favicon 等）就别覆盖它。
            // 用大图标判断归属：ICON_BIG 读回来的是精确句柄，ICON_SMALL2 可能被系统换成缩放版。
            if (NativeMethods.WindowIcon(item.Handle, big: true) == item.SceneIconBig)
            {
                NativeMethods.SetWindowIcon(item.Handle, big: true, item.OriginalIconBig);
                NativeMethods.SetWindowIcon(item.Handle, big: false, item.OriginalIconSmall);
            }
        }
        if (item.SceneIconBig != IntPtr.Zero) _icons?.Release(item.SceneIconBig);
        if (item.SceneIconSmall != IntPtr.Zero) _icons?.Release(item.SceneIconSmall);
        item.SceneIconBig = IntPtr.Zero;
        item.SceneIconSmall = IntPtr.Zero;
        item.OriginalIconBig = IntPtr.Zero;
        item.OriginalIconSmall = IntPtr.Zero;
    }

    /// <summary>场景装饰（图标 + 旧版标题前缀）全部摘掉：窗口回到应用自己的样子。</summary>
    private void ClearSceneDecoration(StashedWindow item)
    {
        RemoveSceneIcon(item);
        RemoveLegacyTitlePrefix(item);
    }

    /// <summary>摘掉 1.6.6 及更早版本写在标题前的场景 emoji。</summary>
    private static void RemoveLegacyTitlePrefix(StashedWindow item)
    {
        if (!NativeMethods.IsWindow(item.Handle)) return;
        string current = NativeMethods.Title(item.Handle);
        if (current.Length == 0) return;
        string stripped = SceneTitle.Strip(current);
        if (!string.Equals(current, stripped, StringComparison.Ordinal))
            NativeMethods.SetTitle(item.Handle, stripped);
    }

    private static void RefreshPlacement(StashedWindow item)
    {
        var placement = new NativeMethods.WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>()
        };
        bool hasPlacement = NativeMethods.GetWindowPlacement(item.Handle, ref placement);
        // rcNormalPosition 用的是「工作区坐标」，窗口矩形是屏幕坐标。任务栏位置 / DPI 变化后
        // 工作区原点会变，直接把 rcNormal 存下来再写回去会整体平移，所以统一转成屏幕坐标保存。
        // 换算只扣任务栏偏移量（rcWork - rcMonitor），不能加显示器在虚拟桌面里的原点。
        if (hasPlacement)
            placement.rcNormalPosition = NativeMethods.ToScreenCoordinates(
                placement.rcNormalPosition, NativeMethods.WorkspaceOffsetOfWindow(item.Handle));

        item.Placement = placement;
        item.Maximized = NativeMethods.IsZoomed(item.Handle) ||
                         (hasPlacement && placement.showCmd == NativeMethods.SW_SHOWMAXIMIZED);
        item.MonitorDevice = NativeMethods.MonitorDeviceOf(item.Handle);
    }

    private static bool ReleaseFallback(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;
        // 最小化但不激活：窗口回到任务栏，用户可以照常点任务栏找回。
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWMINNOACTIVE);
        return NativeMethods.IsWindowVisible(hwnd);
    }

    private sealed class ProcessMeta
    {
        public string Name;
        public string Path;
        public DateTime? StartTime;
    }

    // 同一批收纳里同一 pid 往往有多个窗口（浏览器、IDE）：进程元数据一次运行内不会变，按 pid 缓存。
    private readonly Dictionary<uint, ProcessMeta> _processCache = new();

    private ProcessMeta ReadProcess(uint pid)
    {
        if (_processCache.TryGetValue(pid, out var cached)) return cached;

        var meta = new ProcessMeta();
        try
        {
            using var process = Process.GetProcessById((int)pid);
            meta.Name = process.ProcessName;
            // 被保护 / 已退出的进程读不到 MainModule 与启动时间：元数据缺省即可。
            try { meta.Path = process.MainModule?.FileName; } catch { }
            try { meta.StartTime = process.StartTime.ToUniversalTime(); } catch { }
        }
        catch { /* 进程已退出（句柄可能已失效），返回空元数据 */ }

        _processCache[pid] = meta;
        return meta;
    }
}
