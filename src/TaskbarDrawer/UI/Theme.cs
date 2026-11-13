#nullable enable

using System.Windows.Media;

namespace TaskbarDrawer.UI;

/// <summary>深色主题的唯一来源：所有窗口共用同一套画刷，避免各处维护出细微色差。</summary>
internal static class Theme
{
    /// <summary>面板底色，带轻微透明（主面板用）。</summary>
    internal static readonly SolidColorBrush PanelBackground = Frozen(0xF7, 0x1C, 0x1C, 0x1E);

    /// <summary>面板描边色。</summary>
    internal static readonly SolidColorBrush PanelBorder = Frozen(0xFF, 0x38, 0x38, 0x3A);

    /// <summary>按钮 / 输入框的静止底色。</summary>
    internal static readonly SolidColorBrush ControlBackground = Frozen(0xFF, 0x3A, 0x3A, 0x3C);

    /// <summary>按钮悬停底色。</summary>
    internal static readonly SolidColorBrush ControlHover = Frozen(0xFF, 0x48, 0x48, 0x4A);

    /// <summary>正文主色（纯白）。</summary>
    internal static readonly SolidColorBrush TextPrimary = Frozen(0xFF, 0xFF, 0xFF, 0xFF);

    /// <summary>正文次要色（小节标题等）。</summary>
    internal static readonly SolidColorBrush TextSecondary = Frozen(0xFF, 0x98, 0x98, 0x9D);

    /// <summary>说明文字 / 弱化提示色。</summary>
    internal static readonly SolidColorBrush TextMuted = Frozen(0xFF, 0x8E, 0x8E, 0x93);

    /// <summary>强调色（主按钮、选中描边、录制中的标签）。</summary>
    internal static readonly SolidColorBrush AccentBrush = Frozen(0xFF, 0x0A, 0x84, 0xFF);

    /// <summary>成功态（已展开、开关打开、保存成功）。</summary>
    internal static readonly SolidColorBrush SuccessBrush = Frozen(0xFF, 0x34, 0xC7, 0x59);

    /// <summary>错误态（改键失败的提示文字）。</summary>
    internal static readonly SolidColorBrush DangerBrush = Frozen(0xFF, 0xFF, 0x45, 0x3A);

    /// <summary>细分隔线（1 px 横线）。</summary>
    internal static readonly SolidColorBrush Divider = Frozen(0xFF, 0x38, 0x38, 0x3A);

    /// <summary>开关滑轨的关闭态底色。</summary>
    internal static readonly SolidColorBrush SwitchTrackOff = Frozen(0xFF, 0x39, 0x39, 0x3D);

    /// <summary>主面板卡片底色。</summary>
    internal static readonly SolidColorBrush PanelCard = Frozen(0xFF, 0x2C, 0x2C, 0x2E);

    /// <summary>主面板卡片悬停底色。</summary>
    internal static readonly SolidColorBrush PanelCardHover = Frozen(0xFF, 0x3A, 0x3A, 0x3C);

    /// <summary>创建并冻结画刷：冻结后属性不可变，跨线程读取也不再走锁。</summary>
    public static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
