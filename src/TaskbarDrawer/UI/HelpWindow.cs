using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.UI;

/// <summary>
/// 使用说明：主面板里所有解释性文字都收在这里，配矢量示意图与编号标注。
/// 示意图用 SVG 重绘，构建前转成纯 WPF 图形 + 原生文本，不引入 SVG 运行时。
/// 独立置顶小窗，Esc / 右上角关闭；内容超长时整体滚动。
/// </summary>
public sealed class HelpWindow : Window
{
    public HelpWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        Width = 620;
        // 初始高度不铺满整屏：内容多时用窗内滚动条，Esc / 右上角关闭。
        Height = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Content = BuildRoot();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    /// <summary>按主面板所在的显示器居中，并夹在目标屏的工作区内。</summary>
    public void PlaceOnMonitorOf(Window anchor)
    {
        const double margin = 24;
        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : 600;
        double left = anchor != null ? anchor.Left - width - 10 : 0;
        double top = anchor != null ? anchor.Top : 0;

        IntPtr hwnd = anchor == null ? IntPtr.Zero : new WindowInteropHelper(anchor).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect) ||
            !NativeMethods.TryGetMonitorOfRect(rect, out var monitor))
        {
            Left = Math.Max(0, left);
            Top = Math.Max(0, top);
            return;
        }

        double scale = NativeMethods.DpiOfDevice(monitor.szDevice) / 96.0;
        double workLeft = monitor.rcWork.Left / scale;
        double workTop = monitor.rcWork.Top / scale;
        double workRight = monitor.rcWork.Right / scale;
        double workBottom = monitor.rcWork.Bottom / scale;

        // 优先贴在面板左侧；放不下就居中到面板所在屏。
        double candidate = anchor.Left - width - 10;
        if (candidate < workLeft + margin)
            candidate = workLeft + (workRight - workLeft - width) / 2;

        // 高度上限必须按「本窗将要落到的那块屏」算：
        // OnSourceInitialized 执行时窗口还在主屏，主屏 4K + 副屏 1080p 会按主屏算出上限，
        // 结果窗口比副屏工作区还高，底部的「关闭」按钮落到屏幕外。
        MaxHeight = Math.Max(MinHeight, workBottom - workTop - 64);
        if (Height > MaxHeight) Height = MaxHeight;
        height = Math.Min(height, MaxHeight);

        Left = Math.Min(Math.Max(candidate, workLeft + 8), Math.Max(workLeft + 8, workRight - width - 8));
        Top = Math.Min(Math.Max(workTop + margin, workTop + 8), Math.Max(workTop + 8, workBottom - height - 8));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.ApplyToolWindowStyle(hwnd);
    }

    // ---------- 布局 ----------

    private FrameworkElement BuildRoot()
    {
        var stack = new StackPanel { Margin = new Thickness(20, 4, 20, 18) };
        stack.Children.Add(Ui.DragHandle(this));
        stack.Children.Add(BuildHeader());
        stack.Children.Add(Intro());
        stack.Children.Add(Ui.Divider());
        stack.Children.Add(BuildSection(
            "主面板",
            "所有操作都在这一块面板上：顶部拖动条与空白处可拖动，场景卡片右侧打开详情。",
            "help-panel",
            new[]
            {
                "① 顶部横条 / 面板空白处按住即可拖动；按钮、卡片、滑块上不会误拖。",
                "② 「＋ 新建场景」把这块屏幕上不属于任何场景的窗口收成一个新场景。",
                "③ 场景行点整行收起 / 展开；右侧「详情」在面板旁打开场景详情。",
                "④ 「说明」随时打开本窗口；「✕」只收起面板，不退出程序。",
                "⑤ 外观两个滑块调整任务栏图标里程序图标 / 场景标识的大小。"
            }));
        stack.Children.Add(Ui.Divider());
        stack.Children.Add(BuildSection(
            "场景详情",
            "详情窗贴在面板旁边，初始高度约为屏幕一半，内容多时用窗内滚动条；右下角 ◢ 可以手动拉高。移除 / 追加会立刻同步到正在运行的场景。",
            "help-scene",
            new[]
            {
                "① 详情窗顶部同样可以拖动；右下角 ◢ 拖动调整大小。",
                "② 场景里的窗口点「移除」后不再属于这个场景；收在收纳区里的窗口会放回桌面。",
                "③ 追加窗口列出这块屏幕上的全部窗口：已在当前场景 / 属于其它场景的也会显示并说明原因。",
                "④ 场景标识：颜色 + 数字 1–9 + 左上 / 右上（默认右上）；右键任意色块可改成自定义颜色。"
            }));
        stack.Children.Add(Ui.Divider());
        stack.Children.Add(BuildHotkeySection());
        stack.Children.Add(Ui.Divider());
        stack.Children.Add(BuildFaqSection());
        stack.Children.Add(BuildFooter());
        stack.Children.Add(new Border { Height = 4 });

        var scroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Ui.ApplyDarkScrollBars(scroll);

        var root = new Border
        {
            Background = Theme.PanelBackground,
            BorderBrush = Theme.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Effect = new DropShadowEffect
            {
                BlurRadius = 34,
                ShadowDepth = 8,
                Opacity = 0.55,
                Color = Colors.Black
            },
            Child = scroll
        };
        Ui.EnableDrag(root, this);
        return root;
    }

    private FrameworkElement BuildHeader()
    {
        var grid = new Grid { MinHeight = 34 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = Ui.Text("使用说明", 16, FontWeights.SemiBold, Theme.TextPrimary);
        title.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(title);

        var close = Ui.Id(Ui.ActionButton("✕", Close), "help.close");
        close.Background = Brushes.Transparent;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.MouseEnter += (_, _) => close.Background = Theme.PanelCardHover;
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        Grid.SetColumn(close, 1);
        grid.Children.Add(close);
        return grid;
    }

    private static FrameworkElement Intro()
    {
        var text = Ui.Text(
            "任务栏收纳把一组窗口收成一个「场景」：收起后任务栏只留一枚带彩色圆点的胶囊，点一下按原位展开。下面是各处的操作标注。",
            11.5, FontWeights.Normal, Theme.TextSecondary, wrap: true);
        text.Margin = new Thickness(0, 8, 0, 0);
        return text;
    }

    private FrameworkElement BuildSection(
        string title, string summary, string imageName, IReadOnlyList<string> bullets)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(SectionTitle(title));

        var summaryText = Ui.Text(summary, 11, FontWeights.Normal, Theme.TextSecondary, wrap: true);
        summaryText.Margin = new Thickness(2, 6, 2, 0);
        panel.Children.Add(summaryText);

        var illustration = LoadIllustration(imageName);
        if (illustration != null)
        {
            var frame = new Border
            {
                Background = Theme.PanelCard,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6),
                Margin = new Thickness(0, 10, 0, 0),
                Child = illustration
            };
            panel.Children.Add(frame);
        }

        foreach (string bullet in bullets)
            panel.Children.Add(Bullet(bullet));
        return panel;
    }

    private FrameworkElement BuildHotkeySection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(SectionTitle("快捷键与托盘"));
        panel.Children.Add(Bullet("默认 Ctrl+Alt+Shift+D：有未收纳窗口时新建场景；否则收起已展开的场景；都没有时展开最近的场景。"));
        panel.Children.Add(Bullet("托盘图标：双击打开主面板，单击切换单窗口模式；右键菜单可以新建场景、展开全部、退出。"));
        panel.Children.Add(Bullet("任务栏胶囊：左键收起 / 展开当前场景；右键可以重命名、关闭窗口或删除场景。"));
        panel.Children.Add(Bullet("以管理员身份运行的窗口需要以管理员身份启动本程序才能收纳；面板 / 托盘里都有「以管理员身份重启」。"));
        return panel;
    }

    private FrameworkElement BuildFaqSection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(SectionTitle("常见问题"));
        panel.Children.Add(Bullet("「关闭窗口」和「删除场景」：前者真的关闭这个场景的窗口，快照保留、之后还能按原位打开；后者只删除场景记录，窗口留在桌面上，不再由本程序管理。"));
        panel.Children.Add(Bullet("单窗口模式：同一场景的窗口算一个窗口。切到场景里的任意窗口，同场景窗口一起保留；切到场景外时整个场景收回胶囊。资源管理器默认不计入（面板开关「资源管理器不计入」），既不会被最小化，也不会因它触发单窗口动作。"));
        panel.Children.Add(Bullet("多显示器：一块屏幕一个场景。场景详情只列这块屏幕上的窗口，避免收起 / 展开时跨屏带走窗口。"));
        panel.Children.Add(Bullet("移除窗口后窗口去哪了：展开中的窗口留在原位，只是不再跟着场景收起；收在收纳区里的窗口会被放回桌面，不会凭空消失。"));
        return panel;
    }

    private FrameworkElement BuildFooter()
    {
        var done = Ui.Id(Ui.ActionButton("关闭", Close, accent: true), "help.done");
        done.HorizontalAlignment = HorizontalAlignment.Right;
        done.Margin = new Thickness(0, 16, 0, 0);
        return done;
    }

    private static FrameworkElement Bullet(string text)
    {
        var grid = new Grid { Margin = new Thickness(2, 5, 2, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = Theme.TextMuted,
            Margin = new Thickness(2, 5, 8, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        grid.Children.Add(dot);

        var body = Ui.Text(text, 11, FontWeights.Normal, Theme.TextPrimary, wrap: true);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private static FrameworkElement SectionTitle(string text)
    {
        var title = Ui.Text(text, 12, FontWeights.SemiBold, Theme.TextSecondary);
        title.Margin = new Thickness(2, 0, 0, 0);
        return title;
    }

    /// <summary>
    /// 从嵌入资源读矢量示意图：图形 XAML（DrawingGroup）+ 原生文本 XAML（Canvas）。
    /// 文本不转曲，避免中文字形路径把资源撑大，也不依赖本机字体文件路径。
    /// 资源缺失时返回 null，说明窗口仍可正常查看文字。
    /// </summary>
    private static FrameworkElement LoadIllustration(string name)
    {
        Drawing drawing = LoadXaml<Drawing>("TaskbarDrawer.Assets." + name + ".xaml");
        Canvas labels = LoadXaml<Canvas>("TaskbarDrawer.Assets." + name + "-labels.xaml");
        if (drawing == null || labels == null) return null;

        double width = labels.Width > 0 ? labels.Width : 760;
        double height = labels.Height > 0 ? labels.Height : 1000;

        var canvas = new Canvas { Width = width, Height = height };
        canvas.Children.Add(new Image
        {
            Source = new DrawingImage(drawing),
            Width = width,
            Height = height,
            Stretch = Stretch.Fill
        });
        canvas.Children.Add(labels);

        return new Viewbox
        {
            Stretch = Stretch.Uniform,
            MaxWidth = 548,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = canvas
        };
    }

    private static T LoadXaml<T>(string resourceName) where T : class
    {
        try
        {
            using Stream stream = typeof(HelpWindow).Assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                Diagnostics.Info("help", $"缺少说明图资源 {resourceName}");
                return null;
            }

            using var reader = new StreamReader(stream);
            return XamlReader.Parse(reader.ReadToEnd()) as T;
        }
        catch (Exception ex)
        {
            Diagnostics.Write("help.xaml", ex);
            return null;
        }
    }
}
