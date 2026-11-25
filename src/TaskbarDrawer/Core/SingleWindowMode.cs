using System.Windows.Threading;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>
/// 单窗口模式：监听前台变化，把除前台窗口以外的窗口系统最小化（保留任务栏按钮与 Alt+Tab）。
///
/// 只处理「合法的前台窗口」事件：桌面 / 任务栏 / 开始菜单 / 本程序成为前台时什么都不做，
/// 否则点一下开始菜单就会把所有窗口最小化。
///
/// <b>一块屏幕是完全独立的</b>：前台在哪块屏，就只管哪块屏上的窗口。另一块屏上的场景
/// 不会被这里的事件收起 / 最小化。场景属于<b>它自己的那块屏幕</b>（收纳时确定，之后成员
/// 被拖到别的屏也不改）：只有在这块屏上切换窗口，才会把属于它的场景收回胶囊。
/// </summary>
public sealed class SingleWindowMode : IDisposable
{
    private readonly DispatcherTimer _coalesce;
    private readonly NativeMethods.WinEventProc _callback;
    private IntPtr _hook;
    private IntPtr _moveHook;
    private IntPtr _pending;
    private DateTime _suppressUntil;
    private bool _enabled;
    private bool _applying;
    private bool _disposed;

    public SingleWindowMode(Dispatcher dispatcher)
    {
        _callback = OnWinEvent;

        // OUTOFCONTEXT 回调在注册线程到达；用 60 ms 合并窗口把连续切换压成一次扫描。
        _coalesce = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _coalesce.Tick += (_, _) =>
        {
            _coalesce.Stop();
            // 定时器路径上的局部失败不该把常驻进程带走：记一条日志，下一次切换前台时再试。
            try { Apply(_pending); }
            catch (Exception ex) { Diagnostics.Write("single-window apply", ex); }
            _pending = IntPtr.Zero;
        };
    }

    public bool Enabled => _enabled;

    /// <summary>
    /// 排除列表（默认只有内置的资源管理器）：命中的应用不参与单窗口模式——
    /// 成为前台时不收起场景 / 不最小化别的窗口，它自己的窗口也不会被最小化。
    /// 属于场景的窗口仍按场景处理，不受排除影响。
    /// </summary>
    public IReadOnlyList<WindowExclusion> Exclusions { get; set; } = Array.Empty<WindowExclusion>();

    /// <summary>
    /// 由组合根注入：返回窗口所属的场景运行组号，0 表示不属于任何场景。
    /// 注入后「同一场景展开的窗口」在单窗口模式下被当成一个整体：切到场景内任一窗口，其余同伴恢复；
    /// 切到场景外时，整个场景收回胶囊（而不是拆成最小化窗口）。
    /// </summary>
    public Func<IntPtr, int> GroupOf { get; set; }

    /// <summary>
    /// 由组合根注入：把某一场景整组收回收纳区（隐藏窗口、保留组），返回收起的窗口数。
    /// 场景是整体：成员被拖到别的屏幕也一起收（它仍然属于这个场景）。
    /// </summary>
    public Func<int, int> CollapseGroup { get; set; }

    /// <summary>
    /// 由组合根注入：场景归属的屏幕（收纳 / 创建时确定，拖动成员不改变）。
    /// 只有当前台就在这块屏上时，这个场景才算「被激活」；也只有在它自己的屏上
    /// 切换窗口，才会把它收起。别的屏上的场景成员即使被拖到这里，也不参与本屏的判定。
    /// </summary>
    public Func<int, string> GroupScreen { get; set; }

    /// <summary>
    /// 由组合根注入：场景正在屏幕上、但某个成员的程序被关掉又重开时，先把新窗口认回场景。
    /// 必须赶在「切到场景外 → 整组收回胶囊」之前：否则用户刚打开的程序会把整个场景收走。
    /// 返回 true 表示已接入，后续归组判断就会把它当成场景成员。
    /// </summary>
    public Func<IntPtr, bool> ClaimForeground { get; set; }

    /// <summary>
    /// 由组合根注入：前台和某个场景的成员是同进程 / 子进程时返回该场景组号（否则 0）。
    /// 用途只有一个：判断「同应用但不是场景成员」→ 单窗口模式直接跳过、不做任何动作。
    /// 不要把返回值赋给 group 再跑下面的最小化循环——那会把整个场景当成被激活（1.14.4）。
    /// </summary>
    public Func<IntPtr, int> GroupOfRelated { get; set; }

