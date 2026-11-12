using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using TaskbarDrawer.Core;
using TaskbarDrawer.Interop;
using Forms = System.Windows.Forms;

namespace TaskbarDrawer.UI;

/// <summary>
/// 一枚胶囊的内容：运行时的一个编组，或保存下来的场景。
/// 文案 / 悬浮提示由 AppShell 组装，胶囊层只负责摆放和点击分发。
/// </summary>
public sealed class ChipEntry
{
    public string Text { get; init; } = "";
    public string Tooltip { get; init; } = "";

    /// <summary>true = 已展开（编组）/ 运行中（场景），胶囊显示为蓝色。</summary>
    public bool Highlight { get; init; }

    /// <summary>运行时组号；0 表示这是场景。</summary>
    public int GroupId { get; init; }

    /// <summary>场景 Id；null 表示这是普通编组。</summary>
    public string SavedId { get; init; }

    /// <summary>true = 已关闭的场景（只剩快照）：点击会重新打开并摆回位置。</summary>
    public bool Dormant { get; init; }

    public int Count { get; init; }
    public string Name { get; init; }

    public bool IsSaved => SavedId != null;
}

/// <summary>
/// 任务栏胶囊层：每个编组 / 场景一枚胶囊，贴着托盘区依次排开
/// （横排任务栏向左排、竖排任务栏向上排）；超过上限时末尾合并成 <c>+N</c>。
/// 左键展开（场景为打开），右键管理：取消编组 / 存储编组 / 重命名 / 关闭窗口 / 解散场景。
///
/// 胶囊是贴住任务栏的独立 topmost 小窗（Win11 不能真嵌进任务栏），每 2 s 重贴一次防被盖；
/// 组消失时对应胶囊自动隐藏，空出来的胶囊留作待用窗口，避免反复创建 HWND。
/// </summary>
public sealed class TaskbarChipLayer : IDisposable
{
    private const int MaxChips = 7;
    private const int GapPx = 4;
    private const int EdgeOffsetPx = 8;
    private const int MinChipWidthPx = 40;

    private sealed class ChipSlot
    {
        public TaskbarChipWindow Window;
        public ChipEntry Entry;
        public bool IsOverflow;
        public bool Active = true;
        public string Text = "";

        /// <summary>溢出胶囊代表的各组（第 8 项起），右键菜单里逐项列出以便直达。</summary>
        public List<ChipEntry> Overflow = new();
    }

    private readonly List<ChipSlot> _slots = new();
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    // 上一次布局的输入：任务栏句柄 / 任务栏与托盘矩形、全屏状态。
    // 句柄也要比：Explorer 重启后任务栏是新窗口，矩形可能一模一样，但新任务栏一定压在最上面。
    private IntPtr _lastTaskbar;
    private NativeMethods.RECT _lastBar;
    private NativeMethods.RECT _lastAnchor;
    private bool _lastHideAll;
    private bool _laidOut;
    private int _idleTicks;

    /// <summary>内容变了（组数 / 计数 / 展开态）就置脏，下一次必须完整重排。</summary>
    private bool _dirty = true;

    /// <summary>静止状态下每 15 个计时周期（约 30 s）仍完整重贴一次，兜底其它置顶窗口。</summary>
    private const int KeepAliveTicks = 15;

