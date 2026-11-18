using System.Text;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using TaskbarDrawer.Core;
using TaskbarDrawer.Interop;
using Forms = System.Windows.Forms;

namespace TaskbarDrawer.UI;

/// <summary>
/// 一枚胶囊的内容：一个运行中的场景，或一个已关闭的场景。
/// 文案 / 悬浮提示由 AppShell 组装，胶囊层只负责摆放和点击分发。
/// </summary>
public sealed class ChipEntry
{
    public string Text { get; init; } = "";
    public string Tooltip { get; init; } = "";

    /// <summary>场景标识：胶囊与右键菜单都显示它。</summary>
    public SceneBadge Badge { get; init; } = SceneBadge.Default;

    /// <summary>true = 场景已展开，胶囊显示为蓝色。</summary>
    public bool Highlight { get; init; }

    /// <summary>场景对应的运行时组号；0 表示场景已关闭。</summary>
    public int GroupId { get; init; }

    /// <summary>场景 Id；正常运行时必有。</summary>
    public string SavedId { get; init; }

    /// <summary>true = 已关闭的场景（只剩快照）：点击会重新打开并摆回位置。</summary>
    public bool Dormant { get; init; }

    public int Count { get; init; }
    public string Name { get; init; }
}

/// <summary>
/// 任务栏胶囊层：每个场景一枚胶囊，贴着托盘区依次排开
/// （横排任务栏向左排、竖排任务栏向上排）；超过上限时末尾合并成 <c>+N</c>。
/// 左键收起 / 展开（已关闭场景为打开），右键管理：重命名 / 关闭窗口 / 删除场景。
///
/// 胶囊是贴住任务栏的独立 topmost 小窗（Win11 不能真嵌进任务栏），每 2 s 重贴一次防被盖；
/// 场景消失时对应胶囊自动隐藏，空出来的胶囊留作待用窗口，避免反复创建 HWND。
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
        public SceneBadge Badge;

        /// <summary>溢出胶囊代表的各场景（第 8 项起），右键菜单里逐项列出以便直达。</summary>
        public List<ChipEntry> Overflow = new();
    }

    private readonly List<ChipSlot> _slots = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _guard;
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

    /// <summary>上一次同步的内容签名：内容没变就不重建胶囊，位置仍由 2 s 定时器体检。</summary>
    private string _signature;

    /// <summary>静止状态下每 15 个计时周期（约 30 s）仍完整重贴一次，兜底其它置顶窗口。</summary>
    private const int KeepAliveTicks = 15;

    /// <summary>守卫体检间隔：被盖住后最坏等这么久复位（原来要等满一个 2 s 周期）。</summary>
    private static readonly TimeSpan GuardInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>一次前台切换后连续守卫多少轮（约 2 s），之后退回 2 s 保活。</summary>
    private const int GuardTicks = 8;

    /// <summary>守卫剩余轮数；0 = 守卫没在跑。</summary>
    private int _guardTicks;

    /// <summary>触发这次体检的事件时刻，用来在日志里量「被盖 → 复位」的真实耗时。</summary>
    private DateTime _triggerAt;

    /// <summary>位置监听当前订阅的进程（Explorer）；它重启后 PID 会变，需要重新挂。</summary>
    private uint _locationHookProcess;

    private IntPtr _foregroundHook;
    private IntPtr _locationHook;

    // 必须存成字段：委托被回收后原生回调会踩到野桩，直接终止进程。
    private NativeMethods.WinEventProc _foregroundProc;
    private NativeMethods.WinEventProc _locationProc;

    public TaskbarChipLayer()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += (_, _) => RepositionAll();
        _timer.Start();

        // 守卫只干一件轻活（一次 WindowFromPoint 命中测试 + 必要时的纯 z 序复位），不做布局。
        // 平时不跑，只有前台切换 / 任务栏移动之后高频跑 2 s。
        _guard = new DispatcherTimer(DispatcherPriority.Background) { Interval = GuardInterval };
        _guard.Tick += (_, _) => GuardTick();

        InstallHooks();
    }

    /// <summary>
    /// 挂「前台切换」监听：点任务栏、点任务栏按钮切窗口、开始菜单收起都落在这一刻，
    /// 任务栏抬自己也发生在这里。只读监听，不注入 DLL、不装全局钩子。
    /// 挂不上时只是回到原来的 2 s 轮询，不影响主流程。
    /// </summary>
    private void InstallHooks()
    {
        try
        {
            _locationProc = OnTaskbarMoved;
            _foregroundProc = OnForegroundChanged;
            _foregroundHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _foregroundProc, 0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

            if (_foregroundHook == IntPtr.Zero)
                Diagnostics.Info("chip", "前台事件监听未就绪，退回 2 s 轮询");
        }
        catch (Exception ex)
        {
            Diagnostics.Write("chip hook", ex);
        }
    }

    /// <summary>
    /// 「任务栏几何变化」监听只在拿到任务栏之后按 **Explorer 进程**订阅。
    /// 全局订阅 LOCATIONCHANGE 会在拖动窗口 / 动画时收到成百上千次回调（每个子对象都算一次），
    /// 按进程过滤后只剩任务栏自己的移动：自动隐藏滑动、改边、换分辨率、通知图标增减。
    /// Explorer 重启后 PID 会变，所以每次全量体检都拿当前任务栏的 PID 比一次。
    /// </summary>
    private void EnsureLocationHook(IntPtr taskbar)
    {
        if (_locationProc == null) return;

        NativeMethods.GetWindowThreadProcessId(taskbar, out uint pid);
        if (pid == 0 || pid == _locationHookProcess) return;

        if (_locationHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }

        _locationHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _locationProc, pid, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        _locationHookProcess = _locationHook == IntPtr.Zero ? 0 : pid;
    }

    /// <summary>
    /// 前台切换 = 最可能「任务栏把自己抬到胶囊上面」的时刻，先就地面检一次，
    /// 再开一小段守卫跟上（有些场景会在激活之后才抬任务栏）。
    /// </summary>
    private void OnForegroundChanged(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // 这是交给 Win32 的回调：异常逃回原生栈会直接终止进程，只能就地隔离。
        try
        {
            if (_disposed || hwnd == IntPtr.Zero) return;
            if (idObject != 0) return;                              // 只认 OBJID_WINDOW
            if (WindowEnumerator.IsOwnProcessWindow(hwnd)) return;   // 自己弹面板 / 提示不算被压

            _triggerAt = DateTime.UtcNow;
            TryRaiseIfBuried("前台切换");
            KickGuard();
        }
        catch (Exception ex)
        {
            Diagnostics.Write("chip foreground", ex);
        }
    }

    /// <summary>任务栏 / 托盘区几何变了：置脏，让下一次守卫走完整重排（位置真的变了，抬 z 序不够）。</summary>
    private void OnTaskbarMoved(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (_disposed || hwnd == IntPtr.Zero) return;

            // 订阅已按 Explorer 进程收窄，但桌面 / 开始菜单等也在同一个进程里：
            // 只认「根窗口就是任务栏」的对象（任务栏自身 + 托盘区等子窗口）。
            IntPtr taskbar = _lastTaskbar;
            if (taskbar == IntPtr.Zero) return;
            if (hwnd != taskbar && NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != taskbar) return;

            _dirty = true;
            KickGuard();
        }
        catch (Exception ex)
        {
            Diagnostics.Write("chip taskbar move", ex);
        }
    }

    /// <summary>开一小段高频守卫；已经在跑就只续命，不重复启动。</summary>
    private void KickGuard()
    {
        if (_disposed) return;
        _guardTicks = GuardTicks;
        if (!_guard.IsEnabled) _guard.Start();
    }

    private void GuardTick()
    {
        if (_disposed)
        {
            _guard.Stop();
            return;
        }

        if (_dirty) RepositionAll();
        else TryRaiseIfBuried("守卫");

        // 守卫到期才清触发时间：别让它跨到下一个 2 s 周期里算出一个离谱的耗时。
        if (--_guardTicks <= 0)
        {
            _guard.Stop();
            _triggerAt = default;
        }
    }

    /// <summary>
    /// 体检 + 最小复位：只把胶囊抬回 topmost 波段最上面，不动位置尺寸、不重排。
    /// 任务栏句柄已失效（Explorer 重启）时什么都不做，交给 2 s 全量体检重建。
    /// 返回 true 表示这次确实有胶囊被压住并已复位。
    /// </summary>
    private bool TryRaiseIfBuried(string source)
    {
        if (_disposed) return false;

        try
        {
            IntPtr taskbar = _lastTaskbar;
            if (taskbar == IntPtr.Zero || !NativeMethods.IsWindow(taskbar)) return false;

            int buried = CountBuriedChips(taskbar);
            if (buried == 0) return false;

            foreach (var slot in _slots)
            {
                if (!slot.Active || !slot.Window.Shown) continue;
                slot.Window.Raise();
            }

            // 刚复位过：30 s 保活重新计时，别紧接着再白跑一次全量重排。
            _idleTicks = 0;

            double elapsed = -1;
            if (_triggerAt != default)
            {
                elapsed = (DateTime.UtcNow - _triggerAt).TotalMilliseconds;
                _triggerAt = default;
            }

            // 这一行是这次修复的验收依据：实机点任务栏后看耗时是否稳定在几十毫秒内。
            Diagnostics.Info("chip",
                $"被任务栏压住 {buried} 枚 → 已复位 来源={source} " +
                $"耗时={(elapsed >= 0 ? $"{elapsed:0}ms" : "(非事件触发)")}");
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Write("chip raise", ex);
            return false;
        }
    }

    /// <summary>左键点胶囊：运行中的场景切换收起 / 展开，已关闭的场景打开。</summary>
    public event Action<ChipEntry> Clicked;

    /// <summary>场景胶囊右键「重命名」。</summary>
    public event Action<ChipEntry> RenameSceneRequested;

    /// <summary>场景胶囊右键「关闭窗口」：真的关掉场景内窗口（等价逐个点 ×）。</summary>
    public event Action<ChipEntry> CloseWindowsRequested;

    /// <summary>场景胶囊右键「删除场景」：只删快照，窗口不动。</summary>
    public event Action<ChipEntry> DissolveSceneRequested;

    /// <summary>溢出胶囊被点击 / 右键选了「打开主面板」。</summary>
    public event Action ShowPanelRequested;

    /// <summary>按最新的胶囊清单重排（数量、文案、tooltip 全量刷新）。</summary>
    public void Sync(IReadOnlyList<ChipEntry> entries)
    {
        if (_disposed) return;

        string signature = SignatureOf(entries);
        if (signature == _signature) return;
        _signature = signature;

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
            slot.Badge = entry.Badge;
            slot.Window.SetContent(entry.Badge, entry.Text, entry.Tooltip, entry.Highlight);
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
            slot.Badge = null;
            slot.Overflow = rest;
            slot.Window.SetContent(null, slot.Text,
                $"还有 {rest.Count} 个场景 · {restWindows} 个窗口\n点击打开主面板 · 右键逐场景切换",
                rest.Any(e => e.Highlight));
        }

        // 多余的胶囊退回待用（不销毁，下次收纳直接复用）。
        for (int i = used; i < _slots.Count; i++)
        {
            _slots[i].Active = false;
            _slots[i].Window.HideChip();
        }

        RepositionAll();
        // 刚摆好的一瞬间最容易被任务栏盖住（它常在胶囊出现的同时重排自己）：开 2 s 守卫盯着。
        KickGuard();
    }

    /// <summary>胶囊内容的稳定签名：只比较会影响画面的字段，避免每次刷新都全量重排。</summary>
    private static string SignatureOf(IReadOnlyList<ChipEntry> entries)
    {
        var builder = new StringBuilder();
        builder.Append(entries.Count).Append('|');
        foreach (var entry in entries)
        {
            builder.Append(entry.Text).Append('|')
                .Append(entry.Tooltip).Append('|')
                .Append(entry.Highlight).Append('|')
                .Append(entry.Dormant).Append('|')
                .Append(entry.GroupId).Append('|')
                .Append(entry.SavedId).Append('|')
                .Append(entry.Count).Append('|')
                .Append(entry.Name).Append('|')
                .Append(entry.Badge?.Color).Append('|')
                .Append(entry.Badge?.Number).Append('|')
                .Append(entry.Badge?.Right).Append(';');
        }
        return builder.ToString();
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
                // 溢出胶囊背后可能是好几个场景：逐项列出来，点哪项就切哪项，
                // 不再逼用户「打开主面板 → 再找场景」。
                foreach (var entry in slot.Overflow)
                {
                    ChipEntry e = entry;
                    var item = new Forms.ToolStripMenuItem(Describe(e));
                    item.Click += (_, _) => Clicked?.Invoke(e);
                    menu.Items.Add(item);
                }

                if (slot.Overflow.Count > 0) menu.Items.Add(new Forms.ToolStripSeparator());

                var open = new Forms.ToolStripMenuItem($"还有 {slot.Overflow.Count} 个场景 · 打开主面板");
                open.Click += (_, _) => ShowPanelRequested?.Invoke();
                menu.Items.Add(open);
            }
            else if (slot.Entry != null)
            {
                ChipEntry entry = slot.Entry;
                if (!string.IsNullOrEmpty(entry.SavedId))
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

                    var dissolve = new Forms.ToolStripMenuItem("删除场景");
                    dissolve.Click += (_, _) => DissolveSceneRequested?.Invoke(entry);
                    menu.Items.Add(dissolve);
                }
                else
                {
                    // 兜底：理论上每个运行时组都有场景；真丢了也给一个可操作的出口。
                    var open = new Forms.ToolStripMenuItem("打开主面板");
                    open.Click += (_, _) => ShowPanelRequested?.Invoke();
                    menu.Items.Add(open);
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
        string prefix = SceneBadge.PrefixOf(entry.Badge);
        if (entry.Dormant) return $"{prefix}{entry.Name} · {entry.Count} 个窗口 · 已关闭";
        return $"{prefix}{entry.Name} · {entry.Count} 个窗口 · {(entry.Highlight ? "已展开" : "已收起")}";
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

        // Explorer 重启后任务栏是新的句柄 / 新的 PID：位置监听要跟着换。
        EnsureLocationHook(taskbar);

        // 只有全屏窗口和任务栏在同一块屏时才让位：副屏全屏游戏不该把主屏胶囊也藏起来。
        string fullscreenDevice = FullscreenForegroundDevice();
        bool hideAll = fullscreenDevice != null &&
                       fullscreenDevice == NativeMethods.MonitorDeviceOf(taskbar);

        // 贴在托盘区旁边；拿不到托盘区时退回任务栏末端。
        var anchor = bar;
        IntPtr tray = NativeMethods.FindWindowExW(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray != IntPtr.Zero && NativeMethods.GetWindowRect(tray, out var trayRect) && !trayRect.IsEmpty)
            anchor = trayRect;

        // 循环里会改写 anchor（逐枚往上 / 往左叠），先留一份起始锚点用于比对。
        NativeMethods.RECT startAnchor = anchor;

        // 输入没变就不重排：先只做「是不是被任务栏压住了」的轻量体检，
        // 真被压住时抬一次 z 序就够，不必重算整轮布局（省掉每枚胶囊一次带位置尺寸的 SetWindowPos）。
        bool unchanged = _laidOut && !_dirty && _lastHideAll == hideAll && _lastTaskbar == taskbar &&
                         Same(bar, _lastBar) && Same(startAnchor, _lastAnchor);
        if (unchanged)
        {
            if (TryRaiseIfBuried("定时器")) return;
            if (++_idleTicks < KeepAliveTicks) return;
        }
        _idleTicks = 0;
        _triggerAt = default;

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
                // 只留 4 px 边距，多出来的宽度给「标识 + 场景名」。
                widthPx = Math.Max(MinChipWidthPx, bar.Width - 8);
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
                widthPx = WidthFor(slot.Text, heightPx, slot.Badge != null);
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
    /// 有多少枚胶囊被任务栏压住。胶囊就画在任务栏矩形里，用户一碰任务栏，
    /// Explorer 就把 Shell_TrayWnd 抬到 topmost 波段最上面，胶囊仍然 WS_VISIBLE，
    /// 但从半透明任务栏里透出来、点不到。命中测试落在任务栏自身或其子窗口上就是被压住了。
    /// 只认任务栏：被开始菜单 / 通知中心盖住是正常的（它们盖上去时用户也不需要胶囊），
    /// 硬抢反而会让胶囊浮在这些浮层上面；那些浮层收起时前台事件会把我们叫醒。
    /// </summary>
    private int CountBuriedChips(IntPtr taskbar)
    {
        int buried = 0;
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
            if (hit == taskbar || NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT) == taskbar) buried++;
        }

        return buried;
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

    /// <summary>宽度随文字长度增长，按任务栏高度等比缩放；带场景标识时再加一枚圆点的宽度。</summary>
    private static int WidthFor(string text, int heightPx, bool hasBadge = false)
    {
        int length = text?.Length ?? 0;
        double scaled = (18 + length * 8 + (hasBadge ? 19 : 0)) * heightPx / 28.0;
        return Math.Max(MinChipWidthPx, (int)Math.Round(scaled));
    }

    /// <summary>
    /// 前台窗口铺满整块屏幕（全屏游戏 / 视频）时返回它所在的显示器，否则 null。
    /// 返回设备名而不是 bool：调用方要判断全屏的是不是胶囊所在的那块屏。
    /// </summary>
    private static string FullscreenForegroundDevice()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return null;
        if (WindowEnumerator.IsOwnProcessWindow(foreground)) return null;
        if (WindowEnumerator.IsShellWindow(foreground)) return null;
        if (!NativeMethods.GetWindowRect(foreground, out var rect) || rect.IsEmpty) return null;
        if (!NativeMethods.TryGetMonitorOfWindow(foreground, out var monitor)) return null;

        var screen = monitor.rcMonitor;
        bool fullscreen = rect.Left <= screen.Left + 2 && rect.Top <= screen.Top + 2 &&
                          rect.Right >= screen.Right - 2 && rect.Bottom >= screen.Bottom - 2;
        return fullscreen ? monitor.szDevice : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _guard.Stop();

        // 先摘监听再关窗口：回调可能已经在消息队列里，_disposed 会在入口处直接返回。
        if (_foregroundHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_foregroundHook);
        if (_locationHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_locationHook);
        _foregroundHook = _locationHook = IntPtr.Zero;
        _foregroundProc = null;
        _locationProc = null;

        foreach (var slot in _slots)
        {
            try { slot.Window.Close(); } catch { /* 退出路径不再抛 */ }
        }
        _slots.Clear();
    }
}
