namespace TaskbarDrawer.Core;

/// <summary>
/// 关闭场景的实测结果：<see cref="Requested"/> 是真正投出去的 <c>WM_CLOSE</c> 个数，
/// <see cref="Closed"/> 是过了一会儿确认已经消失的个数。
///
/// 两者不等就是「有程序弹了保存框 / 还在慢慢退」。以前提示直接报投递数，
/// 目标程序弹一次「是否保存」就会显示「已关闭 3 个窗口」，而实际上一个都没关。
/// </summary>
public readonly struct CloseReport
{
    public CloseReport(int requested, int closed)
    {
        Requested = requested < 0 ? 0 : requested;
        Closed = closed < 0 ? 0 : Math.Min(closed, Requested);
    }

    /// <summary>发出 WM_CLOSE 的窗口数。</summary>
    public int Requested { get; }

    /// <summary>确认已经关掉的窗口数。</summary>
    public int Closed { get; }

    /// <summary>发了请求但还活着的个数（弹保存框、卡住、或者程序自己不肯退）。</summary>
    public int Waiting => Requested - Closed;

    /// <summary>
    /// 数一遍这批句柄里还有几个活着。<paramref name="isAlive"/> 抽成参数是为了能单测：
    /// 测试进程不去创建真实窗口，用一个假的判活函数就能覆盖三种分支。
    /// </summary>
    public static CloseReport Measure(IReadOnlyList<IntPtr> handles, Func<IntPtr, bool> isAlive)
    {
        if (handles == null) return new CloseReport(0, 0);

        int alive = 0;
        foreach (IntPtr hwnd in handles)
        {
            if (hwnd == IntPtr.Zero) continue;
            if (isAlive(hwnd)) alive++;
        }
        return new CloseReport(handles.Count, handles.Count - alive);
    }

    /// <summary>有关联快照的场景：提示里带场景名与标识。</summary>
    public string DescribeScene(string name, string badge)
    {
        string head = string.IsNullOrEmpty(badge) ? "" : badge;
        if (Requested <= 0) return $"「{name}」没有可关闭的窗口";
        if (Waiting == 0) return $"{head}已关闭「{name}」· {Closed} 个窗口";
        return $"{head}已关闭「{name}」· {Closed} 个窗口，{Waiting} 个仍在等待响应";
    }

    /// <summary>没有快照的旧运行时组：只有数字，没有场景名。</summary>
    public string DescribeGroup()
    {
        if (Requested <= 0) return "没有可关闭的窗口";
        if (Waiting == 0) return $"已关闭 {Closed} 个窗口";
        return $"已关闭 {Closed} 个窗口，{Waiting} 个仍在等待响应";
    }
}
