using System.Windows.Threading;
using TaskbarDrawer.Core;
using TaskbarDrawer.Core.Rules;
using TaskbarDrawer.Interop;
using TaskbarDrawer.UI;

namespace TaskbarDrawer;

/// <summary>
/// 入口接线与窗口管理。
/// 这里只做两件事：把四个入口（托盘 / 面板 / 胶囊 / 单窗口模式）的事件接到命令上，
/// 以及管理主面板 / 详情侧窗 / 说明窗口的打开、定位与数据推送。
/// 每个订阅都就地包 <see cref="Safe"/>：<c>Changed</c> 是同步广播，UI 异常不能冒进业务调用栈。
/// </summary>
public sealed partial class AppShell
{
    /// <summary>
    /// 建服务并接上它们的内部事件。顺序即依赖：设置 → 图标 → 收纳 → 场景 → 场景写入口 → 剪枝定时器。
    /// </summary>
    private void WireServices()
    {
        _settings = AppSettings.Load();

        _sceneIcons = new SceneIconPainter(_settings.SceneAppIconSize, _settings.SceneBadgeSize);
        _stash = new StashAllService(_sceneIcons);
        // Changed 是同步广播：UI 构建一旦抛异常，会沿「业务方法 → RunRules → 入口 Safe」冒泡，
        // 结果是状态已改、界面没刷、日志还记在错误的 scope 上。隔离必须在订阅处收口。
        _stash.Changed += () => Safe("surface", RequestSurfaceUpdate);

        // 场景：收纳时立刻抓快照落盘，之后可一键关闭 / 还原，跨启动保留。
        _workspace = new WorkspaceService();
        // 写盘失败不能静默：场景还在内存里能用，但重启就会丢，必须当场告诉用户。
        // 延后一拍再提示：失败往往发生在「已收起 / 已改名」的成功提示之前，同步弹会被覆盖。
        _workspace.SaveFailed += message => Safe("workspace.save", () =>
            _dispatcher.BeginInvoke(new Action(() => Feedback(message)), DispatcherPriority.Background));
        // 构造函数里的旧数据迁移等这里订阅完再落盘：写失败才能通过 SaveFailed 提示用户。
        _workspace.PersistPendingMigrations();

        // 场景的写操作只走这一个入口：快照与运行时组的名字 / 标识 / 关联不会各改一边。
        _scenes = new SceneService(_workspace, _stash);

        // 窗口销毁 / 显示 / 隐藏的事件订阅：两个定时器的分工从这里分开——
        //   事件（150 ms 合并）负责「状态变了要立刻看见」；
        //   定时器（5 s）只做两件没法订阅的事：图标重贴（WM_SETICON 不产生事件）和整体兜底。
        _windowEvents = new WindowEventWatcher();
        _windowEvents.Changed += (hwnd, _) => Safe("watch", () => OnWindowEvent(hwnd));

        _eventPrune = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _eventPrune.Tick += (_, _) => Safe("prune.event", FlushWindowEvents);

        // 被收纳的窗口如果被外部还原 / 关闭，计数要跟着变，不能让胶囊一直显示旧数字。
        _pruneTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _pruneTimer.Tick += (_, _) => Safe("prune", () =>
        {
            if (_disposed) return;
            if (_stash.Count > 0)
            {
                // 兜底对账：钩子装不上 / 漏事件时（进程刚起来、权限不足）仍然会更新。
                _stash.Prune();
                // 应用换图标（浏览器切页面换 favicon）没有任何事件可订阅，只能定期补。
                _stash.ReapplyIcons();
            }
            // 新窗口事件钩子装不上时，用 5 s 巡检兜底「关掉又重开的程序认不回来」。
            if (_windowShownHook == IntPtr.Zero)
                Safe("reattach.scan", ReattachVisibleCandidates);
            // 兜底落盘：隐藏集合变化的第一时间由 UpdateSurface 写，这里只补外部还原 / 关闭。
            WindowRecovery.Save(_stash.Items);
        });
        _pruneTimer.Start();
    }

