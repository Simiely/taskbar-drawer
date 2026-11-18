using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>窗口的三种「显示」语义。别用一个 bool 表达——SW_HIDE / SW_SHOWNA / SW_RESTORE / SW_SHOWMINNOACTIVE 行为不同。</summary>
public enum WindowVisibility
{
    /// <summary>SW_HIDE：藏起来，任务栏按钮也消失。</summary>
    Hidden,

    /// <summary>SW_SHOWNA：显示但不激活（放回同簇弹窗用）。</summary>
    Shown,

    /// <summary>SW_RESTORE：从最小化 / 隐藏状态还原（展开场景用）。</summary>
    Restored,

    /// <summary>SW_SHOWMINNOACTIVE：最小化但不抢焦点（还原失败时交回任务栏用）。</summary>
    Minimized
}

/// <summary>一个窗口的只读事实：标题、类名、进程元数据、样式位、矩形。收纳前的准入判断全靠它。</summary>
public sealed class WindowFacts
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = "";
    public string ClassName { get; init; } = "";
    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = "";

    /// <summary>exe 全路径；受保护进程读不到时为空（这类窗口进不了场景）。</summary>
    public string ProcessPath { get; init; } = "";

    /// <summary>进程启动时间：用来把「同名但重启过」的窗口和旧记录区分开。</summary>
    public DateTime? ProcessStartTime { get; init; }

    public bool Visible { get; init; }

    /// <summary>WS_EX_TOOLWINDOW：工具窗，不进 Alt+Tab。</summary>
    public bool ToolWindow { get; init; }

    /// <summary>被 DWM cloak（其它虚拟桌面 / 后台 UWP）。</summary>
    public bool Cloaked { get; init; }

    /// <summary>进程已提权：普通权限实例动不了它的窗口。</summary>
    public bool Elevated { get; init; }

    /// <summary>窗口矩形（屏幕坐标）。</summary>
    public NativeMethods.RECT Rect { get; init; }
}

/// <summary>
/// 还原位置：<see cref="Value"/>.<c>rcNormalPosition</c> 约定为<b>屏幕坐标</b>
/// （由网关负责和工作区坐标互换），与 <see cref="StashedWindow.Placement"/> 的约定一致。
/// </summary>
public readonly struct WindowPlacement
{
    public NativeMethods.WINDOWPLACEMENT Value { get; init; }

    public bool Maximized { get; init; }

    public string MonitorDevice { get; init; }
}

/// <summary>
/// 状态机依赖的最小窗口操作端口。
///
/// 为什么要它：<c>StashAllService</c> 的收起 / 展开 / 接入 / 摘除是纯状态迁移，
/// 但每一步都直接调 Win32，单元测试没法跑（测试进程不创建真实窗口）。
/// 有了这个端口，测试用假实现在内存里跑完整迁移矩阵，Win32 细节留给
/// <see cref="Win32WindowGateway"/>，由实机夹具验收。
///
/// 只收「状态机真的会调」的动作，不要给 <c>NativeMethods</c> 每个函数都建接口——
/// 那会得到一个 30 多个方法的巨型接口，测试里全是没人读的桩。
/// 读操作折进 <see cref="Describe"/>（一次调用给全准入判断需要的字段），
/// 四种显示语义合成 <see cref="SetVisibility"/>。
/// </summary>
public interface IWindowGateway
{
    bool IsWindow(IntPtr hwnd);

    bool IsVisible(IntPtr hwnd);

    /// <summary>窗口是否挂起（不响应消息）：跨进程读写图标前要先问它，否则会被卡死的程序拖住。</summary>
    bool IsHung(IntPtr hwnd);

    /// <summary>当前前台窗口；没有时返回 0。</summary>
    IntPtr Foreground();

    /// <summary>窗口所在显示器设备名（如 <c>\\.\DISPLAY1</c>）。</summary>
    string MonitorOf(IntPtr hwnd);

    /// <summary>可被本程序接管的顶层窗口（已按可见 / 有标题 / 非工具窗 / 非 shell / 非弹窗过滤）。</summary>
    IReadOnlyList<WindowCandidate> Enumerate(bool includeMinimized);

    /// <summary>读一个窗口的只读事实；句柄失效时返回 null。</summary>
    WindowFacts Describe(IntPtr hwnd);

    /// <summary>记下还原所需的位置（隐藏前调用）。<c>rcNormalPosition</c> 转成屏幕坐标。</summary>
    WindowPlacement CapturePlacement(IntPtr hwnd);

    /// <summary>
    /// 按记录还原：还原显示状态 → 写回位置（屏幕坐标转回工作区坐标）→ 必要时再校正一次矩形。
    /// 调用方负责先把矩形夹回可见工作区（那是纯规则，在 <see cref="DisplayRules"/>）。
    /// </summary>
    void ApplyPlacement(IntPtr hwnd, WindowPlacement placement);

    void SetVisibility(IntPtr hwnd, WindowVisibility visibility);

    /// <summary>同簇的可见弹窗（沿 GetLastActivePopup 链，最多 4 层）。</summary>
    IReadOnlyList<IntPtr> OwnedPopups(IntPtr hwnd);

    /// <summary>把被一起藏起来的弹窗放回来（不激活）。</summary>
    void ShowOwnedPopup(IntPtr hwnd);

    /// <summary>把窗口带到前台（展开场景后置前用）。</summary>
    void Activate(IntPtr hwnd);
}
