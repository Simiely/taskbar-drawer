using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using TaskbarDrawer.Core;
using TaskbarDrawer.Interop;
using Forms = System.Windows.Forms;

namespace TaskbarDrawer.UI;

/// <summary>
/// 场景详情：在主面板旁边展开的非模态小窗，显示场景里的窗口并完成移除 / 追加 / 重命名 / 删除。
/// 只读数据 + 抛事件，真正的读写留在 <see cref="WorkspaceService"/>；AppShell 负责推送最新数据。
/// 移除 / 追加会同时同步运行中的场景（收起中的窗口会放回桌面），不需要关掉再打开。
/// </summary>
public sealed class SceneEditorWindow : Window
{
    private readonly TextBlock _name;
    private readonly WrapPanel _positionRow = new();
    private readonly WrapPanel _numberRow = new();
    private readonly WrapPanel _colorRow = new();
    private readonly StackPanel _windowList = new();
    private readonly StackPanel _appendList = new();
    private readonly Border _runningHint;
    private readonly TextBlock _windowEmpty;
    private readonly TextBlock _appendEmpty;
    private ScrollViewer _scroll;

    /// <summary>当前选中的场景标识（颜色 + 可选数字 + 角上位置）。</summary>
    private SceneBadge _badge = SceneBadge.Default;

    /// <summary>可选的 8 个颜色：由 AppShell 从设置里推过来，右键任意色块可以改。</summary>
    private IReadOnlyList<string> _palette = SceneBadge.DefaultPalette;

    /// <summary>上一次渲染的数据签名：2.5 s 的 Prune 刷新不能把滚动位置打回顶部。</summary>
    private string _signature = "";

    /// <summary>上一次展示的场景 Id：切换场景时回到顶部，同一场景刷新时保留滚动位置。</summary>
    private string _detailId;

    public SceneEditorWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        Width = 440;
        // 初始高度只占屏幕一半左右，内容多时用窗内滚动条；右下角可以手动拉高。
        Height = 620;
        MinWidth = 380;
        MinHeight = 360;
        MaxWidth = 760;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _name = Ui.Text("", 10.5, FontWeights.Normal, Theme.TextMuted);
        _name.Margin = new Thickness(0, 3, 0, 0);

        _runningHint = new Border
        {
            Background = Theme.PanelCard,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 12, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = Ui.Text("这个场景正在运行：移除 / 追加会立刻同步到当前窗口。",
                10.5, FontWeights.Normal, Theme.AccentBrush, wrap: true)
        };

        _windowEmpty = Ui.Text("这个场景里还没有窗口。", 11, FontWeights.Normal, Theme.TextMuted);
        _windowEmpty.Margin = new Thickness(2, 8, 0, 0);
        _windowEmpty.Visibility = Visibility.Collapsed;

        _appendEmpty = Ui.Text("这块屏幕上没有可追加的窗口。",
            11, FontWeights.Normal, Theme.TextMuted, wrap: true);
        _appendEmpty.Margin = new Thickness(2, 8, 0, 0);
        _appendEmpty.Visibility = Visibility.Collapsed;

        Content = BuildRoot();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    /// <summary>移除场景里第 index 个窗口。</summary>
    public event Action<int> RemoveWindowRequested;

    /// <summary>把某个顶层窗口（句柄）追加进场景；场景运行中会立刻接入。</summary>
    public event Action<long> AppendWindowRequested;

    /// <summary>重新枚举这块屏幕上的可追加窗口。</summary>
    public event Action RefreshListRequested;

    public event Action RenameRequested;
    public event Action DeleteRequested;

    /// <summary>用户在编辑器里换了场景标识（颜色 / 数字 / 位置）。</summary>
    public event Action<SceneBadge> BadgeChangeRequested;

    /// <summary>右键改了调色板第 index 个色块（新颜色是 #RRGGBB）。</summary>
    public event Action<int, string> PaletteColorRequested;

    /// <summary>把这 8 个色块恢复成默认配色。</summary>
    public event Action PaletteResetRequested;

    /// <summary>AppShell 把当前生效的调色板推过来：只重建颜色一行，不影响其它内容。</summary>
    public void SetPalette(IReadOnlyList<string> palette)
    {
        if (palette == null || palette.Count == 0) return;
        _palette = palette.ToArray();
        RebuildColorRow();
    }

