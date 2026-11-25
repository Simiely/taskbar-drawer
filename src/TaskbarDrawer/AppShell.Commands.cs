using System.Windows;
using TaskbarDrawer.Core;
using TaskbarDrawer.Core.Rules;
using TaskbarDrawer.Interop;
using TaskbarDrawer.UI;

namespace TaskbarDrawer;

/// <summary>
/// 命令实现：每个用户动作「真正要做的事」。
/// 四个入口（托盘 / 面板 / 胶囊 / 热键）只负责把点击翻译成一个命令；
/// 命令本身只走 <see cref="SceneService"/> / <see cref="StashAllService"/> / <see cref="WorkspaceService"/>，
/// 不直接改控件——界面刷新一律通过 <see cref="RequestSurfaceUpdate"/> 汇总一次。
/// </summary>
public sealed partial class AppShell
{
    /// <summary>统一的用户反馈：底部浮窗弹一条，随后自动消失；托盘提示同步更新。</summary>
    /// <param name="monitorDevice">提示出现在哪块屏幕；null = 鼠标所在屏幕。</param>
    private void Feedback(string message, string monitorDevice = null)
    {
        ToastWindow.Show(message, monitorDevice);
        _tray?.SetStatus(message);
    }

    // ---------- 场景：收纳 / 展开 / 收起 ----------

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
            var saved = _scenes.New(groupId);
            if (saved == null)
            {
                // 两种情况都落到这里：读不到程序路径 / 快照写盘失败。窗口已经放回桌面。
                Feedback("无法存成场景，窗口已放回桌面", monitorDevice);
            }
            else
            {
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
        RequestSurfaceUpdate();
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
        int hidden = StashExpandedGroups(monitorDevice);

        Feedback(hidden > 0 ? $"已收起 {hidden} 个窗口" : "这块屏幕上没有可收起的窗口", monitorDevice);
        RequestSurfaceUpdate();
    }

    /// <summary>
    /// 把这块屏幕上已展开的场景整组收回胶囊；keepGroupId 非 0 时保留指定组。
    /// 只收「归属屏 == 这块屏」的场景：另一块屏的场景不受影响。
    /// 场景是整体——收起时连被拖到别的屏幕的成员一起收。
    /// </summary>
    private int StashExpandedGroups(string monitorDevice, int keepGroupId = 0)
    {
        int hidden = 0;
        foreach (var group in _stash.Groups()
                     .Where(g => g.Expanded && g.Id != keepGroupId)
                     .Where(g => DisplayRules.BelongsTo(monitorDevice, _stash.MonitorOfGroup(g.Id)))
                     .ToArray())
            hidden += _stash.StashGroup(group.Id);
        return hidden;
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
        // 这块屏上展开的场景里可能有人把程序关掉又重开了：把桌面上已开着的同程序窗口认回来。
        if (shown > 0) shown += _scenes.ReclaimVisible(_stash.LastExpandedGroupId);
        if (shown > 0) CollapseOtherScenes(_stash.LastExpandedGroupId);
        if (shown > 0) FinishSceneOpen(_stash.LastExpandedGroupId);
        Feedback(shown > 0
            ? $"{BadgeText(BadgeOfGroup(_stash.LastExpandedGroupId))}已展开「{SceneLabel(_stash.LastExpandedGroupId)}」· {shown} 个窗口"
            : "这块屏幕上没有已收起的场景", monitorDevice);
        RequestSurfaceUpdate();
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
        if (shown > 0)
        {
            // 展开后同样认回这块屏幕上已开着的同程序窗口（关掉又重开的成员）。
            foreach (var group in _stash.Groups().Where(g => g.Expanded).ToArray())
                shown += _scenes.ReclaimVisible(group.Id);
        }
        Feedback(shown > 0
            ? $"已展开这块屏幕上的全部场景 · {shown} 个窗口"
            : "这块屏幕上没有已收起的场景", monitorDevice);
        RequestSurfaceUpdate();
    }

    /// <summary>点胶囊 / 面板场景行：已收起 → Restore，已展开 → Stash（场景始终保留）。</summary>
    private void ToggleScene(int groupId)
    {
        if (_disposed) return;
        _single.SuspendTemporarily();
        string label = SceneLabel(groupId);
        // 提示跟着场景走：在任务栏胶囊上操作副屏场景时，提示出现在副屏而不是主屏。
        string monitor = _stash.MonitorOfGroup(groupId);
        var summary = _stash.Groups().FirstOrDefault(g => g.Id == groupId);
        if (summary == null)
        {
            Feedback($"「{label}」已经不存在", monitor);
            RequestSurfaceUpdate();
            return;
        }

        if (summary.Expanded)
        {
            int count = _scenes.Stash(groupId);
            Feedback(count > 0
                ? $"已收起「{label}」· {count} 个窗口"
                : $"「{label}」已经不存在", monitor);
        }
        else
        {
            int count = _scenes.Restore(groupId);
            if (count == 0)
            {
                Feedback($"「{label}」已经不存在", monitor);
            }
            else
            {
                int collapsed = CollapseOtherScenes(groupId);
                FinishSceneOpen(groupId);
                Feedback(collapsed > 0
                    ? $"已展开「{label}」· {count} 个窗口 · 其它场景已收起"
                    : $"已展开「{label}」· {count} 个窗口", monitor);
            }
        }
        RequestSurfaceUpdate();
    }

