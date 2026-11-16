using System.Runtime.InteropServices;

namespace TaskbarDrawer.Interop;

/// <summary>
/// Win32 互操作层分片：窗口图标（读原图标 / 换合成图标 / 用像素生成 HICON）。
///
/// 任务栏按钮的布局是「程序图标 + 标题」，没有地方能单独插一个场景标识；
/// 唯一可行的办法是把标识和程序图标画成一张图标，再用 WM_SETICON 换上去。
/// 图标句柄是系统级 USER 对象，跨进程传给别的窗口同样有效（AutoHotkey 的 WinSet Icon 也这么做）。
/// </summary>
public static partial class NativeMethods
{
    private const uint WM_GETICON = 0x007F;
    private const uint WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int ICON_SMALL2 = 2;
    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeoutValue(
        IntPtr h, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtrW(IntPtr h, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    private static extern uint ExtractIconExW(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte[] bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    /// <summary>
    /// 读窗口图标：先问窗口自己（WM_GETICON），再退回窗口类的图标。
    /// 返回的是目标进程的 HICON；图标句柄系统级共享，可以直接拿来做像素转换。
    /// </summary>
    public static IntPtr WindowIcon(IntPtr hwnd, bool big)
    {
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        IntPtr icon = IconMessage(hwnd, WM_GETICON, (IntPtr)(big ? ICON_BIG : ICON_SMALL2));
        if (icon == IntPtr.Zero && !big)
            icon = IconMessage(hwnd, WM_GETICON, (IntPtr)ICON_SMALL);
        if (icon == IntPtr.Zero)
            icon = GetClassLongPtrW(hwnd, big ? GCLP_HICON : GCLP_HICONSM);
        return icon;
    }

    /// <summary>
    /// 句柄是否还是一张有效图标。上一进程被强杀时，窗口会残留一张已销毁的 HICON；
    /// WM_GETICON 仍会返回这个非零值，直接拿去画只会得到空白。
    /// </summary>
    public static bool IsIconValid(IntPtr icon)
    {
        if (icon == IntPtr.Zero) return false;
        if (!GetIconInfo(icon, out ICONINFO info)) return false;
        // GetIconInfo 会把两张位图交给我们，必须自己释放。
        if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
        return true;
    }

    /// <summary>读窗口类自己的图标（窗口没有单独设过图标时用它）。</summary>
    public static IntPtr ClassIcon(IntPtr hwnd, bool big)
        => hwnd == IntPtr.Zero ? IntPtr.Zero : GetClassLongPtrW(hwnd, big ? GCLP_HICON : GCLP_HICONSM);

    /// <summary>
    /// 按进程 exe 抽一张程序图标（资源管理器这类窗口的图标由 shell 单独设置，
    /// 清掉之后类图标是空的，只能退回 exe 自带的那张）。图标归调用方，用完记得 DestroyIcon。
    /// </summary>
    public static IntPtr IconFromExecutable(string path, bool big)
    {
        if (string.IsNullOrWhiteSpace(path)) return IntPtr.Zero;
        try
        {
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            uint count = ExtractIconExW(path, 0, large, small, 1);
            if (count == 0 || count == uint.MaxValue) return IntPtr.Zero;

            // 只要一张，另一张立刻销毁。
            if (big)
            {
                if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
                return large[0];
            }
            if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
            return small[0];
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// 给窗口换图标（可以跨进程）。icon 传 0 表示清掉窗口自己的图标、退回窗口类默认图标。
    /// 必须用 SendMessageTimeout：目标线程挂起时 SetIcon 不会把调用方卡死。
    /// </summary>
    public static bool SetWindowIcon(IntPtr hwnd, bool big, IntPtr icon)
    {
        if (hwnd == IntPtr.Zero) return false;
        IntPtr ok = SendMessageTimeoutValue(
            hwnd, WM_SETICON, (IntPtr)(big ? ICON_BIG : ICON_SMALL), icon,
            SMTO_ABORTIFHUNG, 200, out _);
        return ok != IntPtr.Zero;
    }

    private static IntPtr IconMessage(IntPtr hwnd, uint msg, IntPtr wParam)
    {
        IntPtr ok = SendMessageTimeoutValue(
            hwnd, msg, wParam, IntPtr.Zero, SMTO_ABORTIFHUNG, 120, out IntPtr result);
        return ok == IntPtr.Zero ? IntPtr.Zero : result;
    }

    /// <summary>
    /// 用 BGRA 像素（自上而下、非预乘 alpha）生成 32 位 HICON；失败返回 0。
    /// 图标由调用方持有，不再使用时记得 DestroyIcon。
    /// </summary>
    public static IntPtr CreateIconFromPixels(byte[] bgra, int width, int height)
    {
        if (bgra == null || width <= 0 || height <= 0) return IntPtr.Zero;
        if (bgra.Length < width * height * 4) return IntPtr.Zero;

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                // 正数 = 自下而上：CreateDIBSection 按行翻转写入，CreateIconIndirect 读到的方向才正。
                biHeight = height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            }
        };

        IntPtr color = IntPtr.Zero;
        IntPtr mask = IntPtr.Zero;
        try
        {
            color = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (color == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;

            // 逐行倒着拷：DIB 原点在左下，屏幕像素原点在左上。
            int stride = width * 4;
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(bgra, (height - 1 - y) * stride, bits + y * stride, stride);
            }

            // 32 位图标的透明由 alpha 决定；掩码给全 0（全部不透明）即可。
            mask = CreateBitmap(width, height, 1, 1, new byte[((width + 31) / 32) * 4 * height]);
            if (mask == IntPtr.Zero) return IntPtr.Zero;

            var info = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            if (mask != IntPtr.Zero) DeleteObject(mask);
            if (color != IntPtr.Zero) DeleteObject(color);
        }
    }
}