    /// <summary>单窗口模式：只把开关状态和「哪个场景算一个窗口」的判定接进来。</summary>
    private void WireSingleWindow()
    {
        _single = new SingleWindowMode(Dispatcher.CurrentDispatcher);
        // 排除列表（默认只有资源管理器）；面板的「排除管理」里勾选 / 增删，改完立刻生效。
        _single.Exclusions = _settings.Exclusions();
        // 打开开关时这块屏幕上没有可接管的窗口：提示用户，而不是默默什么都不做。
        _single.NoCandidateOnScreen += () => Safe("single.none", () =>
            Feedback("这块屏幕上没有可接管的窗口"));
        // 让单窗口模式认识场景：同一场景展开的窗口当成一个整体，而不是互斥的多个窗口。
        _single.GroupOf = hwnd => _stash.GroupIdOf(hwnd);
        // 场景应用的辅助窗口（微信图片查看器 / 小程序宿主）同进程或子进程，按所属场景处理。
        _single.GroupOfRelated = hwnd => ProcessSceneLink.GroupOf(hwnd, _stash.Items);
        // 场景只属于它自己的那块屏幕（收纳时确定，拖动成员不改变）：本屏事件只处理本屏的场景。
        _single.GroupScreen = groupId => _stash.MonitorOfGroup(groupId);
        // 场景成员的程序关掉又重开：先认回场景再判定，避免「刚打开的程序把整个场景收走」。
        _single.ClaimForeground = hwnd => _scenes.TryReattach(hwnd);
        // 切到别的场景 / 别的窗口时，本屏的其它场景整组收回（场景是整体，成员拖到哪都一起收）。
        _single.CollapseGroup = groupId => _stash.StashGroup(groupId);
        _single.Applied += count => Safe("single.applied", () =>
        {
            if (count > 0) _tray?.SetStatus($"单窗口模式：已收起 {count} 个窗口");
        });
    }

    /// <summary>任务栏胶囊：左键走规则入口，右键是三个显式命令。</summary>
    private void WireChips()
    {
        _chips = new TaskbarChipLayer();
        _chips.Clicked += entry => Safe("chip.click", () => OnChipClicked(entry));
        _chips.RenameSceneRequested += entry => Safe("chip.renameScene", () => RenameSavedGroup(entry.SavedId));
        _chips.CloseWindowsRequested += entry =>
            Safe("chip.closeScene", () => DissolveScene(entry.SavedId, keepSnapshot: true));
        _chips.DissolveSceneRequested += entry =>
            Safe("chip.dissolveScene", () => DissolveScene(entry.SavedId, keepSnapshot: false));
        _chips.ShowPanelRequested += () => Safe("chip.panel", OpenPanel);
    }

    /// <summary>
    /// 消息专用窗口 + 热键宿主。胶囊会随场景增删，不能拿它的句柄当宿主。
    /// 顺带做两件启动兜底：回收上次被强杀时遗留的隐藏窗口、清理失效的窗口图标句柄。
    /// </summary>
    private void WireHotkeyHost()
    {
        _messageHost = CreateMessageHost();
        _hotkeys = new HotkeyManager(_messageHost.Handle, _messageHost);
        _hotkeys.Pressed += OnHotkeyPressed;

        // 上次如果是被强杀 / 断电结束的，这里把还处在隐藏状态的窗口交回任务栏。
        Safe("recovery", () => WindowRecovery.Recover());

        // 强杀时来不及还原窗口图标：窗口上会残留一张本进程已销毁的 HICON（任务栏图标空白 /
        // 残留圆点）。启动时统一清掉失效句柄，让系统回落到窗口类图标；有效图标不动。
        Safe("icons.cleanup", () =>
        {
            int cleared = NativeMethods.ClearStaleWindowIcons();
            if (cleared > 0) Diagnostics.Info("icons", $"启动清理失效窗口图标 {cleared} 个");
        });
    }

    /// <summary>托盘：菜单动作全部是显式命令，不进规则层。</summary>
    private void WireTray()
    {
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
    }

