using System.Windows.Threading;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>
/// 单窗口模式：监听前台变化，把除前台窗口以外的候选窗口系统最小化（保留任务栏按钮与 Alt+Tab）。
///
/// 只处理「合法的前台窗口」事件：桌面 / 任务栏 / 开始菜单 / 本程序成为前台时什么都不做，
/// 否则点一下开始菜单就会把所有窗口最小化。
/// </summary>
public sealed class SingleWindowMode : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _coalesce;
    private readonly NativeMethods.WinEventProc _callback;
    private IntPtr _hook;
    private IntPtr _pending;
    private DateTime _suppressUntil;
    private bool _enabled;
    private bool _applying;
    private bool _disposed;

    /// <summary>
    /// 被本模式最小化的窗口。切回场景内同伴时只恢复这些；
    /// 用户自己点最小化的窗口不在集合里，不会被自动弹回来。
    /// </summary>
    private readonly HashSet<long> _selfMinimized = new();

    public SingleWindowMode(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _callback = OnForegroundEvent;

        // OUTOFCONTEXT 回调在注册线程到达；用 60 ms 合并窗口把连续切换压成一次扫描。
        _coalesce = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _coalesce.Tick += (_, _) =>
        {
            _coalesce.Stop();
            // 定时器路径上的局部失败不该把常驻进程带走：记一条日志，下一次切换前台时再试。
            try { Apply(_pending); }
            catch (Exception ex) { Diagnostics.Write("single-window apply", ex); }
            _pending = IntPtr.Zero;
        };
    }

    public bool Enabled => _enabled;

    /// <summary>
    /// 由组合根注入：返回窗口所属的场景运行组号，0 表示不属于任何场景。
    /// 注入后「同一场景展开的窗口」在单窗口模式下被当成一个整体：切到场景内任一窗口，其余同伴恢复；
    /// 切到场景外时，整个场景收回胶囊（而不是拆成最小化窗口）。
    /// </summary>
    public Func<IntPtr, int> GroupOf { get; set; }

    /// <summary>
    /// 由组合根注入：把某一场景在这块屏幕上整组收回收纳区（隐藏窗口、保留组），返回收起的窗口数。
    /// 必须带显示器设备名：场景里可能混有其它屏幕的窗口（旧快照还原 / 用户拖过去），
    /// 不能因为这块屏幕上的切换让另一块屏幕的窗口跟着消失。
    /// 注入后「切到别的窗口」时，别的场景会整体回到胶囊里，而不是散成一排最小化按钮。
    /// </summary>
    public Func<int, string, int> CollapseGroup { get; set; }

    /// <summary>一次应用里被最小化的窗口数（订阅方可用于状态提示）。</summary>
    public event Action<int> Applied;

    /// <summary>打开开关时这块屏幕上没有可接管的窗口（组合根用来提示，而不是静默无反应）。</summary>
    public event Action NoCandidateOnScreen;

    public void Start()
    {
        if (_disposed || _hook != IntPtr.Zero) return;
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    public void SetEnabled(bool value, bool applyImmediately = true)
    {
        if (_disposed) return;
        _enabled = value;
        if (!value)
        {
            _coalesce.Stop();
            _pending = IntPtr.Zero;
            // 关掉开关后不再替用户恢复窗口；集合清空，避免下次打开时误弹旧窗口。
            _selfMinimized.Clear();
            return;
        }
        // 打开开关的瞬间先执行一次：此刻屏幕上可能已经有很多窗口。
        // 启动时恢复设置传 false——否则登录那一刻会把用户所有窗口清空。
        if (applyImmediately) ApplyForegroundOrCandidate();
    }

    /// <summary>
    /// 打开开关时立即应用一次。入口只有托盘 / 面板，鼠标（面板）在哪块屏，就只处理哪块屏。
    /// 不能优先用前台窗口：点托盘菜单时前台很可能还是另一块屏上刚才用的窗口，
    /// 拿它当目标会把另一块屏清空，看起来像「点一块屏、两块都被清」。
    /// 这块屏没有可接管的窗口就提示，不回退到前台窗口。
    /// </summary>
    private void ApplyForegroundOrCandidate()
    {
        var windows = WindowEnumerator.Enumerate(includeMinimized: false);
        string monitor = NativeMethods.MonitorDeviceOfCursor();

        var target = windows.FirstOrDefault(w =>
            DisplayRules.BelongsTo(monitor, NativeMethods.MonitorDeviceOf(w.Handle)));

        if (target == null)
        {
            Diagnostics.Info("single", $"开关应用：鼠标所在屏 {monitor ?? "(未知)"} 没有可接管窗口");
            NoCandidateOnScreen?.Invoke();
            return;
        }
        Diagnostics.Info("single",
            $"开关应用：鼠标屏={monitor ?? "(未知)"} 目标=0x{target.Handle.ToInt64():X}「{target.Title}」");
        Apply(target.Handle);
    }

    /// <summary>
    /// 临时暂停一小段时间：程序自己批量还原一个场景的窗口时会连续切换前台，
    /// 不该在还原过程中把刚恢复的同伴又最小化掉。
    /// </summary>
    public void SuspendTemporarily(int milliseconds = 800, string reason = null)
    {
        _suppressUntil = DateTime.UtcNow.AddMilliseconds(milliseconds);
        if (!string.IsNullOrEmpty(reason))
            Diagnostics.Info("single", $"暂停 {milliseconds}ms：{reason}");
    }

    /// <summary>
    /// 立刻按某个窗口应用一次，忽略上面那段抑制窗口。
    /// 场景刚从收纳区 / 快照恢复到屏幕后必须补这一次：批量恢复期间的前台事件全被压掉了，
    /// 之后系统不会再有事件，不补的话「其它窗口最小化」永远不会发生
    /// ——单窗口模式看起来就像没生效。
    /// </summary>
    public void ApplyNow(IntPtr anchor)
    {
        if (_disposed || !_enabled || anchor == IntPtr.Zero) return;
        _suppressUntil = default;
        try
        {
            Apply(anchor);
        }
        catch (Exception ex)
        {
            // 这里由入口调用的收尾路径触发，失败只影响本次收尾。
            Diagnostics.Write("single-window apply now", ex);
        }
    }

    private void OnForegroundEvent(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // 这是交给 Win32 的回调：异常一旦逃回原生栈会直接终止进程，所以就地隔离。
        try
        {
            if (!_enabled || _disposed) return;
            if (idObject != 0) return;          // 只认 OBJID_WINDOW，忽略子元素事件
            if (hwnd == IntPtr.Zero) return;
            _pending = hwnd;
            _coalesce.Stop();
            _coalesce.Start();
        }
        catch (Exception ex)
        {
            Diagnostics.Write("foreground hook", ex);
        }
    }

    private void Apply(IntPtr foreground)
    {
        if (!_enabled || _applying || _disposed) return;
        if (DateTime.UtcNow < _suppressUntil) return;
        if (!IsEligibleForeground(foreground)) return;

        int minimized = 0;
        int collapsedCount = 0;
        string monitorDevice = null;
        _applying = true;
        try
        {
            var groupOf = GroupOf;

            // 前台可能是一个弹窗 / 浮层（浏览器扩展弹窗、应用的工具窗、场景成员自己弹的对话框）：
            // 点它的时候系统把「弹窗」设为前台，而弹窗永远不是场景成员，于是下面那段
            // 「别的场景整组收回胶囊」会把弹窗所属的场景整个收起来——用户只是点了插件。
            //
            // 两道兜底，顺序不能反：
            // ① 按窗口簇归组：沿 owner 链走到簇主，簇主在场景里就当作「还是那个场景」；
            // ② 簇主也对不上（Chromium 的气泡由中间一层无 owner 的 border 窗口拥有，
            //    owner 链断在半路），那这次前台变化就不是「切换到另一个窗口」：
            //    按准入规则它连窗口都算不上，直接不处理。
            IntPtr head = NativeMethods.RootOwner(foreground);
            int group = ResolveGroup(foreground, groupOf, _ => head);
            string exclusion = group == 0 ? WindowEnumerator.ExclusionReason(foreground) : null;
            if (ShouldIgnoreForeground(group, exclusion))
            {
                Diagnostics.Info("single",
                    $"忽略前台 0x{foreground.ToInt64():X}（{exclusion}）" +
                    $" class={NativeMethods.ClassName(foreground)}" +
                    $" owner=0x{NativeMethods.Owner(foreground).ToInt64():X}" +
                    $" rootOwner=0x{head.ToInt64():X}");
                return;
            }

            // 已经被用户还原 / 关掉的窗口从「自己最小化」集合里清出去，
            // 免得下次切回同伴时对已不在最小化状态的窗口发还原请求。
            _selfMinimized.RemoveWhere(hwnd =>
                !NativeMethods.IsWindow(new IntPtr(hwnd)) || !NativeMethods.IsIconic(new IntPtr(hwnd)));

            // 多显示器：单窗口模式只管前台所在的那块屏幕——在哪块屏操作，就只影响哪块屏，
            // 另一块屏上的窗口保持原样。
            IntPtr monitor = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);
            monitorDevice = NativeMethods.MonitorDeviceOf(foreground);

            // 前台属于某个组：先把组里「被本模式最小化」的同伴恢复（不抢焦点）。
            // 用户手动最小化的场景窗口跳过：手动最小化是明确意图，不能马上弹回来。
            if (group > 0)
            {
                foreach (var member in WindowEnumerator.Enumerate(includeMinimized: true))
                {
                    if (member.Handle == foreground) continue;
                    if (groupOf(member.Handle) != group) continue;
                    if (NativeMethods.MonitorFromWindow(member.Handle, NativeMethods.MONITOR_DEFAULTTONEAREST) != monitor) continue;
                    if (!NativeMethods.IsIconic(member.Handle)) continue;
                    if (!_selfMinimized.Remove(member.Handle.ToInt64())) continue;
                    NativeMethods.ShowNoActivate(member.Handle);
                }
            }

            var collapsed = new HashSet<int>();
            foreach (var candidate in WindowEnumerator.Enumerate(includeMinimized: false))
            {
                if (candidate.Handle == foreground) continue;
                // 别的屏幕上的窗口不归这一轮管。
                if (NativeMethods.MonitorFromWindow(candidate.Handle, NativeMethods.MONITOR_DEFAULTTONEAREST) != monitor) continue;
                int memberGroup = groupOf != null ? groupOf(candidate.Handle) : 0;

                // 同一场景的同伴不最小化：一个场景就是「一个单窗口」。
                if (group > 0 && memberGroup == group) continue;

                // 别的场景整组收回胶囊（一个场景只收一次）：单窗口模式里场景是一个整体，
                // 应该「收起来」而不是拆成一排最小化按钮留在任务栏。
                if (memberGroup > 0 && CollapseGroup != null && !string.IsNullOrEmpty(monitorDevice))
                {
                    if (collapsed.Add(memberGroup)) collapsedCount += CollapseGroup(memberGroup, monitorDevice);
                    continue;
                }

                // 必须用 SW_SHOWMINNOACTIVE 而不是 SW_MINIMIZE：后者会「激活 Z 序里的下一个顶层窗口」，
                // 最小化一圈下来焦点会被抢走，用户看到的是场景窗口刚置前又被顶下去。
                if (NativeMethods.ShowWindow(candidate.Handle, NativeMethods.SW_SHOWMINNOACTIVE))
                    _selfMinimized.Add(candidate.Handle.ToInt64());
                minimized++;
            }
        }
        finally
        {
            _applying = false;
        }

        if (minimized + collapsedCount > 0)
        {
            uint foregroundPid = 0;
            NativeMethods.GetWindowThreadProcessId(foreground, out foregroundPid);
            Diagnostics.Info("single",
                $"应用 前台=0x{foreground.ToInt64():X}({NativeMethods.ClassName(foreground)} pid={foregroundPid}) " +
                $"owner=0x{NativeMethods.Owner(foreground).ToInt64():X} screen={monitorDevice ?? "(未知)"} " +
                $"最小化={minimized} 收回场景={collapsedCount}");
        }
        if (minimized + collapsedCount > 0) Applied?.Invoke(minimized + collapsedCount);
    }

    /// <summary>
    /// 前台窗口该归到哪个场景组：自己就是成员就用自己，否则看窗口簇的簇主
    /// （沿 owner 链走到第一个无 owner 的顶层窗口 = 用户真正在用的那个窗口）。
    /// 簇是 1.9.0 引入的模型：弹窗随簇主一起隐藏 / 恢复，所以判断「用户切到了哪一组」
    /// 也必须按簇主算，否则点一下弹窗就等于「切换到了场景外」。
    /// </summary>
    public static int ResolveGroup(
        IntPtr foreground, Func<IntPtr, int> groupOf, Func<IntPtr, IntPtr> rootOwner)
    {
        if (groupOf == null) return 0;

        int group = groupOf(foreground);
        if (group != 0) return group;
        if (rootOwner == null) return 0;

        IntPtr head = rootOwner(foreground);
        if (head == IntPtr.Zero || head == foreground) return 0;
        return groupOf(head);
    }

    /// <summary>
    /// 这次前台变化要不要当作「用户切换了窗口」。
    /// 前台既不属于任何场景、又按准入规则连窗口都算不上（工具窗 / 被拥有的弹窗 / 无标题 /
    /// cloaked），那它只是某个应用的临时 UI——不处理，绝不能因此把别的场景收起来。
    /// </summary>
    public static bool ShouldIgnoreForeground(int group, string exclusionReason)
        => group == 0 && exclusionReason != null;

    private static bool IsEligibleForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd)) return false;
        if (WindowEnumerator.IsOwnProcessWindow(hwnd)) return false;
        if (WindowEnumerator.IsShellWindow(hwnd)) return false;
        if (NativeMethods.IsCloaked(hwnd, out _)) return false;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        _selfMinimized.Clear();
        _coalesce.Stop();
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
