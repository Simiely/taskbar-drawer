using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>
/// 多显示器判定规则（纯函数，不碰窗口句柄）。
/// 抽出来的目的：按屏隔离是「不知道就什么都不做」，这条语义必须在所有入口一致，
/// 而且能脱离 Win32 直接测——显示器相关的 bug 在本机单屏环境里没法实机复现。
/// </summary>
public static class DisplayRules
{
    /// <summary>目标矩形与工作区的交集低于这个比例，就认为窗口「看不见」。</summary>
    public const double MinVisibleRatio = 0.3;

    /// <summary>
    /// 这个窗口是否属于目标屏幕。目标屏未知（null / 空）时<b>一律不归属</b>：
    /// 「不知道在哪块屏」必须 fail-closed，绝不能扩大成「所有屏」。
    /// </summary>
    public static bool BelongsTo(string targetDevice, string windowDevice)
        => !string.IsNullOrEmpty(targetDevice) &&
           string.Equals(targetDevice, windowDevice, StringComparison.Ordinal);

    /// <summary>
    /// 把矩形夹回工作区：尺寸保留但不超过工作区，左上角贴进工作区。
    /// 目标屏已拔掉时 `work` 会是最近的现存工作区，窗口因此不会落在屏幕外。
    /// </summary>
    public static NativeMethods.RECT ClampToWorkArea(NativeMethods.RECT rect, NativeMethods.RECT work)
    {
        if (rect.IsEmpty || work.IsEmpty) return rect;

        int width = Math.Min(rect.Width, work.Width);
        int height = Math.Min(rect.Height, work.Height);
        int left = Math.Clamp(rect.Left, work.Left, Math.Max(work.Left, work.Right - width));
        int top = Math.Clamp(rect.Top, work.Top, Math.Max(work.Top, work.Bottom - height));
        return NativeMethods.RECT.FromLTRB(left, top, left + width, top + height);
    }

    /// <summary>矩形与工作区的交集面积占矩形面积的比例（0–1）。</summary>
    public static double VisibleRatio(NativeMethods.RECT rect, NativeMethods.RECT work)
    {
        if (rect.IsEmpty || work.IsEmpty) return 0;

        int left = Math.Max(rect.Left, work.Left);
        int top = Math.Max(rect.Top, work.Top);
        int right = Math.Min(rect.Right, work.Right);
        int bottom = Math.Min(rect.Bottom, work.Bottom);
        if (right <= left || bottom <= top) return 0;

        double intersection = (double)(right - left) * (bottom - top);
        return intersection / ((double)rect.Width * rect.Height);
    }

    /// <summary>
    /// 还原 / 摆位前的兜底：露得太少就夹回工作区。
    /// 「目标屏被拔掉」和「换分辨率后窗口跑到桌面外」都走这里。
    /// </summary>
    public static NativeMethods.RECT EnsureVisible(NativeMethods.RECT rect)
    {
        if (rect.IsEmpty) return rect;
        if (!NativeMethods.TryGetMonitorOfRect(rect, out var monitor)) return rect;
        if (VisibleRatio(rect, monitor.rcWork) >= MinVisibleRatio) return rect;
        return ClampToWorkArea(rect, monitor.rcWork);
    }

    /// <summary>
    /// 「展开最近」的轮转：在按组号倒序排好的候选里，取第一个比 fromId 更旧的；
    /// 都比它新（或 fromId 无效）时回到最新一组。
    /// </summary>
    public static int NextRotation(IReadOnlyList<int> descendingIds, int fromId)
    {
        if (descendingIds == null || descendingIds.Count == 0) return 0;
        foreach (int id in descendingIds)
        {
            if (id < fromId) return id;
        }
        return descendingIds[0];
    }

    /// <summary>快照窗口属于哪块屏：优先用保存时的设备名，旧快照退回「保存矩形最近的显示器」。</summary>
    public static string MonitorOf(SavedWindow window)
    {
        if (window == null) return null;
        if (!string.IsNullOrEmpty(window.MonitorDevice)) return window.MonitorDevice;
        if (window.Width <= 0 || window.Height <= 0) return null;
        return NativeMethods.MonitorDeviceOfRect(NativeMethods.RECT.FromLTRB(
            window.Left, window.Top, window.Left + window.Width, window.Top + window.Height));
    }

    /// <summary>一组快照窗口所属的屏幕；没有可用信息时返回 null（旧数据）。</summary>
    public static string MonitorOf(IEnumerable<SavedWindow> windows)
    {
        if (windows == null) return null;
        foreach (var window in windows)
        {
            string device = MonitorOf(window);
            if (device != null) return device;
        }
        return null;
    }
}
