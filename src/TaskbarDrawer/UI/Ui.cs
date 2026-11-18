#nullable enable

using System.Globalization;
using System.Windows;
using System.Windows.Automation;
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
            // 禁用按钮的 Cursor 是 Arrow：只认 Hand 会把「按下禁用按钮」当成空白处，
            // 拖窗逻辑随即生效、整个窗口被拖走（详情窗的「移除」、追加列表大量处于禁用态）。
            if (node is FrameworkElement tagged && Equals(tagged.Tag, NoDragTag)) return true;
            if (node is FrameworkElement element && element.Cursor == Cursors.Hand) return true;
            if (node is Slider or ScrollBar or TextBoxBase or ButtonBase) return true;
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject node)
    {
        // 按钮还没加载完（模板没生成）时 VisualTreeHelper 可能取不到父级，退回逻辑树。
        DependencyObject? parent = node is Visual or Visual3D
            ? VisualTreeHelper.GetParent(node)
            : null;
        return parent ?? LogicalTreeHelper.GetParent(node);
    }

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

    /// <summary>禁用按钮的标记：拖窗逻辑看到它就当作「按在控件上」，不拖窗口。</summary>
    internal const string NoDragTag = "taskbar-drawer:no-drag";

    /// <summary>
    /// 给控件挂 UIA 自动化标识：检查器 / 自动化工具按 AutomationId 定位。
    /// 内容不是纯文本的控件（卡片行）要额外给 name，否则读屏和检查器里是一串空白。
    /// </summary>
    internal static T Id<T>(T element, string id, string? name = null) where T : FrameworkElement
    {
        AutomationProperties.SetAutomationId(element, id);
        if (!string.IsNullOrEmpty(name)) AutomationProperties.SetName(element, name);
        return element;
    }

    /// <summary>
    /// 统一的操作按钮：深色底 / 强调色，悬停变亮；enabled=false 时只保留静态外观。
    /// 必须是真的 Button —— Border 在 WPF 里没有 automation peer，UIA 树里根本看不见，
    /// 挂 AutomationId 也没用（实测 ControlView / RawView 都查不到）。
    /// </summary>
    internal static Button ActionButton(string text, Action onClick, bool accent = false, bool enabled = true)
        => ActionButton(text, onClick, accent, enabled, out _);

    internal static Button ActionButton(
        string text, Action onClick, bool accent, bool enabled, out TextBlock label)
    {
        label = Text(text, 11.5, accent ? FontWeights.SemiBold : FontWeights.Normal,
            accent ? Brushes.White : Theme.TextPrimary);
        var button = new Button
        {
            Content = label,
            Style = accent ? AccentButtonStyle : PlainButtonStyle,
            Padding = new Thickness(11, 5, 11, 5),
            Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
            Opacity = enabled ? 1 : 0.45,
            IsEnabled = enabled,
            VerticalAlignment = VerticalAlignment.Center
        };
        // 禁用态的 Cursor 是 Arrow：拖窗判断只认 Cursor 会漏，这里补上显式标记。
        if (!enabled) button.Tag = NoDragTag;
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>描边危险操作按钮（删除场景）：透明底 + 红边，悬停变亮。</summary>
    internal static Button DangerButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = Text(text, 11.5, FontWeights.Normal, Theme.DangerBrush),
            Style = DangerButtonStyle,
            Padding = new Thickness(11, 4, 11, 4),
            BorderBrush = Theme.DangerBrush,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>整块可点的卡片（场景行 / 开关行）：圆角底色 + 悬停高亮。</summary>
    internal static Button CardButton(
        UIElement content, Action onClick, string? tooltip, Thickness padding, string? name)
    {
        var card = new Button
        {
            Content = content,
            Style = CardButtonStyle,
            Padding = padding,
            Cursor = Cursors.Hand,
            ToolTip = tooltip
        };
        if (!string.IsNullOrEmpty(name)) AutomationProperties.SetName(card, name);

        // 场景卡片里还嵌着「详情 / 关闭窗口」按钮。WPF 的 ButtonBase 注册的是
        // handledEventsToo=true 的类处理器——内层按钮把事件标成 Handled 也挡不住外层跟着 Click，
        // 于是点一次「详情」会把整行的 toggle 也触发一次（场景收起 / 展开交替）。
        // 所以改用点击源判定：PreviewMouseLeftButtonDown 是隧道事件，先于内层处理到达，
        // 能准确看出这次按在不在某个 ButtonBase 上；Click 里据此决定要不要触发整行。
        bool pressedInnerButton = false;
        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressedInnerButton = e.OriginalSource is DependencyObject source && PressedInsideButton(source, card);
        };
        card.Click += (_, _) =>
        {
            if (pressedInnerButton) return;
            onClick();
        };
        return card;
    }

    /// <summary>
    /// 这次按下是不是落在 <paramref name="root"/> 内部的某个 ButtonBase 上。
    /// 抽成可测的纯规则：WPF 的 ButtonBase 类处理器带 handledEventsToo，
    /// 靠 e.Handled 拦不住外层卡片，只能按「点在哪」判断。
    /// </summary>
    internal static bool PressedInsideButton(DependencyObject source, FrameworkElement root)
    {
        for (DependencyObject? node = source; node != null && node != root; node = ParentOf(node))
        {
            if (node is ButtonBase) return true;
        }
        return false;
    }

    private static readonly Style PlainButtonStyle =
        CreateButtonStyle(Theme.ControlBackground, Theme.ControlHover, new CornerRadius(8), 0, false);

    // 强调色按钮原本就没有悬停变色（进入时把背景重新设成同一个强调色），这里保持一致。
    private static readonly Style AccentButtonStyle =
        CreateButtonStyle(Theme.AccentBrush, null, new CornerRadius(8), 0, false);

    private static readonly Style DangerButtonStyle =
        CreateButtonStyle(Brushes.Transparent, Theme.PanelCardHover, new CornerRadius(8), 1, false);

    private static readonly Style CardButtonStyle =
        CreateButtonStyle(Theme.PanelCard, Theme.PanelCardHover, new CornerRadius(10), 0, true);

    /// <summary>
    /// 按钮模板：只有一个圆角底板 + 内容，颜色全部跟着 Background 走（调用方仍可局部覆盖）。
    /// padding 走内容的 Margin，所以改 Button.Padding 依旧生效。
    /// </summary>
    private static Style CreateButtonStyle(
        Brush background, Brush? hover, CornerRadius radius, double borderThickness,
        bool stretchContent)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, radius);
        border.SetBinding(Border.BackgroundProperty, new Binding("Background")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        if (borderThickness > 0)
        {
            border.SetValue(Border.BorderThicknessProperty, new Thickness(borderThickness));
            border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
        }

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.MarginProperty,
            new TemplateBindingExtension(Control.PaddingProperty));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty,
            stretchContent ? HorizontalAlignment.Stretch : HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty,
            stretchContent ? VerticalAlignment.Stretch : VerticalAlignment.Center);
        border.AppendChild(presenter);

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty,
            new ControlTemplate(typeof(Button)) { VisualTree = border }));
        style.Setters.Add(new Setter(Control.BackgroundProperty, background));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        // 这些按钮改造前是 Border，不参与 Tab 焦点链；Focusable=false 也不影响 UIA 的 InvokePattern。
        style.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        if (hover != null) style.Triggers.Add(BrushTrigger(UIElement.IsMouseOverProperty, true, hover));
        return style;
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
