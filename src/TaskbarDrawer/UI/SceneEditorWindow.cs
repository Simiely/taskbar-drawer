using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using TaskbarDrawer.Core;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.UI;

/// <summary>
/// 场景编辑：移除场景里的某个窗口、把其它正在运行的场景追加进来、重命名 / 删除场景。
/// 只读数据 + 抛事件，真正的读写留在 <see cref="WorkspaceService"/>；AppShell 负责推送最新数据。
/// 修改只改保存的快照，正在运行的窗口不会被关闭或移动，下次打开场景才生效。
/// </summary>
public sealed class SceneEditorWindow : Window
{
    private readonly TextBlock _name;
    private readonly StackPanel _windowList = new();
    private readonly StackPanel _runningList = new();
    private readonly Border _runningHint;
    private readonly TextBlock _windowEmpty;
    private readonly TextBlock _runningEmpty;
    private ScrollViewer _scroll;

    /// <summary>上一次渲染的数据签名：2.5 s 的 Prune 刷新不能把滚动位置打回顶部。</summary>
    private string _signature = "";

    public SceneEditorWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _name = Ui.Text("", 10.5, FontWeights.Normal, Theme.TextMuted);
        _name.Margin = new Thickness(0, 3, 0, 0);

        _runningHint = new Border
        {
            Background = Theme.PanelCard,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 12, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = Ui.Text(
                "这个场景正在运行：这里的修改只改保存的快照，下次打开生效；当前窗口不会被关闭。",
                10.5, FontWeights.Normal, Theme.AccentBrush, wrap: true)
        };

        _windowEmpty = Ui.Text("这个场景里还没有窗口。", 11, FontWeights.Normal, Theme.TextMuted);
        _windowEmpty.Margin = new Thickness(2, 8, 0, 0);
        _windowEmpty.Visibility = Visibility.Collapsed;

        _runningEmpty = Ui.Text("现在没有正在运行的其他场景。",
            11, FontWeights.Normal, Theme.TextMuted, wrap: true);
        _runningEmpty.Margin = new Thickness(2, 8, 0, 0);
        _runningEmpty.Visibility = Visibility.Collapsed;

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

    /// <summary>把正在运行的其他场景 groupId 追加进场景。</summary>
    public event Action<int> AppendSceneRequested;

    public event Action RenameRequested;
    public event Action DeleteRequested;

