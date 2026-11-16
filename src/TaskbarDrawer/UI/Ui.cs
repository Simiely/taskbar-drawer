#nullable enable

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TaskbarDrawer.Core;

namespace TaskbarDrawer.UI;

/// <summary>多个窗口共用的控件工厂：只收行为完全一致的部分，有差异的留在各自窗口里。</summary>
internal static class Ui
{
    /// <summary>统一字体：中文用雅黑，西文用 Segoe UI。</summary>
    internal static readonly FontFamily Font =
        new("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI");

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

    /// <summary>场景标识的圆形外观：有数字时白字居中，没有数字就是纯色圆点。</summary>
    internal static Border Badge(SceneBadge badge, double size)
    {
        var normalized = SceneBadge.Coerce(badge);
        FrameworkElement? face = null;
        if (normalized.Number > 0)
        {
            face = new TextBlock
            {
                Text = normalized.Number.ToString(CultureInfo.InvariantCulture),
                FontFamily = Font,
                FontSize = size * 0.62,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = BadgeBrush(normalized),
            Child = face,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static readonly Dictionary<string, SolidColorBrush> BadgeBrushes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>标识颜色 → 冻结画刷；调色板只有 8 色，缓存后各处共用同一份。</summary>
    internal static SolidColorBrush BadgeBrush(SceneBadge badge)
    {
        string hex = SceneBadge.Coerce(badge).Color;
        if (BadgeBrushes.TryGetValue(hex, out SolidColorBrush? cached)) return cached;

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        BadgeBrushes[hex] = brush;
        return brush;
    }
}
