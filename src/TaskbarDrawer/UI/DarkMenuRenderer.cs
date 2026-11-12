using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace TaskbarDrawer.UI;

/// <summary>
/// 托盘 / 胶囊右键菜单的深色渲染器。
///
/// WinForms 的 <see cref="Forms.ContextMenuStrip"/> 走系统「专业」视觉样式，深色胶囊旁边
/// 弹出一块白菜单很割裂；而它不认 WPF 的主题色，只能自己换渲染器。
/// 这里用一张深色 <see cref="Forms.ProfessionalColorTable"/> + 几个自绘回调，把底色 / 悬停 /
/// 分隔线 / 勾选 / 子菜单箭头 / 边框一次性对齐主面板的语义色（<c>#1C1C1E</c> 底、
/// <c>#FFFFFF</c> 字、<c>#0A84FF</c> 勾选）。
/// </summary>
internal sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
{
    internal static readonly Color Back = Color.FromArgb(0x1C, 0x1C, 0x1E);
    private static readonly Color Border = Color.FromArgb(0x38, 0x38, 0x3A);
    private static readonly Color Hover = Color.FromArgb(0x2C, 0x2C, 0x2E);
    private static readonly Color Pressed = Color.FromArgb(0x3A, 0x3A, 0x3C);
    private static readonly Color Text = Color.FromArgb(0xFF, 0xFF, 0xFF);
    private static readonly Color DisabledText = Color.FromArgb(0x8E, 0x8E, 0x93);
    private static readonly Color Accent = Color.FromArgb(0x0A, 0x84, 0xFF);
    private static readonly Color SeparatorLine = Color.FromArgb(0x38, 0x38, 0x3A);

    public DarkMenuRenderer() : base(new DarkColorTable())
    {
        // 专业渲染器默认给下拉菜单做圆角（靠区域裁剪实现），自绘底色后圆角外那一圈会露旧底色；
        // 直接关掉，整块菜单由我们自己填满，只留 1 px 描边。
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Back);
        e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.ToolStrip.Size));
    }

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is not Forms.ToolStripDropDown) return;
        using var pen = new Pen(Border);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        // 未悬停时什么都不画：底色由 OnRenderToolStripBackground 一次性铺满，
        // 逐项再填一遍会盖掉图像留白（勾选区）的层次。
        var item = e.Item;
        if (!item.Selected || !item.Enabled) return;
        var rect = new Rectangle(2, 1, Math.Max(0, item.Width - 4), Math.Max(0, item.Height - 2));
        using var brush = new SolidBrush(item.Pressed ? Pressed : Hover);
        using var path = Rounded(rect, 5);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        // 菜单项默认拿 SystemColors.ControlText（黑字），深色底上完全不可读，必须逐项改。
        e.TextColor = e.Item.Enabled ? Text : DisabledText;

        // 双高度菜单项（单窗口模式）：原生排版把标题压在上半格，这里把文字区挪到正中。
        if (e.Item is TallMenuItem)
        {
            Rectangle text = e.TextRectangle;
            int height = e.Item.Font.Height;
            e.TextRectangle = new Rectangle(text.X, Math.Max(0, (e.Item.Height - height) / 2),
                text.Width, height);
        }

        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
    {
        // 子菜单箭头同理：默认用系统黑，这里换成主题前景色（禁用项压暗）。
        e.ArrowColor = e.Item.Enabled ? Text : DisabledText;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        if (e.Vertical) return;
        int y = e.Item.Height / 2;
        using var pen = new Pen(SeparatorLine);
        e.Graphics.DrawLine(pen, 8, y, Math.Max(8, e.Item.Width - 8), y);
    }

    protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
    {
        // 勾选标记不用系统那枚近黑的对勾（深色底上几乎看不见），自绘一枚强调色对勾。
        const int size = 16;
        Rectangle slot = e.ImageRectangle;
        int x = slot.Width > 0 ? slot.Left + (slot.Width - size) / 2 : 4;
        int y = slot.Height > 0 ? slot.Top + (slot.Height - size) / 2 : (e.Item.Height - size) / 2;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var plate = new SolidBrush(Color.FromArgb(0x33, Accent)))
        using (var path = Rounded(new Rectangle(x, y, size, size), 4))
        {
            g.FillPath(plate, path);
        }
        using var pen = new Pen(Accent, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, new[]
        {
            new Point(x + 4, y + 8),
            new Point(x + 7, y + 11),
            new Point(x + 12, y + 5)
        });
    }

    private static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        if (rect.Width <= d || rect.Height <= d)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>专业渲染器的取色表：菜单底色 / 悬停 / 边框 / 图像留白全部压成深色。</summary>
    private sealed class DarkColorTable : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Back;
        public override Color ToolStripGradientBegin => Back;
        public override Color ToolStripGradientMiddle => Back;
        public override Color ToolStripGradientEnd => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Border;
        public override Color ToolStripBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Pressed;
        public override Color MenuItemPressedGradientMiddle => Pressed;
        public override Color MenuItemPressedGradientEnd => Pressed;
        public override Color SeparatorDark => SeparatorLine;
        public override Color SeparatorLight => Back;
        public override Color CheckBackground => Back;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Pressed;
    }
}
