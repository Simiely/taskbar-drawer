using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using TaskbarDrawer.Core;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.UI;

/// <summary>
/// 排除管理：在主面板右侧展开的非模态小窗。
/// 列出不参与单窗口模式的应用——内置资源管理器默认勾选、可关不可删，其余项由用户添加 / 删除。
/// 只读数据 + 抛事件，真正写配置和生效留在 AppShell；窗口开着时改一项立刻生效。
/// </summary>
public sealed class ExclusionWindow : Window
{
    private readonly StackPanel _ruleList = new();
    private readonly StackPanel _pickerList = new();
    private readonly StackPanel _pickerSection = new()
    {
        Margin = new Thickness(0, 12, 0, 0),
        Visibility = Visibility.Collapsed
    };
    private readonly TextBlock _summary;
    private readonly TextBlock _pickerEmpty;
    private TextBlock _pickerToggleLabel;
    private ScrollViewer _scroll;
    private bool _pickerOpen;

    /// <summary>展开候选前的高度：收起时还回去，避免窗口越撑越高。</summary>
    private double _collapsedHeight = 560;

    private const double BaseHeight = 560;

    public ExclusionWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        Width = 420;
        Height = BaseHeight;
        MinWidth = 360;
        MinHeight = 320;
        MaxWidth = 700;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _summary = Ui.Text("", 10.5, FontWeights.Normal, Theme.TextMuted);

        _pickerEmpty = Ui.Text("当前没有可添加的窗口。",
            11, FontWeights.Normal, Theme.TextMuted, wrap: true);
        _pickerEmpty.Margin = new Thickness(2, 8, 0, 0);
        _pickerEmpty.Visibility = Visibility.Collapsed;

        _pickerToggleLabel = Ui.Text("", 11.5, FontWeights.Normal, Theme.TextPrimary);

        Content = BuildRoot();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    /// <summary>勾选 / 取消第 index 项（内置项也在其中）。</summary>
    public event Action<int, bool> EnabledRequested;

    /// <summary>删除第 index 项（内置项不可删除，界面不提供入口）。</summary>
    public event Action<int> DeleteRequested;

    /// <summary>把一个应用加进排除列表：exe 全路径（可能为空）+ 进程文件名。</summary>
    public event Action<string, string> AddRequested;

    /// <summary>重新枚举「已打开的窗口」候选。</summary>
    public event Action RefreshRequested;