    public TaskbarChipLayer()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += (_, _) => RepositionAll();
        _timer.Start();
    }

    /// <summary>左键点胶囊：编组 = 展开，场景 = 打开。</summary>
    public event Action<ChipEntry> Clicked;

    /// <summary>编组胶囊右键「取消编组」：窗口留在桌面，不再按组管理。</summary>
    public event Action<ChipEntry> UngroupRequested;

    /// <summary>编组胶囊右键「存储编组」：给这一组起名并保存快照。</summary>
    public event Action<ChipEntry> SaveGroupRequested;

    /// <summary>编组胶囊右键「重命名」。</summary>
    public event Action<ChipEntry> RenameGroupRequested;

    /// <summary>场景胶囊右键「重命名」。</summary>
    public event Action<ChipEntry> RenameSceneRequested;

    /// <summary>场景胶囊右键「关闭窗口」：真的关掉组内窗口（等价逐个点 ×）。</summary>
    public event Action<ChipEntry> CloseWindowsRequested;

    /// <summary>场景胶囊右键「解散场景」：只删快照，窗口不动。</summary>
    public event Action<ChipEntry> DissolveSceneRequested;

    /// <summary>溢出胶囊被点击 / 右键选了「打开主面板」。</summary>
    public event Action ShowPanelRequested;

    /// <summary>按最新的胶囊清单重排（数量、文案、tooltip 全量刷新）。</summary>
    public void Sync(IReadOnlyList<ChipEntry> entries)
    {
        if (_disposed) return;

        _dirty = true;
        int shown = Math.Min(entries.Count, MaxChips);
        int used = shown + (entries.Count > shown ? 1 : 0);

        for (int i = 0; i < shown; i++)
        {
            var entry = entries[i];
            var slot = SlotAt(i);
            slot.Entry = entry;
            slot.IsOverflow = false;
            slot.Active = true;
            slot.Overflow = new List<ChipEntry>();
            slot.Text = entry.Text;
            slot.Window.SetContent(entry.Text, entry.Tooltip, entry.Highlight);
        }

        if (entries.Count > shown)
        {
            var rest = entries.Skip(shown).ToList();
            int restWindows = rest.Sum(e => e.Count);
            var slot = SlotAt(shown);
            slot.Entry = null;
            slot.IsOverflow = true;
            slot.Active = true;
            slot.Text = $"+{rest.Count}";
            slot.Overflow = rest;
            slot.Window.SetContent(slot.Text,
                $"还有 {rest.Count} 组 · {restWindows} 个窗口\n点击打开主面板 · 右键逐组切换",
                rest.Any(e => e.Highlight));
        }

        // 多余的胶囊退回待用（不销毁，下次收纳直接复用）。
        for (int i = used; i < _slots.Count; i++)
        {
            _slots[i].Active = false;
            _slots[i].Window.HideChip();
        }

        RepositionAll();
    }

    private ChipSlot SlotAt(int index)
    {
        while (_slots.Count <= index)
        {
            var window = new TaskbarChipWindow();
            window.EnsureReady();
            var slot = new ChipSlot { Window = window };
            window.Clicked += (_, _) => OnChipClicked(slot);
            window.RightClicked += (_, _) => ShowChipMenu(slot);
            _slots.Add(slot);
        }
        return _slots[index];
    }

    private void OnChipClicked(ChipSlot slot)
    {
        if (slot.IsOverflow) ShowPanelRequested?.Invoke();
        else if (slot.Entry != null) Clicked?.Invoke(slot.Entry);
    }

    private void ShowChipMenu(ChipSlot slot)
    {
        var menu = new Forms.ContextMenuStrip { Renderer = new DarkMenuRenderer() };
        try
        {
            if (slot.IsOverflow)
            {
                // 溢出胶囊背后可能是好几组：逐项列出来，点哪项就切哪项，
                // 不再逼用户「打开主面板 → 再找组」。
                foreach (var entry in slot.Overflow)
                {
                    ChipEntry e = entry;
                    var item = new Forms.ToolStripMenuItem(Describe(e));
                    item.Click += (_, _) => Clicked?.Invoke(e);
                    menu.Items.Add(item);
                }

                if (slot.Overflow.Count > 0) menu.Items.Add(new Forms.ToolStripSeparator());

                var open = new Forms.ToolStripMenuItem($"还有 {slot.Overflow.Count} 组 · 打开主面板");
                open.Click += (_, _) => ShowPanelRequested?.Invoke();
                menu.Items.Add(open);
            }
            else if (slot.Entry != null)
            {
                ChipEntry entry = slot.Entry;
                if (entry.IsSaved)
                {
                    var rename = new Forms.ToolStripMenuItem("重命名…");
                    rename.Click += (_, _) => RenameSceneRequested?.Invoke(entry);
                    menu.Items.Add(rename);

                    // 已关闭的场景没有窗口可关，只剩「打开」入口。
                    if (!entry.Dormant)
                    {
                        var close = new Forms.ToolStripMenuItem("关闭窗口");
                        close.Click += (_, _) => CloseWindowsRequested?.Invoke(entry);
                        menu.Items.Add(close);
                    }

                    var dissolve = new Forms.ToolStripMenuItem("解散场景");
                    dissolve.Click += (_, _) => DissolveSceneRequested?.Invoke(entry);
                    menu.Items.Add(dissolve);
                }
                else
                {
                    var ungroup = new Forms.ToolStripMenuItem("取消编组");
                    ungroup.Click += (_, _) => UngroupRequested?.Invoke(entry);
                    menu.Items.Add(ungroup);

                    var save = new Forms.ToolStripMenuItem("存储编组…");
                    save.Click += (_, _) => SaveGroupRequested?.Invoke(entry);
                    menu.Items.Add(save);

                    var rename = new Forms.ToolStripMenuItem("重命名…");
                    rename.Click += (_, _) => RenameGroupRequested?.Invoke(entry);
                    menu.Items.Add(rename);
                }
            }

            menu.Closed += (_, _) =>
            {
                // Closed 事件仍在 WinForms 的 ModalMenuFilter 处理栈里：
                // 当场 Dispose 会让下一次消息处理踩到已释放对象（ObjectDisposedException → 进程退出）。
                // 投递到消息队列之后再释放。
                try { menu.BeginInvoke(new Action(menu.Dispose)); }
                catch { /* 菜单已经被释放：忽略 */ }
            };
            menu.Show(Forms.Cursor.Position);
        }
        catch
        {
            menu.Dispose();
        }
    }

    private static string Describe(ChipEntry entry)
    {
        if (entry.Dormant) return $"★ {entry.Name} · {entry.Count} 个窗口 · 已关闭";
        if (entry.IsSaved)
            return $"★ {entry.Name} · {entry.Count} 个窗口 · {(entry.Highlight ? "已展开" : "已收起")}";
        return $"第 {entry.GroupId} 组 · {entry.Count} 个窗口 · {(entry.Highlight ? "已展开" : "已收起")}";
    }

    /// <summary>
    /// 把胶囊贴到托盘区旁边；任务栏找不到或前台全屏时全部隐藏。
    /// 兼容任务栏的四种位置：横向（底 / 顶）从托盘左侧依次向左排，
    /// 纵向（左 / 右）则从托盘上方依次向上排——任务栏被用户挪到左侧时胶囊不能跑出屏幕。
    /// </summary>
    private void RepositionAll()
    {
        if (_disposed) return;

        // 这是每 2 s 一次的定时器路径：异常一旦逃出去就会升级成进程退出，
        // 所以就地隔离——这次没贴好，下一个周期会自愈。
        try
        {
            RepositionCore();
        }
        catch (Exception ex)
        {
            Diagnostics.Write("chip reposition", ex);
        }
    }

    private void RepositionCore()
    {
        IntPtr taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return;
        if (!NativeMethods.GetWindowRect(taskbar, out var bar) || bar.IsEmpty) return;

        bool hideAll = ForegroundIsFullscreen();

        // 贴在托盘区旁边；拿不到托盘区时退回任务栏末端。
        var anchor = bar;
        IntPtr tray = NativeMethods.FindWindowExW(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray != IntPtr.Zero && NativeMethods.GetWindowRect(tray, out var trayRect) && !trayRect.IsEmpty)
            anchor = trayRect;

        // 循环里会改写 anchor（逐枚往上 / 往左叠），先留一份起始锚点用于比对。
        NativeMethods.RECT startAnchor = anchor;

        // 输入没变就跳过整轮 SetWindowPos；但每轮都要查一次「是不是被任务栏压住了」——
        // 用户点一下任务栏，系统就会把 Shell_TrayWnd 抬到最顶层，胶囊被它盖住后
        // 看得见（半透明透出来）却点不到，只能等下一轮重贴自愈。
        bool unchanged = _laidOut && !_dirty && _lastHideAll == hideAll && _lastTaskbar == taskbar &&
                         Same(bar, _lastBar) && Same(startAnchor, _lastAnchor);
        if (unchanged && ++_idleTicks < KeepAliveTicks && !AnyChipBuriedByTaskbar(taskbar)) return;
        _idleTicks = 0;

        bool vertical = bar.Height > bar.Width;
        bool first = true;

        // 提示框方向：横排弹上方；竖排弹到屏幕内侧，别压住相邻那枚胶囊。
        var tipPlacement = vertical
            ? (TaskbarOnLeftHalf(taskbar, bar) ? PlacementMode.Right : PlacementMode.Left)
            : PlacementMode.Top;

        foreach (var slot in _slots)
        {
            slot.Window.SetTooltipPlacement(tipPlacement);

            if (hideAll || !slot.Active)
            {
                slot.Window.HideChip();
                continue;
            }

            int gap = first ? EdgeOffsetPx : GapPx;
            int widthPx, heightPx, x, y;
            if (vertical)
            {
                // 竖排任务栏：宽度吃满任务栏内宽，胶囊从托盘上方往上叠。
                widthPx = Math.Max(MinChipWidthPx, bar.Width - 12);
                heightPx = Math.Max(20, Math.Min(32, bar.Width - 24));
                x = bar.Left + (bar.Width - widthPx) / 2;
                y = anchor.Top - gap - heightPx;
                if (y < bar.Top + 4)
                {
                    slot.Window.HideChip();
                    continue;
                }
                anchor = new NativeMethods.RECT
                {
                    Left = x,
                    Top = y,
                    Right = x + widthPx,
                    Bottom = y + heightPx
                };
            }
            else
            {
                heightPx = Math.Max(20, bar.Height - 12);
                widthPx = WidthFor(slot.Text, heightPx);
                x = anchor.Left - gap - widthPx;
                y = bar.Top + (bar.Height - heightPx) / 2;
                if (x < bar.Left + 4)
                {
                    // 任务栏已经排不下：宁可少显示，也不压到「开始」按钮上。
                    slot.Window.HideChip();
                    continue;
                }
                anchor = new NativeMethods.RECT
                {
                    Left = x,
                    Top = y,
                    Right = x + widthPx,
                    Bottom = y + heightPx
                };
            }

            slot.Window.ShowAt(x, y, widthPx, heightPx);
            first = false;
        }

        _lastTaskbar = taskbar;
        _lastBar = bar;
        _lastAnchor = startAnchor;
        _lastHideAll = hideAll;
        _laidOut = true;
        _dirty = false;
    }

    /// <summary>
    /// 是否有胶囊被任务栏压住。胶囊就画在任务栏矩形里，用户一碰任务栏，
    /// Explorer 就把 Shell_TrayWnd 抬到 topmost 波段最上面，胶囊仍然 WS_VISIBLE，
    /// 但从半透明任务栏里透出来、点不到。命中测试落在任务栏自身或其子窗口上就是被压住了。
    /// </summary>
    private bool AnyChipBuriedByTaskbar(IntPtr taskbar)
    {
        foreach (var slot in _slots)
        {
            if (!slot.Active || !slot.Window.Shown) continue;

            IntPtr hwnd = slot.Window.Handle;
            if (hwnd == IntPtr.Zero) continue;
            if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.IsEmpty) continue;

            var center = new NativeMethods.POINT
            {
                X = rect.Left + (rect.Right - rect.Left) / 2,
                Y = rect.Top + (rect.Bottom - rect.Top) / 2
            };

            IntPtr hit = NativeMethods.WindowFromPoint(center);
            if (hit == IntPtr.Zero || hit == hwnd) continue;
            if (hit == taskbar || NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT) == taskbar)
                return true;
        }

        return false;
    }

    /// <summary>任务栏是否挂在屏幕左半边（决定竖排时提示框弹右还是弹左）。</summary>
    private static bool TaskbarOnLeftHalf(IntPtr taskbar, in NativeMethods.RECT bar)
    {
        if (!NativeMethods.TryGetMonitorOfWindow(taskbar, out var monitor)) return true;
        int middle = (monitor.rcMonitor.Left + monitor.rcMonitor.Right) / 2;
        return bar.Left + (bar.Right - bar.Left) / 2 < middle;
    }

    private static bool Same(in NativeMethods.RECT a, in NativeMethods.RECT b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    /// <summary>宽度随文字长度增长（<c>2·3</c> 窄、<c>12·34</c> 宽），按任务栏高度等比缩放。</summary>
    private static int WidthFor(string text, int heightPx)
    {
        int length = text?.Length ?? 0;
        double scaled = (34 + length * 8) * heightPx / 28.0;
        return Math.Max(MinChipWidthPx, (int)Math.Round(scaled));
    }

    /// <summary>前台窗口铺满整块屏幕（全屏游戏 / 视频）时不打扰它。</summary>
    private static bool ForegroundIsFullscreen()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        if (WindowEnumerator.IsOwnProcessWindow(foreground)) return false;
        if (WindowEnumerator.IsShellWindow(foreground)) return false;
        if (!NativeMethods.GetWindowRect(foreground, out var rect) || rect.IsEmpty) return false;
        if (!NativeMethods.TryGetMonitorOfWindow(foreground, out var monitor)) return false;

        var screen = monitor.rcMonitor;
        return rect.Left <= screen.Left + 2 && rect.Top <= screen.Top + 2 &&
               rect.Right >= screen.Right - 2 && rect.Bottom >= screen.Bottom - 2;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        foreach (var slot in _slots)
        {
            try { slot.Window.Close(); } catch { /* 退出路径不再抛 */ }
        }
        _slots.Clear();
    }
}
