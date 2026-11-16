using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TaskbarDrawer.Core;
using TaskbarDrawer.Core.Rules;
using TaskbarDrawer.Interop;
using TaskbarDrawer.UI;

namespace TaskbarDrawer;

/// <summary>
/// 组合根：托盘、热键、主面板、单窗口模式、收纳服务、任务栏胶囊层。
/// 所有开关只有这一份状态，托盘勾选 / 面板开关 / 配置文件始终与它同步。
/// </summary>
public sealed class AppShell : IDisposable
{
    private AppSettings _settings;
    private StashAllService _stash;
    private SceneIconPainter _sceneIcons;
    private WorkspaceService _workspace;
    private SingleWindowMode _single;
    private HotkeyManager _hotkeys;
    private HwndSource _messageHost;
    private TrayService _tray;
    private TaskbarChipLayer _chips;
    private MainPanelWindow _panel;
    private SceneEditorWindow _sceneEditor;
    private string _sceneEditorId;
    private RuleSet _rules;
    private RuleRunner _runner;
    private HotkeyDefinition _stashHotkey;
    private DispatcherTimer _pruneTimer;
    private Dispatcher _dispatcher;
    private EventWaitHandle _showSignal;
    private ManualResetEvent _signalStop;
    private Thread _signalThread;
    private bool _disposed;
    private bool _showPending;
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
        _settings = AppSettings.Load();

        _sceneIcons = new SceneIconPainter(_settings.SceneAppIconSize, _settings.SceneBadgeSize);
        _stash = new StashAllService(_sceneIcons);
        // Changed 是同步广播：UI 构建一旦抛异常，会沿「业务方法 → RunRules → 入口 Safe」冒泡，
        // 结果是状态已改、界面没刷、日志还记在错误的 scope 上。隔离必须在订阅处收口。
        _stash.Changed += () => Safe("surface", UpdateSurface);

        // 场景：收纳时立刻抓快照落盘，之后可一键关闭 / 还原，跨启动保留。
        _workspace = new WorkspaceService();
        // 写盘失败不能静默：场景还在内存里能用，但重启就会丢，必须当场告诉用户。
        // 延后一拍再提示：失败往往发生在「已收起 / 已改名」的成功提示之前，同步弹会被覆盖。
        _workspace.SaveFailed += message => Safe("workspace.save", () =>
            _dispatcher.BeginInvoke(new Action(() => Feedback(message)), DispatcherPriority.Background));

        // 被收纳的窗口如果被外部还原 / 关闭，计数要跟着变，不能让胶囊一直显示旧数字。
        _pruneTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        _pruneTimer.Tick += (_, _) => Safe("prune", () =>
        {
            if (_disposed) return;
            if (_stash.Count > 0) _stash.Prune();
            // 兜底落盘：隐藏集合变化的第一时间由 UpdateSurface 写，这里只补外部还原 / 关闭。
            WindowRecovery.Save(_stash.Items);
        });
        _pruneTimer.Start();

        _single = new SingleWindowMode(Dispatcher.CurrentDispatcher);
        // 打开开关时这块屏幕上没有可接管的窗口：提示用户，而不是默默什么都不做。
        _single.NoCandidateOnScreen += () => Safe("single.none", () =>
            Feedback("这块屏幕上没有可接管的窗口"));
        // 让单窗口模式认识场景：同一场景展开的窗口当成一个整体，而不是互斥的多个窗口。
        _single.GroupOf = hwnd => _stash.GroupIdOf(hwnd);
        // 切到别的场景 / 别的窗口时，别的场景整组收回胶囊（而不是最小化成一排任务栏按钮）。
        _single.CollapseGroup = (groupId, monitorDevice) => _stash.StashGroup(groupId, monitorDevice);
        _single.Applied += count => Safe("single.applied", () =>
        {
            if (count > 0) _tray?.SetStatus($"单窗口模式：已收起 {count} 个窗口");
        });

        _chips = new TaskbarChipLayer();
        _chips.Clicked += entry => Safe("chip.click", () => OnChipClicked(entry));
        _chips.RenameSceneRequested += entry => Safe("chip.renameScene", () => RenameSavedGroup(entry.SavedId));
        _chips.CloseWindowsRequested += entry => Safe("chip.closeScene", () => CloseScene(entry.SavedId));
        _chips.DissolveSceneRequested += entry => Safe("chip.dissolveScene", () => DissolveScene(entry.SavedId));
        _chips.ShowPanelRequested += () => Safe("chip.panel", OpenPanel);

        // 热键挂在消息专用窗口上：胶囊会随场景增删，不能拿它的句柄当宿主。
        _messageHost = CreateMessageHost();
        _hotkeys = new HotkeyManager(_messageHost.Handle, _messageHost);
        _hotkeys.Pressed += OnHotkeyPressed;

        // 上次如果是被强杀 / 断电结束的，这里把还处在隐藏状态的窗口交回任务栏。
        Safe("recovery", () => WindowRecovery.Recover());