    /// <summary>一次应用里被最小化的窗口数（订阅方可用于状态提示）。</summary>
    public event Action<int> Applied;

    /// <summary>打开开关时这块屏幕上没有可接管的窗口（组合根用来提示，而不是静默无反应）。</summary>
    public event Action NoCandidateOnScreen;

    public void Start()
    {
        if (_disposed || _hook != IntPtr.Zero) return;
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        // 跨屏拖动「结束」必须补一次判定：拖动过程中前台窗口没变，只有 MOVESIZEEND。
        _moveHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MOVESIZEEND, NativeMethods.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, _callback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    public void SetEnabled(bool value, bool applyImmediately = true)
    {
        if (_disposed) return;
        _enabled = value;
        if (!value)
        {
            _coalesce.Stop();
            _pending = IntPtr.Zero;
            return;
        }
        // 打开开关的瞬间先执行一次：此刻屏幕上可能已经有很多窗口。
        // 启动时恢复设置传 false——否则登录那一刻会把用户所有窗口清空。
        if (applyImmediately) ApplyForegroundOrCandidate();
    }

    /// <summary>
    /// 打开开关时立即应用一次。入口只有托盘 / 面板，鼠标（面板）在哪块屏，就只处理哪块屏。
    /// 不能优先用前台窗口：点托盘菜单时前台很可能还是另一块屏上刚才用的窗口，
    /// 拿它当目标会把另一块屏清空，看起来像「点一块屏、两块都被清」。
    /// 这块屏没有可接管的窗口就提示，不回退到前台窗口。
    /// </summary>
    private void ApplyForegroundOrCandidate()
    {
        var windows = WindowEnumerator.Enumerate(includeMinimized: false);
        string monitor = NativeMethods.MonitorDeviceOfCursor();

        var target = windows.FirstOrDefault(w =>
            DisplayRules.BelongsTo(monitor, NativeMethods.MonitorDeviceOf(w.Handle)));

        if (target == null)
        {
            Diagnostics.Info("single", $"开关应用：鼠标所在屏 {monitor ?? "(未知)"} 没有可接管窗口");
            NoCandidateOnScreen?.Invoke();
            return;
        }
        Diagnostics.Info("single",
            $"开关应用：鼠标屏={monitor ?? "(未知)"} 目标=0x{target.Handle.ToInt64():X}「{target.Title}」");
        Apply(target.Handle);
    }

    /// <summary>
    /// 临时暂停一小段时间：程序自己批量还原一个场景的窗口时会连续切换前台，
    /// 不该在还原过程中把刚恢复的同伴又最小化掉。
    /// </summary>
    public void SuspendTemporarily(int milliseconds = 800, string reason = null)
    {
        _suppressUntil = DateTime.UtcNow.AddMilliseconds(milliseconds);
        if (!string.IsNullOrEmpty(reason))
            Diagnostics.Info("single", $"暂停 {milliseconds}ms：{reason}");
    }

    /// <summary>
    /// 立刻按某个窗口应用一次，忽略上面那段抑制窗口。
    /// 场景刚从收纳区 / 快照恢复到屏幕后必须补这一次：批量恢复期间的前台事件全被压掉了，
    /// 之后系统不会再有事件，不补的话「其它窗口最小化」永远不会发生
    /// ——单窗口模式看起来就像没生效。
    /// </summary>
    public void ApplyNow(IntPtr anchor)
    {
        if (_disposed || !_enabled || anchor == IntPtr.Zero) return;
        _suppressUntil = default;
        try
        {
            Apply(anchor);
        }
        catch (Exception ex)
        {
            // 这里由入口调用的收尾路径触发，失败只影响本次收尾。
            Diagnostics.Write("single-window apply now", ex);
        }
    }

    private void OnWinEvent(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // 这是交给 Win32 的回调：异常一旦逃回原生栈会直接终止进程，所以就地隔离。
        try
        {
            if (!_enabled || _disposed) return;
            if (idObject != 0) return;          // 只认 OBJID_WINDOW，忽略子元素事件
            if (hwnd == IntPtr.Zero) return;

            // 拖动结束：窗口可能刚被拖到另一块屏幕。前台窗口本身没变，系统不会再发
            // FOREGROUND，必须借这次事件按当前前台补一次判定，否则新屏幕上的场景不会收起。
            // 用 GetForegroundWindow 而不是事件里的 hwnd：拖动的窗口不一定被激活。
            IntPtr target = hwnd;
            if (eventType == NativeMethods.EVENT_SYSTEM_MOVESIZEEND)
            {
                target = NativeMethods.GetForegroundWindow();
                if (target == IntPtr.Zero) return;
                Diagnostics.Info("single",
                    $"拖动结束 0x{hwnd.ToInt64():X} → 前台 0x{target.ToInt64():X} " +
                    $"screen={NativeMethods.MonitorDeviceOf(target) ?? "(未知)"}");
            }

            _pending = target;
            _coalesce.Stop();
            _coalesce.Start();
        }
        catch (Exception ex)
        {
            Diagnostics.Write("window event hook", ex);
        }
    }

    private void Apply(IntPtr foreground)
    {
        if (!_enabled || _applying || _disposed) return;
        if (DateTime.UtcNow < _suppressUntil) return;
        if (!IsEligibleForeground(foreground)) return;

        int minimized = 0;
        int collapsedCount = 0;
        int hostedCount = 0;
        string monitorDevice = null;
        _applying = true;
        try
        {
            var groupOf = GroupOf;

            // 前台可能是一个弹窗 / 浮层（浏览器扩展弹窗、应用的工具窗、场景成员自己弹的对话框）：
            // 点它的时候系统把「弹窗」设为前台，而弹窗永远不是场景成员，于是下面那段
            // 「别的场景整组收回胶囊」会把弹窗所属的场景整个收起来——用户只是点了插件。
            //
            // 两道兜底，顺序不能反：
            // ① 按窗口簇归组：沿 owner 链走到簇主，簇主在场景里就当作「还是那个场景」；
            // ② 簇主也对不上（Chromium 的气泡由中间一层无 owner 的 border 窗口拥有，
            //    owner 链断在半路），那这次前台变化就不是「切换到另一个窗口」：
            //    按准入规则它连窗口都算不上，直接不处理。
            IntPtr head = NativeMethods.RootOwner(foreground);
            int group = ResolveGroup(foreground, groupOf, _ => head);
            string exclusion = group == 0 ? WindowEnumerator.ExclusionReason(foreground) : null;

            // 和场景成员同一个应用、但不是场景成员的窗口（微信图片 / 小程序宿主 /
            // 应用自己的对话框的宿主等）：单窗口模式什么都不做。
            //
            // 这里刻意**不**把它归到场景组：归组会走进下面的「保留同组、最小化其它窗口」，
            // 等于把整个场景当成被激活，日志里就会出现「关联组=1 最小化=1」这种误动作。
            if (group == 0 && exclusion == null && GroupOfRelated != null)
            {
                int related = GroupOfRelated(foreground);
                if (related != 0)
                {
                    Diagnostics.Info("single",
                        $"跳过前台 0x{foreground.ToInt64():X}（与场景 {related} 同应用，不是成员） " +
                        $"class={NativeMethods.ClassName(foreground)} " +
                        $"owner=0x{NativeMethods.Owner(foreground).ToInt64():X}");
                    return;
                }
            }

            // 「场景成员的程序关掉又重开」：新窗口此刻不属于任何场景，但它其实是某个
            // 展开场景在等回来的成员。先认领，再继续下面的判定——否则会把它当成场景外窗口，
            // 反而把用户刚打开程序所属的整个场景收走（实机日志 10:39:08）。
            if (group == 0 && ClaimForeground != null && ClaimForeground(foreground))
            {
                head = NativeMethods.RootOwner(foreground);
                group = ResolveGroup(foreground, groupOf, _ => head);
            }

            if (ShouldIgnoreForeground(group, exclusion))
            {
                Diagnostics.Info("single",
                    $"忽略前台 0x{foreground.ToInt64():X}（{exclusion}）" +
                    $" class={NativeMethods.ClassName(foreground)}" +
                    $" owner=0x{NativeMethods.Owner(foreground).ToInt64():X}" +
                    $" rootOwner=0x{head.ToInt64():X}");
                return;
            }

            // 排除项（默认含资源管理器）：它既不是「要保留的那个窗口」，也不是「要收掉的别的窗口」。
            // 属于场景的窗口（group > 0）不在此列，仍按场景整体处理。
            var rules = Exclusions;
            Dictionary<uint, (string Path, string Name)> processCache = null;
            bool IsExcluded(IntPtr hwnd)
            {
                if (rules == null || rules.Count == 0) return false;
                bool explorer = WindowEnumerator.IsExplorerWindow(hwnd);
                // 只有内置项时不需要读进程信息（explorer 按窗口类名就能认）。
                if (!HasUserRules(rules))
                    return WindowExclusion.MatchesAny(rules, explorer, "", "");

                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                processCache ??= new Dictionary<uint, (string Path, string Name)>();
                if (!processCache.TryGetValue(pid, out var info))
                {
                    info = WindowExclusion.ProcessInfoOf(pid);
                    processCache[pid] = info;
                }
                return WindowExclusion.MatchesAny(rules, explorer, info.Path, info.Name);
            }

            if (ShouldSkipExcludedWindow(group, IsExcluded(foreground)))
            {
                Diagnostics.Info("single",
                    $"忽略前台 0x{foreground.ToInt64():X}（排除项）" +
                    $" class={NativeMethods.ClassName(foreground)}");
                return;
            }

            // 每块屏幕完全独立：只处理前台所在的这块屏。
            IntPtr monitor = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);
            monitorDevice = NativeMethods.MonitorDeviceOf(foreground);

            // 场景只属于它自己的那块屏幕：前台虽然属于某个场景，但若这块屏不是它的归属屏
            // （成员被用户拖过来了），它在本屏只是一个普通窗口，不展开 / 收起它的同伴。
            bool activeScene = group > 0 && SceneOnScreen(group, monitorDevice);

            var collapsed = new HashSet<int>();
            // 辅助窗口的宿主进程（飞书媒体中心的父进程 / 微信小程序宿主的父进程等）：
            // 它们的窗口不能被当成「别的窗口」最小化，否则打开图片 / 视频时主窗口会消失。
            var hostPids = ProcessSceneLink.AncestorPids(foreground);
            foreach (var candidate in WindowEnumerator.Enumerate(includeMinimized: false))
            {
                if (candidate.Handle == foreground) continue;
                // 别的屏幕上的窗口一根手指都不能碰：另一块屏在做什么与本屏无关。
                if (NativeMethods.MonitorFromWindow(candidate.Handle, NativeMethods.MONITOR_DEFAULTTONEAREST) != monitor) continue;

                int memberGroup = groupOf != null ? groupOf(candidate.Handle) : 0;
                // 场景成员不参与排除判定：先看组，只有不在场景里才需要读进程信息。
                if (memberGroup == 0 && IsExcluded(candidate.Handle)) continue;
                if (memberGroup > 0)
                {
                    // 别的屏幕的场景：即使有成员被拖到这块屏，也不参与本屏的判定。
                    if (!SceneOnScreen(memberGroup, monitorDevice)) continue;
                    // 同一场景的同伴不最小化：一个场景就是「一个单窗口」。
                    if (activeScene && memberGroup == group) continue;

                    // 场景是整体：收起时连被拖到别的屏幕的成员一起收（归属屏不变）。
                    if (collapsed.Add(memberGroup) && CollapseGroup != null)
                        collapsedCount += CollapseGroup(memberGroup);
                    continue;
                }

                // 辅助窗口的宿主（飞书 / 微信主进程等）不是「别的窗口」：打开图片、视频时
                // 不能把宿主主窗口最小化掉，否则用户看到的就是「主窗口被隐藏」。
                if (hostPids.Contains(candidate.ProcessId))
                {
                    hostedCount++;
                    Diagnostics.Info("single",
                        $"保留宿主 0x{candidate.Handle.ToInt64():X}(pid={candidate.ProcessId}) " +
                        $"class={candidate.ClassName} 前台=0x{foreground.ToInt64():X}" +
                        $"（辅助窗口的宿主）");
                    continue;
                }

                // 场景应用的辅助窗口（微信图片 / 小程序宿主等）也不是「别的窗口」：
                // 当前场景的不动；别的屏幕的场景的辅助窗口同样不归这块屏管。
                if (GroupOfRelated != null)
                {
                    int related = GroupOfRelated(candidate.Handle);
                    if (related != 0)
                    {
                        if (!SceneOnScreen(related, monitorDevice)) continue;
                        if (activeScene && related == group) continue;
                    }
                }

                // 必须用 SW_SHOWMINNOACTIVE 而不是 SW_MINIMIZE：后者会「激活 Z 序里的下一个顶层窗口」，
                // 最小化一圈下来焦点会被抢走，用户看到的是场景窗口刚置前又被顶下去。
                if (NativeMethods.ShowWindow(candidate.Handle, NativeMethods.SW_SHOWMINNOACTIVE))
                    minimized++;
            }
        }
        finally
        {
            _applying = false;
        }

        if (minimized + collapsedCount + hostedCount > 0)
        {
            uint foregroundPid = 0;
            NativeMethods.GetWindowThreadProcessId(foreground, out foregroundPid);
            Diagnostics.Info("single",
                $"应用 前台=0x{foreground.ToInt64():X}({NativeMethods.ClassName(foreground)} pid={foregroundPid}) " +
                $"owner=0x{NativeMethods.Owner(foreground).ToInt64():X} screen={monitorDevice ?? "(未知)"} " +
                $"最小化={minimized} 收回场景={collapsedCount} 宿主={hostedCount}");
        }
        if (minimized + collapsedCount > 0) Applied?.Invoke(minimized + collapsedCount);
    }

