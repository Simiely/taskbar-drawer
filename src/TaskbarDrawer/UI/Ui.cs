#nullable enable

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TaskbarDrawer.UI;

/// <summary>多个窗口共用的控件工厂：只收行为完全一致的部分，有差异的留在各自窗口里。</summary>
internal static class Ui
{
    /// <summary>统一字体的文本块；wrap 为 true 时自动换行（长说明文案用）。</summary>
    internal static TextBlock Text(string text, double size, FontWeight weight, Brush foreground, bool wrap = false) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI"),
        FontSize = size,
        FontWeight = weight,
        Foreground = foreground,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
    };

    /// <summary>把 Border 变成可点击控件：悬停换底色，左键抬起时回调并吞掉事件。</summary>
    internal static void Clickable(Border border, Action onClick, Brush? hover = null, Brush? normal = null)
    {
        var rest = normal ?? border.Background;
        border.Cursor = Cursors.Hand;
        if (hover != null)
        {
            border.MouseEnter += (_, _) => border.Background = hover;
            border.MouseLeave += (_, _) => border.Background = rest;
        }
        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
    }
}
