using System.Diagnostics;
using System.IO;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>
/// 还原：从快照把窗口摆回原位，或者关掉这一组自己的窗口。
/// 配对规则在 <see cref="WindowIdentity"/>，窗口操作在 <see cref="IWindowGateway"/>，
/// 这一片只负责「先摆位还是先启动」的编排与 12 s 的等待窗口期。
/// </summary>
public sealed partial class WorkspaceService
{
    /// <summary>
    /// 关掉这一组自己的窗口（WM_CLOSE，等价逐个点 ×）。返回<b>真正投出去的句柄</b>：
    /// WM_CLOSE 是异步的，投递成功不代表窗口关了（程序可能弹保存框），
    /// 调用方要过一会儿用 <see cref="CloseReport.Measure"/> 数实际关掉了几个。
    ///
    /// 只认运行时组里记录的句柄，不按 exe 路径扫全系统——否则同一个程序里
    /// 不属于这一组的窗口（例如正在下载的浏览器）也会被一起关掉，
    /// 那些程序就会各自弹「这将取消所有传输」之类的确认框。
    /// </summary>
    public IReadOnlyList<IntPtr> Close(StashAllService stash, int groupId)
    {
        var posted = new List<IntPtr>();
        var handles = stash.Items
            .Where(i => i.GroupId == groupId)
            .Select(i => i.Handle)
            .Distinct()
            .ToArray();

        // 旧场景里可能混进过弹窗：它的根 owner 也在这组时跳过——关主窗口时它会跟着关，
        // 单独发一次 WM_CLOSE 反而可能触发应用自己的「关闭主窗口」逻辑。
        var members = new HashSet<long>(handles.Select(h => h.ToInt64()));

        foreach (IntPtr hwnd in handles)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) continue;

            IntPtr rootOwner = NativeMethods.RootOwner(hwnd);
            if (rootOwner != hwnd && members.Contains(rootOwner.ToInt64())) continue;

            if (NativeMethods.PostMessageW(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
                posted.Add(hwnd);
        }
        return posted;
    }

