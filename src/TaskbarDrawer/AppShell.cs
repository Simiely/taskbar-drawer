using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TaskbarDrawer.Core;
using TaskbarDrawer.Core.Rules;
using TaskbarDrawer.Interop;
using TaskbarDrawer.UI;

namespace TaskbarDrawer;

/// <summary>
/// 组合根与生命周期。
/// 这一片只负责三件事：按顺序装配服务、把界面刷新合并成一次、以及退出 / 崩溃时的兜底。
/// 具体命令在 <c>AppShell.Commands.cs</c>，入口订阅与窗口管理在 <c>AppShell.Wiring.cs</c>。
///
/// 所有开关只有这一份状态，托盘勾选 / 面板开关 / 配置文件始终与它同步。
/// </summary>
public sealed partial class AppShell : IDisposable
{
    private AppSettings _settings;
    private StashAllService _stash;
    private SceneIconPainter _sceneIcons;
    private WorkspaceService _workspace;
    private SceneService _scenes;
    private SingleWindowMode _single;
    private HotkeyManager _hotkeys;
    private HwndSource _messageHost;
    private TrayService _tray;
    private TaskbarChipLayer _chips;
    private MainPanelWindow _panel;
    private SceneEditorWindow _sceneEditor;
    private string _sceneEditorId;
    private HelpWindow _helpWindow;
    private RuleSet _rules;
    private RuleRunner _runner;
    private HotkeyDefinition _stashHotkey;
    private DispatcherTimer _pruneTimer;
    private DispatcherTimer _eventPrune;
    private WindowEventWatcher _windowEvents;
    private DispatcherTimer _reattachTimer;
    private IntPtr _windowShownHook;
    private NativeMethods.WinEventProc _windowShownProc;
    private readonly HashSet<long> _pendingShown = new();
    private Dispatcher _dispatcher;
    private EventWaitHandle _showSignal;
    private ManualResetEvent _signalStop;
    private Thread _signalThread;
    private bool _disposed;
    private bool _showPending;
    /// <summary>本轮消息里已经排过一次界面刷新：Changed 和方法末尾的刷新合并成一次重建。</summary>
    private bool _surfaceUpdateScheduled;
    /// <summary>正在打开（等窗口出现）的场景：同一场景连点两次会重复启动实例。</summary>
    private readonly HashSet<string> _restoringScenes = new(StringComparer.Ordinal);

    /// <summary>
    /// 版本标识：csproj 的 Version + MSBuild 写的构建时间。
    /// 托盘菜单和启动提示都显示它，用来确认当前跑的是不是最新构建。
    /// </summary>
    private static readonly string InformationalVersion = ReadInformationalVersion();

    private static string ReadInformationalVersion()
    {
        var attribute = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
            typeof(AppShell).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
        return string.IsNullOrWhiteSpace(attribute?.InformationalVersion) ? "1.1.4" : attribute.InformationalVersion;
    }

    /// <summary>提示文案里只带短版本号（构建时间太长，放托盘菜单里）。</summary>
    private static string ShortVersion => InformationalVersion.Split(' ', '+')[0];

    public void Initialize()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        // 先建唤起事件：启动期间用户再点一次 exe 也能落到这里，而不是静默失败。
        StartShowSignal();

        // 装配顺序即依赖，别随手调整：
        // 1) 服务与设置最先（后面每一层都要读它）；
        // 2) 单窗口 → 胶囊按「谁先建谁先被引用」排（单窗口的回调会用 _stash）；
        // 3) 托盘必须在热键注册之前——注册失败要弹托盘气泡；
        // 4) 面板必须最后建：它要读已生效的热键文案和外观设置。
        WireServices();
        WireSingleWindow();
        WireReattach();
        WireChips();
        WireHotkeyHost();
        WireTray();
        WireRules();
        RegisterHotkeys();
        WirePanel();

        _single.Start();
        SetSingleWindowMode(_settings.SingleWindowMode, persist: false);

