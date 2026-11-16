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
using static TaskbarDrawer.UI.Ui;

namespace TaskbarDrawer.UI;

/// <summary>
/// 主面板（参照 Apple HIG 的深色语义配色）：打开时置顶，贴右下角弹出。
/// 内容分三块——场景（运行中 + 已关闭，逐行收起 / 展开）、开关（iOS 风格）、新建场景热键（查看 + 录制修改）。
/// 关闭面板只隐藏，不退出程序；按 Esc 也能收起。
/// </summary>
public sealed class MainPanelWindow : Window
{
    private const double PanelWidth = 400;
    private const double EstimatedHeight = 780;

    private readonly StackPanel _sceneList = new();
    private ScrollViewer _rootScroll;
    private ScrollViewer _sceneScroll;
    private readonly TextBlock _summary;
    private readonly TextBlock _emptyHint;
    private readonly ToggleRow _singleRow;
    private readonly ToggleRow _autoStartRow;
    private readonly TextBlock _stashHotkeyValue;
    private readonly TextBlock _stashEditLabel;
    private readonly TextBlock _status;
    private Slider _appIconSlider;
    private TextBlock _appIconValue;
    private Slider _badgeSlider;
    private TextBlock _badgeValue;

    // 隐藏期间收到的数据：不建 UI，等 ShowPanel 时一次性补上。
    private IReadOnlyList<StashGroupSummary> _pendingGroups = Array.Empty<StashGroupSummary>();
    private IReadOnlyList<SavedGroupSummary> _pendingSaved = Array.Empty<SavedGroupSummary>();
    private int _pendingTotal;
    private bool _surfaceDirty;

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

        _summary = Text("", 11, FontWeights.Normal, Theme.TextSecondary);

        _emptyHint = Text("还没有场景。按热键或在下面点「新建场景」，把屏幕上的窗口收成一个场景。",
            11.5, FontWeights.Normal, Theme.TextMuted);
        _emptyHint.TextWrapping = TextWrapping.Wrap;
        _emptyHint.Margin = new Thickness(2, 8, 0, 0);

        _singleRow = new ToggleRow("单窗口模式", "同一场景的窗口算一个窗口，切走时整个场景收回",
            value => SingleWindowRequested?.Invoke(value));
        _autoStartRow = new ToggleRow("开机自启", "登录后自动常驻托盘",
            value => AutoStartRequested?.Invoke(value));

        _stashHotkeyValue = Text("Ctrl+Alt+Shift+D", 11, FontWeights.SemiBold, Theme.TextPrimary);
        _status = Text("", 11, FontWeights.Normal, Theme.TextMuted);
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
    public event Action<int> SceneToggleRequested;
    public event Action<int> SceneCloseRequested;
    public event Action<string> SavedToggleRequested;

    /// <summary>打开场景编辑（移除窗口 / 追加其他场景 / 重命名 / 删除）。</summary>
    public event Action<string> SavedEditRequested;
    public event Action ExpandLatestRequested;
    public event Action ExpandAllRequested;
    public event Action<bool> SingleWindowRequested;
    public event Action<bool> AutoStartRequested;
    public event Action<uint, uint> HotkeyChangeRequested;

    /// <summary>场景图标里程序原图标的大小（比例 0.55–1.0）。</summary>
    public event Action<double> AppIconScaleRequested;

    /// <summary>场景图标里场景标识的大小（比例 0.25–0.65）。</summary>
    public event Action<double> SceneBadgeScaleRequested;

    /// <summary>面板隐藏时抛出当前位置（DIP），由组合根写入配置。</summary>
    public event Action<double, double> PositionChanged;

    /// <summary>上次关闭时记住的位置；null 表示还没记录过（按光标所在屏右下角弹出）。</summary>
    public (double Left, double Top)? SavedPosition { get; set; }

    /// <summary>正在录制新热键：此时不要响应系统热键，避免把按键操作吞掉。</summary>
    public bool Recording => _recording;

