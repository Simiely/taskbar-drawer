using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using TaskbarDrawer.Core;
using TaskbarDrawer.Interop;
using Forms = System.Windows.Forms;

namespace TaskbarDrawer.UI;

/// <summary>
/// 主面板（参照 Apple HIG 的深色语义配色）：打开时置顶，贴右下角弹出。
/// 内容分三块——分组（逐组展开 / 收起，组常驻）、开关（iOS 风格）、收起热键（查看 + 录制修改）。
/// 关闭面板只隐藏，不退出程序；按 Esc 也能收起。
/// </summary>
public sealed class MainPanelWindow : Window
{
    private const double PanelWidth = 400;
    private const double EstimatedHeight = 780;

    // Apple 深色语义色（systemBackground / secondarySystemBackground / systemBlue …）
    private static readonly SolidColorBrush PanelBackground = Frozen(0xF7, 0x1C, 0x1C, 0x1E);
    private static readonly SolidColorBrush PanelBorder = Frozen(0xFF, 0x38, 0x38, 0x3A);
    private static readonly SolidColorBrush CardBackground = Frozen(0xFF, 0x2C, 0x2C, 0x2E);
    private static readonly SolidColorBrush CardHover = Frozen(0xFF, 0x3A, 0x3A, 0x3C);
    private static readonly SolidColorBrush ControlBackground = Frozen(0xFF, 0x3A, 0x3A, 0x3C);
    private static readonly SolidColorBrush ControlHover = Frozen(0xFF, 0x48, 0x48, 0x4A);
    private static readonly SolidColorBrush TextPrimary = Frozen(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush TextSecondary = Frozen(0xFF, 0x98, 0x98, 0x9D);
    private static readonly SolidColorBrush TextMuted = Frozen(0xFF, 0x8E, 0x8E, 0x93);
    private static readonly SolidColorBrush AccentBrush = Frozen(0xFF, 0x0A, 0x84, 0xFF);
    private static readonly SolidColorBrush SuccessBrush = Frozen(0xFF, 0x34, 0xC7, 0x59);
    private static readonly SolidColorBrush DangerBrush = Frozen(0xFF, 0xFF, 0x45, 0x3A);
    private static readonly SolidColorBrush DividerBrush = Frozen(0xFF, 0x38, 0x38, 0x3A);
    private static readonly SolidColorBrush SwitchTrackOff = Frozen(0xFF, 0x39, 0x39, 0x3D);

    private readonly StackPanel _groupList = new();
    private readonly StackPanel _savedList = new();
    private readonly TextBlock _summary;
    private readonly TextBlock _emptyHint;
    private readonly TextBlock _savedEmpty;
    private readonly ToggleRow _singleRow;
    private readonly ToggleRow _autoStartRow;
    private readonly TextBlock _stashHotkeyValue;
    private readonly TextBlock _stashEditLabel;
    private readonly TextBlock _status;

    private bool _recording;

    public MainPanelWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Height;
        Width = PanelWidth;

        _summary = Text("", 11, FontWeights.Normal, TextSecondary);

        _emptyHint = Text("还没有收纳任何窗口。按热键把屏幕上的窗口收成一组，之后点任务栏胶囊就能展开。",
            11.5, FontWeights.Normal, TextMuted);
        _emptyHint.TextWrapping = TextWrapping.Wrap;
        _emptyHint.Margin = new Thickness(2, 8, 0, 0);

        _savedEmpty = Text("没有已关闭的场景。把一组窗口「存储编组」再「关闭窗口」，场景就留在这里，点一下就能按原位置打开。",
            11.5, FontWeights.Normal, TextMuted);
        _savedEmpty.TextWrapping = TextWrapping.Wrap;
        _savedEmpty.Margin = new Thickness(2, 8, 0, 0);

        _singleRow = new ToggleRow("单窗口模式", "切换到某个窗口时，其余窗口自动最小化",
            value => SingleWindowRequested?.Invoke(value));
        _autoStartRow = new ToggleRow("开机自启", "登录后自动常驻托盘",
            value => AutoStartRequested?.Invoke(value));

        _stashHotkeyValue = Text("Ctrl+Alt+Shift+D", 11, FontWeights.SemiBold, TextPrimary);
        _status = Text("", 11, FontWeights.Normal, TextMuted);
        _status.Margin = new Thickness(2, 10, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;

        var stashEdit = CreateAction("修改", () => StartRecording(), out _stashEditLabel);

        Content = BuildRoot(stashEdit);

        // 每次隐藏（✕ / Esc / 失焦关闭）都记下当前位置：
        // 下次打开就回到这里，而不是永远弹回右下角。
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) return;
            if (double.IsNaN(Left) || double.IsNaN(Top)) return;
            SavedPosition = (Left, Top);
            PositionChanged?.Invoke(Left, Top);
        };
    }

    public event Action StashRequested;
    public event Action<int> ToggleGroupRequested;
    public event Action<int> CloseGroupRequested;
    public event Action<int> SaveGroupRequested;
    public event Action<int> GroupRenameRequested;
    public event Action<string> SavedToggleRequested;
    public event Action<string> SavedRenameRequested;
    public event Action<string> SavedDeleteRequested;
    public event Action ExpandLatestRequested;
    public event Action ExpandAllRequested;
    public event Action<bool> SingleWindowRequested;
    public event Action<bool> AutoStartRequested;
    public event Action<uint, uint> HotkeyChangeRequested;

    /// <summary>面板隐藏时抛出当前位置（DIP），由组合根写入配置。</summary>
    public event Action<double, double> PositionChanged;

    /// <summary>上次关闭时记住的位置；null 表示还没记录过（按光标所在屏右下角弹出）。</summary>
    public (double Left, double Top)? SavedPosition { get; set; }

    /// <summary>正在录制新热键：此时不要响应系统热键，避免把按键操作吞掉。</summary>
    public bool Recording => _recording;

    /// <summary>刷新分组列表（每次收纳 / 展开 / 收起 / 外部关闭后由 AppShell 调用）。</summary>
    public void SetGroups(IReadOnlyList<StashGroupSummary> groups, int total)
    {
        _groupList.Children.Clear();
        foreach (var group in groups)
            _groupList.Children.Add(BuildGroupRow(group));

        _emptyHint.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _summary.Text = total > 0 ? $"{total} 个窗口 · {groups.Count} 个编组" : "";
    }

    /// <summary>刷新场景列表（存储 / 重命名 / 关闭 / 解散后由 AppShell 调用）。</summary>
    public void SetSavedGroups(IReadOnlyList<SavedGroupSummary> saved)
    {
        _savedList.Children.Clear();
        foreach (var group in saved)
            _savedList.Children.Add(BuildSavedRow(group));

        _savedEmpty.Visibility = saved.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetSingleWindow(bool value) => _singleRow.Set(value);
    public void SetAutoStart(bool value) => _autoStartRow.Set(value);

    public void SetHotkeyText(string gesture) => _stashHotkeyValue.Text = gesture;

    /// <summary>改键结果回填：成功显示新组合，失败用错误色说明原因。</summary>
    public void ApplyHotkeyResult(bool success, string gesture, string error)
    {
        StopRecording();
        if (success)
        {
            SetHotkeyText(gesture);
            _status.Foreground = TextMuted;
            _status.Text = $"已改为 {gesture}";
        }
        else
        {
            _status.Foreground = DangerBrush;
            _status.Text = error;
        }
    }

    /// <summary>
    /// 打开面板：优先回到上次关闭时的位置；没有记录或位置已不在屏幕内时，贴光标所在屏右下角。
    /// 置顶状态由窗体自身保证。
    /// </summary>
    public void ShowPanel()
    {
        if (!IsVisible)
        {
            bool restored = TryRestorePosition();
            Show();
            UpdateLayout();
            if (restored) ClampToWorkArea();
            else PositionCorner();
        }
        Activate();
    }

    public void HidePanel() => Hide();

    /// <summary>退出程序时置 true：允许真正关闭窗口，否则 Closing 一律转成隐藏。</summary>
    public bool AllowClose { get; set; }

    /// <summary>把上一次的位置套上去；没记录返回 false。</summary>
    private bool TryRestorePosition()
    {
        var saved = SavedPosition;
        if (saved == null) return false;
        Left = saved.Value.Left;
        Top = saved.Value.Top;
        return true;
    }

    /// <summary>恢复的位置可能在屏幕外（换分辨率 / 拔掉副屏）：拉回最近的可用区域。</summary>
    private void ClampToWorkArea()
    {
        double height = ActualHeight > 0 ? ActualHeight : EstimatedHeight;
        var center = new System.Drawing.Point((int)(Left + Width / 2), (int)(Top + height / 2));
        var area = Forms.Screen.FromPoint(center).WorkingArea;
        Rect work = new Rect(area.Left, area.Top, area.Width, area.Height);

        Left = Math.Min(Math.Max(Left, work.Left + 8), Math.Max(work.Left + 8, work.Right - Width - 12));
        Top = Math.Min(Math.Max(Top, work.Top + 8), Math.Max(work.Top + 8, work.Bottom - height - 12));
    }

    private void PositionCorner()
    {
        // 跟着鼠标所在的显示器走：多屏下点哪块屏的托盘 / 胶囊，面板就出现在哪块屏，
        // 不再永远贴主屏右下角（副屏用户会以为面板「跑到别处去了」）。
        System.Drawing.Point cursor = Forms.Cursor.Position;
        var area = Forms.Screen.FromPoint(cursor).WorkingArea;
        Rect work = new Rect(area.Left, area.Top, area.Width, area.Height);

        double height = ActualHeight > 0 ? ActualHeight : EstimatedHeight;
        Left = Math.Max(work.Left + 8, work.Right - Width - 12);
        Top = Math.Max(work.Top + 8, work.Bottom - height - 12);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 面板是普通可激活窗口，但用工具窗样式保证不占 Alt+Tab 和任务栏。
        NativeMethods.ApplyToolWindowStyle(new WindowInteropHelper(this).Handle);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // 录制中点了别的窗口：面板收不到后续按键，但 _recording 会一直为 true，
        // 结果热键被永久忽略、用户以为程序坏了。这里主动收尾。
        if (!_recording) return;
        StopRecording();
        _status.Foreground = TextMuted;
        _status.Text = "已取消修改";
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 关闭 = 隐藏：常驻程序只能从托盘菜单退出（退出时 AllowClose=true 放行）。
        if (AllowClose) return;
        e.Cancel = true;
        Hide();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (!_recording)
        {
            if (e.Key == Key.Escape) Hide();
            return;
        }

        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            StopRecording();
            _status.Foreground = TextMuted;
            _status.Text = "已取消修改";
            return;
        }

        if (IsModifierKey(key) || e.IsRepeat)
        {
            _status.Foreground = TextMuted;
            _status.Text = "继续按一个非修饰键（如 S、F2、Space）";
            return;
        }

        uint modifiers = ModifierBits();
        if ((modifiers & (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_WIN)) == 0)
        {
            _status.Foreground = DangerBrush;
            _status.Text = "快捷键至少要包含 Ctrl / Alt / Win 之一";
            return;
        }

        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return;
        HotkeyChangeRequested?.Invoke(modifiers, virtualKey);
    }

    private void StartRecording()
    {
        _recording = true;
        _stashEditLabel.Text = "按下…";
        _stashEditLabel.Foreground = AccentBrush;
        _status.Foreground = TextMuted;
        _status.Text = "按下新的组合键（需含 Ctrl / Alt / Win），Esc 取消";
    }

    private void StopRecording()
    {
        _recording = false;
        _stashEditLabel.Text = "修改";
        _stashEditLabel.Foreground = TextPrimary;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static uint ModifierBits()
    {
        uint modifiers = 0;
        ModifierKeys keys = Keyboard.Modifiers;
        if ((keys & ModifierKeys.Control) != 0) modifiers |= NativeMethods.MOD_CONTROL;
        if ((keys & ModifierKeys.Alt) != 0) modifiers |= NativeMethods.MOD_ALT;
        if ((keys & ModifierKeys.Shift) != 0) modifiers |= NativeMethods.MOD_SHIFT;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) modifiers |= NativeMethods.MOD_WIN;
        return modifiers;
    }

    // ---------- 布局 ----------

    private FrameworkElement BuildRoot(Border stashEdit)
    {
        var root = new Border
        {
            Background = PanelBackground,
            BorderBrush = PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Effect = new DropShadowEffect
            {
                BlurRadius = 34,
                ShadowDepth = 8,
                Opacity = 0.55,
                Color = Colors.Black
            }
        };

        var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        stack.Children.Add(BuildHeader());
        stack.Children.Add(BuildGroups());
        stack.Children.Add(BuildSavedGroups());
        stack.Children.Add(Divider());
        stack.Children.Add(BuildToggles());
        stack.Children.Add(Divider());
        stack.Children.Add(BuildHotkeys(stashEdit));

        // 组多起来后整块面板会超过屏幕高度：整体套一层滚动，保证最下面的开关也够得着。
        root.Child = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 40)
        };
        return root;
    }

    private FrameworkElement BuildHeader()
    {
        var grid = new Grid { Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel();
        texts.Children.Add(Text("任务栏收纳", 16, FontWeights.SemiBold, TextPrimary));
        var hint = Text("每组一枚胶囊贴在任务栏 · 左键收起 / 展开，右键管理", 10.5, FontWeights.Normal, TextMuted);
        hint.Margin = new Thickness(0, 3, 0, 0);
        texts.Children.Add(hint);
        Grid.SetColumn(texts, 0);
        grid.Children.Add(texts);

        var close = CreateAction("✕", Hide);
        close.Background = Brushes.Transparent;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.MouseEnter += (_, _) => close.Background = CardHover;
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        Grid.SetColumn(close, 1);
        grid.Children.Add(close);

        // 空白处按住即可拖动面板（关闭按钮自己吃掉了 MouseDown，不会误触拖动）。
        grid.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { /* 已松手 */ }
        };
        return grid;
    }

    private FrameworkElement BuildGroups()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Text("编组", 11, FontWeights.SemiBold, TextSecondary);
        title.Margin = new Thickness(2, 0, 0, 0);
        head.Children.Add(title);
        _summary.HorizontalAlignment = HorizontalAlignment.Right;
        _summary.Margin = new Thickness(0, 0, 2, 0);
        Grid.SetColumn(_summary, 1);
        head.Children.Add(_summary);
        panel.Children.Add(head);

        _groupList.Margin = new Thickness(0, 8, 0, 0);

        // 组多了会把面板顶出屏幕：给列表一个高度上限并允许滚动，否则底部几组永远点不到。
        var scroll = new ScrollViewer
        {
            Content = _groupList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(180, SystemParameters.WorkArea.Height * 0.45)
        };
        panel.Children.Add(scroll);
        panel.Children.Add(_emptyHint);

        // 三个动作会自动换行，窄屏 / 大字号下也不会挤出面板。
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var stashAction = CreateAction("把屏幕上的窗口收成一组", () => StashRequested?.Invoke(), accent: true);
        stashAction.Margin = new Thickness(0, 0, 8, 6);
        stashAction.ToolTip = "把当前屏幕上可见的窗口全部收进新的一组，任务栏留一枚胶囊";
        actions.Children.Add(stashAction);
        var latestAction = CreateAction("展开最近一组", () => ExpandLatestRequested?.Invoke());
        latestAction.Margin = new Thickness(0, 0, 8, 6);
        actions.Children.Add(latestAction);
        var allAction = CreateAction("全部展开", () => ExpandAllRequested?.Invoke());
        allAction.Margin = new Thickness(0, 0, 8, 6);
        actions.Children.Add(allAction);
        panel.Children.Add(actions);
        return panel;
    }

    private FrameworkElement BuildSavedGroups()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };

        var title = Text("已关闭的场景", 11, FontWeights.SemiBold, TextSecondary);
        title.Margin = new Thickness(2, 0, 0, 0);
        panel.Children.Add(title);

        _savedList.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(new ScrollViewer
        {
            Content = _savedList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(120, SystemParameters.WorkArea.Height * 0.28)
        });
        panel.Children.Add(_savedEmpty);

        var note = Text("场景 = 保存过的一组软件和窗口位置。运行中它和编组一样：点胶囊收起 / 展开；「关闭窗口」（或窗口自己的 ×）才是真的关掉，关完后胶囊留下，点一下按保存的位置重新打开。",
            10.5, FontWeights.Normal, TextMuted);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(2, 8, 0, 0);
        panel.Children.Add(note);
        return panel;
    }

    private FrameworkElement BuildToggles()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var title = Text("开关", 11, FontWeights.SemiBold, TextSecondary);
        title.Margin = new Thickness(2, 0, 0, 0);
        panel.Children.Add(title);

        _singleRow.Root.Margin = new Thickness(0, 8, 0, 0);
        _autoStartRow.Root.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_singleRow.Root);
        panel.Children.Add(_autoStartRow.Root);

        var note = Text("单窗口模式开启时，展开一组也只保留最后激活的那个窗口，其余自动最小化。",
            10.5, FontWeights.Normal, TextMuted);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(2, 10, 0, 0);
        panel.Children.Add(note);
        return panel;
    }

    private FrameworkElement BuildHotkeys(Border stashEdit)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var title = Text("快捷键", 11, FontWeights.SemiBold, TextSecondary);
        title.Margin = new Thickness(2, 0, 0, 0);
        panel.Children.Add(title);
        panel.Children.Add(BuildHotkeyRow("收起 / 展开", _stashHotkeyValue, stashEdit));
        panel.Children.Add(_status);

        var note = Text("托盘左键单击图标打开主面板。编组胶囊右键：取消编组 / 存储编组 / 重命名；场景胶囊右键：重命名 / 关闭窗口 / 解散场景。",
            10.5, FontWeights.Normal, TextMuted);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(2, 10, 0, 0);
        panel.Children.Add(note);
        return panel;
    }

    private static FrameworkElement BuildHotkeyRow(string title, TextBlock value, Border edit)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = Text(title, 12, FontWeights.Normal, TextPrimary);
        name.Margin = new Thickness(2, 0, 0, 0);
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        var keyCap = new Border
        {
            Background = ControlBackground,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 3, 9, 4),
            Child = value,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        Grid.SetColumn(keyCap, 1);
        grid.Children.Add(keyCap);

        edit.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(edit, 2);
        grid.Children.Add(edit);
        return grid;
    }

    private Border BuildGroupRow(StashGroupSummary group)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel();
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
        titleLine.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = group.Expanded ? SuccessBrush : TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 7, 0)
        });
        titleLine.Children.Add(Text(string.IsNullOrEmpty(group.Name)
            ? $"第 {group.Id} 组 · {group.Count} 个窗口"
            : group.SavedId != null
                ? $"★ {group.Name} · {group.Count} 个窗口"
                : $"{group.Name} · {group.Count} 个窗口", 12.5, FontWeights.SemiBold, TextPrimary));
        texts.Children.Add(titleLine);

        var subGrid = new Grid { Margin = new Thickness(14, 2, 8, 0) };
        subGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        subGrid.ColumnDefinitions.Add(new ColumnDefinition());
        subGrid.Children.Add(Text(group.State, 10.5, FontWeights.SemiBold,
            group.Expanded ? SuccessBrush : TextMuted));
        var titles = Text("· " + DescribeTitles(group), 10.5, FontWeights.Normal, TextMuted);
        titles.TextTrimming = TextTrimming.CharacterEllipsis;
        titles.Margin = new Thickness(5, 0, 0, 0);
        Grid.SetColumn(titles, 1);
        subGrid.Children.Add(titles);
        texts.Children.Add(subGrid);

        Grid.SetColumn(texts, 0);
        grid.Children.Add(texts);

        int id = group.Id;
        bool isScene = !string.IsNullOrEmpty(group.SavedId);

        // 按钮跟胶囊右键菜单完全一致：
        // 编组 = 取消编组 / 存储编组 / 重命名；场景 = 重命名 / 关闭窗口 / 解散场景。
        // 收起 / 展开仍然点这一行（见下面的 MouseLeftButtonUp）。
        Border first;
        Border second;
        Border third;

        if (isScene)
        {
            string savedId = group.SavedId;

            first = CreateAction("重命名", () => SavedRenameRequested?.Invoke(savedId));
            first.ToolTip = "重命名这个场景（任务栏胶囊同步显示新名字）";

            second = CreateAction("关闭窗口", () => CloseGroupRequested?.Invoke(id));
            second.ToolTip = "关闭这一组自己的窗口（等于逐个点 ×）；场景胶囊保留，之后还能打开";

            third = CreateAction("解散场景", () => SavedDeleteRequested?.Invoke(savedId));
            third.ToolTip = "只删除这个场景的记录，不会关闭正在运行的窗口";
        }
        else
        {
            first = CreateAction("取消编组", () => CloseGroupRequested?.Invoke(id));
            first.ToolTip = "解除这一组的管理关系，窗口原位留在桌面上，不会被关闭或最小化";

            second = CreateAction("存储编组", () => SaveGroupRequested?.Invoke(id));
            second.ToolTip = "给这一组起名并保存软件 + 窗口位置；之后关掉窗口也能一键打开";

            third = CreateAction("重命名", () => GroupRenameRequested?.Invoke(id));
            third.ToolTip = "给这一组起个显示名（重启后不保留；要留底请用「存储编组」）";
        }

        int column = 1;
        foreach (var action in new[] { first, second, third })
        {
            action.Margin = new Thickness(0, 0, 6, 0);
            action.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(action, column++);
            grid.Children.Add(action);
        }

        var row = new Border
        {
            Background = CardBackground,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 10, 9),
            Margin = new Thickness(0, 6, 0, 0),
            Child = grid,
            Cursor = Cursors.Hand,
            ToolTip = $"{group.CreatedAt:HH:mm} 收成一组 · 现在是{group.State}\n点这一行收起 / 展开"
        };
        row.MouseEnter += (_, _) => row.Background = CardHover;
        row.MouseLeave += (_, _) => row.Background = CardBackground;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ToggleGroupRequested?.Invoke(id);
        };
        return row;
    }

    private Border BuildSavedRow(SavedGroupSummary group)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel();
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
        titleLine.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 7, 0)
        });
        titleLine.Children.Add(Text("★ " + ShortName(group.Name), 12.5, FontWeights.SemiBold, TextPrimary));
        texts.Children.Add(titleLine);

        var sub = Text($"{group.State} · {group.Count} 个窗口 · 点这一行打开", 10.5, FontWeights.SemiBold, TextMuted);
        sub.Margin = new Thickness(14, 2, 0, 0);
        texts.Children.Add(sub);

        Grid.SetColumn(texts, 0);
        grid.Children.Add(texts);

        string id = group.Id;
        var rename = CreateAction("重命名", () => SavedRenameRequested?.Invoke(id));
        rename.Margin = new Thickness(0, 0, 6, 0);
        rename.VerticalAlignment = VerticalAlignment.Center;
        rename.ToolTip = "重命名这个场景（任务栏胶囊同步显示新名字）";
        Grid.SetColumn(rename, 1);
        grid.Children.Add(rename);

        var toggle = CreateAction("打开", () => SavedToggleRequested?.Invoke(id), accent: true);
        toggle.Margin = new Thickness(0, 0, 6, 0);
        toggle.VerticalAlignment = VerticalAlignment.Center;
        toggle.ToolTip = "启动这组软件，并摆回保存时的窗口位置";
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);

        var delete = CreateAction("解散场景", () => SavedDeleteRequested?.Invoke(id));
        delete.VerticalAlignment = VerticalAlignment.Center;
        delete.ToolTip = "只删除这个场景的记录，不会关闭任何窗口";
        Grid.SetColumn(delete, 3);
        grid.Children.Add(delete);

        var row = new Border
        {
            Background = CardBackground,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 10, 9),
            Margin = new Thickness(0, 6, 0, 0),
            Child = grid,
            Cursor = Cursors.Hand,
            ToolTip = group.Titles.Count == 0
                ? "场景保存的窗口记录\n点这一行打开"
                : string.Join("\n", group.Titles.Take(6)) + "\n点这一行打开"
        };
        row.MouseEnter += (_, _) => row.Background = CardHover;
        row.MouseLeave += (_, _) => row.Background = CardBackground;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SavedToggleRequested?.Invoke(id);
        };
        return row;
    }

    private static string ShortName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "未命名";
        return name.Length > 10 ? name[..10] + "…" : name;
    }

    private static string DescribeTitles(StashGroupSummary group)
    {
        if (group.Titles.Count == 0) return "（窗口已关闭）";
        string joined = string.Join(" · ", group.Titles.Take(2));
        return group.Titles.Count > 2 ? $"{joined} 等 {group.Titles.Count} 个" : joined;
    }

    private static FrameworkElement Divider() => new Border
    {
        Height = 1,
        Background = DividerBrush,
        Margin = new Thickness(0, 16, 0, 0)
    };

    private static TextBlock Text(string text, double size, FontWeight weight, Brush foreground) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI"),
        FontSize = size,
        FontWeight = weight,
        Foreground = foreground
    };

    private static Border CreateAction(string text, Action onClick, bool accent = false)
        => CreateAction(text, onClick, out _, accent);

    private static Border CreateAction(string text, Action onClick, out TextBlock label, bool accent = false)
    {
        label = Text(text, 11.5, accent ? FontWeights.SemiBold : FontWeights.Normal,
            accent ? Brushes.White : TextPrimary);
        var border = new Border
        {
            Background = accent ? AccentBrush : ControlBackground,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 5, 11, 5),
            Child = label,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        border.MouseEnter += (_, _) => border.Background = accent ? AccentBrush : ControlHover;
        border.MouseLeave += (_, _) => border.Background = accent ? AccentBrush : ControlBackground;
        border.MouseLeftButtonDown += (_, e) => e.Handled = true;
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

    /// <summary>一行带 iOS 风格开关的条目：左侧标题 + 说明，右侧 40×24 滑动开关。</summary>
    private sealed class ToggleRow
    {
        private const double TrackWidth = 40;
        private const double TrackHeight = 24;
        private const double KnobSize = 20;

        private readonly Border _track;
        private readonly Ellipse _knob;
        private readonly Action<bool> _changed;

        public ToggleRow(string title, string hint, Action<bool> changed)
        {
            _changed = changed;

            _knob = new Ellipse
            {
                Width = KnobSize,
                Height = KnobSize,
                Fill = Brushes.White,
                Margin = new Thickness(2),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _track = new Border
            {
                Width = TrackWidth,
                Height = TrackHeight,
                CornerRadius = new CornerRadius(TrackHeight / 2),
                Background = SwitchTrackOff,
                Child = _knob,
                VerticalAlignment = VerticalAlignment.Center
            };

            var texts = new StackPanel();
            texts.Children.Add(Text(title, 12, FontWeights.Normal, TextPrimary));
            var sub = Text(hint, 10.5, FontWeights.Normal, TextMuted);
            sub.Margin = new Thickness(0, 2, 8, 0);
            texts.Children.Add(sub);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(texts, 0);
            grid.Children.Add(texts);
            Grid.SetColumn(_track, 1);
            grid.Children.Add(_track);

            Root = new Border
            {
                Background = CardBackground,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 9, 12, 9),
                Child = grid,
                Cursor = Cursors.Hand
            };
            Root.MouseEnter += (_, _) => Root.Background = CardHover;
            Root.MouseLeave += (_, _) => Root.Background = CardBackground;
            Root.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Value = !Value;
                Apply();
                _changed(Value);
            };
        }

        public Border Root { get; }

        public bool Value { get; private set; }

        /// <summary>外部同步状态：只更新视觉，不再回调（避免设置时又被当成用户操作）。</summary>
        public void Set(bool value)
        {
            Value = value;
            Apply();
        }

        private void Apply()
        {
            _track.Background = Value ? SuccessBrush : SwitchTrackOff;
            _knob.HorizontalAlignment = Value ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
    }
}