    /// <summary>由 AppShell 推送最新数据；数据没变化时不重建，避免打断滚动。</summary>
    public void Reload(
        SavedGroupDetail detail,
        int runningGroupId,
        IReadOnlyList<AppendableWindowItem> appendable)
    {
        if (detail == null) return;

        string signature = Signature(detail, runningGroupId, appendable);
        if (signature == _signature) return;
        _signature = signature;

        bool switchedScene = !string.Equals(_detailId, detail.Id, StringComparison.Ordinal);
        _detailId = detail.Id;
        double offset = _scroll?.VerticalOffset ?? 0;

        _name.Text = string.IsNullOrWhiteSpace(detail.Name) ? "未命名场景" : detail.Name;
        _badge = SceneBadge.Coerce(detail.Badge);
        RebuildBadgeRows();
        _runningHint.Visibility = runningGroupId > 0 ? Visibility.Visible : Visibility.Collapsed;

        _windowList.Children.Clear();
        foreach (var window in detail.Windows)
            _windowList.Children.Add(BuildWindowRow(window, detail.Windows.Count));
        _windowEmpty.Visibility = detail.Windows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var windows = appendable ?? Array.Empty<AppendableWindowItem>();
        _appendList.Children.Clear();
        foreach (var item in windows)
            _appendList.Children.Add(BuildAppendRow(item));
        _appendEmpty.Visibility = windows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 追加 / 移除后整块重建：同一场景保留用户刚才的滚动位置，切换场景才回到顶部。
        if (_scroll != null)
        {
            _scroll.UpdateLayout();
            if (switchedScene) _scroll.ScrollToTop();
            else _scroll.ScrollToVerticalOffset(offset);
        }
    }

