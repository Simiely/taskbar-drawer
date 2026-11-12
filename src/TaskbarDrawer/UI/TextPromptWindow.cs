using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TaskbarDrawer.UI;

/// <summary>
/// 轻量重命名输入框：深色、置顶、回车确定 / Esc 取消。
/// 主面板与胶囊右键菜单共用，避免为了改一个名字再开一套设置页。
/// </summary>
public sealed class TextPromptWindow : Window
{
    private static readonly SolidColorBrush PanelBackground = Frozen(0xF7, 0x1C, 0x1C, 0x1E);
    private static readonly SolidColorBrush PanelBorder = Frozen(0xFF, 0x38, 0x38, 0x3A);
    private static readonly SolidColorBrush ControlBackground = Frozen(0xFF, 0x3A, 0x3A, 0x3C);
    private static readonly SolidColorBrush ControlHover = Frozen(0xFF, 0x48, 0x48, 0x4A);
    private static readonly SolidColorBrush TextPrimary = Frozen(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush TextMuted = Frozen(0xFF, 0x8E, 0x8E, 0x93);
    private static readonly SolidColorBrush AccentBrush = Frozen(0xFF, 0x0A, 0x84, 0xFF);

    private readonly TextBox _input;

    private TextPromptWindow(string title, string hint, string initial)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 340;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _input = new TextBox
        {
            Text = initial ?? "",
            FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI"),
            FontSize = 12.5,
            Foreground = TextPrimary,
            Background = ControlBackground,
            BorderBrush = PanelBorder,
            BorderThickness = new Thickness(1),
            CaretBrush = Brushes.White,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 10, 0, 0),
            SelectionBrush = AccentBrush
        };
        _input.SelectAll();

        var stack = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        stack.Children.Add(MakeText(title, 13, FontWeights.SemiBold, TextPrimary));
        stack.Children.Add(MakeText(hint, 10.5, FontWeights.Normal, TextMuted));
        stack.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var cancel = MakeAction("取消", false, () => { DialogResult = false; });
        cancel.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(cancel);
        buttons.Children.Add(MakeAction("确定", true, () =>
        {
            if (string.IsNullOrWhiteSpace(_input.Text)) return;
            DialogResult = true;
        }));
        stack.Children.Add(buttons);

        var root = new Border
        {
            Background = PanelBackground,
            BorderBrush = PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = stack
        };
        root.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { /* 已松手 */ }
        };
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (!string.IsNullOrWhiteSpace(_input.Text)) DialogResult = true;
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DialogResult = false;
            }
        };

        Loaded += (_, _) => _input.Focus();
    }

    /// <summary>弹出输入框；取消 / 空名字返回 null。</summary>
    public static string Ask(string title, string hint, string initial)
    {
        var window = new TextPromptWindow(title, hint, initial);
        return window.ShowDialog() == true ? window._input.Text.Trim() : null;
    }

    private static TextBlock MakeText(string text, double size, FontWeight weight, Brush foreground) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI"),
        FontSize = size,
        FontWeight = weight,
        Foreground = foreground,
        TextWrapping = TextWrapping.Wrap
    };

    private static Border MakeAction(string text, bool accent, Action onClick)
    {
        var label = MakeText(text, 11.5, accent ? FontWeights.SemiBold : FontWeights.Normal,
            accent ? Brushes.White : TextPrimary);
        var border = new Border
        {
            Background = accent ? AccentBrush : ControlBackground,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(13, 5, 13, 5),
            Child = label,
            Cursor = Cursors.Hand
        };
        border.MouseEnter += (_, _) => border.Background = accent ? AccentBrush : ControlHover;
        border.MouseLeave += (_, _) => border.Background = accent ? AccentBrush : ControlBackground;
        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return border;
    }

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
