namespace TaskbarDrawer.Core;

/// <summary>
/// 场景合成图标的尺寸约束：默认值与可调范围只保留这一份。
/// 设置读写（<see cref="AppSettings"/>）、图标绘制（SceneIconPainter）、
/// 面板滑块与运行时夹取（AppShell）都从这里取，避免四处各写一套数字。
/// </summary>
public static class SceneIconMetrics
{
    /// <summary>程序原图标占画布边长的比例。</summary>
    public const double DefaultAppIconScale = 0.90;
    public const double MinAppIconScale = 0.55;
    public const double MaxAppIconScale = 1.0;

    /// <summary>场景标识占画布边长的比例（叠在原图标角上）。</summary>
    public const double DefaultBadgeScale = 0.55;
    public const double MinBadgeScale = 0.25;
    public const double MaxBadgeScale = 0.65;

    /// <summary>把程序图标比例夹进合法区间；NaN 退回默认值。</summary>
    public static double ClampAppIconScale(double value)
        => double.IsNaN(value)
            ? DefaultAppIconScale
            : Math.Clamp(value, MinAppIconScale, MaxAppIconScale);

    /// <summary>把标识比例夹进合法区间；NaN 退回默认值。</summary>
    public static double ClampBadgeScale(double value)
        => double.IsNaN(value)
            ? DefaultBadgeScale
            : Math.Clamp(value, MinBadgeScale, MaxBadgeScale);
}