    /// <summary>
    /// 单窗口模式下展开一个场景时，把其它已展开的场景收回：
    /// 「每个场景就是一个单窗口」，切场景时上一个场景应该回到收纳区，而不是留在桌面上。
    ///
    /// 每块屏幕独立：只收和这个场景属于同一块屏的场景；另一块屏上的场景保持不动。
    /// 场景是整体：收起时成员拖到哪都一起收（<see cref="StashAllService.StashGroup"/> 不传屏幕）。
    /// </summary>
    private int CollapseOtherScenes(int keepGroupId)
    {
        if (!_single.Enabled) return 0;

        string screen = _stash.MonitorOfGroup(keepGroupId);
        if (screen == null) return 0;

        return StashExpandedGroups(screen, keepGroupId);
    }

    /// <summary>面板场景行的「关闭窗口」：有关联快照就按场景关闭，否则兜底直接关这个场景的窗口。</summary>
    private void CloseRunningScene(int groupId)
    {
        if (_disposed) return;
        string savedId = _stash.SavedIdOf(groupId);
        if (savedId != null)
        {
            DissolveScene(savedId, keepSnapshot: true);
            return;
        }

        ReportClose(_scenes.CloseLegacy(groupId), null, null, _stash.MonitorOfGroup(groupId));
        RequestSurfaceUpdate();
    }