    /// <summary>
    /// 这个场景是不是属于这块屏幕。归属屏在收纳 / 创建时确定，拖动成员不改变；
    /// 归属屏未知（旧运行时组）时按旧行为「算本屏」，由调用方继续按成员位置处理。
    /// </summary>
    private bool SceneOnScreen(int groupId, string monitorDevice)
    {
        string screen = GroupScreen?.Invoke(groupId);
        if (string.IsNullOrEmpty(screen)) return true;
        return DisplayRules.BelongsTo(monitorDevice, screen);
    }

    /// <summary>
    /// 前台窗口该归到哪个场景组：自己就是成员就用自己，否则看窗口簇的簇主
    /// （沿 owner 链走到第一个无 owner 的顶层窗口 = 用户真正在用的那个窗口）。
    /// 簇是 1.9.0 引入的模型：弹窗随簇主一起隐藏 / 恢复，所以判断「用户切到了哪一组」
    /// 也必须按簇主算，否则点一下弹窗就等于「切换到了场景外」。
    /// </summary>
    public static int ResolveGroup(
        IntPtr foreground, Func<IntPtr, int> groupOf, Func<IntPtr, IntPtr> rootOwner)
    {
        if (groupOf == null) return 0;

        int group = groupOf(foreground);
        if (group != 0) return group;
        if (rootOwner == null) return 0;

        IntPtr head = rootOwner(foreground);
        if (head == IntPtr.Zero || head == foreground) return 0;
        return groupOf(head);
    }