    /// <summary>由 AppShell 推送最新数据；数据没变化时不重建，避免打断滚动。</summary>
    public void Reload(SavedGroupDetail detail, IReadOnlyList<StashGroupSummary> runningScenes, int runningGroupId)
    {
        if (detail == null) return;

        string signature = Signature(detail, runningScenes, runningGroupId);
        if (signature == _signature) return;
        _signature = signature;

        _name.Text = string.IsNullOrWhiteSpace(detail.Name) ? "未命名场景" : detail.Name;
        _runningHint.Visibility = runningGroupId > 0 ? Visibility.Visible : Visibility.Collapsed;

        _windowList.Children.Clear();
        foreach (var window in detail.Windows)
            _windowList.Children.Add(BuildWindowRow(window, detail.Windows.Count));
        _windowEmpty.Visibility = detail.Windows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var running = runningScenes == null
            ? Array.Empty<StashGroupSummary>()
            : runningScenes.Where(g => g.Count > 0).ToArray();

        _runningList.Children.Clear();
        foreach (var group in running)
            _runningList.Children.Add(BuildRunningSceneRow(group, group.Id == runningGroupId));
        _runningEmpty.Visibility = running.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            _scroll.MaxHeight = Math.Max(240, workHeight - 80);
        }
    }

    // ---------- 布局 ----------

    private FrameworkElement BuildRoot()
    {
        var stack = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        stack.Children.Add(BuildHeader());
        stack.Children.Add(_runningHint);
        stack.Children.Add(BuildWindowSection());
        stack.Children.Add(BuildAppendSection());
        stack.Children.Add(Divider());
        stack.Children.Add(BuildManageActions());
        stack.Children.Add(Divider());
        stack.Children.Add(BuildFooter());

        // 初始值按主屏算，OnSourceInitialized 里再按实际所在显示器重算。
        _scroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 80)
        };

        return new Border
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
            Child = _scroll
        };
    }

    private FrameworkElement BuildHeader()
    {
        var grid = new Grid { Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel();
        texts.Children.Add(Ui.Text(Theme.SceneIcon + " 场景编辑", 15, FontWeights.SemiBold, Theme.TextPrimary));
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

        grid.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { /* 已松手 */ }
        };
        return grid;
    }

    private FrameworkElement BuildWindowSection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(SectionTitle("场景里的窗口"));
        _windowList.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_windowList);
        panel.Children.Add(_windowEmpty);

        var note = Ui.Text("移除只改保存的快照：当前正在运行的窗口不会关闭，下次打开场景时少开一个。",
            10.5, FontWeights.Normal, Theme.TextMuted, wrap: true);
        note.Margin = new Thickness(2, 8, 0, 0);
        panel.Children.Add(note);
        return panel;
    }

    private FrameworkElement BuildAppendSection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(SectionTitle(Theme.SceneIcon + " 把其它场景的窗口追加进来"));
        _runningList.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_runningList);
        panel.Children.Add(_runningEmpty);

        var note = Ui.Text("追加的是那个场景此刻的窗口和位置。同样只改快照：当前场景正在运行时，下次打开才生效。",
            10.5, FontWeights.Normal, Theme.TextMuted, wrap: true);
        note.Margin = new Thickness(2, 8, 0, 0);
        panel.Children.Add(note);
        return panel;
    }

    private FrameworkElement BuildManageActions()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var actions = new WrapPanel();

        var rename = MakeAction("重命名场景", false, () => RenameRequested?.Invoke());
        rename.Margin = new Thickness(0, 0, 8, 6);
        actions.Children.Add(rename);

        var delete = MakeDanger("删除场景", () => DeleteRequested?.Invoke());
        delete.Margin = new Thickness(0, 0, 8, 6);
        actions.Children.Add(delete);
        panel.Children.Add(actions);

        var note = Ui.Text("删除场景会移除保存的记录：正在运行的窗口会留在桌面上，不再由本程序管理。",
            10.5, FontWeights.Normal, Theme.TextMuted, wrap: true);
        note.Margin = new Thickness(2, 2, 0, 0);
        panel.Children.Add(note);
        return panel;
    }

    private FrameworkElement BuildFooter()
    {
        var done = MakeAction("完成", true, Close);
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
        var remove = MakeAction("移除", false, () => RemoveWindowRequested?.Invoke(index), enabled: total > 1);
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

    private Border BuildRunningSceneRow(StashGroupSummary group, bool isSelf)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel();
        string head = string.IsNullOrEmpty(group.Name)
            ? $"{Theme.SceneIcon} 场景 {group.Id} · {group.Count} 个窗口"
            : $"{Theme.SceneIcon} {group.Name} · {group.Count} 个窗口";
        var title = Ui.Text(head, 12, FontWeights.SemiBold, Theme.TextPrimary);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        texts.Children.Add(title);

        var sub = Ui.Text((group.Expanded ? "已展开 · " : "已收起 · ") + DescribeTitles(group),
            10.5, FontWeights.Normal, Theme.TextMuted);
        sub.Margin = new Thickness(0, 2, 0, 0);
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        texts.Children.Add(sub);

        Grid.SetColumn(texts, 0);
        grid.Children.Add(texts);

        int groupId = group.Id;
        var append = MakeAction("追加", false, () => AppendSceneRequested?.Invoke(groupId), enabled: !isSelf);
        if (isSelf) append.ToolTip = "这就是当前场景自己";
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

    private static string DescribeTitles(StashGroupSummary group)
    {
        if (group.Titles.Count == 0) return "（窗口已关闭）";
        string joined = string.Join(" · ", group.Titles.Take(2));
        return group.Titles.Count > 2 ? $"{joined} 等 {group.Titles.Count} 个" : joined;
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
        SavedGroupDetail detail, IReadOnlyList<StashGroupSummary> runningScenes, int runningGroupId)
    {
        var builder = new StringBuilder();
        builder.Append(detail.Id).Append('|').Append(detail.Name).Append('|')
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
        if (runningScenes != null)
        {
            foreach (var group in runningScenes)
            {
                builder.Append(group.Id).Append(':')
                    .Append(group.Count).Append(':')
                    .Append(group.Expanded).Append(':')
                    .Append(group.SavedId).Append(':')
                    .Append(group.Name).Append(':')
                    .Append(string.Join("/", group.Titles)).Append(';');
            }
        }
        return builder.ToString();
    }

    private static FrameworkElement SectionTitle(string text)
    {
        var title = Ui.Text(text, 11, FontWeights.SemiBold, Theme.TextSecondary);
        title.Margin = new Thickness(2, 0, 0, 0);
        return title;
    }

    private static FrameworkElement Divider() => new Border
    {
        Height = 1,
        Background = Theme.Divider,
        Margin = new Thickness(0, 16, 0, 0)
    };

    private static Border MakeAction(string text, bool accent, Action onClick, bool enabled = true)
    {
        var label = Ui.Text(text, 11.5, accent ? FontWeights.SemiBold : FontWeights.Normal,
            accent ? Brushes.White : Theme.TextPrimary);
        var border = new Border
        {
            Background = accent ? Theme.AccentBrush : Theme.ControlBackground,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 5, 11, 5),
            Child = label,
            Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
            Opacity = enabled ? 1 : 0.45,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!enabled) return border;

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