    /// <summary>
    /// 还原这一组：已经在跑的窗口直接挪回保存位置，没跑的程序启动后等它出窗口再摆位。
    /// 每摆好一个窗口回调一次（启动的窗口在后台线程回调），由上层接入运行时组，
    /// 之后这一组就按普通场景收起 / 展开，不需要反复关掉再开。
    /// excluded 是已经被其它场景管理的窗口句柄：它们不属于这次还原，绝不能挪走。
    /// </summary>
    public (int moved, int launched, int busy, int failed) Restore(
        SavedGroup group,
        IReadOnlySet<long> excluded,
        Func<IntPtr, bool> adopt,
        Action<IntPtr> adoptLater,
        Action onComplete = null)
    {
        // 只枚举一次：matches 与 known 用同一份快照，避免两次枚举之间窗口状态变化导致重复匹配。
        var windows = TopLevelWindows();
        var known = new HashSet<long>(windows.Select(w => w.Handle.ToInt64()));
        // 位次必须基于「本次枚举到的全部窗口」一次算好：不能一边配对一边去掉已用的，
        // 否则第二个保存项算出来的位次会漂移，两个窗口就会互换位置。
        var ordinals = WindowIdentity.AssignOrdinals(OrdinalCandidates(windows));
        var used = new HashSet<long>();
        var pending = new List<SavedWindow>();
        int moved = 0;
        int busy = 0;
        int failed = 0;

        Diagnostics.Info("scene",
            $"还原开始 scene={group.Name}({group.Id}) windows={group.Windows.Count} 已被其它场景管理={excluded?.Count ?? 0}");

        foreach (var saved in group.Windows)
        {
            string exe = Normalize(saved.ExePath);
            if (exe == null) continue;

            string expectedDevice = DisplayRules.MonitorOf(saved);
            bool deviceExists = expectedDevice == null ||
                                NativeMethods.TryGetMonitorOfDevice(expectedDevice, out _);
            Diagnostics.Info("scene",
                $"保存项 exe={Path.GetFileName(saved.ExePath)} 「{Brief(saved.Title)}」 " +
                $"device={expectedDevice ?? "(未记录)"} 屏存在={deviceExists} " +
                $"rect=({saved.Left},{saved.Top},{saved.Left + saved.Width},{saved.Top + saved.Height}) " +
                $"max={saved.Maximized}");

            // 同程序的窗口：这里**不先按可见性过滤**。收在别的场景里的窗口是 SW_HIDE 的
            // （IsWindowVisible=false），但程序确实已经在跑；早先只看可见窗口，于是还原一个
            // 「程序都收在另一个场景里」的场景时会再启动一遍实例——单实例程序不出新窗口，
            // 还原要白等 12 s 超时，用户还多出一份重复进程（2026-11-18 日志 15:41:40 现场）。
            // 可见性只用来挑「可以接管 / 摆位」的候选，busy 判断见下面的 excluded 检查。
            //
            // 候选不要求「必须和保存时同一块屏」：程序重启后系统通常把窗口开在主屏，
            // 按屏过滤只会得到 busy、用户看到「位置没恢复」；现在会把它挪回保存屏。
            var sameExe = windows
                .Where(w => Normalize(w.Path) == exe)
                .ToArray();
            var candidates = new List<DisplayRules.RestoreCandidate>();
            foreach (var window in sameExe)
            {
                if (!window.Visible || window.Title.Length == 0 || window.ToolWindow || window.Owned) continue;

                long handle = window.Handle.ToInt64();
                if (used.Contains(handle)) continue;
                if (excluded != null && excluded.Contains(handle)) continue;

                candidates.Add(new DisplayRules.RestoreCandidate
                {
                    Handle = handle,
                    Title = window.Title,
                    MonitorDevice = NativeMethods.MonitorDeviceOf(window.Handle),
                    Rect = window.Rect,
                    AppId = WindowIdentity.AppId(null, window.Path),
                    ClassName = window.ClassName
                });
            }

            if (candidates.Count > 0)
            {
                Diagnostics.Info("scene", "  候选 " + string.Join("; ", candidates.Select(c =>
                    $"0x{c.Handle:X}「{Brief(c.Title)}」{c.MonitorDevice} {c.Rect}")));
            }

            int index = DisplayRules.PickRestoreCandidate(candidates, saved, ordinals);
            if (index >= 0)
            {
                var match = candidates[index];
                var hwnd = new IntPtr(match.Handle);
                Diagnostics.Info("scene", $"  → 匹配 0x{match.Handle:X}「{Brief(match.Title)}」");
                ApplyPlacement(hwnd, saved);
                used.Add(match.Handle);
                if (adopt == null || adopt(hwnd)) moved++;
                else
                {
                    failed++;
                    Diagnostics.Info("scene", "  → 接入运行时组失败（可能已属于别的场景）");
                }
                continue;
            }

            // 同程序窗口存在，但全被其它场景管理（可能正收在收纳区，所以 IsWindowVisible=false）：
            // 既不能挪，也不能再开一个副本。
            if (excluded != null && sameExe.Any(w => excluded.Contains(w.Handle.ToInt64())))
            {
                busy++;
                Diagnostics.Info("scene", "  → busy：同程序窗口已在其它场景");
                continue;
            }

            Diagnostics.Info("scene", "  → 没有可接管的窗口，准备启动程序");
            pending.Add(saved);
        }

        // 只有真正启动成功的才值得等窗口出现；启动失败的项目等 12 s 也不会出现。
        var launchedPending = pending.Where(Launch).ToList();
        if (launchedPending.Count > 0)
            PositionLater(launchedPending, known, adoptLater, onComplete);
        else
            onComplete?.Invoke();

        Diagnostics.Info("scene",
            $"还原结束 scene={group.Name} 已摆位={moved} 启动={launchedPending.Count} 跳过={busy} 失败={failed}");
        return (moved, launchedPending.Count, busy, failed);
    }

    /// <summary>
    /// 把一个窗口摆回快照记录的位置：自动认回「关掉又重开」的程序时用。
    /// 与批量还原共用同一套摆位 / 夹回工作区逻辑。
    /// </summary>
    public void PlaceWindow(IntPtr hwnd, SavedWindow saved) => ApplyPlacement(hwnd, saved);

