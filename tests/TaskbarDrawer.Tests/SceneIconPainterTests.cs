using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskbarDrawer.Core;
using TaskbarDrawer.Interop;
using TaskbarDrawer.UI;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>
/// 合成图标的像素级检查：标识圆点确实落在程序图标的左上 / 右上角，
/// 位置开关不是只改数据、没改画面。
/// </summary>
public class SceneIconPainterTests
{
    private const int Size = 32;

    [Fact]
    public void 标识默认叠在右上角_程序图标在右下角()
    {
        OnSta(() =>
        {
            IntPtr appIcon = WhiteIcon(Size);
            var painter = new SceneIconPainter(SceneIconMetrics.DefaultAppIconScale, SceneIconMetrics.DefaultBadgeScale);
            try
            {
                var badge = new SceneBadge { Color = SceneBadge.DefaultColor };
                Assert.True(painter.TryCreate(badge, appIcon, appIcon, 96, out IntPtr big, out IntPtr small));
                try
                {
                    byte[] pixels = ReadPixels(big);
                    Assert.True(IsBadgeColor(pixels, 24, 8), "右上角应画出标识圆点");
                    Assert.True(IsWhite(pixels, 8, 8), "左上角应保留程序原图标");
                    Assert.True(IsWhite(pixels, 29, 29), "右下角应保留程序原图标");
                    Assert.True(IsWhite(pixels, 8, 29), "图标左侧其余区域仍是程序图标");
                }
                finally
                {
                    painter.Release(big);
                    painter.Release(small);
                }
            }
            finally
            {
                NativeMethods.DestroyIcon(appIcon);
            }
        });
    }

    [Fact]
    public void 关闭Right后标识挪到左上角()
    {
        OnSta(() =>
        {
            IntPtr appIcon = WhiteIcon(Size);
            var painter = new SceneIconPainter(SceneIconMetrics.DefaultAppIconScale, SceneIconMetrics.DefaultBadgeScale);
            try
            {
                var badge = new SceneBadge { Color = "#0A6CDE", Right = false };
                Assert.True(painter.TryCreate(badge, appIcon, appIcon, 96, out IntPtr big, out IntPtr small));
                try
                {
                    byte[] pixels = ReadPixels(big);
                    Assert.True(IsColor(pixels, 8, 8, 0x0A, 0x6C, 0xDE), "左上角应画出标识圆点");
                    Assert.True(IsWhite(pixels, 24, 8), "右上角不应再画标识");
                }
                finally
                {
                    painter.Release(big);
                    painter.Release(small);
                }
            }
            finally
            {
                NativeMethods.DestroyIcon(appIcon);
            }
        });
    }

    private static IntPtr WhiteIcon(int size)
    {
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0xFF;
            pixels[i + 1] = 0xFF;
            pixels[i + 2] = 0xFF;
            pixels[i + 3] = 0xFF;
        }
        return NativeMethods.CreateIconFromPixels(pixels, size, size);
    }

    private static byte[] ReadPixels(IntPtr icon)
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        var straight = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[Size * Size * 4];
        straight.CopyPixels(pixels, Size * 4, 0);
        return pixels;
    }

    private static bool IsBadgeColor(byte[] pixels, int x, int y)
        => IsColor(pixels, x, y, 0xE5, 0x48, 0x4D);

    private static bool IsWhite(byte[] pixels, int x, int y)
        => IsColor(pixels, x, y, 0xFF, 0xFF, 0xFF);

    /// <summary>BGRA 像素近似比较（允许 2 的量化误差）。</summary>
    private static bool IsColor(byte[] pixels, int x, int y, byte r, byte g, byte b)
    {
        int offset = (y * Size + x) * 4;
        return Math.Abs(pixels[offset] - b) <= 2 &&
               Math.Abs(pixels[offset + 1] - g) <= 2 &&
               Math.Abs(pixels[offset + 2] - r) <= 2 &&
               pixels[offset + 3] > 0xF0;
    }

    /// <summary>WPF 渲染与 HICON 转换都要求 STA。</summary>
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