    /// <summary>由 AppShell 推送最新数据；重建后保留滚动位置。</summary>
    public void Reload(
        IReadOnlyList<WindowExclusion> rules,
        IReadOnlyList<ExclusionCandidate> candidates)
    {
        var items = rules ?? Array.Empty<WindowExclusion>();
        var apps = candidates ?? Array.Empty<ExclusionCandidate>();

        double offset = _scroll?.VerticalOffset ?? 0;

        _ruleList.Children.Clear();
        int enabledCount = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Enabled) enabledCount++;
            _ruleList.Children.Add(BuildRuleRow(items[i], i));
        }
        _summary.Text = $"共 {items.Count} 项 · 已排除 {enabledCount} 项";

        _pickerList.Children.Clear();
        for (int i = 0; i < apps.Count; i++)
            _pickerList.Children.Add(BuildCandidateRow(apps[i]));
        _pickerEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_scroll != null)
        {
            _scroll.UpdateLayout();
            _scroll.ScrollToVerticalOffset(offset);
        }
    }

    /// <summary>贴在主面板旁边：优先放右侧，放不下自动换到左侧。</summary>
    public void PlaceBeside(Window anchor) => Ui.PlaceBeside(this, anchor);

    /// <summary>
    /// 首次打开就展开候选列表：用户进排除管理就是为了加窗口，
    /// 不该先点一轮「添加已打开的窗口」再自己找列表在哪。
    /// </summary>
    public void ExpandPicker()
    {
        if (_pickerOpen) return;
        TogglePicker();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.ApplyToolWindowStyle(new WindowInteropHelper(this).Handle);
    }

    // ---------- 布局 ----------

    private FrameworkElement BuildRoot()
    {
        var stack = new StackPanel { Margin = new Thickness(18, 4, 18, 16) };
        stack.Children.Add(Ui.DragHandle(this));
        stack.Children.Add(BuildHeader());
        stack.Children.Add(BuildIntro());
        stack.Children.Add(BuildRuleSection());
        stack.Children.Add(Ui.Divider());
        stack.Children.Add(BuildPickerSection());
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
        host.Children.Add(Ui.ResizeGrip(this, "拖动调整窗口大小"));

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

    private FrameworkElement BuildHeader()
    {
        var grid = new Grid { Background = Brushes.Transparent, MinHeight = 32 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel();
        texts.Children.Add(Ui.Text("排除管理", 15, FontWeights.SemiBold, Theme.TextPrimary));
        var caption = Ui.Text("不参与单窗口模式的应用", 10.5, FontWeights.Normal, Theme.TextMuted);
        caption.Margin = new Thickness(0, 3, 0, 0);
        texts.Children.Add(caption);
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

    private static FrameworkElement BuildIntro()
    {
        var text = Ui.Text(
            "被排除的应用不会触发单窗口模式：它成为前台时不收起场景、也不会被最小化；" +
            "它已经打开的窗口同样不会被最小化。",
            10.5, FontWeights.Normal, Theme.TextSecondary, wrap: true);
        text.Margin = new Thickness(2, 14, 0, 0);
        return text;
    }

    private FrameworkElement BuildRuleSection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Ui.Text("已排除", 11, FontWeights.SemiBold, Theme.TextSecondary);
        title.Margin = new Thickness(2, 0, 0, 0);
        head.Children.Add(title);
        _summary.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_summary, 1);
        head.Children.Add(_summary);
        panel.Children.Add(head);

        _ruleList.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_ruleList);
        return panel;
    }

    private FrameworkElement BuildPickerSection()
    {
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var add = Ui.Id(Ui.ActionButton("＋ 添加已打开的窗口", TogglePicker,
            accent: false, enabled: true, out _pickerToggleLabel), "exclusions.add");
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.ToolTip = "从当前打开的窗口里选一个应用加进排除列表";
        Grid.SetColumn(add, 0);
        head.Children.Add(add);

        var refresh = Ui.Id(Ui.ActionButton("刷新", () => RefreshRequested?.Invoke()), "exclusions.refresh");
        refresh.ToolTip = "重新扫描当前打开的窗口";
        Grid.SetColumn(refresh, 1);
        head.Children.Add(refresh);
        _pickerSection.Children.Add(head);

        var hint = Ui.Text("按应用生效：同一个程序关掉重开窗口后仍然排除。",
            10.5, FontWeights.Normal, Theme.TextMuted, wrap: true);
        hint.Margin = new Thickness(2, 8, 0, 0);
        _pickerSection.Children.Add(hint);

        _pickerList.Margin = new Thickness(0, 4, 0, 0);
        _pickerSection.Children.Add(_pickerList);
        _pickerSection.Children.Add(_pickerEmpty);
        return _pickerSection;
    }

    private FrameworkElement BuildFooter()
    {
        var stack = new StackPanel();
        var note = Ui.Text("属于场景的窗口仍按场景收起 / 展开，不受这里的排除影响。",
            10.5, FontWeights.Normal, Theme.TextMuted, wrap: true);
        note.Margin = new Thickness(2, 12, 0, 0);
        stack.Children.Add(note);

        var done = Ui.Id(Ui.ActionButton("完成", Close, accent: true), "exclusions.done");
        done.HorizontalAlignment = HorizontalAlignment.Right;
        done.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(done);
        return stack;
    }

    /// <summary>展开 / 收起候选列表；展开时顺手重新扫一遍窗口。</summary>
    private void TogglePicker()
    {
        _pickerOpen = !_pickerOpen;
        _pickerSection.Visibility = _pickerOpen ? Visibility.Visible : Visibility.Collapsed;
        _pickerToggleLabel.Text = _pickerOpen ? "收起窗口列表" : "＋ 添加已打开的窗口";
        if (!_pickerOpen)
        {
            Height = Math.Max(MinHeight, _collapsedHeight);
            return;
        }

        _collapsedHeight = Height;
        RefreshRequested?.Invoke();

        // 候选行刚加进视觉树，这一刻 ExtentHeight 还是展开前的值。
        // 等这一轮布局跑完再量高度、再滚到列表开头，否则窗口不会长高，
        // 用户会对着一个没变化的小窗以为「列表是空的」。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            GrowToShowPicker();
            _pickerSection.BringIntoView();
        }), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 把窗口撑到能看见候选列表：候选多时宁可让窗口变高，
    /// 也不要让用户对着一个没变化的小窗自己找滚动条。
    /// 上限是窗口当前所在屏的工作区，再高就交给窗内滚动。
    /// </summary>
    private void GrowToShowPicker()
    {
        if (_scroll == null) return;
        UpdateLayout();

        // Viewport 之外还有边框 / 滚动条，换算成同样的外框高度一并加上。
        double chrome = Math.Max(0, ActualHeight - _scroll.ViewportHeight);
        double wanted = _scroll.ExtentHeight + chrome;

        double workTop = double.NaN;
        double workBottom = double.NaN;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var rect) &&
            NativeMethods.TryGetMonitorOfRect(rect, out var monitor))
        {
            double scale = NativeMethods.DpiOfDevice(monitor.szDevice) / 96.0;
            workTop = monitor.rcWork.Top / scale;
            workBottom = monitor.rcWork.Bottom / scale;
        }

        double limit = double.IsNaN(workTop)
            ? SystemParameters.WorkArea.Height
            : workBottom - workTop - 40;
        double height = Math.Max(MinHeight, Math.Min(wanted, Math.Max(MinHeight, limit)));
        if (height > Height + 0.5) Height = height;

        // 变高以后可能顶出屏幕底部：整体上移到工作区内。
        if (!double.IsNaN(workBottom) && Top + Height > workBottom - 8)
            Top = Math.Max(workTop + 8, workBottom - Height - 8);
    }

    private Button BuildRuleRow(WindowExclusion rule, int index)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = rule.Enabled ? Theme.SuccessBrush : Theme.TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var texts = new StackPanel();
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
        var title = Ui.Text(rule.DisplayName, 12, FontWeights.SemiBold, Theme.TextPrimary);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        titleLine.Children.Add(title);
        if (rule.BuiltIn)
        {
            titleLine.Children.Add(new Border
            {
                Background = Theme.ControlBackground,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = Ui.Text("内置", 9.5, FontWeights.Normal, Theme.TextSecondary)
            });
        }
        texts.Children.Add(titleLine);

        var sub = Ui.Text(
            rule.BuiltIn ? "默认排除；可以关闭，但不能删除" : PathOf(rule),
            10.5, FontWeights.Normal, Theme.TextMuted);
        sub.Margin = new Thickness(0, 2, 0, 0);
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        if (!string.IsNullOrWhiteSpace(rule.Path)) sub.ToolTip = rule.Path;
        texts.Children.Add(sub);

        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        if (!rule.BuiltIn)
        {
            var delete = Ui.DangerButton("删除", () => DeleteRequested?.Invoke(index));
            delete.Margin = new Thickness(8, 0, 8, 0);
            delete.ToolTip = "从排除列表里删掉这个应用";
            Grid.SetColumn(delete, 2);
            grid.Children.Add(delete);
        }

        var toggle = new Switch();
        toggle.Set(rule.Enabled);
        Grid.SetColumn(toggle.Root, 3);
        grid.Children.Add(toggle.Root);

        var card = Ui.CardButton(grid, () =>
        {
            bool next = !rule.Enabled;
            toggle.Set(next);
            EnabledRequested?.Invoke(index, next);
        }, "点这一行开启 / 关闭排除", new Thickness(11, 8, 10, 8), rule.DisplayName);
        card.Margin = new Thickness(0, 5, 0, 0);
        return Ui.Id(card, $"exclusions.rule.{index}");
    }

    private Border BuildCandidateRow(ExclusionCandidate candidate)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel();
        var title = Ui.Text(candidate.DisplayName, 12, FontWeights.SemiBold, Theme.TextPrimary);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        texts.Children.Add(title);

        string meta = candidate.WindowCount > 1
            ? $"{candidate.ProcessName} · {candidate.WindowCount} 个窗口"
            : candidate.ProcessName;
        var sub = Ui.Text(meta, 10.5, FontWeights.Normal, Theme.TextMuted);
        sub.Margin = new Thickness(0, 2, 0, 0);
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        if (!string.IsNullOrWhiteSpace(candidate.Path)) sub.ToolTip = candidate.Path;
        texts.Children.Add(sub);

        Grid.SetColumn(texts, 0);
        grid.Children.Add(texts);

        var add = Ui.ActionButton(
            candidate.Excluded ? "已排除" : "添加",
            () => AddRequested?.Invoke(candidate.Path, candidate.ProcessName),
            accent: !candidate.Excluded,
            enabled: !candidate.Excluded);
        add.Margin = new Thickness(8, 0, 0, 0);
        if (!candidate.Excluded) add.ToolTip = "把这个应用加进排除列表；重开窗口仍然有效";
        Grid.SetColumn(add, 1);
        grid.Children.Add(add);

        return new Border
        {
            Background = Theme.PanelCard,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 8, 7),
            Margin = new Thickness(0, 5, 0, 0),
            Child = grid
        };
    }

    private static string PathOf(WindowExclusion rule)
        => string.IsNullOrWhiteSpace(rule.Path) ? rule.ProcessName : rule.Path;

    /// <summary>iOS 风格的小开关：轨道变色 + 圆点靠右，纯视觉，点击由整行卡片处理。</summary>
    private sealed class Switch
    {
        private const double TrackHeight = 22;
        private const double KnobSize = 18;

        private readonly Border _track;
        private readonly Ellipse _knob;

        public Switch()
        {
            _knob = new Ellipse
            {
                Width = KnobSize,
                Height = KnobSize,
                Fill = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2)
            };
            _track = new Border
            {
                Width = 38,
                Height = TrackHeight,
                CornerRadius = new CornerRadius(TrackHeight / 2),
                Background = Theme.SwitchTrackOff,
                Child = _knob,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        public Border Root => _track;

        public void Set(bool value)
        {
            _track.Background = value ? Theme.SuccessBrush : Theme.SwitchTrackOff;
            _knob.HorizontalAlignment = value
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Left;
        }
    }
}
