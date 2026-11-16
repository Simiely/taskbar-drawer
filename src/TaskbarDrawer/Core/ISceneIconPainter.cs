namespace TaskbarDrawer.Core;

/// <summary>
/// 把场景标识（有色圆点 / 数字）与窗口原图标画成一张新图标，
/// 让任务栏按钮呈现「角标 + 程序图标」，一眼看出窗口属于哪个场景。
/// Core 只认这个抽象，具体绘制留在 UI 层（WPF）。
/// </summary>
public interface ISceneIconPainter
{
    /// <summary>
    /// 生成大 / 小两枚图标句柄（大图标给任务栏与 Alt+Tab，小图标给标题栏）。
    /// 失败返回 false，调用方保持窗口原图标；成功时句柄归实现所有，用完必须 <see cref="Release"/>。
    /// </summary>
    bool TryCreate(SceneBadge badge, IntPtr originalBig, IntPtr originalSmall, uint dpi,
        out IntPtr big, out IntPtr small);

    /// <summary>销毁 <see cref="TryCreate"/> 产出的图标句柄。</summary>
    void Release(IntPtr icon);
}
