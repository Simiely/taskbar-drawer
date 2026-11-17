using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.UI;

/// <summary>
/// 操作反馈浮层：屏幕底部中央弹出一条文字，停留约 1.7 s 后淡出，不抢焦点、不占任务栏。
/// 全局复用一个实例，连续操作时直接换文字并重新计时。
/// </summary>
public sealed class ToastWindow : Window
{
    private static ToastWindow _instance;

    private readonly TextBlock _text;
    private readonly Border _pill;
    private readonly DispatcherTimer _hold;

    private ToastWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 520;

        _text = new TextBlock
        {
            Text = "",
            Foreground = new SolidColorBrush(Color.FromArgb(0xF5, 0xF5, 0xF5, 0xF7)),
            FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        };

        _pill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18, 9, 18, 10),
            Child = _text,
            Effect = new DropShadowEffect
            {
                BlurRadius = 26,
                ShadowDepth = 6,
                Opacity = 0.45,
                Color = Colors.Black
            }
        };
        Content = _pill;

        _hold = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1700)
        };
        _hold.Tick += (_, _) => FadeOut();
    }

    /// <summary>
    /// 弹一条反馈；默认出现在鼠标所在屏幕的底部中央，自动淡出。
    /// 多显示器下由调用方传入操作发生的那块屏幕，副屏操作就不会把提示丢到主屏。
    /// </summary>
    public static void Show(string message, string monitorDevice = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _instance ??= new ToastWindow();
        _instance.ShowMessage(message, monitorDevice);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.ApplyChipStyles(hwnd);
    }

    private void ShowMessage(string message, string monitorDevice)
    {
        _hold.Stop();
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        _text.Text = message;

        if (!IsVisible) Show();
        UpdateLayout();
        PositionBottom(monitorDevice);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fadeIn);
        _hold.Start();
    }

    private void FadeOut()
    {
        _hold.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void PositionBottom(string monitorDevice)
    {
        double width = ActualWidth > 0 ? ActualWidth : 240;
        double height = ActualHeight > 0 ? ActualHeight : 40;

        // 按「操作发生的那块屏」定位，并用这块屏自己的 DPI 换算：
        // 用 SystemParameters.WorkArea 永远是主屏，副屏上什么都看不到。
        string device = monitorDevice ?? NativeMethods.MonitorDeviceOfCursor();
        if (NativeMethods.TryGetMonitorOfDevice(device, out var monitor))
        {
            double scale = NativeMethods.DpiOfDevice(monitor.szDevice) / 96.0;
            double left = monitor.rcWork.Left / scale;
            double areaWidth = monitor.rcWork.Width / scale;
            double bottom = monitor.rcWork.Bottom / scale;
            Left = left + (areaWidth - width) / 2;
            Top = bottom - height - 84;
            return;
        }

        Rect work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - width) / 2;
        Top = work.Bottom - height - 84;
    }
}
