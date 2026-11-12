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
    public string AppKey { get; init; }
    public string MonitorDevice { get; set; }
    public uint Dpi { get; set; }
    public bool Maximized { get; set; }
    public NativeMethods.WINDOWPLACEMENT Placement { get; set; }

    /// <summary>true = 正被收在收纳区里；false = 组已展开、窗口在屏幕上。</summary>
    public bool Hidden { get; set; }

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
            Title = title,
            ClassName = NativeMethods.ClassName(hwnd),
            ProcessId = pid,
            ProcessName = meta.Name,
            ProcessPath = meta.Path,
            ProcessStartTime = meta.StartTime,
            AppKey = WindowIdentity.FromProcess(meta.Path, meta.Name),
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

        NativeMethods.ShowWindow(item.Handle, NativeMethods.SW_HIDE);

        item.Hidden = true;
        item.RestoreRequestedAt = default;
        item.StashedAt = DateTime.UtcNow;
        _items[item.Handle.ToInt64()] = item;
        return true;
    }

    /// <summary>再次收起：先按此刻的位置刷新记录，再 SW_HIDE（组记录保持不变）。</summary>
    public bool HideAgain(long hwnd)
    {
        if (!_items.TryGetValue(hwnd, out var item)) return false;
        if (!NativeMethods.IsWindow(item.Handle)) return false;

        // 展开期间用户可能挪过窗口 / 换过显示器，收起前重新记一次。
        RefreshPlacement(item);

        NativeMethods.ShowWindow(item.Handle, NativeMethods.SW_HIDE);

        item.Hidden = true;
        item.RestoreRequestedAt = default;
        item.StashedAt = DateTime.UtcNow;
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

        if (!item.Placement.rcNormalPosition.IsEmpty)
        {
            var placement = item.Placement;
            placement.length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>();
            placement.flags = 0;
            placement.showCmd = item.Maximized
                ? NativeMethods.SW_SHOWMAXIMIZED
                : NativeMethods.SW_SHOWNORMAL;
            // 保存的是屏幕坐标，SetWindowPlacement 要的是工作区坐标：按「此刻」该显示器的工作区原点换算。
            placement.rcNormalPosition = NativeMethods.ToWorkspaceCoordinates(
                placement.rcNormalPosition, NativeMethods.WorkOriginOfDevice(item.MonitorDevice));
            NativeMethods.SetWindowPlacement(item.Handle, ref placement);
        }

        // SW_RESTORE 同样是跨线程投递：此刻 IsWindowVisible 可能还是 false，不能当场判定失败。
        // 先乐观标记为已展开，由 Prune 在宽限期后确认；真没起来再退回任务栏（记录保留）。
        item.RestoreRequestedAt = DateTime.UtcNow;

        // 个别应用会在 ShowWindow 后重排布局；再按保存的窗口矩形校正一次，确保「原位回去」。
        var target = item.Placement.rcNormalPosition;
        if (!item.Maximized && !target.IsEmpty)
        {
            NativeMethods.GetWindowRect(item.Handle, out var now);
            if (Math.Abs(now.Left - target.Left) > 2 || Math.Abs(now.Top - target.Top) > 2 ||
                Math.Abs(now.Width - target.Width) > 2 || Math.Abs(now.Height - target.Height) > 2)
            {
                NativeMethods.SetWindowPos(item.Handle, IntPtr.Zero, target.Left, target.Top,
                    target.Width, target.Height, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
        }

        item.Hidden = false;
        return true;
    }

    public void RemoveGroup(int groupId)
    {
        foreach (long key in _items.Where(p => p.Value.GroupId == groupId).Select(p => p.Key).ToArray())
            _items.Remove(key);
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
            if (!item.Hidden) continue;
            if (ReleaseFallback(item.Handle)) released++;
        }
        _items.Clear();
        return released;
    }

    /// <summary>
    /// 清理已经销毁的记录。被外部还原（任务栏 / 应用自己 ShowWindow）的窗口不删除记录，
    /// 只把状态改成「已展开」，组仍然保留，随时可以再收起。
    /// </summary>
    public int Prune()
    {
        // ShowWindow(SW_HIDE) 是「投递到目标线程」的异步调用：刚收进的那一瞬间窗口往往还是
        // 可见的。给一个短暂的宽限期，避免把刚收起的窗口误判成「已展开」。
        long now = DateTime.UtcNow.Ticks;
        int removed = 0;
        foreach (long key in _items.Keys.ToArray())
        {
            var item = _items[key];
            if (!NativeMethods.IsWindow(item.Handle))
            {
                _items.Remove(key);
                removed++;
                continue;
            }
            if (item.Hidden)
            {
                if (NativeMethods.IsWindowVisible(item.Handle) && now - item.StashedAt.Ticks > PruneGraceTicks)
                    item.Hidden = false;
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
        }
        return removed;
    }

    /// <summary>收进之后多久才承认「窗口又回到屏幕上了」（留给异步 ShowWindow 落地）。</summary>
    private static readonly long PruneGraceTicks = TimeSpan.FromMilliseconds(700).Ticks;

    private static void RefreshPlacement(StashedWindow item)
    {
        var placement = new NativeMethods.WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>()
        };
        bool hasPlacement = NativeMethods.GetWindowPlacement(item.Handle, ref placement);
        // rcNormalPosition 用的是「工作区坐标」，窗口矩形是屏幕坐标。任务栏位置 / DPI 变化后
        // 工作区原点会变，直接把 rcNormal 存下来再写回去会整体平移，所以统一转成屏幕坐标保存。
        if (hasPlacement)
            placement.rcNormalPosition = NativeMethods.ToScreenCoordinates(
                placement.rcNormalPosition, NativeMethods.WorkOriginOfWindow(item.Handle));

        item.Placement = placement;
        item.Maximized = NativeMethods.IsZoomed(item.Handle) ||
                         (hasPlacement && placement.showCmd == NativeMethods.SW_SHOWMAXIMIZED);
        item.MonitorDevice = NativeMethods.MonitorDeviceOf(item.Handle);
        item.Dpi = NativeMethods.DpiOf(item.Handle);
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

    private static ProcessMeta ReadProcess(uint pid)
    {
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
        return meta;
    }
}
