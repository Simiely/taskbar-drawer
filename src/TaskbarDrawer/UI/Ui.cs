#nullable enable

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
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

    /// <summary>区块之间的 1 px 分隔线。</summary>
    internal static Border Divider() => new()
    {
        Height = 1,
        Background = Theme.Divider,
        Margin = new Thickness(0, 16, 0, 0)
    };

    /// <summary>
    /// 把一块表面变成拖动区：空白 / 文字上按下即可拖动窗口，
    /// 点到可点击控件（Cursor=Hand、按钮、滑块、滚动条）时不抢事件。
    /// </summary>
    internal static void EnableDrag(FrameworkElement surface, Window window)
    {
        surface.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            if (IsInteractive(e.OriginalSource as DependencyObject)) return;
            try { window.DragMove(); } catch (InvalidOperationException) { /* 已松手 */ }
        };
    }

    /// <summary>窗口顶部统一的拖动条：一条小横条 + 整行透明拖动区，告诉用户这里能拖。</summary>
    internal static FrameworkElement DragHandle(Window window)
    {
        var grabber = new Border
        {
            Width = 36,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = Theme.Divider,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var strip = new Border
        {
            Height = 24,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            Child = grabber
        };
        strip.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            e.Handled = true;
            try { window.DragMove(); } catch (InvalidOperationException) { /* 已松手 */ }
        };
        return strip;
    }

    /// <summary>事件源到窗口之间的任一层是可点击控件，就认为这次按下不该触发拖窗。</summary>
    private static bool IsInteractive(DependencyObject? source)
    {
        for (var node = source; node != null; node = ParentOf(node))
        {
            if (node is FrameworkElement element && element.Cursor == Cursors.Hand) return true;
            if (node is Slider or ScrollBar or TextBoxBase or ButtonBase) return true;
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject node)
        => node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    /// <summary>
    /// 把 ScrollViewer 的滚动条换成深色细条：透明轨道、圆角拇指、没有箭头按钮，
    /// 只作用于这棵子树，不影响系统中其它滚动条。
    /// </summary>
    internal static void ApplyDarkScrollBars(ScrollViewer scroll)
    {
        if (scroll == null) return;
        scroll.Resources[typeof(ScrollBar)] = DarkScrollBarStyle;
    }

    private static readonly Style DarkScrollBarStyle = CreateDarkScrollBarStyle();
    private static readonly Style DarkThumbStyle = CreateDarkThumbStyle();

    private static Style CreateDarkScrollBarStyle()
    {
        // 模板只放一个 PART_Track：ScrollBar 基类会负责把它接到 Minimum / Maximum / Value 上，
        // 拇指和翻页按钮在 Loaded 时补进去（纯代码 UI 里没法用 XAML 的 Track.Thumb 语法）。
        var track = new FrameworkElementFactory(typeof(Track), "PART_Track");
        track.SetValue(Track.IsDirectionReversedProperty, true);
        var template = new ControlTemplate(typeof(ScrollBar)) { VisualTree = track };

        var style = new Style(typeof(ScrollBar));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 10.0));
        // 上下留出圆角，滚动条像一枚悬浮的小胶囊，不顶到面板圆角外面。
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 8, 3, 8)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new EventSetter(FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ConfigureScrollBar((ScrollBar)sender))));
        return style;
    }

    /// <summary>ScrollBar 的 PART_Track 在模板里是空的：加载后补上拇指与上下翻页区。</summary>
    private static void ConfigureScrollBar(ScrollBar bar)
    {
        if (bar.Template?.FindName("PART_Track", bar) is not Track track) return;
        if (track.Thumb != null) return;

        track.Thumb = new Thumb
        {
            Style = DarkThumbStyle,
            MinHeight = 28,
            // 横向不留边：拇指的布局宽度仍是整条 10 px，保持原来的拖动判定范围；
            // 视觉宽度由模板里的 3 px 细条决定（原来约 6 px，缩小一半）。
            Margin = new Thickness(0, 2, 0, 2)
        };
        track.DecreaseRepeatButton = new RepeatButton
        {
            Command = ScrollBar.PageUpCommand,
            Opacity = 0,
            Focusable = false,
            IsTabStop = false
        };
        track.IncreaseRepeatButton = new RepeatButton
        {
            Command = ScrollBar.PageDownCommand,
            Opacity = 0,
            Focusable = false,
            IsTabStop = false
        };
    }

    private static Style CreateDarkThumbStyle()
    {
        // 外层 Grid 透明但可命中：整条 10 px 都能拖；
        // 内层 Border 只有 3 px 宽，视觉上比原来（约 6 px）缩小一半。
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(Panel.BackgroundProperty, Brushes.Transparent);

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(FrameworkElement.WidthProperty, 3.0);
        border.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(1.5));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        grid.AppendChild(border);

        var template = new ControlTemplate(typeof(Thumb)) { VisualTree = grid };

        var style = new Style(typeof(Thumb));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Theme.ScrollThumb));
        style.Triggers.Add(BrushTrigger(UIElement.IsMouseOverProperty, true, Theme.ScrollThumbHover));
        style.Triggers.Add(BrushTrigger(Thumb.IsDraggingProperty, true, Theme.ScrollThumbDrag));
        return style;
    }

    private static Trigger BrushTrigger(DependencyProperty property, object value, Brush brush)
    {
        var trigger = new Trigger { Property = property, Value = value };
        trigger.Setters.Add(new Setter(Control.BackgroundProperty, brush));
        return trigger;
    }

    /// <summary>统一的操作按钮：深色底 / 强调色，悬停变亮；enabled=false 时只保留静态外观。</summary>
    internal static Border ActionButton(string text, Action onClick, bool accent = false, bool enabled = true)
        => ActionButton(text, onClick, accent, enabled, out _);

    internal static Border ActionButton(
        string text, Action onClick, bool accent, bool enabled, out TextBlock label)
    {
        label = Text(text, 11.5, accent ? FontWeights.SemiBold : FontWeights.Normal,
            accent ? Brushes.White : Theme.TextPrimary);
        var border = new Border
        {
            Background = accent ? Theme.AccentBrush : Theme.ControlBackground,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 5, 11, 5),
            Child = label,
            Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
            Opacity = enabled ? 1 : 0.45,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!enabled) return border;

        border.MouseEnter += (_, _) => border.Background = accent ? Theme.AccentBrush : Theme.ControlHover;
        border.MouseLeave += (_, _) => border.Background = accent ? Theme.AccentBrush : Theme.ControlBackground;
        border.MouseLeftButtonDown += (_, e) => e.Handled = true;
        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return border;
    }

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
        // 用户可以反复改自定义颜色：给缓存一个上限，避免颜色历史无限增长。
        if (BadgeBrushes.Count >= 64) BadgeBrushes.Clear();
        BadgeBrushes[hex] = brush;
        return brush;
    }
}
