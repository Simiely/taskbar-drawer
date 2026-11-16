using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskbarDrawer.Core;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.UI;

/// <summary>
/// 把场景标识（有色圆点 / 数字）画在窗口原图标的角上，产出 HICON 交给 WM_SETICON，
/// 任务栏按钮就变成「角标 + 程序图标」。
///
/// 标识是纯几何形状：圆底 + 白色数字（不选数字就是纯色圆点），缩到 16–24 px 时
/// 比 emoji 字形清楚，也不依赖系统字体或图片资源。
/// </summary>
public sealed class SceneIconPainter : ISceneIconPainter
{
    /// <summary>默认程序图标大小：占画布 90%，右下对齐，把左上角让给场景标识。</summary>
    public const double DefaultAppIconScale = 0.90;

    /// <summary>默认场景标识大小：占画布 55%，叠在原图标角上。</summary>
    public const double DefaultBadgeScale = 0.55;

    public const double MinAppIconScale = 0.55;
    public const double MaxAppIconScale = 1.0;
    public const double MinBadgeScale = 0.25;
    public const double MaxBadgeScale = 0.65;

    private static readonly Typeface DigitTypeface = new(
        new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public SceneIconPainter(double appIconScale = DefaultAppIconScale, double badgeScale = DefaultBadgeScale)
    {
        AppIconScale = appIconScale;
        BadgeScale = badgeScale;
    }

    /// <summary>程序原图标占画布边长的比例（0.55–1.0）。</summary>
    public double AppIconScale
    {
        get => _appIconScale;
        set => _appIconScale = double.IsNaN(value)
            ? DefaultAppIconScale
            : Math.Clamp(value, MinAppIconScale, MaxAppIconScale);
    }

    /// <summary>场景标识占画布边长的比例（0.25–0.65）。</summary>
    public double BadgeScale
    {
        get => _badgeScale;
        set => _badgeScale = double.IsNaN(value)
            ? DefaultBadgeScale
            : Math.Clamp(value, MinBadgeScale, MaxBadgeScale);
    }

    private double _appIconScale = DefaultAppIconScale;
    private double _badgeScale = DefaultBadgeScale;

    public bool TryCreate(SceneBadge badge, IntPtr originalBig, IntPtr originalSmall, uint dpi,
        out IntPtr big, out IntPtr small)
    {
        big = IntPtr.Zero;
        small = IntPtr.Zero;
        try
        {
            SceneBadge normalized = SceneBadge.Coerce(badge);
            BitmapSource appIcon = IconSource(originalBig) ?? IconSource(originalSmall);

            double scale = Math.Max(1.0, dpi / 96.0);
            big = Build(normalized, appIcon, (int)Math.Round(32 * scale));
            small = Build(normalized, appIcon, (int)Math.Round(16 * scale));

            if (big != IntPtr.Zero && small != IntPtr.Zero) return true;
            Release(big);
            Release(small);
            big = small = IntPtr.Zero;
            return false;
        }
        catch (Exception ex)
        {
            Diagnostics.Write("scene icon", ex);
            Release(big);
            Release(small);
            big = small = IntPtr.Zero;
            return false;
        }
    }

    public void Release(IntPtr icon)
    {
        if (icon != IntPtr.Zero) NativeMethods.DestroyIcon(icon);
    }

    private IntPtr Build(SceneBadge badge, BitmapSource appIcon, int size)
    {
        size = Math.Clamp(size, 8, 96);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, size, size));
            if (appIcon != null)
            {
                // 原图标贴右下角，角上的位置留给场景标识。
                double iconSide = size * _appIconScale;
                dc.DrawImage(appIcon, new Rect(size - iconSide, size - iconSide, iconSide, iconSide));

                double diameter = size * _badgeScale;
                double x = badge.Right ? size - diameter / 2 : diameter / 2;
                DrawBadge(dc, badge, new Point(x, diameter / 2), diameter);
            }
            else
            {
                // 读不到窗口原图标（少数无图标程序）：标识放大居中，仍能区分场景。
                double diameter = size * 0.86;
                DrawBadge(dc, badge, new Point(size / 2.0, size / 2.0), diameter);
            }
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        // 预乘 alpha → 直通 alpha：CreateIconIndirect 需要非预乘的 BGRA。
        var straight = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[size * size * 4];
        straight.CopyPixels(pixels, size * 4, 0);
        return NativeMethods.CreateIconFromPixels(pixels, size, size);
    }

    /// <summary>圆形场景标识：圆底 + 可选白色数字。</summary>
    private static void DrawBadge(DrawingContext dc, SceneBadge badge, Point center, double diameter)
    {
        dc.DrawEllipse(Ui.BadgeBrush(badge), null, center, diameter / 2, diameter / 2);
        if (badge.Number <= 0) return;

        var text = new FormattedText(
            badge.Number.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            DigitTypeface, diameter * 0.62, Brushes.White, 1.0);

        // 极窄字号下数字可能比圆还宽：等比缩到圆内。
        double limit = diameter * 0.72;
        if (text.Width > limit) text.SetFontSize(diameter * 0.62 * (limit / text.Width));
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }

    private static BitmapSource IconSource(IntPtr icon)
    {
        if (icon == IntPtr.Zero) return null;
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }
}
