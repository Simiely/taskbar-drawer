using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarDrawer.Core;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.UI;

/// <summary>
/// 一枚任务栏胶囊：贴住任务栏的 topmost 无边框小窗，显示「场景标识 + 场景名」。
/// 视觉跟任务栏按钮一致：静止时完全透明，鼠标悬停叠一层半透明灰；
/// 已展开的场景常驻一层更淡的灰，一眼能看出哪个场景正在屏幕上。
/// Win11 不支持 DeskBand，所以它是独立窗口——样式必须带 TOOLWINDOW（不进 Alt+Tab）+
/// NOACTIVATE（点击不抢前台），但<b>不能</b>带 TRANSPARENT（那是鼠标穿透，会点不到）。
/// </summary>
public sealed class TaskbarChipWindow : Window
{
    private static readonly SolidColorBrush NormalBackground = Brushes.Transparent;
    private static readonly SolidColorBrush NormalHover = Theme.Frozen(0x2E, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush ExpandedBackground = Theme.Frozen(0x1A, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush ExpandedHover = Theme.Frozen(0x3D, 0xFF, 0xFF, 0xFF);

    private readonly Border _root;
    private readonly ContentControl _badge;
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

        _badge = new ContentControl
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
            Visibility = Visibility.Collapsed,
            IsTabStop = false,
            Focusable = false
        };

        _text = new TextBlock
        {
            Text = "",
            Foreground = Theme.TextPrimary,
            FontFamily = Ui.Font,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            // 竖排任务栏只有 ~100 px 宽：场景名太长时省略，而不是被窗口边缘硬切掉。
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var panel = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumn(_badge, 0);
        Grid.SetColumn(_text, 1);
        panel.Children.Add(_badge);
        panel.Children.Add(_text);

        _root = new Border
        {
            CornerRadius = new CornerRadius(13),
            Background = NormalBackground,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 0, 8, 0),
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
            FontFamily = Ui.Font,
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

    /// <summary>设置场景标识、显示文字、悬浮提示与展开态配色。</summary>
    public void SetContent(SceneBadge badge, string text, string tooltip, bool expanded)
    {
        _badge.Content = badge == null ? null : Ui.Badge(badge, 14);
        _badge.Visibility = badge == null ? Visibility.Collapsed : Visibility.Visible;
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

    /// <summary>
    /// 只把窗口抬回 topmost 波段的最上面，不动位置和尺寸。
    /// 任务栏和胶囊同属一个波段，谁最后被抬起谁在上面；被任务栏压住后
    /// 走完整重排（SetWindowPos 带位置尺寸 + WPF 重新布局）代价太大，
    /// 这条最小复位动作只改 z 序，每次被盖都能立刻用。
    /// </summary>
    public void Raise()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void ApplyVisual()
    {
        if (_expanded)
            _root.Background = _hovering ? ExpandedHover : ExpandedBackground;
        else
            _root.Background = _hovering ? NormalHover : NormalBackground;
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
}