        // 先修一次 Run 项里的旧 exe 路径：升级后它仍指向旧目录，开机拉的是旧副本。
        Safe("autostart.repair", AutoStart.Repair);
        bool autoStart = AutoStart.IsSupported && AutoStart.IsEnabled();
        _settings.AutoStart = autoStart;
        _settings.Save();
        _tray.SetAutoStartChecked(autoStart);
        _panel.SetAutoStart(autoStart);
        _panel.SetExcludeExplorer(_settings.ExcludeExplorerFromSingleWindow);
        _panel.SetSceneIconSizes(_settings.SceneAppIconSize, _settings.SceneBadgeSize);

        _tray.SetStatus($"{_stashHotkey.Gesture} 新建场景 / 展开");

        // Win11 默认把新托盘图标收进折叠区（^），首次运行提示一下入口在哪、热键是什么。
        if (!_settings.ShowedWelcome)
        {
            _settings.ShowedWelcome = true;
            _settings.Save();
            _tray.ShowBalloon(
                $"{_stashHotkey.Gesture} 把当前窗口收成一个场景；这块屏幕上没窗口时再按一次展开最近的场景。" +
                "双击托盘图标打开主面板（单击切换单窗口模式）。");
        }

        UpdateSurface();

        // 启动期间收到过「打开面板」的唤起：等面板和界面都就绪再补开，别把这次点击吞掉。
        if (_showPending)
        {
            _showPending = false;
            OpenPanel();
        }