    /// <summary>
    /// 这次前台变化要不要当作「用户切换了窗口」。
    /// 前台既不属于任何场景、又按准入规则连窗口都算不上（工具窗 / 被拥有的弹窗 / 无标题 /
    /// cloaked），那它只是某个应用的临时 UI——不处理，绝不能因此把别的场景收起来。
    /// </summary>
    public static bool ShouldIgnoreForeground(int group, string exclusionReason)
        => group == 0 && exclusionReason != null;

    /// <summary>
    /// 排除项要不要跳过：只对不属于任何场景的窗口生效。
    /// 属于场景的窗口（含资源管理器）仍按场景整体处理，不能因为被排除就留在桌面上。
    /// </summary>
    public static bool ShouldSkipExcludedWindow(int group, bool excluded)
        => excluded && group == 0;

    /// <summary>排除列表里是否有用户添加的应用（决定要不要读进程信息）。</summary>
    private static bool HasUserRules(IReadOnlyList<WindowExclusion> rules)
    {
        foreach (var rule in rules)
        {
            if (rule != null && rule.Enabled && !rule.BuiltIn) return true;
        }
        return false;
    }

    private static bool IsEligibleForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd)) return false;
        if (WindowEnumerator.IsOwnProcessWindow(hwnd)) return false;
        if (WindowEnumerator.IsShellWindow(hwnd)) return false;
        if (NativeMethods.IsCloaked(hwnd, out _)) return false;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        _coalesce.Stop();
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        if (_moveHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_moveHook);
            _moveHook = IntPtr.Zero;
        }
    }
}