        _tray = new TrayService();
        // 每个入口都过 Safe：托盘 / 面板的菜单点击是用户随时会触发的路径，
        // 一次局部失败不应该把整个常驻进程带走（退出路径本身不包）。
        _tray.OpenPanelRequested += () => Safe("tray.panel", OpenPanel);
        _tray.ToggleSingleWindowRequested += () => Safe("tray.toggleSingle", () => SetSingleWindowMode(!_single.Enabled));
        _tray.StashRequested += () => Safe("tray.stash", () => NewScene());
        _tray.ExpandLatestRequested += () => Safe("tray.expandLatest", () => ExpandLatest());
        _tray.SceneRequested += id => Safe("tray.scene", () => ToggleScene(id));
        _tray.ExpandAllRequested += () => Safe("tray.expandAll", () => ExpandAll());
        _tray.SingleWindowRequested += value => Safe("tray.singleWindow", () => SetSingleWindowMode(value));
        _tray.AutoStartRequested += value => Safe("tray.autoStart", () => SetAutoStart(value));
        _tray.ExitRequested += Exit;
        _tray.RestartElevatedRequested += () => Safe("tray.elevate", RestartElevated);
        // 已经是管理员就没有「提权重启」的必要，菜单项只对普通权限实例显示。
        _tray.SetElevated(NativeMethods.IsCurrentProcessElevated);
        _tray.SetVersion(InformationalVersion);

        // 入口固定两个，流程写在 RuleSet.CreateDefault() 里，运行期只读。
        _rules = RuleSet.CreateDefault();
        _runner = CreateRuleRunner();

        RegisterHotkeys();

        _panel = new MainPanelWindow();
        // 面板位置跨启动记忆：上次关在哪，下次就开在哪。
        if (_settings.HasPanelPosition)
            _panel.SavedPosition = (_settings.PanelLeft, _settings.PanelTop);
        _panel.PositionChanged += (left, top) => Safe("panel.position", () =>
        {
            _settings.PanelLeft = left;
            _settings.PanelTop = top;
            _settings.HasPanelPosition = true;
            _settings.Save();
        });
        _panel.StashRequested += () => Safe("panel.stash", () => NewScene());
        _panel.SceneToggleRequested += id => Safe("panel.scene", () => ToggleScene(id));
        _panel.SceneCloseRequested += id => Safe("panel.closeScene", () => CloseRunningScene(id));
        _panel.SavedToggleRequested += id => Safe("panel.savedToggle", () => RestoreSavedGroup(id));
        _panel.SavedEditRequested += id => Safe("panel.savedEdit", () => OpenSceneEditor(id));
        _panel.ExpandLatestRequested += () => Safe("panel.expandLatest", () => ExpandLatest());
        _panel.ExpandAllRequested += () => Safe("panel.expandAll", () => ExpandAll());
        _panel.SingleWindowRequested += value => Safe("panel.singleWindow", () => SetSingleWindowMode(value));
        _panel.AutoStartRequested += value => Safe("panel.autoStart", () => SetAutoStart(value));
        _panel.HotkeyChangeRequested += (modifiers, key) => Safe("panel.hotkey", () => ChangeHotkey(modifiers, key));
        _panel.AppIconScaleRequested += value => Safe("panel.appIconSize", () => SetSceneIconSizes(value, null));
        _panel.SceneBadgeScaleRequested += value => Safe("panel.badgeSize", () => SetSceneIconSizes(null, value));

        _single.Start();
        SetSingleWindowMode(_settings.SingleWindowMode, persist: false);

        bool autoStart = AutoStart.IsSupported && AutoStart.IsEnabled();
        _settings.AutoStart = autoStart;
        _settings.Save();
        _tray.SetAutoStartChecked(autoStart);
        _panel.SetAutoStart(autoStart);
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
        var candidate = new HotkeyDefinition(fallback.Id, fallback.Name, modifiers, virtualKey);
        var registration = _hotkeys.Register(candidate);
        if (registration.Success) return candidate;

