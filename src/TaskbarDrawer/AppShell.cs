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
    private WorkspaceService _workspace;
    private SingleWindowMode _single;
    private HotkeyManager _hotkeys;
    private HwndSource _messageHost;
    private TrayService _tray;
    private TaskbarChipLayer _chips;
    private MainPanelWindow _panel;
    private RuleSet _rules;
    private RuleRunner _runner;
    private HotkeyDefinition _stashHotkey;
    private DispatcherTimer _pruneTimer;
    private Dispatcher _dispatcher;
    private EventWaitHandle _showSignal;
    private ManualResetEvent _signalStop;
    private Thread _signalThread;
    private bool _disposed;

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
        _settings = AppSettings.Load();

        _stash = new StashAllService();
        // Changed 是同步广播：UI 构建一旦抛异常，会沿「业务方法 → RunRules → 入口 Safe」冒泡，
        // 结果是状态已改、界面没刷、日志还记在错误的 scope 上。隔离必须在订阅处收口。
        _stash.Changed += () => Safe("surface", UpdateSurface);

        // 场景：从运行时编组抓快照落盘，之后可一键关闭 / 还原，跨启动保留。
        _workspace = new WorkspaceService();

        // 被收纳的窗口如果被外部还原 / 关闭，计数要跟着变，不能让胶囊一直显示旧数字。
        _pruneTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        _pruneTimer.Tick += (_, _) => Safe("prune", () =>
        {
            if (_disposed) return;
            if (_stash.Count > 0) _stash.Prune();
            // 隐藏集合变化时落一份回收记录：强杀 / 断电后下次启动能把窗口救回来。
            WindowRecovery.Save(_stash.Items);
        });
        _pruneTimer.Start();

        _single = new SingleWindowMode(Dispatcher.CurrentDispatcher);
        // 让单窗口模式认识「组」：一组展开的窗口当成一个整体，而不是互斥的多个窗口。
        _single.GroupOf = hwnd => _stash.GroupIdOf(hwnd);
        // 切到别的组 / 别的窗口时，别的组整组收回胶囊（而不是最小化成一排任务栏按钮）。
        _single.CollapseGroup = groupId => _stash.StashGroup(groupId);
        _single.Applied += count => Safe("single.applied", () =>
        {
            if (count > 0) _tray?.SetStatus($"单窗口模式：已收起 {count} 个窗口");
        });

        _chips = new TaskbarChipLayer();
        _chips.Clicked += entry => Safe("chip.click", () => OnChipClicked(entry));
        _chips.UngroupRequested += entry => Safe("chip.ungroup", () => Ungroup(entry.GroupId));
        _chips.SaveGroupRequested += entry => Safe("chip.saveGroup", () => SaveAsScene(entry.GroupId));
        _chips.RenameGroupRequested += entry => Safe("chip.renameGroup", () => RenameRuntimeGroup(entry.GroupId));
        _chips.RenameSceneRequested += entry => Safe("chip.renameScene", () => RenameSavedGroup(entry.SavedId));
        _chips.CloseWindowsRequested += entry => Safe("chip.closeScene", () => CloseScene(entry.SavedId));
        _chips.DissolveSceneRequested += entry => Safe("chip.dissolveScene", () => DissolveScene(entry.SavedId));
        _chips.ShowPanelRequested += () => Safe("chip.panel", OpenPanel);

        // 热键挂在消息专用窗口上：胶囊会随分组增删，不能拿它的句柄当宿主。
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
        _tray.StashRequested += () => Safe("tray.stash", StashNewGroup);
        _tray.ExpandLatestRequested += () => Safe("tray.expandLatest", ExpandLatest);
        _tray.GroupRequested += id => Safe("tray.group", () => ToggleGroup(id));
        _tray.ExpandAllRequested += () => Safe("tray.expandAll", ExpandAll);
        _tray.SingleWindowRequested += value => Safe("tray.singleWindow", () => SetSingleWindowMode(value));
        _tray.AutoStartRequested += value => Safe("tray.autoStart", () => SetAutoStart(value));
        _tray.ExitRequested += Exit;
        _tray.RestartElevatedRequested += () => Safe("tray.elevate", RestartElevated);
        // 已经是管理员就没有「提权重启」的必要，菜单项只对普通权限实例显示。
        _tray.SetElevated(NativeMethods.IsCurrentProcessElevated);
        _tray.SetVersion(InformationalVersion);

        // 入口固定三个，流程写在 RuleSet.CreateDefault() 里，运行期只读。
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
        _panel.StashRequested += () => Safe("panel.stash", StashNewGroup);
        _panel.ToggleGroupRequested += id => Safe("panel.group", () => ToggleGroup(id));
        _panel.CloseGroupRequested += id => Safe("panel.closeGroup", () => CloseOrUngroup(id));
        _panel.SaveGroupRequested += id => Safe("panel.saveGroup", () => SaveAsScene(id));
        _panel.GroupRenameRequested += id => Safe("panel.renameGroup", () => RenameRuntimeGroup(id));
        _panel.SavedToggleRequested += id => Safe("panel.savedToggle", () => RestoreSavedGroup(id));
        _panel.SavedRenameRequested += id => Safe("panel.savedRename", () => RenameSavedGroup(id));
        _panel.SavedDeleteRequested += id => Safe("panel.savedDelete", () => DissolveScene(id));
        _panel.ExpandLatestRequested += () => Safe("panel.expandLatest", ExpandLatest);
        _panel.ExpandAllRequested += () => Safe("panel.expandAll", ExpandAll);
        _panel.SingleWindowRequested += value => Safe("panel.singleWindow", () => SetSingleWindowMode(value));
        _panel.AutoStartRequested += value => Safe("panel.autoStart", () => SetAutoStart(value));
        _panel.HotkeyChangeRequested += (modifiers, key) => Safe("panel.hotkey", () => ChangeHotkey(modifiers, key));

        StartShowSignal();

        _single.Start();
        SetSingleWindowMode(_settings.SingleWindowMode, persist: false);

        bool autoStart = AutoStart.IsSupported && AutoStart.IsEnabled();
        _settings.AutoStart = autoStart;
        _settings.Save();
        _tray.SetAutoStartChecked(autoStart);
        _panel.SetAutoStart(autoStart);

        _tray.SetStatus($"{_stashHotkey.Gesture} 收起 / 展开");

        // Win11 默认把新托盘图标收进折叠区（^），首次运行提示一下入口在哪、热键是什么。
        if (!_settings.ShowedWelcome)
        {
            _settings.ShowedWelcome = true;
            _settings.Save();
            _tray.ShowBalloon(
                $"{_stashHotkey.Gesture} 把当前窗口收成新一组；屏幕上没窗口时再按一次展开最近一组。" +
                "左键点托盘图标打开主面板。");
        }

        UpdateSurface();

        // 启动反馈：底部浮窗一闪，确认程序已经在跑、热键是什么。
        ToastWindow.Show($"任务栏收纳 v{ShortVersion} 已启动 · {_stashHotkey.Gesture} 收起 / 展开");
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
            RunRules(RuleTriggerKind.Hotkey, () => new RuleFacts
            {
                HasUntrackedWindows = _stash.HasUntrackedVisibleWindows(),
                HasExpandedGroup = _stash.Groups().Any(g => g.Expanded)
            });
        });
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

        runner.On(RuleCatalog.ConditionHasUntracked, _ => _stash.HasUntrackedVisibleWindows());
        runner.On(RuleCatalog.ConditionHasExpandedGroup, _ => _stash.Groups().Any(g => g.Expanded));
        runner.On(RuleCatalog.ConditionChipOnScreen, facts => facts.ChipOnScreen);
        runner.On(RuleCatalog.ConditionSceneRunning, facts => facts.SceneRunning);

        runner.Do(RuleCatalog.ActionStashNew, (_, _) => StashNewGroup());
        runner.Do(RuleCatalog.ActionExpandLatest, (_, _) => ExpandLatest());
        runner.Do(RuleCatalog.ActionExpandAll, (_, _) => ExpandAll());
        runner.Do(RuleCatalog.ActionCollapseExpanded, (_, _) => CollapseExpandedGroups());
        runner.Do(RuleCatalog.ActionExpandGroup, (facts, _) => ExpandChipGroup(facts.GroupId));
        runner.Do(RuleCatalog.ActionToggleGroup, (facts, _) => ToggleGroup(facts.GroupId));
        runner.Do(RuleCatalog.ActionRestoreScene, (facts, _) =>
        {
            if (!string.IsNullOrEmpty(facts.SavedId)) RestoreSavedGroup(facts.SavedId);
        });
        runner.Do(RuleCatalog.ActionOpenPanel, (_, _) => OpenPanel());
        runner.Do(RuleCatalog.ActionToast, (_, step) =>
        {
            string text = step.Args != null && step.Args.TryGetValue("text", out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : "已执行";
            ToastWindow.Show(text);
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
            _panel.ApplyHotkeyResult(false, null,
                $"{candidate.Gesture} 注册失败：{registration.ErrorText}");
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
    private void Feedback(string message)
    {
        ToastWindow.Show(message);
        _tray?.SetStatus(message);
    }

    /// <summary>把所有正展开的编组一起收回胶囊（规则里的「收起所有已展开编组」）。</summary>
    private void CollapseExpandedGroups()
    {
        if (_disposed) return;
        _single.SuspendTemporarily();

        int hidden = 0;
        foreach (var group in _stash.Groups().Where(g => g.Expanded).ToArray())
            hidden += _stash.StashGroup(group.Id);

        Feedback(hidden > 0 ? $"已收起 {hidden} 个窗口" : "没有可操作的窗口");
        UpdateSurface();
    }

    /// <summary>展开被点击的那一组（规则里的「展开这个编组」）。</summary>
    private void ExpandChipGroup(int groupId)
    {
        if (_disposed || groupId == 0) return;
        _single.SuspendTemporarily();

        int shown = _stash.ExpandGroup(groupId);
        if (shown == 0)
        {
            Feedback("这一组已经不存在");
        }
        else
        {
            int collapsed = CollapseOtherGroups(groupId);
            string message = $"已展开「{GroupLabel(groupId)}」· {shown} 个窗口";
            Feedback(collapsed > 0 ? $"{message} · 其它组已收起" : message);
        }
        UpdateSurface();
    }

    private void StashNewGroup()
    {
        if (_disposed) return;
        _single.SuspendTemporarily();
        int count = _stash.StashAll();
        if (count > 0)
        {
            var newest = _stash.Groups().FirstOrDefault();
            string message = newest != null ? $"已收起 {count} 个窗口 · 第 {newest.Id} 组" : $"已收起 {count} 个窗口";
            Feedback(WithElevationNote(message));
        }
        else if (_stash.LastBlockedByElevation > 0)
        {
            Feedback($"{_stash.LastBlockedByElevation} 个窗口以管理员身份运行，需以管理员身份重启本程序才能收纳");
        }
        else
        {
            Feedback("没有可收起的窗口");
        }
        UpdateSurface();
    }

    /// <summary>
    /// 单窗口模式下展开一组时，把其它已展开的组收回胶囊：
    /// 「每个组就是一个单窗口」，切组时上一组应该回到收纳区，而不是留在桌面上。
    /// </summary>
    private int CollapseOtherGroups(int keepGroupId)
    {
        if (!_single.Enabled) return 0;

        // 多显示器：只收起和这一组在同一块屏幕上的组，另一块屏上的组保持不动。
        IntPtr monitor = IntPtr.Zero;
        foreach (var item in _stash.Items)
        {
            if (item.GroupId != keepGroupId || item.Hidden) continue;
            monitor = NativeMethods.MonitorFromWindow(item.Handle, NativeMethods.MONITOR_DEFAULTTONEAREST);
            break;
        }

        int hidden = 0;
        foreach (var group in _stash.Groups().Where(g => g.Id != keepGroupId && g.Expanded).ToArray())
        {
            if (monitor != IntPtr.Zero && !GroupOnMonitor(group.Id, monitor)) continue;
            hidden += _stash.StashGroup(group.Id);
        }
        return hidden;
    }

    /// <summary>这一组在当前这块屏幕上还有没有展开着的窗口。</summary>
    private bool GroupOnMonitor(int groupId, IntPtr monitor)
    {
        foreach (var item in _stash.Items)
        {
            if (item.GroupId != groupId || item.Hidden) continue;
            if (NativeMethods.MonitorFromWindow(item.Handle, NativeMethods.MONITOR_DEFAULTTONEAREST) == monitor) return true;
        }
        return false;
    }

    /// <summary>收纳成功但漏掉了管理员窗口时，在提示里补一句原因，避免用户以为程序坏了。</summary>
    private string WithElevationNote(string message)
    {
        int blocked = _stash.LastBlockedByElevation;
        return blocked > 0 ? $"{message}（{blocked} 个管理员窗口未收）" : message;
    }

    private void ExpandLatest()
    {
        if (_disposed) return;
        _single.SuspendTemporarily();
        int shown = _stash.ExpandNewest();
        if (shown > 0) CollapseOtherGroups(_stash.LastExpandedGroupId);
        Feedback(shown > 0
            ? $"已展开第 {_stash.LastExpandedGroupId} 组 · {shown} 个窗口"
            : "没有已收起的组");
        UpdateSurface();
    }

    /// <summary>点胶囊 / 面板按钮：已收起 → 展开，已展开 → 收起（组始终保留）。</summary>
    private void ToggleGroup(int groupId)
    {
        if (_disposed) return;
        _single.SuspendTemporarily();
        string label = GroupLabel(groupId);
        var (expanded, count) = _stash.ToggleGroup(groupId);
        if (count == 0)
            Feedback($"{label}已经不存在");
        else if (expanded)
        {
            int collapsed = CollapseOtherGroups(groupId);
            Feedback(collapsed > 0
                ? $"已展开「{label}」· {count} 个窗口 · 其它组已收起"
                : $"已展开「{label}」· {count} 个窗口");
        }
        else
            Feedback($"已收起「{label}」· {count} 个窗口");
        UpdateSurface();
    }

    private string GroupLabel(int groupId)
    {
        var group = _stash.Groups().FirstOrDefault(g => g.Id == groupId);
        return string.IsNullOrEmpty(group?.Name) ? $"第 {groupId} 组" : group.Name;
    }

    /// <summary>面板上的第三个按钮：场景 = 关闭窗口（真关），编组 = 取消编组（窗口留在桌面）。</summary>
    private void CloseOrUngroup(int groupId)
    {
        if (_disposed) return;
        string savedId = _stash.SavedIdOf(groupId);
        if (savedId != null) CloseScene(savedId);
        else Ungroup(groupId);
    }

    /// <summary>取消编组：窗口原位留在桌面上，只是不再按组管理。</summary>
    private void Ungroup(int groupId)
    {
        if (_disposed) return;
        int shown = _stash.Ungroup(groupId);
        Feedback(shown > 0
            ? $"已取消编组 · {shown} 个窗口留在桌面上"
            : "这一组已经不存在");
        UpdateSurface();
    }

    /// <summary>
    /// 关闭场景里的窗口（WM_CLOSE，等价逐个点 ×）：只关这一组自己的窗口，
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
            Feedback($"「{saved.Name}」已经没有打开的窗口");
            return;
        }

        // 关之前按当前位置再存一次，下次打开才是用户最后摆放的样子。
        _workspace.UpdateSnapshot(saved, _stash, groupId);
        int closed = _workspace.Close(_stash, groupId);
        Feedback(closed > 0
            ? $"已关闭「{saved.Name}」· {closed} 个窗口"
            : $"「{saved.Name}」没有可关闭的窗口");
        UpdateSurface();
    }

    private void ExpandAll()
    {
        if (_disposed) return;
        _single.SuspendTemporarily();
        int shown = _stash.ExpandAll();
        Feedback(shown > 0 ? $"已展开全部 · {shown} 个窗口" : "没有已收起的组");
        UpdateSurface();
    }

    /// <summary>左键点胶囊：把点击当成规则的一个入口，具体做什么由固定规则决定。</summary>
    private void OnChipClicked(ChipEntry entry)
    {
        if (_disposed) return;

        bool isScene = entry.IsSaved;
        RunRules(isScene ? RuleTriggerKind.SceneChip : RuleTriggerKind.GroupChip, () => new RuleFacts
        {
            ChipOnScreen = entry.Highlight,
            SceneRunning = isScene && !entry.Dormant,
            GroupId = entry.GroupId,
            SavedId = entry.SavedId
        });
    }

    /// <summary>存储编组：起个名字，把这一组的「软件 + 位置」存成场景快照。</summary>
    private void SaveAsScene(int groupId)
    {
        if (_disposed) return;
        var group = _stash.Groups().FirstOrDefault(g => g.Id == groupId);
        if (group == null)
        {
            Feedback("这一组已经不存在");
            return;
        }

        string suggested = string.IsNullOrEmpty(group.Name)
            ? $"场景 {_workspace.Summaries().Count + 1}"
            : group.Name;

        // 从胶囊右键菜单进来时菜单还开着：等这一轮消息处理完再弹输入框，避免两个界面叠在一起。
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            string name = TextPromptWindow.Ask("存储编组", "给这组窗口起个名字，关掉后还能一键打开", suggested);
            if (name == null) return;

            var saved = _workspace.SaveFrom(_stash, groupId, name);
            if (saved == null)
            {
                Feedback("这一组没有可保存的窗口");
                return;
            }
            _stash.LinkSaved(groupId, saved.Id, saved.Name);
            Feedback($"已存为场景「{saved.Name}」· {saved.Windows.Count} 个窗口");
            UpdateSurface();
        }));
    }

    /// <summary>
    /// 打开已关闭的场景：启动软件并摆回保存位置；已经在跑的窗口直接挪回去，不重复开实例。
    /// 拉起来的窗口会接入运行时编组，之后这一组和普通组一样收起 / 展开。
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

        _single.SuspendTemporarily();
        var (moved, launched) = _workspace.Restore(group,
            hwnd => _dispatcher.BeginInvoke(new Action(() => AdoptRestoredWindow(group, hwnd))));

        Feedback(moved + launched > 0
            ? $"正在打开「{group.Name}」· {moved + launched} 个窗口"
            : $"「{group.Name}」的程序都没找到");
        UpdateSurface();
    }

    /// <summary>把刚摆好位置的窗口接入运行时组（后台线程回调，已在 UI 线程上执行）。</summary>
    private void AdoptRestoredWindow(SavedGroup group, IntPtr hwnd)
    {
        if (_disposed) return;
        if (_stash.Adopt(hwnd, group.Id, group.Name)) UpdateSurface();
    }

    /// <summary>给普通编组起名：只改胶囊上的显示名，不保存快照。</summary>
    private void RenameRuntimeGroup(int groupId)
    {
        if (_disposed) return;
        var group = _stash.Groups().FirstOrDefault(g => g.Id == groupId);
        if (group == null) return;

        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            string initial = string.IsNullOrEmpty(group.Name) ? $"第 {groupId} 组" : group.Name;
            string name = TextPromptWindow.Ask("重命名编组", "名字显示在任务栏胶囊上，重启后不保留", initial);
            if (name == null) return;

            if (_stash.RenameGroup(groupId, name)) Feedback($"已重命名为「{name}」");
            UpdateSurface();
        }));
    }

    private void RenameSavedGroup(string id)
    {
        if (_disposed) return;
        var group = _workspace.Find(id);
        if (group == null) return;

        // 从胶囊右键菜单进来时菜单还开着：等这一轮消息处理完再弹输入框，避免两个模态界面叠在一起。
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            string name = TextPromptWindow.Ask("重命名场景", "这个名字会显示在任务栏胶囊上", group.Name);
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

    /// <summary>解散场景：只删这条快照；正在运行的那组退回普通编组，窗口不动。</summary>
    private void DissolveScene(string id)
    {
        if (_disposed) return;
        var group = _workspace.Find(id);
        if (group == null)
        {
            UpdateSurface();
            return;
        }

        if (_workspace.Delete(id))
        {
            // 正在运行的那组退回普通编组，继续可收起 / 展开。
            _stash.UnlinkSaved(id);
            Feedback($"已解散场景「{group.Name}」");
        }
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

    private void UpdateSurface()
    {
        if (_disposed) return;
        var groups = _stash.Groups();
        // 面板的场景区块只列当前没有窗口的那些（还活着的就在上面的编组列表里）。
        var dormant = ChipProjection.Dormant(groups, _workspace.Summaries());
        _chips.Sync(ChipProjection.Chips(groups, dormant));
        _tray.SetGroups(groups, _stash.Count);
        _tray.SetHotkeys(_stashHotkey.Gesture);
        _panel.SetGroups(groups, _stash.Count);
        _panel.SetSavedGroups(dormant);
        _panel.SetHotkeyText(_stashHotkey.Gesture);
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
                    try { _dispatcher.BeginInvoke(new Action(OpenPanel)); }
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

        _pruneTimer?.Stop();
        try { _signalStop?.Set(); } catch { }
        try { _signalThread?.Join(200); } catch { }
        try { _showSignal?.Dispose(); } catch { }
        try { _signalStop?.Dispose(); } catch { }

        try { _single.SetEnabled(false); _single.Dispose(); } catch { }
        try { _stash.ReleaseAll(); } catch { }
        // 窗口已经主动放回任务栏，回收记录作废。
        WindowRecovery.Clear();
        _hotkeys?.Dispose();
        try { _messageHost?.Dispose(); } catch { }
        _tray?.Dispose();
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
        Application.Current?.Shutdown();
    }

    public void Dispose() => Exit();
}
