#nullable enable

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TaskbarDrawer.UI;

/// <summary>多个窗口共用的控件工厂：只收行为完全一致的部分，有差异的留在各自窗口里。</summary>
internal static class Ui
{
    /// <summary>统一字体：中文用雅黑，末尾补 Segoe UI Emoji 兜底 🧩 / 💾 图标。</summary>
    internal static readonly FontFamily Font =
        new("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI Emoji, Segoe UI");

    /// <summary>统一字体的文本块；wrap 为 true 时自动换行（长说明文案用）。</summary>
    internal static TextBlock Text(string text, double size, FontWeight weight, Brush foreground, bool wrap = false) => new()
    {
        Text = text,
        FontFamily = Font,
        FontSize = size,
        FontWeight = weight,
        Foreground = foreground,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
    };
}
