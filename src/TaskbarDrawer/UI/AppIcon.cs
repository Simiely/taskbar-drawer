using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace TaskbarDrawer.UI;

/// <summary>
/// 应用图标：蓝色圆角方块 + 三张叠放卡片，代表「被收起来的一组窗口」。
/// 图标真身是 Assets/AppIcon.ico（由 tools/make_icon.py 生成），既作为 exe 图标，
/// 也以嵌入资源方式供托盘按 16 / 20 / 24 取清晰帧；单窗口模式再叠一枚「1」角标。
/// </summary>
internal static class AppIcon
{
    private const string ResourceName = "TaskbarDrawer.Assets.AppIcon.ico";

    /// <summary>生成指定像素的图标位图；单窗口模式会加「1」角标。</summary>
    public static Bitmap CreateBitmap(int size, bool singleWindowMode)
    {
        Bitmap bitmap = LoadFrame(size) ?? DrawFallback(size);
        if (!singleWindowMode) return bitmap;

        Bitmap badged = AddModeBadge(bitmap);
        bitmap.Dispose();
        return badged;
    }

    private static Bitmap LoadFrame(int size)
    {
        try
        {
            using Stream stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null) return null;

            using var icon = new Icon(stream, new Size(size, size));
            using Bitmap frame = icon.ToBitmap();
            return Resize(frame, size);
        }
        catch
        {
            // 资源缺失或 ico 损坏时退回内联画法，托盘不能没有图标。
            return null;
        }
    }

    /// <summary>兜底画法：同一套视觉（蓝底 + 三张白卡片），不依赖资源文件。</summary>
    private static Bitmap DrawFallback(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float s = size / 256f;
        using (GraphicsPath shape = RoundedTile(new RectangleF(0, 0, size, size), 56 * s))
        using (var fill = new LinearGradientBrush(
                   new RectangleF(0, 0, size, size),
                   Color.FromArgb(255, 18, 140, 255),
                   Color.FromArgb(255, 0, 86, 214),
                   LinearGradientMode.Vertical))
        {
            g.FillPath(fill, shape);
        }

        (double X0, double Y0, double X1, double Y1, double R, int Alpha)[] cards =
        {
            (0.295, 0.240, 0.705, 0.405, 0.050, 100),
            (0.255, 0.375, 0.745, 0.560, 0.055, 170),
            (0.215, 0.510, 0.785, 0.720, 0.060, 255)
        };
        foreach (var card in cards)
        {
            var rect = new RectangleF(
                (float)(card.X0 * size), (float)(card.Y0 * size),
                (float)((card.X1 - card.X0) * size), (float)((card.Y1 - card.Y0) * size));
            using GraphicsPath path = RoundedTile(rect, (float)(card.R * size));
            using var brush = new SolidBrush(Color.FromArgb(card.Alpha, 255, 255, 255));
            g.FillPath(brush, path);
        }

        return bitmap;
    }

    /// <summary>正中间叠一枚黑色圆底、白色「1」的角标，表示当前是单窗口模式。</summary>
    private static Bitmap AddModeBadge(Bitmap source)
    {
        var bitmap = new Bitmap(source.Width, source.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawImage(source, 0, 0, source.Width, source.Height);

            int diameter = Math.Max(9, (int)Math.Round(source.Width * 0.66));
            var rect = new Rectangle(
                (source.Width - diameter) / 2,
                (source.Height - diameter) / 2,
                diameter, diameter);

            using var fill = new SolidBrush(Color.FromArgb(255, 12, 12, 14));
            g.FillEllipse(fill, rect);

            // 16 px 的托盘图标放不下字体渲染的「1」（会糊成一团），直接按几何画。
            using var glyph = new SolidBrush(Color.White);
            float cx = rect.X + rect.Width / 2f;
            float cy = rect.Y + rect.Height / 2f;
            float d = rect.Width;
            float stemHalf = d * 0.085f;
            float top = cy - d * 0.30f;
            float bottom = cy + d * 0.28f;

            g.FillRectangle(glyph, cx - stemHalf, top, stemHalf * 2, bottom - top);
            g.FillPolygon(glyph, new[]
            {
                new PointF(cx - d * 0.27f, cy - d * 0.05f),
                new PointF(cx - stemHalf, top),
                new PointF(cx + stemHalf, top),
                new PointF(cx - d * 0.02f, cy - d * 0.12f)
            });
            g.FillRectangle(glyph,
                cx - d * 0.26f, cy + d * 0.24f, d * 0.52f, Math.Max(1.2f, d * 0.11f));
        }
        return bitmap;
    }

    private static Bitmap Resize(Bitmap source, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, 0, 0, size, size);
        return bitmap;
    }

    private static GraphicsPath RoundedTile(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
