using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.UI;

/// <summary>
/// 一枚任务栏胶囊：贴住任务栏的 topmost 无边框小窗，显示「组号·窗口数」（如 <c>2·3</c>）。
/// 已收起的组用中性灰，已展开的组用系统蓝高亮，一眼能看出哪组正在屏幕上。
/// Win11 不支持 DeskBand，所以它是独立窗口——样式必须带 TOOLWINDOW（不进 Alt+Tab）+
/// NOACTIVATE（点击不抢前台），但<b>不能</b>带 TRANSPARENT（那是鼠标穿透，会点不到）。
/// </summary>
public sealed class TaskbarChipWindow : Window
{
    private static readonly SolidColorBrush NormalBackground = Theme.Frozen(0xE6, 0x2C, 0x2C, 0x2E);
    private static readonly SolidColorBrush NormalHover = Theme.Frozen(0xF5, 0x3A, 0x3A, 0x3C);
    private static readonly SolidColorBrush ExpandedBackground = Theme.Frozen(0xE6, 0x0A, 0x6C, 0xDE);
    private static readonly SolidColorBrush ExpandedHover = Theme.Frozen(0xF5, 0x1A, 0x7C, 0xEE);
    private static readonly SolidColorBrush EdgeNormal = Theme.Frozen(0x2E, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush EdgeExpanded = Theme.Frozen(0x00, 0x00, 0x00, 0x00);

    private readonly Border _root;
    private readonly TextBlock _text;
    private readonly ToolTip _tooltip;
    private IntPtr _hwnd;
    private bool _expanded;
    private bool _hovering;

    public TaskbarChipWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        Width = 54;
        Height = 26;

        _text = new TextBlock
        {
            Text = "",
            Foreground = Theme.TextPrimary,
            FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(BuildStackIcon());
        panel.Children.Add(_text);

        _root = new Border
        {
            CornerRadius = new CornerRadius(13),
            Background = NormalBackground,
            BorderBrush = EdgeNormal,
            BorderThickness = new Thickness(1),
            Child = panel,
            Cursor = Cursors.Hand
        };
        _root.MouseEnter += (_, _) =>
        {
            _hovering = true;
            ApplyVisual();
        };
        _root.MouseLeave += (_, _) =>
        {
            _hovering = false;
            ApplyVisual();
        };
        _root.MouseLeftButtonUp += (_, _) => Clicked?.Invoke(this, EventArgs.Empty);
        _root.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            RightClicked?.Invoke(this, EventArgs.Empty);
        };
        Content = _root;

        _tooltip = new ToolTip
        {
            Background = Theme.Frozen(0xF7, 0x1C, 0x1C, 0x1E),
            Foreground = Theme.Frozen(0xF5, 0xFF, 0xFF, 0xFF),
            BorderBrush = Theme.Frozen(0x33, 0xFF, 0xFF, 0xFF),
            FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI"),
            FontSize = 12,
            Padding = new Thickness(11, 8, 11, 8),
            Placement = PlacementMode.Top,
            PlacementTarget = _root
        };
        _root.ToolTip = _tooltip;
    }

    public event EventHandler Clicked;
    public event EventHandler RightClicked;

    public IntPtr Handle => _hwnd;

    /// <summary>当前是否真的显示在屏幕上（被任务栏盖住仍算显示）。</summary>
    public bool Shown => IsVisible;

    /// <summary>
    /// 调整悬浮提示的弹出方向：横排任务栏弹在上方，竖排任务栏弹到屏幕内侧，
    /// 避免提示框正好盖住相邻那枚胶囊（盖住时用户会以为胶囊消失）。
    /// </summary>
    public void SetTooltipPlacement(PlacementMode mode)
    {
        if (_tooltip.Placement == mode) return;
        _tooltip.Placement = mode;
    }

    /// <summary>创建句柄（不显示），并挂上「点击不抢前台」所需的 WndProc。</summary>
    public void EnsureReady()
    {
        if (_hwnd != IntPtr.Zero) return;
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.ApplyChipStyles(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    /// <summary>设置显示文字、悬浮提示与展开态配色。</summary>
    public void SetContent(string text, string tooltip, bool expanded)
    {
        _text.Text = text;
        _tooltip.Content = tooltip;
        _tooltip.IsEnabled = !string.IsNullOrEmpty(tooltip);
        if (_expanded != expanded)
        {
            _expanded = expanded;
            ApplyVisual();
        }
    }

    /// <summary>按物理像素摆放；首次显示时先设好 DIP 位置再 Show，避免在屏幕左上角闪一下。</summary>
    public void ShowAt(int xPx, int yPx, int widthPx, int heightPx)
    {
        if (_hwnd == IntPtr.Zero) return;

        double scale = NativeMethods.DpiOf(_hwnd) / 96.0;
        if (Math.Abs(Width - widthPx / scale) > 0.5) Width = widthPx / scale;
        if (Math.Abs(Height - heightPx / scale) > 0.5) Height = heightPx / scale;
        _root.CornerRadius = new CornerRadius(heightPx / scale / 2);

        if (!IsVisible)
        {
            Left = xPx / scale;
            Top = yPx / scale;
            Show();
        }

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, xPx, yPx, widthPx, heightPx,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    public void HideChip()
    {
        if (IsVisible) Hide();
    }

    private void ApplyVisual()
    {
        if (_expanded)
        {
            _root.Background = _hovering ? ExpandedHover : ExpandedBackground;
            _root.BorderBrush = EdgeExpanded;
        }
        else
        {
            _root.Background = _hovering ? NormalHover : NormalBackground;
            _root.BorderBrush = EdgeNormal;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    private static UIElement BuildStackIcon()
    {
        var grid = new Grid
        {
            Width = 13,
            Height = 12,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(new Border
        {
            Width = 12,
            Height = 8,
            CornerRadius = new CornerRadius(2),
            Background = Theme.Frozen(0x4D, 0xFF, 0xFF, 0xFF),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        });
        grid.Children.Add(new Border
        {
            Width = 12,
            Height = 8,
            CornerRadius = new CornerRadius(2),
            Background = Theme.Frozen(0xD9, 0xFF, 0xFF, 0xFF),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        });
        return grid;
    }
}