    /// <summary>
    /// 打开已关闭的场景：启动软件并摆回保存位置；已经在跑的窗口直接挪回去，不重复开实例。
    /// 拉起来的窗口会接入运行时组，之后这个场景和普通场景一样收起 / 展开。
    /// </summary>
    private void RestoreScene(string id)
    {
        if (_disposed) return;
        var group = _workspace.Find(id);
        if (group == null)
        {
            Feedback("场景已经不存在");
            RequestSurfaceUpdate();
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
            var (moved, launched, busy, failed) = _scenes.Restore(
                group,
                managed,
                hwnd => _stash.Adopt(hwnd, group.Id, group.Name, group.Badge,
                    _workspace.MonitorOfSaved(group)),
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

            // 已运行的窗口是同步接进来的，此刻就能置前；还没出现的启动窗口由 AdoptRestoredWindow 接管。
            if (moved + launched > 0) FinishSceneOpen(_stash.GroupIdOfSaved(group.Id));

            Feedback(message, _workspace.MonitorOfSaved(group));
            RequestSurfaceUpdate();
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
        if (!_stash.Adopt(hwnd, group.Id, group.Name, group.Badge,
                _workspace.MonitorOfSaved(group))) return;

        // 启动的程序窗口是稍后才出现的：这一刻它就是前台，单窗口模式要立刻按它收一遍，
        // 否则批量恢复期间被压掉的那次应用永远补不回来。
        _single.ApplyNow(hwnd);
        RequestSurfaceUpdate();
    }

    /// <summary>
    /// 场景回到屏幕后的收尾：把场景带到最前，并让单窗口模式立刻生效。
    /// 两步都不能省——不置前则窗口停在 z 序最下面（看着像「没打开」）；
    /// 不补跑一次则「其它窗口最小化」永远不会发生：批量恢复期间的前台事件
    /// 全被 <c>SuspendTemporarily</c> 压掉了，之后系统不会再给事件。
    /// </summary>
    private void FinishSceneOpen(int groupId)
    {
        if (_disposed || groupId == 0) return;

        var anchor = _stash.Items.FirstOrDefault(i => i.GroupId == groupId && !i.Hidden);
        if (anchor == null) return;

        WindowActivator.Activate(anchor.Handle);
        _single.ApplyNow(anchor.Handle);
    }

    /// <summary>
    /// 解除场景：keepSnapshot = true 关闭窗口（快照保留，胶囊转为已关闭）；
    /// false 删除场景（窗口交回桌面，快照移除）。
    /// </summary>
    private void DissolveScene(string id, bool keepSnapshot)
    {
        if (_disposed) return;

        var result = _scenes.Dissolve(id, keepSnapshot);
        if (!result.Found)
        {
            RequestSurfaceUpdate();
            return;
        }

        string badge = BadgeText(result.Badge);
        if (keepSnapshot)
        {
            if (result.GroupId == 0)
                Feedback($"「{result.Name}」已经没有打开的窗口", result.MonitorDevice);
            else
                ReportClose(result.ClosedHandles, result.Name, badge, result.MonitorDevice);
            RequestSurfaceUpdate();
            return;
        }

        if (result.SnapshotDeleted)
        {
            string note = result.GroupId != 0 ? " · 窗口留在桌面上" : "";
            Feedback($"{badge}已删除场景「{result.Name}」{note}");
        }
        else if (result.Released > 0)
        {
            // 快照已经不在了：窗口已经交回桌面，界面刷新即可。
            Feedback($"窗口已交回桌面（{result.Released} 个被还原）");
        }
        RequestSurfaceUpdate();
    }

    /// <summary>
    /// 关闭窗口的结果只能「过一会儿再报」：<c>WM_CLOSE</c> 只是投递到目标线程，返回 N 个成功
    /// 不等于关了 N 个——程序弹一次「是否保存」，一个都没关，而旧提示会照报「已关闭 3 个窗口」。
    /// 250 ms 先看一次（绝大多数程序这一下已经关完，用户感知不到延迟）；
    /// 还有活着的再等 800 ms，给「弹了确认框、用户马上点了确定」留余量，最后按实际数字报。
    /// </summary>
    private void ReportClose(
        IReadOnlyList<IntPtr> handles, string name, string badge, string monitor, int attempt = 0)
    {
        if (_disposed) return;

        if (handles == null || handles.Count == 0)
        {
            var empty = new CloseReport(0, 0);
            Feedback(name != null ? empty.DescribeScene(name, badge) : empty.DescribeGroup(), monitor);
            return;
        }

        TimeSpan delay = attempt == 0 ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromMilliseconds(800);
        WaitThen(delay, () =>
        {
            var report = CloseReport.Measure(handles, NativeMethods.IsWindow);
            if (report.Waiting > 0 && attempt == 0)
            {
                ReportClose(handles, name, badge, monitor, 1);
                return;
            }
            Feedback(name != null ? report.DescribeScene(name, badge) : report.DescribeGroup(), monitor);
        });
    }

    // ---------- 场景：名字 / 标识 / 调色板 ----------

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

            // 运行时组上的名字同步刷新，胶囊文字立刻变。
            if (_scenes.Rename(id, name))
                Feedback($"已重命名为「{name}」");
            RequestSurfaceUpdate();
        }));
    }

    /// <summary>改场景标识：快照、运行时组、已展开成员窗口的图标一起更新。</summary>
    private void SetSceneBadge(string id, SceneBadge badge)
    {
        if (_disposed) return;
        var group = _workspace.Find(id);
        if (group == null) return;

        var normalized = SceneBadge.Coerce(badge);
        // 快照、运行时组、已展开成员窗口的图标一起更新；收起的成员等展开时再挂。
        if (!_scenes.SetBadge(id, normalized))
        {
            // 写不进去（场景已被删掉等）：把界面拉回真实数据，
            // 否则详情窗会一直显示一个根本没存下来的标识。
            RequestSurfaceUpdate();
            return;
        }
        Feedback($"{BadgeText(normalized)}场景标识已更新");
        RequestSurfaceUpdate();
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
        _scenes.RemapBadgeColors(remap);
        _sceneEditor?.SetPalette(_settings.BadgePalette());
        RefreshSceneEditor();
        RequestSurfaceUpdate();
    }

    // ---------- 场景：移除 / 追加窗口 ----------

    /// <summary>从场景快照移除一个窗口；场景正在运行时同步从运行时组摘掉。</summary>
    private void RemoveSceneWindow(string savedId, int index)
    {
        if (string.IsNullOrEmpty(savedId)) return;

        var result = _scenes.RemoveWindow(savedId, index);
        if (!result.Ok)
        {
            Feedback(result.Error);
            RequestSurfaceUpdate();
            return;
        }

        string message = $"已从场景移除「{result.Title}」· 还剩 {result.Count} 个窗口";
        if (result.Detached) message += result.Restored ? " · 窗口已放回桌面" : " · 窗口留在桌面";
        Feedback(message);
        RequestSurfaceUpdate();
    }

    /// <summary>把一个窗口追加进场景；场景正在运行时立刻接入运行时组。</summary>
    private void AppendWindowToScene(string savedId, long handle)
    {
        if (string.IsNullOrEmpty(savedId)) return;

        var result = _scenes.AppendWindows(savedId, new[] { handle });
        if (!result.Ok)
        {
            Feedback(result.Error);
            RequestSurfaceUpdate();
            return;
        }

        var parts = new List<string>();
        if (result.Added > 0) parts.Add($"已追加 {result.Added} 个窗口");
        if (result.Adopted > 0) parts.Add("已接入当前场景");
        if (result.Added == 0 && result.Adopted == 0) parts.Add("窗口已经在场景里");
        Feedback(string.Join(" · ", parts));
        RequestSurfaceUpdate();
    }

    // ---------- 规则：事实计算与卡片绑定 ----------

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
            if (!string.IsNullOrEmpty(facts.SavedId)) RestoreScene(facts.SavedId);
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

    private void ChangeHotkey(uint modifiers, uint virtualKey)
    {
        if (_disposed) return;

        var candidate = new HotkeyDefinition(_stashHotkey.Id, modifiers, virtualKey);
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
        RequestSurfaceUpdate();
    }

    // ---------- 设置：单窗口模式 / 资源管理器 / 开机自启 / 图标尺寸 ----------

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

    // ---------- 排除管理（单窗口模式） ----------

    /// <summary>勾选 / 取消某一项；资源管理器是内置项，同样可以关闭。</summary>
    private void SetExclusionEnabled(int index, bool enabled)
    {
        var rules = _settings.Exclusions().ToList();
        if (index < 0 || index >= rules.Count) return;
        if (rules[index].Enabled == enabled) return;

        rules[index].Enabled = enabled;
        var changed = rules[index];
        SaveExclusions(rules);
        Feedback(changed.BuiltIn
            ? (enabled ? "资源管理器已不计入单窗口模式" : "资源管理器已按普通窗口处理")
            : (enabled ? $"「{changed.DisplayName}」已排除" : $"「{changed.DisplayName}」不再排除"));
    }

    /// <summary>删除用户添加的项；内置资源管理器只能关闭，不能删除。</summary>
    private void DeleteExclusion(int index)
    {
        var rules = _settings.Exclusions().ToList();
        if (index < 0 || index >= rules.Count) return;
        if (rules[index].BuiltIn)
        {
            Feedback("资源管理器是内置项，只能关闭，不能删除");
            return;
        }

        string name = rules[index].DisplayName;
        rules.RemoveAt(index);
        SaveExclusions(rules);
        Feedback($"已删除排除项「{name}」");
    }

    /// <summary>把一个已打开的窗口所属应用加进排除列表（按应用生效，重开窗口仍有效）。</summary>
    private void AddExclusion(string exePath, string processName)
    {
        Diagnostics.Info("exclusions", $"添加请求 path={exePath} name={processName}");
        var rules = _settings.Exclusions().ToList();
        if (WindowExclusion.ContainsApp(rules, exePath, processName))
        {
            Diagnostics.Info("exclusions", $"添加跳过：已在列表里 name={processName}");
            Feedback("这个应用已经在排除列表里");
            return;
        }

        var rule = WindowExclusion.ForApp(exePath, processName);
        if (string.IsNullOrEmpty(rule.DisplayName))
        {
            Diagnostics.Info("exclusions", $"添加失败：读不到进程信息 path={exePath} name={processName}");
            Feedback("读不到这个应用的进程信息，换一个窗口再试");
            return;
        }

        rules.Add(rule);
        SaveExclusions(rules);
        Diagnostics.Info("exclusions", $"已添加「{rule.DisplayName}」 共 {rules.Count} 项");
        Feedback($"「{rule.DisplayName}」已排除");
    }

    /// <summary>排除列表的唯一落盘入口：整理 → 保存 → 立刻推给单窗口模式和界面。</summary>
    private void SaveExclusions(IReadOnlyList<WindowExclusion> rules)
    {
        var normalized = WindowExclusion.Normalize(rules);
        _settings.SingleWindowExclusions = normalized;
        _settings.ExcludeExplorerFromSingleWindow = normalized[0].Enabled;
        _settings.Save();
        Diagnostics.Info("exclusions", $"已保存 {normalized.Count} 项：" +
            string.Join("、", normalized.Select(rule =>
                $"{rule.DisplayName}{(rule.Enabled ? "" : "(关)")}")));
        RefreshExclusionManager();
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
            ? Math.Round(SceneIconMetrics.ClampAppIconScale(appIconScale.Value), 2)
            : _settings.SceneAppIconSize;
        double badge = badgeScale.HasValue
            ? Math.Round(SceneIconMetrics.ClampBadgeScale(badgeScale.Value), 2)
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

    // ---------- 小工具 ----------

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
    private static string BadgeText(SceneBadge badge) => SceneBadge.PrefixOf(badge);

    /// <summary>收纳成功但漏掉了管理员窗口时，在提示里补一句原因，避免用户以为程序坏了。</summary>
    private string WithElevationNote(string message)
    {
        int blocked = _stash.LastBlockedByElevation;
        return blocked > 0 ? $"{message}（{blocked} 个管理员窗口未收）" : message;
    }
}