    /// <summary>刷新场景列表（收纳 / 展开 / 收起 / 关闭 / 删除后由 AppShell 调用）。</summary>
    public void SetScenes(
        IReadOnlyList<StashGroupSummary> running,
        IReadOnlyList<SavedGroupSummary> dormant,
        int total)
    {
        _pendingGroups = running;
        _pendingSaved = dormant;
        _pendingTotal = total;
        if (IsVisible) ApplyScenes();
        else _surfaceDirty = true;
    }

    /// <summary>
    /// 面板隐藏时只记数据、不建行：展开 / 收起是热键高频路径，
    /// 实测全量重建要几毫秒，隐藏时做纯属浪费。
    /// </summary>
    private void ApplyScenes()
    {
        _sceneList.Children.Clear();
        foreach (var group in _pendingGroups)
            _sceneList.Children.Add(BuildSceneRow(group));
        foreach (var group in _pendingSaved)
            _sceneList.Children.Add(BuildClosedSceneRow(group));

        int count = _pendingGroups.Count + _pendingSaved.Count;
        _emptyHint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _summary.Text = _pendingTotal > 0 ? $"{_pendingTotal} 个窗口 · {count} 个场景" : "";
    }

    private void FlushSurface()
    {
        if (!_surfaceDirty) return;
        _surfaceDirty = false;
        ApplyScenes();
    }

    public void SetSingleWindow(bool value) => _singleRow.Set(value);
    public void SetAutoStart(bool value) => _autoStartRow.Set(value);

    /// <summary>把配置里的两个尺寸套到滑块上（0.8 → 80%）。</summary>
    public void SetSceneIconSizes(double appIconScale, double badgeScale)
    {
        _appIconSlider.Value = Math.Round(Math.Clamp(appIconScale, 0.55, 1.0) * 100);
        _appIconValue.Text = $"{_appIconSlider.Value:0}%";
        _badgeSlider.Value = Math.Round(Math.Clamp(badgeScale, 0.25, 0.65) * 100);
        _badgeValue.Text = $"{_badgeSlider.Value:0}%";
    }

    public void SetHotkeyText(string gesture) => _stashHotkeyValue.Text = gesture;

    /// <summary>改键结果回填：成功显示新组合，失败用错误色说明原因。</summary>
    public void ApplyHotkeyResult(bool success, string gesture, string error)
    {
        StopRecording();
        if (success)
        {
            SetHotkeyText(gesture);
            _status.Foreground = Theme.TextMuted;
            _status.Text = $"已改为 {gesture}";
        }
        else
        {
            _status.Foreground = Theme.DangerBrush;
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
            // 先补齐隐藏期间攒下的内容，UpdateLayout 才能量到正确高度。
            FlushSurface();
            Show();
            UpdateLayout();
            // 高度上限要按「面板将要出现的那块屏」重算：主屏 4K、副屏 1080p 时
            // 按主屏工作区算出来的高度会超出副屏，底部按钮点不到。
            // 恢复位置时窗口已经在目标屏上；首次弹出时窗口还在默认位置，用鼠标所在屏。
            if (!ApplyMonitorSizing(preferCursor: !restored))
                ApplyMonitorSizing(preferCursor: true);
            UpdateLayout();
            if (restored) ClampToWorkArea();
            else PositionCorner();
        }
        Activate();
    }

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

