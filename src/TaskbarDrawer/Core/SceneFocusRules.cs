namespace TaskbarDrawer.Core;

/// <summary>
/// 场景回到屏幕后「该把哪个窗口放到最前」的纯规则。
///
/// 为什么需要它：<see cref="StashAllService.ExpandGroup"/> 以前只在
/// <see cref="StashGroup.ForegroundBefore"/> 可用时激活窗口。而从快照拉起的场景是由
/// <see cref="StashAllService.Adopt"/> 现场建组的，<c>ForegroundBefore</c> 是 0——
/// 结果就是「打开场景后窗口都在最下面」，再收起再展开也一样。这里补一条兜底：
/// 记录的前台窗口不可用时，退回场景里的第一个可见窗口。
/// </summary>
public static class SceneFocusRules
{
    /// <summary>
    /// 选一个要置前的窗口：优先收纳前的前台窗口（用户离开时看的就是它），
    /// 不可用时退回 <paramref name="restored"/> 里第一个可用的；都没有返回 <see cref="IntPtr.Zero"/>。
    /// </summary>
    /// <param name="isUsable">判定窗口还能不能当激活目标（存在、可见、不是本程序 / shell）。</param>
    public static IntPtr Pick(IntPtr foregroundBefore, IReadOnlyList<IntPtr> restored, Func<IntPtr, bool> isUsable)
    {
        if (isUsable != null)
        {
            if (foregroundBefore != IntPtr.Zero && isUsable(foregroundBefore)) return foregroundBefore;

            if (restored != null)
            {
                foreach (var hwnd in restored)
                {
                    if (hwnd != IntPtr.Zero && isUsable(hwnd)) return hwnd;
                }
            }
        }

        return IntPtr.Zero;
    }
}
