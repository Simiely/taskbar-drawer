using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>被跟踪窗口上发生的事：销毁 / 显示 / 隐藏。</summary>
public enum WindowEventKind
{
    Destroy,
    Show,
    Hide
}

/// <summary>
/// 订阅「自己管的那些进程」的窗口销毁 / 显示 / 隐藏事件，用来替代定时轮询。
///
/// 为什么要按进程装钩子，而不是装一个全局钩子再在回调里筛：
/// <c>SetWinEventHook</c> 的 <c>idProcess</c> 是<b>注册期</c>过滤，装成全局钩子等于把系统里
/// 所有菜单、提示框、输入法窗口的 SHOW / HIDE 都收进回调，每条还得查一次 PID 才能丢掉。
/// 管几个进程就装几个钩子，回调量接近零；进程退出时钩子跟着消失，不用额外清理。
///
/// 安装线程必须有消息循环（<c>WINEVENT_OUTOFCONTEXT</c> 把事件投递到安装线程的消息队列），
/// 所以只能在 UI 线程上建。
/// </summary>
public sealed class WindowEventWatcher : IDisposable
{
    private readonly Dictionary<uint, IntPtr> _hooks = new();

    /// <summary>
    /// 回调委托必须存字段：只传给 SetWinEventHook 不存引用的话，GC 一收，
    /// 系统回调就落到已回收的存根上，进程直接崩。
    /// </summary>
    private readonly NativeMethods.WinEventProc _proc;

    private bool _disposed;

    public WindowEventWatcher()
    {
        _proc = OnWinEvent;
    }

    /// <summary>某个被跟踪窗口发生了状态变化。在安装线程（UI 线程）上同步回调。</summary>
    public event Action<IntPtr, WindowEventKind> Changed;

    /// <summary>当前挂着的钩子数（= 被跟踪的进程数）。</summary>
    public int HookCount => _hooks.Count;

    /// <summary>
    /// 把订阅集合换成这批进程：新增的装钩子，消失的卸钩子。
    /// 幂等——集合没变就什么都不做，调用方可以每次刷新界面时无脑同步一次。
    /// </summary>
    public void Sync(IEnumerable<uint> processIds)
    {
        if (_disposed) return;

        var wanted = new HashSet<uint>();
        if (processIds != null)
        {
            foreach (uint pid in processIds)
            {
                if (pid != 0) wanted.Add(pid);
            }
        }

        foreach (uint pid in _hooks.Keys.ToArray())
        {
            if (wanted.Contains(pid)) continue;
            NativeMethods.UnhookWinEvent(_hooks[pid]);
            _hooks.Remove(pid);
        }

        foreach (uint pid in wanted)
        {
            if (_hooks.ContainsKey(pid)) continue;
            IntPtr hook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_OBJECT_DESTROY, NativeMethods.EVENT_OBJECT_HIDE,
                IntPtr.Zero, _proc, pid, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
            if (hook == IntPtr.Zero)
            {
                // 装不上不影响功能：调用方还留着低频定时器兜底，只是反应慢一点。
                Diagnostics.Info("watch", $"装窗口事件钩子失败 pid={pid}，退回定时器");
                continue;
            }
            _hooks[pid] = hook;
        }
    }

    /// <summary>卸掉全部钩子（保留对象，之后还能再 Sync）。</summary>
    public void Release()
    {
        foreach (IntPtr hook in _hooks.Values) NativeMethods.UnhookWinEvent(hook);
        _hooks.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }

    private void OnWinEvent(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed) return;
        if (!Classify(eventType, hwnd, idObject, idChild, out WindowEventKind kind)) return;

        try
        {
            Changed?.Invoke(hwnd, kind);
        }
        catch (Exception ex)
        {
            // 回调里抛异常会穿过原生栈，必须就地兜住。
            Diagnostics.Write("watch", ex);
        }
    }

    /// <summary>
    /// 只放行「窗口对象本身」的事件：菜单、提示框、光标、滚动条都会发 SHOW / HIDE，
    /// 它们的 idObject 不是 OBJID_WINDOW（或带 idChild），混进来只会白白触发一轮对账。
    /// 抽成静态纯规则是为了能单测——钩子本身在测试进程里装不了。
    /// </summary>
    public static bool Classify(
        uint eventType, IntPtr hwnd, int idObject, int idChild, out WindowEventKind kind)
    {
        kind = default;
        if (hwnd == IntPtr.Zero) return false;
        if (idObject != NativeMethods.OBJID_WINDOW || idChild != 0) return false;

        switch (eventType)
        {
            case NativeMethods.EVENT_OBJECT_DESTROY:
                kind = WindowEventKind.Destroy;
                return true;
            case NativeMethods.EVENT_OBJECT_SHOW:
                kind = WindowEventKind.Show;
                return true;
            case NativeMethods.EVENT_OBJECT_HIDE:
                kind = WindowEventKind.Hide;
                return true;
            default:
                return false;
        }
    }
}