    /// <summary>入口固定两个，流程写在 RuleSet.CreateDefault() 里，运行期只读。</summary>
    private void WireRules()
    {
        _rules = RuleSet.CreateDefault();
        _runner = CreateRuleRunner();
    }

    /// <summary>主面板：开关、场景行、快捷键录制、外观滑块。</summary>
    private void WirePanel()
    {
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
        _panel.SavedToggleRequested += id => Safe("panel.savedToggle", () => RestoreScene(id));
        _panel.SavedEditRequested += id => Safe("panel.savedEdit", () => OpenSceneEditor(id));
        _panel.HelpRequested += () => Safe("panel.help", ShowHelp);
        // 详情窗跟着面板走：面板收起（✕ / Esc）时一起关掉，不留一个孤立的侧窗。
        _panel.IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) Safe("panel.hide", () =>
            {
                _single.SuspendTemporarily(500, "收起主面板");
                CloseSceneEditor();
                CloseExclusionManager();
            });
        };
        _panel.ExpandLatestRequested += () => Safe("panel.expandLatest", () => ExpandLatest());
        _panel.ExpandAllRequested += () => Safe("panel.expandAll", () => ExpandAll());
        _panel.SingleWindowRequested += value => Safe("panel.singleWindow", () => SetSingleWindowMode(value));
        _panel.ExclusionsRequested += () => Safe("panel.exclusions", OpenExclusionManager);
        _panel.AutoStartRequested += value => Safe("panel.autoStart", () => SetAutoStart(value));
        _panel.HotkeyChangeRequested += (modifiers, key) => Safe("panel.hotkey", () => ChangeHotkey(modifiers, key));
        _panel.AppIconScaleRequested += value => Safe("panel.appIconSize", () => SetSceneIconSizes(value, null));
        _panel.SceneBadgeScaleRequested += value => Safe("panel.badgeSize", () => SetSceneIconSizes(null, value));
    }

    /// <summary>打开主面板（已经打开就置前）：托盘双击 / 胶囊「打开主面板」/ 二次启动都走这里。</summary>
    private void OpenPanel()
    {
        if (_disposed || _panel == null) return;
        bool wasVisible = _panel.IsVisible;
        // 打开自己的面板时 Windows 可能把前台给另一个进程的窗口（本程序窗口被 SKIPOWNPROCESS
        // 挡住，那个事件却会被单窗口模式收到），结果「点一下面板，别的场景全被收了」。
        // 本程序 UI 操作期间暂停单窗口模式，走完再恢复。
        _single.SuspendTemporarily(600, "打开主面板");
        UpdateSurface();
        _panel.ShowPanel();
        if (!wasVisible) Feedback("主面板已打开");
    }

    /// <summary>打开说明窗口（软件截图 + 标注）；已经开着就置前。</summary>
    private void ShowHelp()
    {
        if (_disposed) return;
        if (_helpWindow != null)
        {
            try { _helpWindow.Activate(); } catch { /* 窗口可能正在关闭 */ }
            return;
        }

        _single.SuspendTemporarily(600, "打开说明窗");
        var help = new HelpWindow();
        help.Closed += (_, _) =>
        {
            _single.SuspendTemporarily(500, "关闭说明窗");
            if (ReferenceEquals(_helpWindow, help)) _helpWindow = null;
        };
        _helpWindow = help;

        try
        {
            if (_panel != null && _panel.IsVisible) help.Owner = _panel;
            help.Show();
            help.UpdateLayout();
            if (_panel != null) help.PlaceOnMonitorOf(_panel);
        }
        catch (Exception ex)
        {
            Diagnostics.Write("help", ex);
            if (ReferenceEquals(_helpWindow, help)) _helpWindow = null;
            try { help.Close(); } catch { }
        }
    }

    /// <summary>
    /// 场景详情：贴在主面板旁边的非模态窗口，移除 / 追加 / 重命名 / 删除都在里面完成。
    /// 同一场景再次点详情 = 收起；点另一个场景 = 切换内容并置前。
    /// 移除和追加会同时同步运行中的场景，不需要关掉再打开。
    /// </summary>
    private void OpenSceneEditor(string savedId)
    {
        if (_disposed) return;
        if (_workspace.Find(savedId) == null)
        {
            Feedback("场景已经不存在");
            RequestSurfaceUpdate();
            return;
        }

        // 同一场景再次点「详情」= 收起弹窗；点另一个场景才切内容并置前。
        // 外层卡片的 toggle 已由 Ui.CardButton 按点击源挡住，这里只管详情窗自己。
        if (_sceneEditor != null && SceneEdit.ShouldCloseEditor(_sceneEditorId, savedId))
        {
            CloseSceneEditor();
            return;
        }

        // 已经开着：直接把内容切到新场景并置前，不再叠一个新窗口。
        if (_sceneEditor != null)
        {
            _sceneEditorId = savedId;
            RefreshSceneEditor();
            _single.SuspendTemporarily(600, "切换到场景详情");
            try { _sceneEditor.Activate(); } catch { /* 窗口可能正在关闭 */ }
            if (_panel != null) _sceneEditor.PlaceBeside(_panel);
            return;
        }

        _single.SuspendTemporarily(800, "打开场景详情");
        var editor = new SceneEditorWindow();
        editor.SetPalette(_settings.BadgePalette());
        editor.RemoveWindowRequested += index =>
            Safe("sceneEditor.remove", () => RemoveSceneWindow(_sceneEditorId, index));
        editor.AppendWindowRequested += handle =>
            Safe("sceneEditor.append", () => AppendWindowToScene(_sceneEditorId, handle));
        editor.RefreshListRequested += () => Safe("sceneEditor.refreshList", RefreshSceneEditor);
        editor.RenameRequested += () =>
            Safe("sceneEditor.rename", () => RenameSavedGroup(_sceneEditorId, editor));
        editor.DeleteRequested += () =>
            Safe("sceneEditor.delete", () => DissolveScene(_sceneEditorId, keepSnapshot: false));
        editor.BadgeChangeRequested += badge =>
            Safe("sceneEditor.badge", () => SetSceneBadge(_sceneEditorId, badge));
        editor.PaletteColorRequested += (index, color) =>
            Safe("sceneEditor.paletteColor", () => ChangePaletteColor(index, color));
        editor.PaletteResetRequested += () => Safe("sceneEditor.paletteReset", ResetBadgePalette);
        editor.Closed += (_, _) =>
        {
            _single.SuspendTemporarily(500, "关闭场景详情");
            if (!ReferenceEquals(_sceneEditor, editor)) return;
            _sceneEditor = null;
            _sceneEditorId = null;
        };

        _sceneEditor = editor;
        _sceneEditorId = savedId;
        RefreshSceneEditor();

        try
        {
            // 隐藏的窗口不能当 Owner；面板可见时挂上，保证详情始终跟着面板的层级。
            if (_panel != null && _panel.IsVisible) editor.Owner = _panel;
            editor.Show();
            editor.UpdateLayout();
            if (_panel != null) editor.PlaceBeside(_panel);
        }
        catch (Exception ex)
        {
            Diagnostics.Write("scene editor", ex);
            CloseSceneEditor();
        }
    }

    /// <summary>关闭详情窗；先摘引用再关，避免 Closed 里再次走刷新。</summary>
    private void CloseSceneEditor()
    {
        var editor = _sceneEditor;
        _sceneEditor = null;
        _sceneEditorId = null;
        if (editor == null) return;
        _single.SuspendTemporarily(500, "关闭场景详情");
        try { editor.Close(); } catch { /* 窗口可能已经在关闭 */ }
    }

    /// <summary>把最新场景详情 + 全部可追加窗口推给详情窗；场景被删除时自动关闭。</summary>
    private void RefreshSceneEditor()
    {
        var editor = _sceneEditor;
        if (editor == null || string.IsNullOrEmpty(_sceneEditorId)) return;

        var detail = _scenes.Detail(_sceneEditorId);
        if (detail == null)
        {
            CloseSceneEditor();
            return;
        }

        int runningGroupId = _stash.GroupIdOfSaved(detail.Id);
        editor.Reload(detail, runningGroupId, BuildAppendableWindows(detail, runningGroupId));
    }

    /// <summary>
    /// 排除管理：贴在主面板右侧的非模态窗口；已经开着就置前并刷新。
    /// 勾选 / 删除 / 添加都立刻落盘并推给单窗口模式，不需要关掉面板重开。
    /// </summary>
    private void OpenExclusionManager()
    {
        if (_disposed) return;

        if (_exclusionWindow != null)
        {
            _single.SuspendTemporarily(500, "打开排除管理");
            RefreshExclusionManager();
            try { _exclusionWindow.Activate(); } catch { /* 窗口可能正在关闭 */ }
            if (_panel != null && _panel.IsVisible) _exclusionWindow.PlaceBeside(_panel);
            return;
        }

        _single.SuspendTemporarily(800, "打开排除管理");
        var window = new ExclusionWindow();
        window.EnabledRequested += (index, enabled) =>
            Safe("exclusions.enable", () => SetExclusionEnabled(index, enabled));
        window.DeleteRequested += index =>
            Safe("exclusions.delete", () => DeleteExclusion(index));
        window.AddRequested += (path, name) =>
            Safe("exclusions.add", () => AddExclusion(path, name));
        window.RefreshRequested += () => Safe("exclusions.refresh", RefreshExclusionManager);
        window.Closed += (_, _) =>
        {
            _single.SuspendTemporarily(500, "关闭排除管理");
            if (ReferenceEquals(_exclusionWindow, window)) _exclusionWindow = null;
        };

        _exclusionWindow = window;
        RefreshExclusionManager();

        try
        {
            // 隐藏的窗口不能当 Owner；面板可见时挂上，保证排除管理始终跟着面板的层级。
            if (_panel != null && _panel.IsVisible) window.Owner = _panel;
            window.Show();
            window.UpdateLayout();
            if (_panel != null) window.PlaceBeside(_panel);
        }
        catch (Exception ex)
        {
            Diagnostics.Write("exclusion manager", ex);
            CloseExclusionManager();
        }
    }

    /// <summary>关闭排除管理；先摘引用再关，避免 Closed 里再次走刷新。</summary>
    private void CloseExclusionManager()
    {
        var window = _exclusionWindow;
        _exclusionWindow = null;
        if (window == null) return;
        _single.SuspendTemporarily(500, "关闭排除管理");
        try { window.Close(); } catch { /* 窗口可能已经在关闭 */ }
    }

    /// <summary>把最新排除列表推给单窗口模式 / 面板摘要 / 排除管理窗（开着时才刷新窗口）。</summary>
    private void RefreshExclusionManager()
    {
        if (_disposed) return;
        var rules = _settings.Exclusions();
        _single.Exclusions = rules;
        _panel?.SetExclusionSummary(rules[0].Enabled, rules.Count - 1);
        _exclusionWindow?.Reload(rules, BuildExclusionCandidates(rules));
    }

    /// <summary>
    /// 「添加已打开的窗口」的候选：按应用聚合，一个应用一行。
    /// 资源管理器由内置项覆盖，不再出现在候选里。
    /// </summary>
    private IReadOnlyList<ExclusionCandidate> BuildExclusionCandidates(
        IReadOnlyList<WindowExclusion> rules)
    {
        var apps = new Dictionary<string, CandidateBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in _workspace.EnumerateWindows())
        {
            string path = window.ExePath ?? "";
            string name = WindowExclusion.NormalizeProcessName(window.ProcessName);
            if (name.Length == 0) name = WindowExclusion.FileNameOf(path);
            if (name.Length == 0 && path.Length == 0) continue;
            if (string.Equals(name, WindowExclusion.ExplorerProcess, StringComparison.OrdinalIgnoreCase))
                continue;

            string key = path.Length > 0 ? WindowIdentity.NormalizePath(path) : name;
            if (!apps.TryGetValue(key, out var builder))
            {
                builder = new CandidateBuilder(path, name);
                apps[key] = builder;
            }
            builder.Count++;
        }

        return apps.Values
            .Select(builder => new ExclusionCandidate
            {
                Path = builder.Path,
                ProcessName = builder.Name,
                DisplayName = WindowExclusion.DisplayNameOf(builder.Path, builder.Name),
                WindowCount = builder.Count,
                Excluded = WindowExclusion.MatchesAny(rules, false, builder.Path, builder.Name)
            })
            .OrderBy(candidate => candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>候选聚合的中间状态：同一个应用只保留第一份路径与窗口计数。</summary>
    private sealed class CandidateBuilder
    {
        public CandidateBuilder(string path, string name)
        {
            Path = path;
            Name = name;
        }

        public string Path { get; }
        public string Name { get; }
        public int Count { get; set; }
    }

    /// <summary>
    /// 这块屏幕上的全部窗口：可追加的排在前面；已在当前场景 / 属于其它场景的也列出来，
    /// 只是按钮禁用并说明原因。场景没有窗口时按全部屏幕处理。
    /// </summary>
    private IReadOnlyList<AppendableWindowItem> BuildAppendableWindows(SavedGroupDetail detail, int runningGroupId)
    {
        string monitor = SceneMonitor(detail.Id);
        // 两种键都登记：宽松身份键（应用 + 类名 + 标题）能区分同程序的不同窗口类型，
        // 旧快照没有身份字段时退回 exe + 标题键。位次是相对量、两边不是同一批窗口，
        // 所以这里不用它——只用标题这一层的弱信号，与配对的第一级不冲突。
        var savedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in detail.Windows)
        {
            string loose = WindowIdentity.LooseKey(
                window.AppId, window.ClassName, window.Title);
            if (loose.Length > 0) savedKeys.Add(loose);
            string key = SceneEdit.Key(window.ExePath, window.Title);
            if (key.Length > 0) savedKeys.Add(key);
        }

        bool InSnapshot(WindowSnapshot window)
        {
            string loose = WindowIdentity.LooseKey(
                WindowIdentity.AppId(null, window.ExePath), window.ClassName, window.Title);
            if (loose.Length > 0 && savedKeys.Contains(loose)) return true;
            return savedKeys.Contains(SceneEdit.Key(window.ExePath, window.Title));
        }

        var list = new List<AppendableWindowItem>();
        foreach (var window in _workspace.EnumerateWindows())
        {
            if (monitor != null && window.MonitorDevice != null &&
                !string.Equals(monitor, window.MonitorDevice, StringComparison.Ordinal))
                continue;

            int owner = _stash.GroupIdOf(window.Handle);
            bool inOtherScene = owner != 0 && owner != runningGroupId;
            bool inScene = !inOtherScene &&
                           (owner == runningGroupId && owner != 0 ||
                            owner == 0 && runningGroupId == 0 && InSnapshot(window));

            list.Add(new AppendableWindowItem
            {
                Handle = window.Handle.ToInt64(),
                Title = window.Title,
                ProcessName = window.ProcessName,
                Minimized = window.Minimized,
                InScene = inScene,
                InOtherScene = inOtherScene
            });
        }

        return list
            .OrderBy(item => item.CanAppend ? 0 : 1)
            .ThenBy(item => item.ProcessName)
            .ThenBy(item => item.Title)
            .ToArray();
    }

    /// <summary>场景属于哪块屏幕：运行组优先用实测，已关闭的用快照记录。</summary>
    private string SceneMonitor(string savedId)
    {
        var saved = _workspace.Find(savedId);
        if (saved == null) return null;
        int groupId = _stash.GroupIdOfSaved(savedId);
        return groupId != 0 ? _stash.MonitorOfGroup(groupId) : _workspace.MonitorOfSaved(saved);
    }
}
