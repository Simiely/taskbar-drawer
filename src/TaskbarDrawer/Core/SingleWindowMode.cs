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
    /// 由组合根注入：返回窗口所属的组号，0 表示不属于任何组。
    /// 注入后「一组展开的窗口」在单窗口模式下被当成一个整体：切到组内任一窗口，其余同伴恢复；
    /// 切到组外时，整组收回胶囊（而不是拆成最小化窗口）。
    /// </summary>
    public Func<IntPtr, int> GroupOf { get; set; }

    /// <summary>
    /// 由组合根注入：把某一组整组收回收纳区（隐藏窗口、保留组），返回收起的窗口数。
    /// 注入后「切到别的窗口」时，别的组会整体回到胶囊里，而不是散成一排最小化按钮。
    /// </summary>
    public Func<int, int> CollapseGroup { get; set; }

    /// <summary>一次应用里被最小化的窗口数（订阅方可用于状态提示）。</summary>
    public event Action<int> Applied;

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
            return;
        }
        // 打开开关的瞬间先执行一次：此刻屏幕上可能已经有很多窗口。
        // 启动时恢复设置传 false——否则登录那一刻会把用户所有窗口清空。
        if (applyImmediately) Apply(NativeMethods.GetForegroundWindow());
    }

    /// <summary>
    /// 临时暂停一小段时间：程序自己批量还原一组窗口时会连续切换前台，
    /// 不该在还原过程中把刚恢复的同伴又最小化掉。
    /// </summary>
    public void SuspendTemporarily(int milliseconds = 800)
        => _suppressUntil = DateTime.UtcNow.AddMilliseconds(milliseconds);

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
        _applying = true;
        try
        {
            var groupOf = GroupOf;
            int group = groupOf != null ? groupOf(foreground) : 0;

            // 多显示器：单窗口模式只管前台所在的那块屏幕——在哪块屏操作，就只影响哪块屏，
            // 另一块屏上的窗口保持原样。
            IntPtr monitor = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);

            // 前台属于某个组：先把组里被最小化的同伴恢复（不抢焦点）。
            // 这样「切回组内任意一个窗口」时整组都会回到屏幕上。
            if (group > 0)
            {
                foreach (var member in WindowEnumerator.Enumerate(includeMinimized: true))
                {
                    if (member.Handle == foreground) continue;
                    if (groupOf(member.Handle) != group) continue;
                    if (NativeMethods.MonitorFromWindow(member.Handle, NativeMethods.MONITOR_DEFAULTTONEAREST) != monitor) continue;
                    if (!NativeMethods.IsIconic(member.Handle)) continue;
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

                // 同组的同伴不最小化：整组就是「一个单窗口」。
                if (group > 0 && memberGroup == group) continue;

                // 别的组整组收回胶囊（一组只收一次）：单窗口模式里组是一个整体，
                // 应该「收起来」而不是拆成一排最小化按钮留在任务栏。
                if (memberGroup > 0 && CollapseGroup != null)
                {
                    if (collapsed.Add(memberGroup)) collapsedCount += CollapseGroup(memberGroup);
                    continue;
                }

                NativeMethods.ShowWindow(candidate.Handle, NativeMethods.SW_MINIMIZE);
                minimized++;
            }
        }
        finally
        {
            _applying = false;
        }

        if (minimized + collapsedCount > 0) Applied?.Invoke(minimized + collapsedCount);
    }

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
        _coalesce.Stop();
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