        error = $"{candidate.Gesture} {registration.ErrorText}";
        var retry = _hotkeys.Register(fallback);
        return retry.Success ? fallback : candidate;
    }

    private void OnHotkeyPressed(int id)
    {
        if (_disposed || id != _stashHotkey.Id) return;

        Safe("hotkey", () =>
        {
            // 只在录制新热键时忽略，避免把用户按下的组合当操作；面板开着时也要正常收起 / 展开。
            if (_panel != null && _panel.Recording) return;
            // 屏幕在执行前算一次：收纳之后前台窗口就没了，动作里再取会取错屏。
            string monitor = ActiveMonitorDevice();
            if (monitor == null)
            {
                // 判断不出屏幕就什么都不做：宁可让用户重试，也不能把「不知道哪块屏」当成「所有屏」。
                Feedback("无法判断当前屏幕，请把鼠标移到目标屏幕后重试");
                return;
            }
            RunRules(RuleTriggerKind.Hotkey, () => new RuleFacts
            {
                MonitorDevice = monitor,
                HasUntrackedWindows = _stash.HasUntrackedVisibleWindows(monitor),
                HasExpandedScene = _stash.Groups().Any(g =>
                    g.Expanded && _stash.MonitorOfGroup(g.Id) == monitor)
            });
        });
    }

    /// <summary>
    /// 用户此刻正在操作的屏幕：前台窗口所在显示器。
    /// 前台是本程序 / 桌面 / 任务栏时（点面板、托盘菜单），退回鼠标所在显示器。
    /// </summary>
    private string ActiveMonitorDevice()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            // 面板 / 提示窗是自家窗口：它在哪块屏，就当成用户在哪块屏操作。
            if (WindowEnumerator.IsOwnProcessWindow(foreground))
                return NativeMethods.MonitorDeviceOf(foreground);

            if (!WindowEnumerator.IsShellWindow(foreground))
            {
                string device = NativeMethods.MonitorDeviceOf(foreground);
                if (device != null) return device;
            }
        }
        return NativeMethods.MonitorDeviceOfCursor();
    }

    /// <summary>
    /// 规则执行的公共入口：先剪掉已被外部关闭的窗口，再算事实、走规则。
    /// 顺序很重要——事实必须在剪枝之后算，否则热键会按过期的窗口数量做判断。
    /// </summary>
    private void RunRules(RuleTriggerKind trigger, Func<RuleFacts> buildFacts)
    {
        if (_disposed) return;
        _stash.Prune();
        _runner.Run(trigger, buildFacts());
    }

    /// <summary>
    /// 把规则里的卡片接到具体操作上。新增一种卡片 = 目录加一行 + 这里绑一个处理函数。
    /// </summary>
    private RuleRunner CreateRuleRunner()
    {
        var runner = new RuleRunner(() => _rules);

        runner.On(RuleCatalog.ConditionHasUntracked, facts => facts.HasUntrackedWindows);
        runner.On(RuleCatalog.ConditionHasExpandedScene, facts => facts.HasExpandedScene);
        runner.On(RuleCatalog.ConditionSceneRunning, facts => facts.SceneRunning);

        runner.Do(RuleCatalog.ActionNewScene, (facts, _) => NewScene(facts.MonitorDevice));
        runner.Do(RuleCatalog.ActionExpandLatest, (facts, _) => ExpandLatest(facts.MonitorDevice));
        runner.Do(RuleCatalog.ActionExpandAll, (facts, _) => ExpandAll(facts.MonitorDevice));
        runner.Do(RuleCatalog.ActionCollapseExpanded, (facts, _) => CollapseExpandedScenes(facts.MonitorDevice));
        runner.Do(RuleCatalog.ActionToggleScene, (facts, _) => ToggleScene(facts.GroupId));
        runner.Do(RuleCatalog.ActionRestoreScene, (facts, _) =>
        {
            if (!string.IsNullOrEmpty(facts.SavedId)) RestoreSavedGroup(facts.SavedId);
        });
        runner.Do(RuleCatalog.ActionOpenPanel, (_, _) => OpenPanel());
        runner.Do(RuleCatalog.ActionToast, (facts, step) =>
        {
            string text = step.Args != null && step.Args.TryGetValue("text", out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : "已执行";
            ToastWindow.Show(text, facts.MonitorDevice);
        });

        return runner;
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

    private void ChangeHotkey(uint modifiers, uint virtualKey)
    {
        if (_disposed) return;

        var candidate = new HotkeyDefinition(_stashHotkey.Id, _stashHotkey.Name, modifiers, virtualKey);
        var registration = _hotkeys.Change(candidate);
        if (!registration.Success)
        {
            string error = $"{candidate.Gesture} 注册失败：{registration.ErrorText}";
            if (!string.IsNullOrEmpty(registration.RollbackError))
                error += $"；{registration.RollbackError}";
            _panel.ApplyHotkeyResult(false, null, error);
            return;
        }

        _stashHotkey = candidate;
        _settings.HotkeyModifiers = modifiers;
        _settings.HotkeyVirtualKey = virtualKey;
        _settings.Save();
        _panel.ApplyHotkeyResult(true, candidate.Gesture, null);
        Feedback($"快捷键已更新 · {candidate.Gesture}");
        UpdateSurface();
    }

    /// <summary>统一的用户反馈：底部浮窗弹一条，随后自动消失；托盘提示同步更新。</summary>
    /// <param name="monitorDevice">提示出现在哪块屏幕；null = 鼠标所在屏幕。</param>
    private void Feedback(string message, string monitorDevice = null)
    {
        ToastWindow.Show(message, monitorDevice);
        _tray?.SetStatus(message);
    }

    /// <summary>把所有正展开的场景一起收回胶囊（规则里的「收起所有已展开的场景」）。</summary>
    private void CollapseExpandedScenes(string monitorDevice = null)
    {
        if (_disposed) return;

        // 多显示器：只在当前这块屏幕上收，另一块屏的场景保持展开。屏幕未知时不动任何窗口。
        monitorDevice ??= ActiveMonitorDevice();
        if (monitorDevice == null)
        {
            Feedback("无法判断当前屏幕，请把鼠标移到目标屏幕后重试");
            return;
        }

        _single.SuspendTemporarily();
        int hidden = 0;
        // 不按「场景归属屏」整组过滤：场景可能有窗口被拖到另一块屏，
        // 交给 StashGroup 按屏收紧，跨屏成员留在原屏幕。
        foreach (var group in _stash.Groups().Where(g => g.Expanded).ToArray())
            hidden += _stash.StashGroup(group.Id, monitorDevice);

        Feedback(hidden > 0 ? $"已收起 {hidden} 个窗口" : "这块屏幕上没有可收起的窗口", monitorDevice);
        UpdateSurface();
    }

    private void NewScene(string monitorDevice = null)
    {
        if (_disposed) return;

        // 托盘 / 面板按钮进来时没有「前台业务窗口」，按鼠标位置判断当前屏幕。
        monitorDevice ??= ActiveMonitorDevice();
        if (monitorDevice == null)
        {
            Feedback("无法判断当前屏幕，请把鼠标移到目标屏幕后重试");
            return;
        }

        _single.SuspendTemporarily();
        int count = _stash.StashAll(out int groupId, monitorDevice);
        if (count > 0)
        {
            // 收纳即建场景：每次收起都立刻落一份快照，之后胶囊 / 面板只认场景。
            var saved = _workspace.SaveFrom(_stash, groupId, null);
            if (saved == null)
            {
                _stash.Release(groupId);
                Feedback("这些窗口读不到程序路径，无法存成场景", monitorDevice);
            }
            else
            {
                _stash.LinkSaved(groupId, saved.Id, saved.Name, saved.Badge);
                Feedback(WithElevationNote(
                    $"{BadgeText(saved.Badge)}已收起 {count} 个窗口 · 新建场景「{saved.Name}」"), monitorDevice);
            }
        }
        else if (_stash.LastBlockedByElevation > 0)
        {
            Feedback($"{_stash.LastBlockedByElevation} 个窗口以管理员身份运行，需以管理员身份重启本程序才能收纳", monitorDevice);
        }
        else
        {
            Feedback("这块屏幕上没有可收起的窗口", monitorDevice);
        }
        UpdateSurface();
    }

    /// <summary>
    /// 单窗口模式下展开一个场景时，把其它已展开的场景收回胶囊：
    /// 「每个场景就是一个单窗口」，切场景时上一个场景应该回到收纳区，而不是留在桌面上。
    /// </summary>
    private int CollapseOtherScenes(int keepGroupId)
    {
        if (!_single.Enabled) return 0;

        // 多显示器：只收起和这个场景在同一块屏幕上的窗口，另一块屏上的窗口保持不动。
        // 场景属于哪块屏都判断不出来时，一个都不收：宁可少收，不要跨屏误收。
        string monitor = _stash.MonitorOfGroup(keepGroupId);
        if (monitor == null) return 0;

        int hidden = 0;
        // 不按「场景归属屏」整组过滤：一个场景可能同时有窗口在两块屏上（旧快照 / 用户拖过），
        // StashGroup 按屏收紧，只收当前屏的成员，另一块屏的窗口不会跟着消失。
        foreach (var group in _stash.Groups().Where(g => g.Id != keepGroupId && g.Expanded).ToArray())
            hidden += _stash.StashGroup(group.Id, monitor);
        return hidden;
    }

    /// <summary>收纳成功但漏掉了管理员窗口时，在提示里补一句原因，避免用户以为程序坏了。</summary>
    private string WithElevationNote(string message)
    {
        int blocked = _stash.LastBlockedByElevation;
        return blocked > 0 ? $"{message}（{blocked} 个管理员窗口未收）" : message;
    }

    private void ExpandLatest(string monitorDevice = null)
    {
        if (_disposed) return;

        // 只在这块屏幕上找「最近的场景」，不要把另一块屏的场景调过来。
        monitorDevice ??= ActiveMonitorDevice();
        if (monitorDevice == null)
        {
            Feedback("无法判断当前屏幕，请把鼠标移到目标屏幕后重试");
            return;
        }

        _single.SuspendTemporarily();
        int shown = _stash.ExpandNewest(monitorDevice);
        if (shown > 0) CollapseOtherScenes(_stash.LastExpandedGroupId);
        Feedback(shown > 0
            ? $"{BadgeText(BadgeOfGroup(_stash.LastExpandedGroupId))}已展开「{SceneLabel(_stash.LastExpandedGroupId)}」· {shown} 个窗口"
            : "这块屏幕上没有已收起的场景", monitorDevice);
        UpdateSurface();
    }

    /// <summary>点胶囊 / 面板场景行：已收起 → 展开，已展开 → 收起（场景始终保留）。</summary>
    private void ToggleScene(int groupId)
    {
        if (_disposed) return;
        _single.SuspendTemporarily();
        string label = SceneLabel(groupId);
        // 提示跟着场景走：在任务栏胶囊上操作副屏场景时，提示出现在副屏而不是主屏。
        string monitor = _stash.MonitorOfGroup(groupId);
        var (expanded, count) = _stash.ToggleGroup(groupId);
        if (count == 0)
            Feedback($"「{label}」已经不存在", monitor);
        else if (expanded)
        {
            int collapsed = CollapseOtherScenes(groupId);
            Feedback(collapsed > 0
                ? $"已展开「{label}」· {count} 个窗口 · 其它场景已收起"
                : $"已展开「{label}」· {count} 个窗口", monitor);
        }
        else
            Feedback($"已收起「{label}」· {count} 个窗口", monitor);
        UpdateSurface();
    }

    private string SceneLabel(int groupId)
    {
        var group = _stash.Groups().FirstOrDefault(g => g.Id == groupId);
        return string.IsNullOrEmpty(group?.Name) ? $"场景 {groupId}" : group.Name;
    }

    /// <summary>运行时组当前的场景标识（没有关联快照时退回默认标识）。</summary>
    private SceneBadge BadgeOfGroup(int groupId)
    {
        var group = _stash.Groups().FirstOrDefault(g => g.Id == groupId);
        return SceneBadge.Coerce(group?.Badge);
    }

    /// <summary>提示文字里的标识前缀（① / ● + 空格）。</summary>
    private static string BadgeText(SceneBadge badge)
    {
        string glyph = SceneBadge.GlyphOf(badge);
        return glyph.Length == 0 ? "" : glyph + " ";
    }

    /// <summary>面板场景行的「关闭窗口」：有关联快照就按场景关闭，否则兜底直接关这个场景的窗口。</summary>
    private void CloseRunningScene(int groupId)
    {
        if (_disposed) return;
        string savedId = _stash.SavedIdOf(groupId);
        if (savedId != null)
        {
            CloseScene(savedId);
            return;
        }

        int closed = _workspace.Close(_stash, groupId);
        Feedback(closed > 0 ? $"已关闭 {closed} 个窗口" : "没有可关闭的窗口", _stash.MonitorOfGroup(groupId));
        UpdateSurface();
    }

    /// <summary>
    /// 关闭场景里的窗口（WM_CLOSE，等价逐个点 ×）：只关这个场景自己的窗口，
    /// 胶囊留下来显示已关闭；之后点它是重新打开并摆回位置。
    /// </summary>
    private void CloseScene(string savedId)
    {
        if (_disposed) return;
        var saved = _workspace.Find(savedId);
        if (saved == null)
        {
            UpdateSurface();
            return;
        }

        int groupId = _stash.GroupIdOfSaved(savedId);
        if (groupId == 0)
        {
            Feedback($"「{saved.Name}」已经没有打开的窗口", _workspace.MonitorOfSaved(saved));
            return;
        }

        // 关之前按当前位置再存一次，下次打开才是用户最后摆放的样子。
        _workspace.UpdateSnapshot(saved, _stash, groupId);
        string monitor = _stash.MonitorOfGroup(groupId) ?? _workspace.MonitorOfSaved(saved);
        int closed = _workspace.Close(_stash, groupId);
        Feedback(closed > 0
            ? $"{BadgeText(saved.Badge)}已关闭「{saved.Name}」· {closed} 个窗口"
            : $"「{saved.Name}」没有可关闭的窗口", monitor);
        UpdateSurface();
    }

    private void ExpandAll(string monitorDevice = null)
    {
        if (_disposed) return;

        // 全部展开也只作用于当前这块屏幕；要做另一块屏，就去那块屏上操作。
        monitorDevice ??= ActiveMonitorDevice();
        if (monitorDevice == null)
        {
            Feedback("无法判断当前屏幕，请把鼠标移到目标屏幕后重试");
            return;
        }

        _single.SuspendTemporarily();
        int shown = _stash.ExpandAll(monitorDevice);
        Feedback(shown > 0
            ? $"已展开这块屏幕上的全部场景 · {shown} 个窗口"
            : "这块屏幕上没有已收起的场景", monitorDevice);
        UpdateSurface();
    }

    /// <summary>左键点胶囊：把点击当成规则的一个入口，具体做什么由固定规则决定。</summary>
    private void OnChipClicked(ChipEntry entry)
    {
        if (_disposed) return;

        RunRules(RuleTriggerKind.SceneChip, () => new RuleFacts
        {
            SceneRunning = !entry.Dormant,
            GroupId = entry.GroupId,
            SavedId = entry.SavedId
        });
    }

    /// <summary>
    /// 打开已关闭的场景：启动软件并摆回保存位置；已经在跑的窗口直接挪回去，不重复开实例。
    /// 拉起来的窗口会接入运行时组，之后这个场景和普通场景一样收起 / 展开。
    /// </summary>
    private void RestoreSavedGroup(string id)
    {
        if (_disposed) return;
        var group = _workspace.Find(id);
        if (group == null)
        {
            Feedback("场景已经不存在");
            UpdateSurface();
            return;
        }

        // 同一场景正在等窗口出现时再点一次，会重复启动实例 / 重复摆位：先挡住。
        if (!_restoringScenes.Add(id))
        {
            Feedback($"「{group.Name}」正在打开，请稍候", _workspace.MonitorOfSaved(group));
            return;
        }

        _single.SuspendTemporarily();
        var managed = new HashSet<long>(_stash.Items.Select(i => i.Handle.ToInt64()));
        try
        {
            var (moved, launched, busy, failed) = _workspace.Restore(
                group,
                managed,
                hwnd => _stash.Adopt(hwnd, group.Id, group.Name, group.Badge),
                hwnd => _dispatcher.BeginInvoke(new Action(() => AdoptRestoredWindow(group, hwnd))),
                () =>
                {
                    try { _dispatcher.BeginInvoke(new Action(() => _restoringScenes.Remove(id))); }
                    catch { /* 退出中：集合生命周期已经结束 */ }
                });

            string message = moved + launched > 0
                ? $"正在打开「{group.Name}」· {moved + launched} 个窗口"
                : $"「{group.Name}」的程序都没找到";
            if (busy > 0) message += $"（{busy} 个窗口已在其它场景，未重复打开）";
            if (failed > 0) message += $"（{failed} 个窗口未能接入场景）";
            Feedback(message, _workspace.MonitorOfSaved(group));
            UpdateSurface();
        }
        catch
        {
            _restoringScenes.Remove(id);
            throw;
        }
    }

    /// <summary>把刚摆好位置的窗口接入运行时组（后台线程回调，已在 UI 线程上执行）。</summary>
    private void AdoptRestoredWindow(SavedGroup group, IntPtr hwnd)
    {
        if (_disposed) return;
        if (_stash.Adopt(hwnd, group.Id, group.Name, group.Badge)) UpdateSurface();
    }

    private void RenameSavedGroup(string id, Window owner = null)
    {
        if (_disposed) return;
        var group = _workspace.Find(id);
        if (group == null) return;

        // 从胶囊右键菜单进来时菜单还开着：等这一轮消息处理完再弹输入框，避免两个模态界面叠在一起。
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            string name = TextPromptWindow.Ask("重命名场景", "这个名字会显示在任务栏胶囊上", group.Name, owner);
            if (name == null) return;

            if (_workspace.Rename(id, name))
            {
                // 运行时组上的名字同步刷新，胶囊文字立刻变。
                _stash.RenameSaved(id, name);
                Feedback($"已重命名为「{name}」");
            }
            UpdateSurface();
        }));
    }

    /// <summary>改场景标识：快照、运行时组、已展开成员窗口的图标一起更新。</summary>
    private void SetSceneBadge(string id, SceneBadge badge)
    {
        if (_disposed) return;
        var group = _workspace.Find(id);
        if (group == null) return;

        var normalized = SceneBadge.Coerce(badge);
        if (!_workspace.SetBadge(id, normalized)) return;
        // 正在运行的组同步重画窗口图标；收起的成员等展开时再挂。
        _stash.SetBadge(id, normalized);
        Feedback($"{BadgeText(normalized)}场景标识已更新");
        UpdateSurface();
    }

    /// <summary>
    /// 改调色板里的一个色块：所有用这个颜色的场景一起换，配置与快照同时落盘。
    /// 两个色块被改成同一颜色时无法区分，按「先出现的色块」处理。
    /// </summary>
    private void ChangePaletteColor(int index, string color)
    {
        if (_disposed) return;
        if (!SceneBadge.TryNormalizeColor(color, out string next)) return;

        var palette = _settings.BadgePalette().ToList();
        if (index < 0 || index >= palette.Count) return;
        string previous = palette[index];
        if (string.Equals(previous, next, StringComparison.OrdinalIgnoreCase)) return;

        palette[index] = next;
        _settings.SceneBadgeColors = palette;
        _settings.Save();

        ApplyPaletteRemap(value =>
            string.Equals(value, previous, StringComparison.OrdinalIgnoreCase) ? next : value);
        Feedback($"调色板第 {index + 1} 色已改为 {next}");
    }

    /// <summary>把这 8 个色块恢复默认：按色块位置把场景从当前色换回默认色。</summary>
    private void ResetBadgePalette()
    {
        if (_disposed) return;

        var defaults = SceneBadge.DefaultPalette;
        var current = _settings.BadgePalette();

        // 先按原配色建一张「同时生效」的映射表：直接逐个替换会出现链式改色。
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < defaults.Count && i < current.Count; i++)
        {
            if (!map.ContainsKey(current[i])) map[current[i]] = defaults[i];
        }

        _settings.SceneBadgeColors = defaults.ToList();
        _settings.Save();

        ApplyPaletteRemap(value => map.TryGetValue(value ?? "", out string target) ? target : value);
        Feedback("场景标识颜色已恢复默认");
    }

    /// <summary>调色板变化后统一刷新：场景快照、运行中窗口图标、编辑器色块与胶囊。</summary>
    private void ApplyPaletteRemap(Func<string, string> remap)
    {
        _workspace.RemapBadgeColors(remap);
        _stash.RemapBadgeColors(remap);
        _sceneEditor?.SetPalette(_settings.BadgePalette());
        RefreshSceneEditor();
        UpdateSurface();
    }

    /// <summary>
    /// 删除场景：删掉快照，并把这个场景的窗口交回桌面（不关闭、不移动已经可见的窗口）。
    /// </summary>
    private void DissolveScene(string id)
    {
        if (_disposed) return;
        var group = _workspace.Find(id);
        if (group == null)
        {
            UpdateSurface();
            return;
        }

        int groupId = _stash.GroupIdOfSaved(id);
        int released = groupId != 0 ? _stash.Release(groupId) : 0;

        if (_workspace.Delete(id))
        {
            string note = groupId != 0 ? " · 窗口留在桌面上" : "";
            Feedback($"{BadgeText(group.Badge)}已删除场景「{group.Name}」{note}");
        }
        else if (released > 0)
        {
            // 快照已经不在了：窗口已经交回桌面，界面刷新即可。
            Feedback($"窗口已交回桌面（{released} 个被还原）");
        }
        UpdateSurface();
    }

    /// <summary>
    /// 场景编辑：独立小窗，移除 / 追加 / 重命名 / 删除都在里面完成。
    /// 修改只改保存的快照，正在运行的窗口不关也不动，下次打开场景才生效。
    /// </summary>
    private void OpenSceneEditor(string savedId)
    {
        if (_disposed) return;
        if (_workspace.Detail(savedId) == null)
        {
            Feedback("场景已经不存在");
            UpdateSurface();
            return;
        }

        if (_sceneEditor != null)
        {
            try { _sceneEditor.Activate(); } catch { /* 窗口可能正在关闭 */ }
            RefreshSceneEditor();
            return;
        }

        var editor = new SceneEditorWindow();
        editor.SetPalette(_settings.BadgePalette());
        editor.RemoveWindowRequested += index => Safe("sceneEditor.remove", () => RemoveSceneWindow(savedId, index));
        editor.AppendSceneRequested += groupId => Safe("sceneEditor.append", () => AppendRunningSceneToScene(savedId, groupId));
        editor.RenameRequested += () => Safe("sceneEditor.rename", () => RenameSavedGroup(savedId, editor));
        editor.DeleteRequested += () => Safe("sceneEditor.delete", () => DissolveScene(savedId));
        editor.BadgeChangeRequested += badge => Safe("sceneEditor.badge", () => SetSceneBadge(savedId, badge));
        editor.PaletteColorRequested += (index, color) =>
            Safe("sceneEditor.paletteColor", () => ChangePaletteColor(index, color));
        editor.PaletteResetRequested += () => Safe("sceneEditor.paletteReset", ResetBadgePalette);
        editor.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_sceneEditor, editor)) return;
            _sceneEditor = null;
            _sceneEditorId = null;
        };

        _sceneEditor = editor;
        _sceneEditorId = savedId;
        RefreshSceneEditor();

        try
        {
            // 隐藏的窗口不能当 Owner；只有面板真的可见时才挂，否则让编辑器居中到屏幕。
            if (_panel != null && _panel.IsVisible) editor.Owner = _panel;
            editor.ShowDialog();

            // 正常关闭时 Closed 已经清过；这里兜底，避免留下指向已关闭窗口的引用。
            if (ReferenceEquals(_sceneEditor, editor))
            {
                _sceneEditor = null;
                _sceneEditorId = null;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Write("scene editor", ex);
            if (ReferenceEquals(_sceneEditor, editor))
            {
                _sceneEditor = null;
                _sceneEditorId = null;
            }
            try { editor.Close(); } catch { }
        }
    }

    /// <summary>把最新场景详情 / 正在运行的其他场景推给编辑器；场景已被删除时关闭它。</summary>
    private void RefreshSceneEditor()
    {
        var editor = _sceneEditor;
        if (editor == null || string.IsNullOrEmpty(_sceneEditorId)) return;

        var detail = _workspace.Detail(_sceneEditorId);
        if (detail == null)
        {
            // 先摘掉引用再关闭：Closed 里不会再走刷新，避免递归。
            _sceneEditor = null;
            _sceneEditorId = null;
            try { editor.Close(); } catch { }
            return;
        }

        // 只把同一块屏幕上的运行场景给编辑器：跨屏追加会把「一块屏一个场景」的约束破掉。
        string monitor = SceneMonitor(detail.Id);
        var running = _stash.Groups()
            .Where(g => monitor == null || _stash.MonitorOfGroup(g.Id) == monitor)
            .ToArray();
        editor.Reload(detail, running, _stash.GroupIdOfSaved(detail.Id));
    }

    /// <summary>场景属于哪块屏幕：运行组优先用实测，已关闭的用快照记录。</summary>
    private string SceneMonitor(string savedId)
    {
        var saved = _workspace.Find(savedId);
        if (saved == null) return null;
        int groupId = _stash.GroupIdOfSaved(savedId);
        return groupId != 0 ? _stash.MonitorOfGroup(groupId) : _workspace.MonitorOfSaved(saved);
    }

    /// <summary>从场景快照移除一个窗口（不关闭当前正在运行的窗口）。</summary>
    private void RemoveSceneWindow(string savedId, int index)
    {
        var result = _workspace.RemoveWindow(savedId, index);
        if (!result.Ok)
        {
            Feedback(result.Error);
            UpdateSurface();
            return;
        }

        Feedback($"已从场景移除「{result.Title}」· 还剩 {result.Count} 个窗口");
        UpdateSurface();
    }

    /// <summary>把一个正在运行的其他场景追加进场景快照（下次打开场景生效）。</summary>
    private void AppendRunningSceneToScene(string savedId, int groupId)
    {
        // 场景归属一块屏：把另一块屏的运行场景并进来，之后收起 / 展开就会跨屏。
        string target = SceneMonitor(savedId);
        string source = _stash.MonitorOfGroup(groupId);
        if (target != null && source != null && target != source)
        {
            Feedback("不能把另一块屏幕上的场景加进来");
            UpdateSurface();
            return;
        }

        var result = _workspace.AppendRunningScene(savedId, _stash, groupId);
        if (!result.Ok)
        {
            Feedback(result.Error);
            UpdateSurface();
            return;
        }

        string message = $"已追加 {result.Added} 个窗口到场景";
        if (result.Skipped > 0) message += $"（{result.Skipped} 个已在场景里）";
        Feedback(message);
        UpdateSurface();
    }

    /// <summary>打开主面板（已经打开就置前）：托盘双击 / 胶囊「打开主面板」/ 二次启动都走这里。</summary>
    private void OpenPanel()
    {
        if (_disposed || _panel == null) return;
        bool wasVisible = _panel.IsVisible;
        UpdateSurface();
        _panel.ShowPanel();
        if (!wasVisible) Feedback("主面板已打开");
    }

    private void SetSingleWindowMode(bool value) => SetSingleWindowMode(value, persist: true);

    private void SetSingleWindowMode(bool value, bool persist)
    {
        if (persist || _settings.SingleWindowMode != value)
        {
            _settings.SingleWindowMode = value;
            _settings.Save();
        }
        _single.SetEnabled(value, applyImmediately: persist);
        // 托盘菜单 / 面板收起时，系统会把前台还给刚才的窗口；那不是用户「操作了另一块屏」，
        // 不压住的话会在开关生效后紧接着把另一块屏也清一遍（一块屏留一个，两块各留一个）。
        if (persist && value) _single.SuspendTemporarily();
        _tray.SetSingleWindowChecked(value);
        _panel?.SetSingleWindow(value);
        if (persist) Feedback(value ? "单窗口模式已开启" : "单窗口模式已关闭");
    }

    private void SetAutoStart(bool value)
    {
        bool actual = AutoStart.IsSupported && AutoStart.Set(value);
        _settings.AutoStart = actual;
        _settings.Save();
        _tray.SetAutoStartChecked(actual);
        _panel?.SetAutoStart(actual);
        if (actual != value) Feedback("开机自启写入失败，设置已还原");
        else Feedback(value ? "开机自启已开启" : "开机自启已关闭");
    }

    /// <summary>面板里拖尺寸滑块：立即重画已展开窗口的合成图标，并写入配置。</summary>
    private void SetSceneIconSizes(double? appIconScale, double? badgeScale)
    {
        double appIcon = appIconScale.HasValue
            ? Math.Round(Math.Clamp(appIconScale.Value, SceneIconPainter.MinAppIconScale,
                SceneIconPainter.MaxAppIconScale), 2)
            : _settings.SceneAppIconSize;
        double badge = badgeScale.HasValue
            ? Math.Round(Math.Clamp(badgeScale.Value, SceneIconPainter.MinBadgeScale,
                SceneIconPainter.MaxBadgeScale), 2)
            : _settings.SceneBadgeSize;

        // 滑块每一格都会回调：值没变就什么都不做，别跟着重画图标。
        if (Math.Abs(_settings.SceneAppIconSize - appIcon) < 0.0001 &&
            Math.Abs(_settings.SceneBadgeSize - badge) < 0.0001) return;

        _settings.SceneAppIconSize = appIcon;
        _settings.SceneBadgeSize = badge;
        _settings.Save();
        _sceneIcons.AppIconScale = appIcon;
        _sceneIcons.BadgeScale = badge;
        _stash.RefreshSceneIcons();
    }

    private void UpdateSurface()
    {
        if (_disposed) return;

        // 隐藏集合的任何变化（收纳 / 展开 / 外部还原 / 剪枝）都会走到这里：
        // 立刻落一份回收记录，别把「刚藏完就强杀」的窗口留给下一次启动。
        // Save 内部按句柄签名去重，重复调用不会产生多余的磁盘写入。
        WindowRecovery.Save(_stash.Items);

        var groups = _stash.Groups();
        // 面板 / 托盘只列场景：dormant 是只剩快照、没有窗口的那些。
        var dormant = ChipProjection.Dormant(groups, _workspace.Summaries());
        _chips.Sync(ChipProjection.Chips(groups, dormant));
        _tray.SetScenes(groups, _stash.Count);
        _tray.SetHotkeys(_stashHotkey.Gesture);
        _panel.SetScenes(groups, dormant, _stash.Count);
        _panel.SetHotkeyText(_stashHotkey.Gesture);
        // 场景编辑窗口开着时同步刷新；场景被删掉会在里面自动关闭。
        Safe("sceneEditor.refresh", RefreshSceneEditor);
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

        // 场景编辑是模态窗口：不先关掉，后面的关闭流程会卡在它的消息循环里。
        try { _sceneEditor?.Close(); } catch { /* 退出路径不再抛 */ }

        _pruneTimer?.Stop();
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