        // 用面板真实的物理矩形找所在显示器：DIP 坐标跨不同 DPI 的屏幕没有统一比例，
        // 先用面板自己的缩放把 DIP 换成物理，再取那块屏的 DPI 换回来，才能得到正确答案。
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MONITORINFOEX monitor;
        if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            if (!NativeMethods.TryGetMonitorOfRect(rect, out monitor)) return;
        }
        else if (!NativeMethods.TryGetMonitorOfPoint(CursorPoint(), out monitor)) return;

        Rect work = ToDip(monitor.rcWork, NativeMethods.DpiOfDevice(monitor.szDevice) / 96.0);
        Left = Math.Min(Math.Max(Left, work.Left + 8), Math.Max(work.Left + 8, work.Right - Width - 12));
        Top = Math.Min(Math.Max(Top, work.Top + 8), Math.Max(work.Top + 8, work.Bottom - height - 12));
    }

    private void PositionCorner()
    {
        // 跟着鼠标所在的显示器走：多屏下点哪块屏的托盘 / 胶囊，面板就出现在哪块屏，
        // 不再永远贴主屏右下角（副屏用户会以为面板「跑到别处去了」）。
        if (!NativeMethods.TryGetMonitorOfPoint(CursorPoint(), out var monitor)) return;

        // 换算必须用「目标屏」的 DPI：首次弹出时面板还在主屏，
        // 拿主屏缩放去换算副屏工作区会让面板整体偏移，甚至落到屏幕外。
        Rect work = ToDip(monitor.rcWork, NativeMethods.DpiOfDevice(monitor.szDevice) / 96.0);
        double height = ActualHeight > 0 ? ActualHeight : EstimatedHeight;
        Left = Math.Max(work.Left + 8, work.Right - Width - 12);
        Top = Math.Max(work.Top + 8, work.Bottom - height - 12);
    }

    /// <summary>
    /// 按目标显示器的工作区重算两处高度上限（主滚动区 / 场景列表）。
    /// 这两处原来用 SystemParameters.WorkArea（永远是主屏），小副屏上会把窗口顶出屏幕。
    /// </summary>
    /// <param name="preferCursor">true = 用鼠标所在屏（首次弹出 / 面板还没定位）；false = 用窗口当前所在屏。</param>
    private bool ApplyMonitorSizing(bool preferCursor)
    {
        NativeMethods.MONITORINFOEX monitor;
        if (preferCursor)
        {
            if (!NativeMethods.TryGetMonitorOfPoint(CursorPoint(), out monitor)) return false;
        }
        else
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect) ||
                !NativeMethods.TryGetMonitorOfRect(rect, out monitor))
                return false;
        }

        double workHeight = monitor.rcWork.Height / (NativeMethods.DpiOfDevice(monitor.szDevice) / 96.0);
        if (_rootScroll != null) _rootScroll.MaxHeight = Math.Max(320, workHeight - 40);
        if (_sceneScroll != null) _sceneScroll.MaxHeight = Math.Max(180, workHeight * 0.45);
        return true;
    }

    private static NativeMethods.POINT CursorPoint()
    {
        System.Drawing.Point cursor = Forms.Cursor.Position;
        return new NativeMethods.POINT { X = cursor.X, Y = cursor.Y };
    }

    /// <summary>物理像素矩形 → DIP 矩形。</summary>
    private static Rect ToDip(NativeMethods.RECT area, double scale)
        => new(area.Left / scale, area.Top / scale, area.Width / scale, area.Height / scale);

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
        _status.Foreground = Theme.TextMuted;
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
            _status.Foreground = Theme.TextMuted;
            _status.Text = "已取消修改";
            return;
        }

        if (IsModifierKey(key) || e.IsRepeat)
        {
            _status.Foreground = Theme.TextMuted;
            _status.Text = "继续按一个非修饰键（如 S、F2、Space）";
            return;
        }

        uint modifiers = ModifierBits();
        if ((modifiers & (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_WIN)) == 0)
        {
            _status.Foreground = Theme.DangerBrush;
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
        _stashEditLabel.Foreground = Theme.AccentBrush;
        _status.Foreground = Theme.TextMuted;
        _status.Text = "按下新的组合键（需含 Ctrl / Alt / Win），Esc 取消";
    }

    private void StopRecording()
    {
        _recording = false;
        _stashEditLabel.Text = "修改";
        _stashEditLabel.Foreground = Theme.TextPrimary;
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
            }
        };

        var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        stack.Children.Add(BuildHeader());
        stack.Children.Add(BuildScenes());
        stack.Children.Add(Divider());
        stack.Children.Add(BuildToggles());
        stack.Children.Add(BuildAppearance());
        stack.Children.Add(Divider());
        stack.Children.Add(BuildHotkeys(stashEdit));

        // 组多起来后整块面板会超过屏幕高度：整体套一层滚动，保证最下面的开关也够得着。
        // 初始值按主屏算，ShowPanel 时再按实际所在显示器重算（ApplyMonitorSizing）。
        _rootScroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 40)
        };
        root.Child = _rootScroll;
        return root;
    }

    private FrameworkElement BuildHeader()
    {
        var grid = new Grid { Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel();
        texts.Children.Add(Text("任务栏收纳", 16, FontWeights.SemiBold, Theme.TextPrimary));
        var hint = Text("⭐ 场景 = 保存的窗口组合 · 单窗口模式下每个场景算一个窗口", 10.5, FontWeights.Normal, Theme.TextMuted);
        hint.Margin = new Thickness(0, 3, 0, 0);
        texts.Children.Add(hint);
        Grid.SetColumn(texts, 0);
        grid.Children.Add(texts);

        var close = CreateAction("✕", Hide);
        close.Background = Brushes.Transparent;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.MouseEnter += (_, _) => close.Background = Theme.PanelCardHover;
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

    private FrameworkElement BuildScenes()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Text("场景", 11, FontWeights.SemiBold, Theme.TextSecondary);
        title.Margin = new Thickness(2, 0, 0, 0);
        head.Children.Add(title);
        _summary.HorizontalAlignment = HorizontalAlignment.Right;
        _summary.Margin = new Thickness(0, 0, 2, 0);
        Grid.SetColumn(_summary, 1);
        head.Children.Add(_summary);
        panel.Children.Add(head);

        _sceneList.Margin = new Thickness(0, 8, 0, 0);

        // 场景多了会把面板顶出屏幕：给列表一个高度上限并允许滚动，否则底部几行永远点不到。
        _sceneScroll = new ScrollViewer
        {
            Content = _sceneList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(180, SystemParameters.WorkArea.Height * 0.45)
        };
        panel.Children.Add(_sceneScroll);
        panel.Children.Add(_emptyHint);

        // 三个动作会自动换行，窄屏 / 大字号下也不会挤出面板。
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var stashAction = CreateAction("＋ 新建场景", () => StashRequested?.Invoke(), accent: true);
        stashAction.Margin = new Thickness(0, 0, 8, 6);
        stashAction.ToolTip = "把当前显示器上可见的窗口收成一个新场景并保存，任务栏留一枚 ⭐ 胶囊";
        actions.Children.Add(stashAction);
        var latestAction = CreateAction("展开最近的场景", () => ExpandLatestRequested?.Invoke());
        latestAction.Margin = new Thickness(0, 0, 8, 6);
        actions.Children.Add(latestAction);
        var allAction = CreateAction("全部展开", () => ExpandAllRequested?.Invoke());
        allAction.Margin = new Thickness(0, 0, 8, 6);
        actions.Children.Add(allAction);
        panel.Children.Add(actions);

        var note = Text("⭐ 场景 = 保存下来的「软件 + 位置」。运行中点这一行收起 / 展开；「关闭窗口」才真的关掉，关完后场景留下，点一下按保存的位置重新打开。行上的「编辑」可以换场景图标、移除某个窗口，或把别的场景追加进来。",
            10.5, FontWeights.Normal, Theme.TextMuted);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(2, 8, 0, 0);
        panel.Children.Add(note);
        return panel;
    }

    private FrameworkElement BuildToggles()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var title = Text("开关", 11, FontWeights.SemiBold, Theme.TextSecondary);
        title.Margin = new Thickness(2, 0, 0, 0);
        panel.Children.Add(title);

        _singleRow.Root.Margin = new Thickness(0, 8, 0, 0);
        _autoStartRow.Root.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_singleRow.Root);
        panel.Children.Add(_autoStartRow.Root);

        var note = Text("单窗口模式开启时，每个场景算一个窗口：切到场景里的任意窗口，同场景的窗口一起保留；切到场景外时，整个场景收回胶囊。",
            10.5, FontWeights.Normal, Theme.TextMuted);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(2, 10, 0, 0);
        panel.Children.Add(note);
        return panel;
    }

    /// <summary>外观：任务栏合成图标里原图标多大、角上的场景标识多大，随时可调。</summary>
    private FrameworkElement BuildAppearance()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var title = Text("外观", 11, FontWeights.SemiBold, Theme.TextSecondary);
        title.Margin = new Thickness(2, 0, 0, 0);
        panel.Children.Add(title);

        panel.Children.Add(BuildSizeRow(
            "程序图标大小", SceneIconPainter.DefaultAppIconScale * 100, 55, 100,
            v => AppIconScaleRequested?.Invoke(v), out _appIconSlider, out _appIconValue,
            "标识大小", SceneIconPainter.DefaultBadgeScale * 100, 25, 65,
            v => SceneBadgeScaleRequested?.Invoke(v), out _badgeSlider, out _badgeValue));

        var note = Text("任务栏按钮上，场景标识画成小圆点（带数字时白字居中）叠在程序图标的左上角或右上角；两个滑块分别控制原图标和标识占图标边长的百分比。",
            10.5, FontWeights.Normal, Theme.TextMuted);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(2, 8, 0, 0);
        panel.Children.Add(note);
        return panel;
    }

    /// <summary>一行并排放两组「标签 + 百分比 + 滑块」，省掉一整行高度。</summary>
    private FrameworkElement BuildSizeRow(
        string leftLabel, double leftInitial, double leftMin, double leftMax,
        Action<double> leftChanged, out Slider leftSlider, out TextBlock leftValue,
        string rightLabel, double rightInitial, double rightMin, double rightMax,
        Action<double> rightChanged, out Slider rightSlider, out TextBlock rightValue)
    {
        var grid = new Grid { Margin = new Thickness(2, 8, 2, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var left = BuildSizeField(leftLabel, leftMin, leftMax, leftInitial, leftChanged,
            out leftSlider, out leftValue);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var right = BuildSizeField(rightLabel, rightMin, rightMax, rightInitial, rightChanged,
            out rightSlider, out rightValue);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>一组「标签 + 百分比」标题加滑块；拖动时实时回调百分比对应的小数。</summary>
    private FrameworkElement BuildSizeField(string label, double min, double max, double initial,
        Action<double> changed, out Slider slider, out TextBlock value)
    {
        var stack = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = Text(label, 11.5, FontWeights.Normal, Theme.TextPrimary);
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(name, 0);
        head.Children.Add(name);

        value = Text($"{initial:0}%", 11, FontWeights.SemiBold, Theme.TextPrimary);
        value.MinWidth = 30;
        value.TextAlignment = TextAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 1);
        head.Children.Add(value);

        stack.Children.Add(head);

        slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
            Value = initial
        };
        TextBlock label2 = value;
        slider.ValueChanged += (_, e) =>
        {
            label2.Text = $"{e.NewValue:0}%";
            changed(e.NewValue / 100.0);
        };
        stack.Children.Add(slider);

        return stack;
    }

    private FrameworkElement BuildHotkeys(Border stashEdit)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var title = Text("快捷键", 11, FontWeights.SemiBold, Theme.TextSecondary);
        title.Margin = new Thickness(2, 0, 0, 0);
        panel.Children.Add(title);
        panel.Children.Add(BuildHotkeyRow("新建场景 / 展开", _stashHotkeyValue, stashEdit));
        panel.Children.Add(_status);

        var note = Text("托盘左键单击切换单窗口模式，双击打开主面板。⭐ 场景胶囊右键：重命名 / 关闭窗口 / 删除场景。换图标、移除或追加窗口请用场景行的「编辑」。",
            10.5, FontWeights.Normal, Theme.TextMuted);
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

        var name = Text(title, 12, FontWeights.Normal, Theme.TextPrimary);
        name.Margin = new Thickness(2, 0, 0, 0);
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        var keyCap = new Border
        {
            Background = Theme.ControlBackground,
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

    /// <summary>场景标识的彩色圆点（有数字时带数字）；没有标识时返回 null。</summary>
    private static FrameworkElement BadgeDot(SceneBadge badge, double size)
    {
        if (badge == null) return null;
        var dot = Ui.Badge(badge, size);
        dot.Margin = new Thickness(0, 0, 6, 0);
        return dot;
    }

    private Border BuildSceneRow(StashGroupSummary group)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel();
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
        titleLine.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = group.Expanded ? Theme.SuccessBrush : Theme.TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 7, 0)
        });
        var rowBadge = BadgeDot(group.Badge, 14);
        if (rowBadge != null) titleLine.Children.Add(rowBadge);
        titleLine.Children.Add(Text(string.IsNullOrEmpty(group.Name)
            ? $"场景 {group.Id} · {group.Count} 个窗口"
            : $"{group.Name} · {group.Count} 个窗口", 12.5, FontWeights.SemiBold, Theme.TextPrimary));
        texts.Children.Add(titleLine);

        var subGrid = new Grid { Margin = new Thickness(14, 2, 8, 0) };
        subGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        subGrid.ColumnDefinitions.Add(new ColumnDefinition());
        subGrid.Children.Add(Text(group.State, 10.5, FontWeights.SemiBold,
            group.Expanded ? Theme.SuccessBrush : Theme.TextMuted));
        var titles = Text("· " + DescribeTitles(group), 10.5, FontWeights.Normal, Theme.TextMuted);
        titles.TextTrimming = TextTrimming.CharacterEllipsis;
        titles.Margin = new Thickness(5, 0, 0, 0);
        Grid.SetColumn(titles, 1);
        subGrid.Children.Add(titles);
        texts.Children.Add(subGrid);

        Grid.SetColumn(texts, 0);
        grid.Children.Add(texts);

        int id = group.Id;
        string savedId = group.SavedId;

        // 收起 / 展开点这一行；「关闭窗口」是真的关，「编辑」只改保存的快照。
        var close = CreateAction("关闭窗口", () => SceneCloseRequested?.Invoke(id));
        close.Margin = new Thickness(0, 0, 6, 0);
        close.VerticalAlignment = VerticalAlignment.Center;
        close.ToolTip = "关闭这个场景自己的窗口（等于逐个点 ×）；场景胶囊保留，之后还能打开";
        Grid.SetColumn(close, 1);
        grid.Children.Add(close);

        if (!string.IsNullOrEmpty(savedId))
        {
            var edit = CreateAction("编辑", () => SavedEditRequested?.Invoke(savedId), accent: true);
            edit.VerticalAlignment = VerticalAlignment.Center;
            edit.ToolTip = "编辑场景：换场景图标、移除某个窗口、追加其他正在运行的场景、重命名或删除";
            Grid.SetColumn(edit, 2);
            grid.Children.Add(edit);
        }

        var row = new Border
        {
            Background = Theme.PanelCard,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 10, 9),
            Margin = new Thickness(0, 6, 0, 0),
            Child = grid,
            Cursor = Cursors.Hand,
            ToolTip = $"{group.CreatedAt:HH:mm} 新建场景 · 现在是{group.State}\n点这一行收起 / 展开"
        };
        row.MouseEnter += (_, _) => row.Background = Theme.PanelCardHover;
        row.MouseLeave += (_, _) => row.Background = Theme.PanelCard;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SceneToggleRequested?.Invoke(id);
        };
        return row;
    }

    private Border BuildClosedSceneRow(SavedGroupSummary group)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel();
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
        titleLine.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = Theme.TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 7, 0)
        });
        var closedBadge = BadgeDot(group.Badge, 14);
        if (closedBadge != null) titleLine.Children.Add(closedBadge);
        titleLine.Children.Add(Text(ShortName(group.Name),
            12.5, FontWeights.SemiBold, Theme.TextPrimary));
        texts.Children.Add(titleLine);

        var sub = Text($"{group.State} · {group.Count} 个窗口 · 点这一行打开", 10.5, FontWeights.SemiBold, Theme.TextMuted);
        sub.Margin = new Thickness(14, 2, 0, 0);
        texts.Children.Add(sub);

        Grid.SetColumn(texts, 0);
        grid.Children.Add(texts);

        string id = group.Id;
        var toggle = CreateAction("打开", () => SavedToggleRequested?.Invoke(id), accent: true);
        toggle.Margin = new Thickness(0, 0, 6, 0);
        toggle.VerticalAlignment = VerticalAlignment.Center;
        toggle.ToolTip = "启动这个场景的软件，并摆回保存时的窗口位置";
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        var edit = CreateAction("编辑", () => SavedEditRequested?.Invoke(id));
        edit.VerticalAlignment = VerticalAlignment.Center;
        edit.ToolTip = "编辑场景：换场景图标、移除某个窗口、追加其他正在运行的场景、重命名或删除";
        Grid.SetColumn(edit, 2);
        grid.Children.Add(edit);

        var row = new Border
        {
            Background = Theme.PanelCard,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 10, 9),
            Margin = new Thickness(0, 6, 0, 0),
            Child = grid,
            Cursor = Cursors.Hand,
            ToolTip = group.Titles.Count == 0
                ? "场景保存的窗口记录\n点这一行打开"
                : string.Join("\n", group.Titles.Take(6)) + "\n点这一行打开"
        };
        row.MouseEnter += (_, _) => row.Background = Theme.PanelCardHover;
        row.MouseLeave += (_, _) => row.Background = Theme.PanelCard;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SavedToggleRequested?.Invoke(id);
        };
        return row;
    }

    private static string ShortName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "场景";
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
        Background = Theme.Divider,
        Margin = new Thickness(0, 16, 0, 0)
    };

    private static Border CreateAction(string text, Action onClick, bool accent = false)
        => CreateAction(text, onClick, out _, accent);

    private static Border CreateAction(string text, Action onClick, out TextBlock label, bool accent = false)
    {
        label = Text(text, 11.5, accent ? FontWeights.SemiBold : FontWeights.Normal,
            accent ? Brushes.White : Theme.TextPrimary);
        var border = new Border
        {
            Background = accent ? Theme.AccentBrush : Theme.ControlBackground,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 5, 11, 5),
            Child = label,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
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
                Background = Theme.SwitchTrackOff,
                Child = _knob,
                VerticalAlignment = VerticalAlignment.Center
            };

            var texts = new StackPanel();
            texts.Children.Add(Text(title, 12, FontWeights.Normal, Theme.TextPrimary));
            var sub = Text(hint, 10.5, FontWeights.Normal, Theme.TextMuted);
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
                Background = Theme.PanelCard,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 9, 12, 9),
                Child = grid,
                Cursor = Cursors.Hand
            };
            Root.MouseEnter += (_, _) => Root.Background = Theme.PanelCardHover;
            Root.MouseLeave += (_, _) => Root.Background = Theme.PanelCard;
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
            _track.Background = Value ? Theme.SuccessBrush : Theme.SwitchTrackOff;
            _knob.HorizontalAlignment = Value ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
    }
}