    /// <summary>启动后等主窗口出现再摆位：Process.Start 返回时窗口往往还没建好。</summary>
    private static void PositionLater(
        List<SavedWindow> pending, HashSet<long> known, Action<IntPtr> onWindowReady, Action onComplete)
    {
        Task.Run(() =>
        {
            var startedAt = DateTime.UtcNow;
            try
            {
                var remaining = new List<SavedWindow>(pending);
                var deadline = DateTime.UtcNow.AddSeconds(12);
                Diagnostics.Info("scene", $"等待新窗口 pending={remaining.Count}: " +
                    string.Join("; ", remaining.Select(s =>
                        $"{Path.GetFileName(s.ExePath)}「{Brief(s.Title)}」device={DisplayRules.MonitorOf(s) ?? "(未记录)"}")));

                while (remaining.Count > 0 && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(450);
                    // 每个 tick 只枚举一次：早先是每条等待记录各枚举一遍，等待项多时是 N 倍开销。
                    var windows = TopLevelWindows();
                    // 每个 tick 重算一次：新窗口刚出现，位次要按当前这批窗口重新排。
                    var ordinals = WindowIdentity.AssignOrdinals(OrdinalCandidates(windows));
                    foreach (var saved in remaining.ToArray())
                    {
                        string exe = Normalize(saved.ExePath);
                        if (exe == null)
                        {
                            remaining.Remove(saved);
                            continue;
                        }

                        // 刚启动的窗口一律接管：系统通常把它开在主屏，不能按保存的屏幕过滤，
                        // 否则等待项永远匹配不上（摆位后窗口会被移到保存的那块屏）。
                        // 同程序可能同时出现多个窗口，按标题优先匹配，避免按枚举顺序互相换位。
                        var candidates = new List<DisplayRules.RestoreCandidate>();
                        foreach (var window in windows)
                        {
                            if (!window.Visible || window.Title.Length == 0 || window.ToolWindow) continue;
                            if (window.Owned) continue;
                            if (known.Contains(window.Handle.ToInt64())) continue;
                            if (Normalize(window.Path) != exe) continue;

                            candidates.Add(new DisplayRules.RestoreCandidate
                            {
                                Handle = window.Handle.ToInt64(),
                                Title = window.Title,
                                MonitorDevice = NativeMethods.MonitorDeviceOf(window.Handle),
                                Rect = window.Rect,
                                AppId = WindowIdentity.AppId(null, window.Path),
                                ClassName = window.ClassName
                            });
                        }

                        int index = DisplayRules.PickRestoreCandidate(candidates, saved, ordinals);
                        if (index < 0) continue;

                        var match = candidates[index];
                        var hwnd = new IntPtr(match.Handle);
                        Diagnostics.Info("scene",
                            $"  新窗口出现 0x{match.Handle:X}「{Brief(match.Title)}」device={match.MonitorDevice} " +
                            $"← 保存项「{Brief(saved.Title)}」");
                        ApplyPlacement(hwnd, saved);
                        known.Add(match.Handle);
                        onWindowReady?.Invoke(hwnd);
                        remaining.Remove(saved);
                    }
                }

                foreach (var left in remaining)
                {
                    Diagnostics.Info("scene",
                        $"  等待超时 {Math.Round((DateTime.UtcNow - startedAt).TotalSeconds, 1)}s " +
                        $"exe={Path.GetFileName(left.ExePath)}「{Brief(left.Title)}」");
                }
            }
            finally
            {
                onComplete?.Invoke();
            }
        });
    }

    private static bool Launch(SavedWindow saved)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(saved.ExePath) || !File.Exists(saved.ExePath)) return false;
            Process.Start(new ProcessStartInfo
            {
                FileName = saved.ExePath,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Write("saved launch", ex);
            return false;
        }
    }

    private static void ApplyPlacement(IntPtr hwnd, SavedWindow saved)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;

        NativeMethods.GetWindowRect(hwnd, out var before);
        string deviceBefore = NativeMethods.MonitorDeviceOf(hwnd);

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        var target = NativeMethods.RECT.FromLTRB(
            saved.Left, saved.Top, saved.Left + saved.Width, saved.Top + saved.Height);
        if (saved.Width > 0 && saved.Height > 0)
        {
            // 目标屏可能已经拔掉 / 分辨率变了：先夹回最近的工作区，否则窗口会落在屏幕外。
            target = DisplayRules.EnsureVisible(target);
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, target.Left, target.Top, target.Width, target.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
        if (saved.Maximized) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWMAXIMIZED);

        NativeMethods.GetWindowRect(hwnd, out var after);
        Diagnostics.Info("scene",
            $"摆位 hwnd=0x{hwnd.ToInt64():X} 「{Brief(saved.Title)}」 saved=({saved.Left},{saved.Top}," +
            $"{saved.Left + saved.Width},{saved.Top + saved.Height}) target={target} " +
            $"before={before}({deviceBefore ?? "?"}) after={after}({NativeMethods.MonitorDeviceOf(hwnd) ?? "?"}) " +
            $"max={saved.Maximized}");
    }
}