        // 启动反馈：底部浮窗一闪，确认程序已经在跑、热键是什么。
        // 场景文件损坏 / 无权限时把原因一并带出来，不能静默当成「没有场景」。
        string started = $"任务栏收纳 v{ShortVersion} 已启动 · {_stashHotkey.Gesture} 新建场景 / 展开";
        ToastWindow.Show(_workspace.LoadWarning == null ? started : $"{_workspace.LoadWarning} · {started}");
    }

    /// <summary>消息专用窗口：只用来收 WM_HOTKEY，不占屏幕、不进任务栏。</summary>
    private static HwndSource CreateMessageHost()
    {
        var parameters = new HwndSourceParameters("TaskbarDrawer.MessageHost")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = NativeMethods.HWND_MESSAGE
        };
        return new HwndSource(parameters);
    }

    /// <summary>注册收起 / 展开热键；配置里存的组合被别人占用时退回默认组合，并提示用户。</summary>
    private void RegisterHotkeys()
    {
        _stashHotkey = RegisterOrFallback(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey,
            HotkeyDefaults.StashAll, out string stashError);

        // 记录真正生效的组合（可能是回退后的默认值）。
        _settings.HotkeyModifiers = _stashHotkey.Modifiers;
        _settings.HotkeyVirtualKey = _stashHotkey.VirtualKey;
        _settings.Save();

        if (!string.IsNullOrEmpty(stashError))
            _tray.ShowBalloon($"快捷键注册失败：{stashError}。可用托盘菜单 / 主面板操作。");
    }

    private HotkeyDefinition RegisterOrFallback(
        uint modifiers, uint virtualKey, HotkeyDefinition fallback, out string error)
    {
        error = null;
        var candidate = new HotkeyDefinition(fallback.Id, modifiers, virtualKey);
        var registration = _hotkeys.Register(candidate);
        if (registration.Success) return candidate;

        error = $"{candidate.Gesture} {registration.ErrorText}";
        var retry = _hotkeys.Register(fallback);
        return retry.Success ? fallback : candidate;
    }

    /// <summary>
    /// 局部异常隔离：记录日志后继续运行。
    /// 只包「可恢复的局部失败」（某个胶囊没刷新、某次收纳没成功），
    /// 真正的损坏状态仍然交给 Program 的全局处理器走兜底退出。
    /// </summary>
    private void Safe(string scope, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Diagnostics.Write(scope, ex);
        }
    }

    /// <summary>
    /// 请求刷新界面：一次操作里 Changed 事件和方法末尾的刷新可能各触发一次，
    /// 合并到下一轮 Dispatcher，只重建一次面板 / 托盘 / 胶囊。
    /// </summary>
    private void RequestSurfaceUpdate()
    {
        if (_disposed || _surfaceUpdateScheduled) return;
        _surfaceUpdateScheduled = true;
        try
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                _surfaceUpdateScheduled = false;
                if (!_disposed) Safe("surface", UpdateSurface);
            }), DispatcherPriority.Background);
        }
        catch
        {
            // 退出过程中 Dispatcher 可能已经关闭：放弃这次刷新即可。
            _surfaceUpdateScheduled = false;
        }
    }

    private void UpdateSurface()
    {
        if (_disposed) return;

        // 隐藏集合的任何变化（收纳 / 展开 / 外部还原 / 剪枝）都会走到这里：
        // 立刻落一份回收记录，别把「刚藏完就强杀」的窗口留给下一次启动。
        // Save 内部按句柄签名去重，重复调用不会产生多余的磁盘写入。
        WindowRecovery.Save(_stash.Items);

        // 只订阅自己管的那些进程：钩子是按 PID 注册期过滤的，装到无关进程上纯属浪费。
        SyncWatchedProcesses();

        var groups = _stash.Groups();
        // 面板 / 托盘只列场景：dormant 是只剩快照、没有窗口的那些。
        var dormant = ChipProjection.Dormant(groups, _workspace.Summaries());
        _chips.Sync(ChipProjection.Chips(groups, dormant));
        _tray.SetScenes(groups);
        _tray.SetHotkeys(_stashHotkey.Gesture);
        _panel.SetScenes(groups, dormant, _stash.Count);
        _panel.SetHotkeyText(_stashHotkey.Gesture);
        // 场景编辑窗口开着时同步刷新；场景被删掉会在里面自动关闭。
        Safe("sceneEditor.refresh", RefreshSceneEditor);
    }

    /// <summary>
    /// 被跟踪窗口的销毁 / 显示 / 隐藏事件。以前唯一的信号是 2.5 s 轮询：
    /// 在任务管理器里结束一个成员，胶囊上的计数最坏要等一个整周期才变。
    /// 现在由事件触发，从窗口消失到界面更新稳定在 150–250 ms。
    /// </summary>
    private void OnWindowEvent(IntPtr hwnd)
    {
        if (_disposed || _eventPrune == null) return;

        // 只认记录里的窗口。钩子已经按进程过滤，但一个进程会有一堆自己的辅助窗口
        // （WPF 的 HwndWrapper、浏览器每个标签页、运行时的隐藏消息窗），它们销毁时
        // 一样发 DESTROY，实测关掉一个进程能收到十几条。这里一次字典查找就能全部丢掉。
        if (!_stash.Contains(hwnd)) return;

        // 合并突发事件：最小化 / 还原常常连着改可见性和位置，一串事件只需对账一次。
        _eventPrune.Stop();
        _eventPrune.Start();
    }

    /// <summary>事件合并窗口到期：跑一次状态对账，真的变了才刷界面（Prune 内部按需抛 Changed）。</summary>
    private void FlushWindowEvents()
    {
        _eventPrune?.Stop();
        if (_disposed) return;
        _stash.Prune();
    }

    /// <summary>
    /// 新窗口监听：场景成员的程序被关掉又重开时，新窗口的句柄不在运行时组里，
    /// 快照里却还留着它那一项。只听全局的顶层窗口 SHOW（过滤 OBJID / 本进程），
    /// 150 ms 合并后交给 <see cref="SceneService.TryReattach"/> 认回场景。
    ///
    /// 全局监听是必要的：重新打开的程序通常是一个新进程，按 PID 装的
    /// <see cref="WindowEventWatcher"/> 根本收不到它的事件。
    /// </summary>
    private void WireReattach()
    {
        _reattachTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _reattachTimer.Tick += (_, _) => Safe("reattach.flush", FlushShownWindows);

        try
        {
            _windowShownProc = OnWindowShown;
            _windowShownHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_SHOW,
                IntPtr.Zero, _windowShownProc, 0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
            if (_windowShownHook == IntPtr.Zero)
                Diagnostics.Info("reattach", "新窗口事件监听未就绪，退回 5 s 巡检");
        }
        catch (Exception ex)
        {
            Diagnostics.Write("reattach hook", ex);
        }
    }

    /// <summary>全局 SHOW 回调：只做最便宜的过滤与入队，真正的窗口查询放在合并定时器里。</summary>
    private void OnWindowShown(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (_disposed || hwnd == IntPtr.Zero) return;
            if (idObject != NativeMethods.OBJID_WINDOW || idChild != 0) return;
            if (WindowEnumerator.IsOwnProcessWindow(hwnd)) return;

            _pendingShown.Add(hwnd.ToInt64());
            _reattachTimer?.Stop();
            _reattachTimer?.Start();
        }
        catch (Exception ex)
        {
            Diagnostics.Write("reattach event", ex);
        }
    }

    private void FlushShownWindows()
    {
        _reattachTimer?.Stop();
        if (_disposed) return;

        var pending = _pendingShown.ToArray();
        _pendingShown.Clear();
        foreach (long raw in pending)
        {
            if (_disposed) return;
            var hwnd = new IntPtr(raw);
            if (_stash.Contains(hwnd)) continue;   // 已在本程序的场景里，不需要认领
            _scenes.TryReattach(hwnd);
        }
    }

    /// <summary>
    /// 事件钩子装不上时的 5 s 兜底：只有「有展开场景缺成员」时才枚举窗口，
    /// 正常情况下这条分支一次都不会跑。
    /// </summary>
    private void ReattachVisibleCandidates()
    {
        foreach (var group in _stash.Groups())
        {
            if (!group.Expanded || string.IsNullOrEmpty(group.SavedId)) continue;
            var saved = _workspace.Find(group.SavedId);
            if (saved == null) continue;
            int live = _stash.Items.Count(i => i.GroupId == group.Id && NativeMethods.IsWindow(i.Handle));
            if (saved.Windows.Count <= live) continue;

            foreach (var window in WindowEnumerator.Enumerate(includeMinimized: false))
                _scenes.TryReattach(window.Handle);
        }
    }

    /// <summary>把订阅集合同步成「当前记录里的进程」。钩子按 PID 注册，进程退出后跟着消失。</summary>
    private void SyncWatchedProcesses()
    {
        if (_windowEvents == null) return;

        var pids = new HashSet<uint>();
        foreach (var item in _stash.Items)
        {
            if (item.ProcessId != 0) pids.Add(item.ProcessId);
        }
        _windowEvents.Sync(pids);
    }

    /// <summary>
    /// 延时执行一次，不阻塞 UI 线程。关闭场景要用它：WM_CLOSE 是投递，过一会儿才知道关掉了几个。
    /// </summary>
    private void WaitThen(TimeSpan delay, Action action)
    {
        try
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = delay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_disposed) return;
                Safe("delay", action);
            };
            timer.Start();
        }
        catch
        {
            // 退出过程中调度器已关闭：这次延时动作直接放弃。
        }
    }

    /// <summary>
    /// 第二个实例会 Set 这个命名事件：把「打开主面板」转成 UI 线程上的动作。
    /// 用事件而不是窗口广播，是因为消息专用窗口收不到 HWND_BROADCAST。
    /// </summary>
    private void StartShowSignal()
    {
        try
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowPanelEventName);
            _signalStop = new ManualResetEvent(false);
            _signalThread = new Thread(() =>
            {
                var handles = new WaitHandle[] { _showSignal, _signalStop };
                while (true)
                {
                    try
                    {
                        if (WaitHandle.WaitAny(handles) != 0) break;
                    }
                    catch (ObjectDisposedException)
                    {
                        // 退出时句柄可能先被释放：这是后台线程，异常逃出去会终止进程。
                        break;
                    }
                    try { _dispatcher.BeginInvoke(new Action(ShowFromSignal)); }
                    catch { /* 退出过程中调度器已关闭 */ }
                }
            })
            {
                IsBackground = true,
                Name = "TaskbarDrawer.ShowPanelSignal"
            };
            _signalThread.Start();
        }
        catch
        {
            // 事件创建失败只影响「第二次启动唤起面板」，不影响主功能。
        }
    }

    /// <summary>第二个实例唤起主面板：面板还没建好就先记下，Initialize 末尾补开。</summary>
    private void ShowFromSignal()
    {
        if (_disposed) return;
        if (_panel == null)
        {
            _showPending = true;
            return;
        }
        OpenPanel();
    }

    /// <summary>崩溃 / 未处理异常时的兜底：把隐藏的窗口交回任务栏（只碰 Win32，不碰 WPF）。</summary>
    public void EmergencyRelease()
    {
        try { _stash?.ReleaseAll(); } catch { /* 崩溃路径不再抛异常 */ }
    }

    /// <summary>
    /// 以管理员身份重启：管理员程序的窗口（如 HiBit）隐藏 / 最小化要跨完整性级别，
    /// 普通权限实例调用会被 UIPI 拒绝，只有同级进程才能收纳它们。
    /// </summary>
    private void RestartElevated()
    {
        string exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Feedback("找不到程序路径，无法以管理员身份重启");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--elevated-restart",
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Exception ex)
        {
            // 用户在 UAC 里点了取消：保持当前实例继续运行。
            Diagnostics.Write("restart elevated", ex);
            Feedback("已取消以管理员身份重启");
            return;
        }

        Exit();
    }

    public void Exit()
    {
        if (_disposed) return;
        _disposed = true;

        // 场景详情 / 说明是独立窗口：不先关掉，后面的关闭流程会卡在它们的消息循环里。
        try { _sceneEditor?.Close(); } catch { /* 退出路径不再抛 */ }
        try { _helpWindow?.Close(); } catch { /* 退出路径不再抛 */ }

        _pruneTimer?.Stop();
        _eventPrune?.Stop();
        _reattachTimer?.Stop();
        try
        {
            if (_windowShownHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_windowShownHook);
                _windowShownHook = IntPtr.Zero;
            }
        }
        catch { /* 退出路径不再抛 */ }
        try { _windowEvents?.Dispose(); } catch { }
        try { _signalStop?.Set(); } catch { }
        try { _signalThread?.Join(200); } catch { }
        try { _showSignal?.Dispose(); } catch { }
        try { _signalStop?.Dispose(); } catch { }

        // ReleaseAll 会清空记录，先记下还有多少窗口藏着，才能知道有没有漏放。
        int hidden = 0;
        foreach (var item in _stash.Items)
        {
            if (item.Hidden) hidden++;
        }

        try { _single.SetEnabled(false); _single.Dispose(); } catch { }
        int released = 0;
        try { released = _stash.ReleaseAll(); } catch { }

        // 只有确认全部放回任务栏才作废回收记录；漏放的留给下次启动继续救。
        if (released >= hidden) WindowRecovery.Clear();

        // 释放顺序无关紧要，但任何一步抛异常都不能跳过 Shutdown，否则进程会残留。
        try { _hotkeys?.Dispose(); } catch { }
        try { _messageHost?.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _chips?.Dispose(); } catch { }
        try
        {
            if (_panel != null)
            {
                _panel.AllowClose = true;
                _panel.Close();
            }
        }
        catch { /* 退出路径不再抛 */ }

        try { Application.Current?.Shutdown(); } catch { }
    }

    public void Dispose() => Exit();
}