    /// <summary>贴在主面板旁边：优先放右侧；面板贴屏幕右边放不下时自动换到左侧。</summary>
    public void PlaceBeside(Window anchor)
    {
        if (anchor == null) return;

        const double gap = 10;
        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : Height;
        double left = anchor.Left + anchor.ActualWidth + gap;
        double top = anchor.Top;

        IntPtr hwnd = new WindowInteropHelper(anchor).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect) ||
            !NativeMethods.TryGetMonitorOfRect(rect, out var monitor))
        {
            Left = left;
            Top = top;
            return;
        }

        // 面板 / 详情必须在同一块屏：工作区从物理像素换成面板所在屏的 DIP。
        double scale = NativeMethods.DpiOfDevice(monitor.szDevice) / 96.0;
        double workLeft = monitor.rcWork.Left / scale;
        double workTop = monitor.rcWork.Top / scale;
        double workRight = monitor.rcWork.Right / scale;
        double workBottom = monitor.rcWork.Bottom / scale;

        if (left + width > workRight - 8) left = anchor.Left - width - gap;
        Left = Math.Min(Math.Max(left, workLeft + 8), Math.Max(workLeft + 8, workRight - width - 8));
        Top = Math.Min(Math.Max(top, workTop + 8), Math.Max(workTop + 8, workBottom - height - 8));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.ApplyToolWindowStyle(hwnd);

        // 高度上限按编辑器实际所在的显示器算：SystemParameters.WorkArea 永远是主屏，
        // 主屏 4K + 副屏 1080p 时编辑器会比副屏还高，底部按钮点不到。
        if (NativeMethods.GetWindowRect(hwnd, out var rect) &&
            NativeMethods.TryGetMonitorOfRect(rect, out var monitor))
        {
            double workHeight = monitor.rcWork.Height / (NativeMethods.DpiOfDevice(monitor.szDevice) / 96.0);
            MaxHeight = Math.Max(MinHeight, workHeight - 40);
            if (Height > MaxHeight) Height = MaxHeight;
        }
    }

    // ---------- 布局 ----------

    private FrameworkElement BuildRoot()
    {
        var stack = new StackPanel { Margin = new Thickness(18, 4, 18, 16) };
        stack.Children.Add(Ui.DragHandle(this));
        stack.Children.Add(BuildHeader());
        stack.Children.Add(BuildBadgeSection());
        stack.Children.Add(_runningHint);
        stack.Children.Add(BuildWindowSection());
        stack.Children.Add(BuildAppendSection());
        stack.Children.Add(Ui.Divider());
        stack.Children.Add(BuildManageActions());
        stack.Children.Add(Ui.Divider());
        stack.Children.Add(BuildFooter());

        // 窗口高度固定（可拖右下角调整），内容超出时在窗内滚动。
        _scroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Ui.ApplyDarkScrollBars(_scroll);

        var host = new Grid();
        host.Children.Add(_scroll);
        host.Children.Add(BuildResizeGrip());

        var root = new Border
        {
            Background = Theme.PanelBackground,
            BorderBrush = Theme.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Effect = new DropShadowEffect
            {
                BlurRadius = 30,
                ShadowDepth = 8,
                Opacity = 0.55,
                Color = Colors.Black
            },
            Child = host
        };
        Ui.EnableDrag(root, this);
        return root;
    }

    /// <summary>右下角的调整手柄：拖动按像素改宽高，窗口高度不跟内容无限增长。</summary>
    private FrameworkElement BuildResizeGrip()
    {
        var grip = new TextBlock
        {
            Text = "◢",
            FontFamily = Ui.Font,
            FontSize = 13,
            Foreground = Theme.TextMuted,
            Cursor = Cursors.SizeNWSE,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 6, 2),
            ToolTip = "拖动调整详情窗大小"
        };

        Point origin = default;
        Size start = default;
        bool dragging = false;

        grip.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            dragging = true;
            origin = e.GetPosition(this);
            start = new Size(ActualWidth, ActualHeight);
            grip.CaptureMouse();
            e.Handled = true;
        };
        grip.MouseMove += (_, e) =>
        {
            if (!dragging || e.LeftButton != MouseButtonState.Pressed) return;
            Point now = e.GetPosition(this);
            double width = Math.Max(MinWidth, start.Width + (now.X - origin.X));
            double height = Math.Max(MinHeight, start.Height + (now.Y - origin.Y));
            Width = Math.Min(Math.Min(width, MaxWidth), SystemParameters.VirtualScreenWidth);
            Height = MaxHeight > 0
                ? Math.Min(height, MaxHeight)
                : Math.Min(height, SystemParameters.VirtualScreenHeight);
        };
        grip.MouseLeftButtonUp += (_, e) =>
        {
            dragging = false;
            grip.ReleaseMouseCapture();
            e.Handled = true;
        };
        // 切换窗口 / 被其它窗口抢走捕获时，必须结束拖动，否则鼠标再移动会继续改大小。
        grip.LostMouseCapture += (_, _) => dragging = false;
        return grip;
    }

    private FrameworkElement BuildHeader()
    {
        var grid = new Grid { Background = Brushes.Transparent, MinHeight = 32 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel();
        texts.Children.Add(Ui.Text("场景详情", 15, FontWeights.SemiBold, Theme.TextPrimary));
        texts.Children.Add(_name);
        Grid.SetColumn(texts, 0);
        grid.Children.Add(texts);

        var close = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 3, 9, 5),
            Child = Ui.Text("✕", 12, FontWeights.Normal, Theme.TextSecondary),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top
        };
        close.MouseEnter += (_, _) => close.Background = Theme.PanelCardHover;
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        Grid.SetColumn(close, 1);
        grid.Children.Add(close);
        return grid;
    }

    private FrameworkElement BuildBadgeSection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        panel.Children.Add(SectionTitle("场景标识（叠在任务栏窗口图标的左上角或右上角）"));
        panel.Children.Add(BuildChoiceRow("位置", _positionRow));
        panel.Children.Add(BuildChoiceRow("数字", _numberRow));
        panel.Children.Add(BuildChoiceRow("颜色", _colorRow));
        return panel;
    }

    private static FrameworkElement BuildChoiceRow(string label, FrameworkElement cells)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0)
        };
        var text = Ui.Text(label, 10.5, FontWeights.Normal, Theme.TextMuted);
        text.Width = 32;
        text.Margin = new Thickness(2, 5, 0, 0);
        row.Children.Add(text);
        row.Children.Add(cells);
        return row;
    }

    /// <summary>重建三行选项；选中的那一格用强调色标出来。</summary>
    private void RebuildBadgeRows()
    {
        _positionRow.Children.Clear();
        _positionRow.Children.Add(ChoiceCell("左上", !_badge.Right,
            () => ChangeBadge(badge => badge.Right = false)));
        _positionRow.Children.Add(ChoiceCell("右上", _badge.Right,
            () => ChangeBadge(badge => badge.Right = true)));

        _numberRow.Children.Clear();
        _numberRow.Children.Add(ChoiceCell("无", _badge.Number == 0,
            () => ChangeBadge(badge => badge.Number = 0)));
        for (int n = 1; n <= 9; n++)
        {
            int number = n;
            _numberRow.Children.Add(ChoiceCell(number.ToString(CultureInfo.InvariantCulture), _badge.Number == number,
                () => ChangeBadge(badge => badge.Number = number)));
        }

        RebuildColorRow();
    }

    /// <summary>颜色行：8 个可右键改色的色块 + 恢复默认按钮。</summary>
    private void RebuildColorRow()
    {
        _colorRow.Children.Clear();
        for (int index = 0; index < _palette.Count; index++)
        {
            int slot = index;
            string picked = _palette[index];
            _colorRow.Children.Add(ColorCell(picked,
                string.Equals(_badge.Color, picked, StringComparison.OrdinalIgnoreCase),
                () => ChangeBadge(badge => badge.Color = picked),
                () => EditPaletteColor(slot, picked)));
        }

        var reset = Ui.ActionButton("恢复默认", () => PaletteResetRequested?.Invoke());
        reset.Margin = new Thickness(4, 2, 0, 2);
        reset.ToolTip = "把这 8 个颜色恢复成默认配色；用了这些颜色的场景会按色块位置跟着换回来";
        _colorRow.Children.Add(reset);
    }

    /// <summary>右键色块：用系统取色器改成任意颜色；确认后交给 AppShell 统一改配置和场景。</summary>
    private void EditPaletteColor(int index, string color)
    {
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = ToDrawingColor(color)
        };

        IntPtr handle = new WindowInteropHelper(this).Handle;
        Forms.DialogResult result = handle == IntPtr.Zero
            ? dialog.ShowDialog()
            : dialog.ShowDialog(new OwnerWindow(handle));
        if (result != Forms.DialogResult.OK) return;

        string picked = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        if (string.Equals(picked, color, StringComparison.OrdinalIgnoreCase)) return;
        PaletteColorRequested?.Invoke(index, picked);
    }

    private static System.Drawing.Color ToDrawingColor(string hex)
    {
        string value = SceneBadge.NormalizeColor(hex);
        return System.Drawing.Color.FromArgb(
            Convert.ToInt32(value.Substring(1, 2), 16),
            Convert.ToInt32(value.Substring(3, 2), 16),
            Convert.ToInt32(value.Substring(5, 2), 16));
    }

    /// <summary>取色对话框要一个 IWin32Window 当 owner，否则可能被置顶的编辑器盖住。</summary>
    private sealed class OwnerWindow : Forms.IWin32Window
    {
        public OwnerWindow(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }

    /// <summary>在当前标识上改一处（颜色 / 数字 / 位置），然后通知 AppShell。</summary>
    private void ChangeBadge(Action<SceneBadge> change)
    {
        var next = new SceneBadge { Color = _badge.Color, Number = _badge.Number, Right = _badge.Right };
        change(next);
        var normalized = next.Normalized();
        if (normalized.SameAs(_badge)) return;

        _badge = normalized;
        RebuildBadgeRows();
        BadgeChangeRequested?.Invoke(_badge);
    }

    private Border ChoiceCell(string text, bool selected, Action onClick)
    {
        var cell = new Border
        {
            Background = selected ? Theme.AccentBrush : Theme.ControlBackground,
            BorderBrush = selected ? Theme.AccentBrush : Theme.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 2, 9, 3),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            Child = Ui.Text(text, 11,
                selected ? FontWeights.SemiBold : FontWeights.Normal,
                selected ? Brushes.White : Theme.TextPrimary)
        };
        cell.MouseEnter += (_, _) =>
        {
            if (!selected) cell.Background = Theme.ControlHover;
        };
        cell.MouseLeave += (_, _) =>
        {
            if (!selected) cell.Background = Theme.ControlBackground;
        };
        cell.MouseLeftButtonDown += (_, e) => e.Handled = true;
        cell.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!selected) onClick();
        };
        return cell;
    }

    private Border ColorCell(string color, bool selected, Action onClick, Action onEdit)
    {
        var swatch = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = Ui.BadgeBrush(new SceneBadge { Color = color }),
            // 深色底上黑色圆点几乎看不见，给每个色块描一圈细边把轮廓勾出来。
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1)
        };
        var cell = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = selected ? Theme.TextPrimary : Brushes.Transparent,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 2, 0),
            Cursor = Cursors.Hand,
            Child = swatch,
            ToolTip = "左键选用；右键修改这个色块"
        };
        cell.MouseEnter += (_, _) =>
        {
            if (!selected) cell.BorderBrush = Theme.TextSecondary;
        };
        cell.MouseLeave += (_, _) =>
        {
            if (!selected) cell.BorderBrush = Brushes.Transparent;
        };
        cell.MouseLeftButtonDown += (_, e) => e.Handled = true;
        cell.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!selected) onClick();
        };
        cell.MouseRightButtonDown += (_, e) => e.Handled = true;
        cell.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            onEdit();
        };
        return cell;
    }

    private FrameworkElement BuildWindowSection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(SectionTitle("场景里的窗口"));
        _windowList.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_windowList);
        panel.Children.Add(_windowEmpty);
        return panel;
    }

    /// <summary>追加区：列出这块屏幕上的全部窗口，已在场景 / 其它场景的也显示出来并说明原因。</summary>
    private FrameworkElement BuildAppendSection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(SectionTitle("追加窗口（这块屏幕上的全部窗口）"));

        var refresh = Ui.ActionButton("刷新", () => RefreshListRequested?.Invoke());
        refresh.VerticalAlignment = VerticalAlignment.Center;
        refresh.ToolTip = "重新扫描这块屏幕上的窗口";
        Grid.SetColumn(refresh, 1);
        head.Children.Add(refresh);
        panel.Children.Add(head);

        _appendList.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_appendList);
        panel.Children.Add(_appendEmpty);
        return panel;
    }

    private FrameworkElement BuildManageActions()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var actions = new WrapPanel();

        var rename = Ui.ActionButton("重命名场景", () => RenameRequested?.Invoke());
        rename.Margin = new Thickness(0, 0, 8, 6);
        actions.Children.Add(rename);

        var delete = MakeDanger("删除场景", () => DeleteRequested?.Invoke());
        delete.Margin = new Thickness(0, 0, 8, 6);
        actions.Children.Add(delete);
        panel.Children.Add(actions);
        return panel;
    }

    private FrameworkElement BuildFooter()
    {
        var done = Ui.ActionButton("完成", Close, accent: true);
        done.HorizontalAlignment = HorizontalAlignment.Right;
        done.Margin = new Thickness(0, 14, 0, 0);
        return done;
    }

    private Border BuildWindowRow(SavedWindowItem item, int total)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel();
        var title = Ui.Text(
            string.IsNullOrWhiteSpace(item.Title) ? FallbackTitle(item) : item.Title,
            12, FontWeights.SemiBold, Theme.TextPrimary);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        texts.Children.Add(title);

        var sub = Ui.Text(WindowMeta(item), 10.5, FontWeights.Normal, Theme.TextMuted);
        sub.Margin = new Thickness(0, 2, 0, 0);
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        texts.Children.Add(sub);

        Grid.SetColumn(texts, 0);
        grid.Children.Add(texts);

        int index = item.Index;
        var remove = Ui.ActionButton("移除", () => RemoveWindowRequested?.Invoke(index), enabled: total > 1);
        if (total <= 1) remove.ToolTip = "场景至少保留一个窗口；整个场景不要了请用「删除场景」";
        remove.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(remove, 1);
        grid.Children.Add(remove);

        return new Border
        {
            Background = Theme.PanelCard,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 8, 7),
            Margin = new Thickness(0, 5, 0, 0),
            Child = grid
        };
    }

    /// <summary>一个可追加 / 已在场景 / 属于其它场景的窗口行。</summary>
    private Border BuildAppendRow(AppendableWindowItem item)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel();
        var title = Ui.Text(string.IsNullOrWhiteSpace(item.Title) ? "未命名窗口" : item.Title,
            12, FontWeights.SemiBold, Theme.TextPrimary);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        texts.Children.Add(title);

        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.ProcessName)) details.Add(item.ProcessName);
        if (item.Minimized) details.Add("已最小化");
        if (item.InScene) details.Add("已在当前场景");
        else if (item.InOtherScene) details.Add("属于其它场景");
        else details.Add("可追加");
        var sub = Ui.Text(string.Join(" · ", details), 10.5, FontWeights.Normal,
            item.CanAppend ? Theme.TextMuted : Theme.TextSecondary);
        sub.Margin = new Thickness(0, 2, 0, 0);
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        texts.Children.Add(sub);

        Grid.SetColumn(texts, 0);
        grid.Children.Add(texts);

        string label;
        bool enabled;
        if (item.InScene)
        {
            label = "已在场景";
            enabled = false;
        }
        else if (item.InOtherScene)
        {
            label = "在其它场景";
            enabled = false;
        }
        else
        {
            label = "追加";
            enabled = true;
        }

        long handle = item.Handle;
        var append = Ui.ActionButton(label,
            () => AppendWindowRequested?.Invoke(handle), accent: enabled, enabled: enabled);
        if (!item.CanAppend)
            append.ToolTip = item.InScene
                ? "这个窗口已经在当前场景里"
                : "这个窗口被另一个场景管理，先把它从那个场景移除";
        else
            append.ToolTip = "把这个窗口加入当前场景；场景正在运行会立刻生效";
        append.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(append, 1);
        grid.Children.Add(append);

        return new Border
        {
            Background = Theme.PanelCard,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 8, 7),
            Margin = new Thickness(0, 5, 0, 0),
            Child = grid
        };
    }

    private static string FallbackTitle(SavedWindowItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ProcessName)) return item.ProcessName;
        string name = System.IO.Path.GetFileNameWithoutExtension(item.ExePath);
        return string.IsNullOrWhiteSpace(name) ? "未知窗口" : name;
    }

    private static string WindowMeta(SavedWindowItem item)
    {
        string process = string.IsNullOrWhiteSpace(item.ProcessName) ? "未知程序" : item.ProcessName;
        return item.Maximized ? process + " · 打开时最大化" : process + " · 打开时还原位置";
    }

    private static string Signature(
        SavedGroupDetail detail, int runningGroupId, IReadOnlyList<AppendableWindowItem> appendable)
    {
        var builder = new StringBuilder();
        builder.Append(detail.Id).Append('|').Append(detail.Name).Append('|')
            .Append(BadgeKey(detail.Badge)).Append('|')
            .Append(runningGroupId).Append('|');

        foreach (var window in detail.Windows)
        {
            builder.Append(window.Index).Append(':')
                .Append(window.Title).Append(':')
                .Append(window.ProcessName).Append(':')
                .Append(window.ExePath).Append(':')
                .Append(window.Maximized).Append(';');
        }

        builder.Append('|');
        if (appendable != null)
        {
            foreach (var window in appendable)
            {
                builder.Append(window.Handle).Append(':')
                    .Append(window.Title).Append(':')
                    .Append(window.ProcessName).Append(':')
                    .Append(window.Minimized).Append(':')
                    .Append(window.InScene).Append(':')
                    .Append(window.InOtherScene).Append(';');
            }
        }
        return builder.ToString();
    }

    /// <summary>标识参与数据签名：改颜色 / 数字 / 位置也要触发重绘。</summary>
    private static string BadgeKey(SceneBadge badge)
        => badge == null ? "-" : $"{badge.Color}/{badge.Number}/{(badge.Right ? "right" : "left")}";

    private static FrameworkElement SectionTitle(string text)
    {
        var title = Ui.Text(text, 11, FontWeights.SemiBold, Theme.TextSecondary);
        title.Margin = new Thickness(2, 0, 0, 0);
        return title;
    }

    private static Border MakeDanger(string text, Action onClick)
    {
        var border = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Theme.DangerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 4, 11, 4),
            Child = Ui.Text(text, 11.5, FontWeights.Normal, Theme.DangerBrush),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        border.MouseEnter += (_, _) => border.Background = Theme.PanelCardHover;
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonDown += (_, e) => e.Handled = true;
        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return border;
    }
}
