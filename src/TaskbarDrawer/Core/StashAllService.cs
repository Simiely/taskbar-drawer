using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>一组收纳：一个场景的运行态，持有窗口句柄，展开后仍然保留。</summary>
public sealed class StashGroup
{
    public int Id { get; init; }
    public DateTime CreatedAt { get; init; }
    public IntPtr ForegroundBefore { get; init; }

    /// <summary>关联的场景 Id；正常运行时必有，胶囊显示场景名字。</summary>
    public string SavedId { get; set; }

    /// <summary>场景的名字（保存 / 重命名后同步到这里）。</summary>
    public string Name { get; set; }

    /// <summary>场景标识（颜色 / 数字 / 位置）：展开期间会画在成员窗口图标的角上。</summary>
    public SceneBadge Badge { get; set; } = SceneBadge.Default;
}

/// <summary>给托盘菜单 / 任务栏胶囊 / 主面板用的只读分组摘要。</summary>
public sealed class StashGroupSummary
{
    public int Id { get; init; }
    public DateTime CreatedAt { get; init; }
    public int Count { get; init; }

    /// <summary>true = 这一组正展开在屏幕上；false = 收在收纳区里。</summary>
    public bool Expanded { get; init; }

    /// <summary>组内窗口标题（最近一次收纳 / 展开时的快照），用于胶囊 tooltip 与面板列表。</summary>
    public IReadOnlyList<string> Titles { get; init; } = Array.Empty<string>();

    /// <summary>关联的场景 Id；正常运行时必有。</summary>
    public string SavedId { get; init; }

    /// <summary>场景名字。</summary>
    public string Name { get; init; }

    /// <summary>场景标识（颜色 / 数字 / 位置）。</summary>
    public SceneBadge Badge { get; init; } = SceneBadge.Default;

    public string State => Expanded ? "已展开" : "已收起";
}

/// <summary>
/// 「收进任务栏」状态机（v4：场景常驻）。
/// 运行时组就是场景的运行态：收纳 = 记录位置后 SW_HIDE；展开 = 原位还原但<b>不删组</b>；
/// 再次点同一组 = 重新收起。这样一组窗口可以当「工作区」反复复用。
/// 新一次收纳只抓「不属于任何场景」的窗口，不会把已展开的场景卷进新组。
/// </summary>
public sealed class StashAllService
{
    private readonly WindowStash _stash;
    private readonly List<StashGroup> _groups = new();
    private int _nextGroupId = 1;

    /// <summary>窗口操作端口：状态机只通过它碰真实窗口，单测注入假实现。</summary>
    private readonly IWindowGateway _gateway;

    public StashAllService(ISceneIconPainter icons = null, IWindowGateway gateway = null)
    {
        _gateway = gateway ?? Win32WindowGateway.Instance;
        // 同一个网关实例要交给 WindowStash：假实现里窗口状态是一份内存，两边必须看到同一份。
        _stash = new WindowStash(icons, _gateway);
    }

    /// <summary>下一次 ExpandNewest 从比它更旧的组开始选，实现多组轮转；按显示器各自独立。</summary>
    private readonly Dictionary<string, int> _rotateFrom = new(StringComparer.Ordinal);

    /// <summary>仍被跟踪的窗口总数（含已展开的组）。</summary>
    public int Count => _stash.Count;

    /// <summary>当前所有被收纳的窗口（供崩溃回收记录使用）。</summary>
    public IReadOnlyCollection<StashedWindow> Items => _stash.Items;

    public event Action Changed;

    /// <summary>这个句柄此刻是否在记录里（窗口事件回调用它快速丢掉无关窗口）。</summary>
    public bool Contains(IntPtr hwnd) => hwnd != IntPtr.Zero && _stash.Contains(hwnd.ToInt64());

    /// <summary>重贴场景图标（应用改图标不会产生任何事件，只能定期补）。返回真正重画过的条数。</summary>
    public int ReapplyIcons() => _stash.ReapplyIcons();

    /// <summary>最新的组在前。</summary>
    public IReadOnlyList<StashGroupSummary> Groups()
    {
        SyncGroups();
        return _groups
            .OrderByDescending(g => g.Id)
            .Select(g =>
            {
                var items = _stash.Items.Where(i => i.GroupId == g.Id).ToArray();
                return new StashGroupSummary
                {
                    Id = g.Id,
                    CreatedAt = g.CreatedAt,
                    Count = items.Length,
                    Expanded = items.Length > 0 && items.Any(i => !i.Hidden),
                    Titles = items.Select(i => i.Title).ToArray(),
                    SavedId = g.SavedId,
                    Name = g.Name,
                    Badge = SceneBadge.Coerce(g.Badge)
                };
            })
            .Where(g => g.Count > 0)
            .ToArray();
    }

    /// <summary>
    /// 把屏幕上「还不属于任何场景」的窗口收成新的一组；没有可收的窗口时不建组。
    /// monitorDevice 必填：只收这块屏幕的窗口，多显示器下每块屏幕各自形成独立场景。
    /// 屏幕未知时返回 0（fail-closed）——绝不把「不知道哪块屏」当成「所有屏」。
    /// groupId 返回新建组的组号（0 = 没有建组），供上层立刻保存成场景。
    /// </summary>
    public int StashAll(out int groupId, string monitorDevice)
    {
        groupId = 0;
        if (string.IsNullOrEmpty(monitorDevice)) return 0;
        _stash.Prune();
        SyncGroups();

        IntPtr foreground = _gateway.Foreground();
        int newGroupId = _nextGroupId;
        LastBlockedByElevation = 0;

        int hidden = 0;
        foreach (var candidate in _gateway.Enumerate(includeMinimized: false))
        {
            // 已属于某组（正展开）的窗口不再重复收，否则会把上一组拆散。
            if (_stash.Contains(candidate.Handle.ToInt64())) continue;

            // 只看当前这块屏幕：在显示器 1 操作时不要动显示器 2 的窗口。
            if (!DisplayRules.BelongsTo(monitorDevice, _gateway.MonitorOf(candidate.Handle))) continue;

            // 管理员窗口：本程序不是管理员时 SW_HIDE 会被 UIPI 拒绝（ShowWindow 返回 false），
            // 硬收只会留下一个点不开的空胶囊，所以跳过并计数，由界面提示原因。
            // 提权判断在网关里（只有普通权限实例才需要跨进程查令牌）。
            if (_gateway.Describe(candidate.Handle)?.Elevated == true)
            {
                LastBlockedByElevation++;
                continue;
            }

            var item = _stash.Capture(candidate.Handle, newGroupId);
            if (item == null) continue;
            if (_stash.Hide(item)) hidden++;
        }

        if (hidden == 0) return 0;

        _nextGroupId++;
        _groups.Add(new StashGroup
        {
            Id = newGroupId,
            CreatedAt = DateTime.Now,
            ForegroundBefore = IsEligibleActivationTarget(foreground) &&
                               DisplayRules.BelongsTo(monitorDevice, _gateway.MonitorOf(foreground))
                ? foreground
                : IntPtr.Zero
        });
        groupId = newGroupId;
        Diagnostics.Info("scene",
            $"收纳新场景 group={newGroupId} screen={monitorDevice} 收起={hidden} 管理员跳过={LastBlockedByElevation}");
        Changed?.Invoke();
        return hidden;
    }

    /// <summary>最近一次「展开一组」实际展开的组号，用于反馈文案。</summary>
    public int LastExpandedGroupId { get; private set; }

    /// <summary>
    /// 上一次收纳里因为「窗口属于管理员进程、本程序不是」而没能收进去的窗口数。
    /// 跨完整性级别隐藏窗口会被 UIPI 拒绝，只能跳过并如实告知用户。
    /// </summary>
    public int LastBlockedByElevation { get; private set; }

    /// <summary>窗口所属的组号；0 = 不在任何组里。供单窗口模式做「整组即一个窗口」判定。</summary>
    public int GroupIdOf(IntPtr hwnd) => _stash.GroupIdOf(hwnd);

    /// <summary>按窗口当前实际位置刷新记录（保存场景前调用，拿到最新矩形）。</summary>
    public void RefreshPlacement(StashedWindow item) => _stash.Refresh(item);

    /// <summary>只读抓取一个窗口的快照数据（不加入任何组、不隐藏），用于「追加窗口」。</summary>
    public StashedWindow CaptureSnapshot(IntPtr hwnd) => _stash.Capture(hwnd, 0);

    /// <summary>
    /// 从运行中的场景摘掉一个窗口：按两层身份配对（位次 → 标题 → 最近），收起中的放回桌面。
    ///
    /// 早先这里用 exe + 标题匹配旧快照，同程序多窗口且标题早就变了时直接匹配不到、
    /// 放弃摘除——运行中的成员还挂在组里，关闭场景时又被写回快照，用户看到的就是
    /// 「移除的窗口下次打开还在」。这和还原走的是同一个判断，必须共用 <see cref="WindowIdentity.Pair"/>。
    /// </summary>
    public bool Detach(string savedId, SavedWindow target, out bool restored)
    {
        restored = false;
        int groupId = GroupIdOfSaved(savedId);
        if (groupId == 0 || target == null) return false;

        var items = _stash.Items.Where(i => i.GroupId == groupId).ToArray();
        if (items.Length == 0) return false;

        var candidates = items.Select(WindowIdentity.Candidate).ToArray();
        var ordinals = WindowIdentity.AssignOrdinals(candidates);
        int index = WindowIdentity.Pair(candidates, target, ordinals);
        if (index < 0) return false;

        var match = items[index];
        if (!_stash.Untrack(match.Handle, out restored)) return false;
        SyncGroups();
        Diagnostics.Info("scene",
            $"从场景移除运行窗口 group={groupId}「{match.Title}」 已放回桌面={restored}");
        Changed?.Invoke();
        return true;
    }

    /// <summary>这个场景现在有没有对应的运行时组（0 = 已关闭，只有存下来的快照）。</summary>
    public int GroupIdOfSaved(string savedId)
        => string.IsNullOrEmpty(savedId) ? 0 : _groups.FirstOrDefault(g => g.SavedId == savedId)?.Id ?? 0;

    /// <summary>这一组关联的场景 Id；正常运行时必有。</summary>
    public string SavedIdOf(int groupId) => _groups.FirstOrDefault(g => g.Id == groupId)?.SavedId;

    /// <summary>保存后把运行时组和场景记关联起来：胶囊从此显示自定义名字。</summary>
    public void LinkSaved(int groupId, string savedId, string name, SceneBadge badge)
    {
        var group = _groups.FirstOrDefault(g => g.Id == groupId);
        if (group == null) return;
        group.SavedId = savedId;
        group.Name = name;
        group.Badge = SceneBadge.Coerce(badge);
        // 组的标识也可能在收起状态下改过：先把标识记到每个成员上，展开时直接生效。
        foreach (var item in _stash.Items.Where(i => i.GroupId == groupId))
            _stash.SetSceneIcon(item, group.Badge);
        Changed?.Invoke();
    }

    /// <summary>重命名场景时同步刷新运行时组上的名字。</summary>
    public void RenameSaved(string savedId, string name)
    {
        bool changed = false;
        foreach (var group in _groups.Where(g => g.SavedId == savedId))
        {
            group.Name = name;
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// 改场景标识：已经展开的成员窗口任务栏图标立刻重画；收起的成员等展开时再挂。
    /// </summary>
    public void SetBadge(string savedId, SceneBadge badge)
    {
        var normalized = SceneBadge.Coerce(badge);
        bool changed = false;
        foreach (var group in _groups.Where(g => g.SavedId == savedId))
        {
            group.Badge = normalized;
            foreach (var item in _stash.Items.Where(i => i.GroupId == group.Id))
                _stash.SetSceneIcon(item, normalized);
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// 调色板换了颜色：运行时组的标识颜色一起换，已展开成员窗口的任务栏图标立即重画；
    /// 恢复默认也走同一条路，映射一次性算好，避免链式替换。
    /// </summary>
    public int RemapBadgeColors(Func<string, string> remap)
    {
        if (remap == null) return 0;

        int changed = 0;
        foreach (var group in _groups)
        {
            if (group.Badge == null) continue;
            string next = SceneBadge.NormalizeColor(remap(group.Badge.Color));
            if (string.Equals(next, group.Badge.Color, StringComparison.OrdinalIgnoreCase)) continue;

            group.Badge = new SceneBadge
            {
                Color = next,
                Number = group.Badge.Number,
                Right = group.Badge.Right
            };
            foreach (var item in _stash.Items.Where(i => i.GroupId == group.Id))
                _stash.SetSceneIcon(item, group.Badge);
            changed++;
        }

        if (changed > 0) Changed?.Invoke();
        return changed;
    }

    /// <summary>图标尺寸参数变了：已展开窗口的合成图标按新尺寸重画。</summary>
    public void RefreshSceneIcons() => _stash.RefreshSceneIcons();

    /// <summary>
    /// 把从场景拉起的窗口接入运行时组（保持可见，不隐藏）。
    /// 这样打开后的组和普通场景一样：点胶囊收起 / 展开，而不是反复关掉再开。
    /// 窗口已属于别的组时不动它，避免拆散别的场景。
    /// </summary>
    public bool Adopt(IntPtr hwnd, string savedId, string name, SceneBadge badge)
    {
        if (hwnd == IntPtr.Zero) return false;
        _stash.Prune();
        if (_stash.Contains(hwnd.ToInt64())) return false;

        int groupId = GroupIdOfSaved(savedId);
        if (groupId == 0)
        {
            groupId = _nextGroupId++;
            _groups.Add(new StashGroup
            {
                Id = groupId,
                CreatedAt = DateTime.Now,
                SavedId = savedId,
                Name = name,
                Badge = SceneBadge.Coerce(badge)
            });
        }

        var item = _stash.Capture(hwnd, groupId);
        if (item == null) return false;
        // 展开中的场景窗口图标换成「场景标识 + 程序图标」，任务栏按钮一眼能看出属于哪个场景。
        _stash.SetSceneIcon(item, _groups.First(g => g.Id == groupId).Badge);
        _stash.Track(item);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 这个场景现在属于哪块屏幕：展开时按可见窗口实测（用户可能把它拖到别的屏），
    /// 全部收起时用收纳时记下的设备名。多显示器下热键 / 收起都靠它按屏隔离。
    /// </summary>
    public string MonitorOfGroup(int groupId)
    {
        var items = _stash.Items.Where(i => i.GroupId == groupId).ToArray();
        foreach (var item in items.Where(i => !i.Hidden))
        {
            string device = _gateway.MonitorOf(item.Handle);
            if (device != null) return device;
        }
        return items.FirstOrDefault(i => !string.IsNullOrEmpty(i.MonitorDevice))?.MonitorDevice;
    }

    /// <summary>
    /// 展开一组仍收着的窗口。多组时按组号从新到旧轮转：这次展开第 3 组，下次第 2 组，
    /// 到最旧后回到最新。早先固定取「最新一个未展开组」，会导致热键永远只在同一组上来回，
    /// 更旧的组只能去点胶囊或面板。
    /// monitorDevice 必填：只在这块屏幕上轮转，轮转指针也按屏幕各自记录，互不影响。
    /// </summary>
    public int ExpandNewest(string monitorDevice)
    {
        if (string.IsNullOrEmpty(monitorDevice)) return 0;

        var collapsed = Groups()
            .Where(g => !g.Expanded)
            .Where(g => DisplayRules.BelongsTo(monitorDevice, MonitorOfGroup(g.Id)))
            .OrderByDescending(g => g.Id)
            .ToArray();
        if (collapsed.Length == 0) return 0;

        _rotateFrom.TryGetValue(monitorDevice, out int fromId);
        int nextId = DisplayRules.NextRotation(collapsed.Select(g => g.Id).ToArray(), fromId);
        _rotateFrom[monitorDevice] = nextId;
        LastExpandedGroupId = nextId;
        return ExpandGroup(nextId);
    }

    /// <summary>展开某一组（组继续保留，随时可再收起）。</summary>
    public int ExpandGroup(int groupId)
    {
        var group = _groups.FirstOrDefault(g => g.Id == groupId);
        if (group == null) return 0;

        int shown = 0;
        var restored = new List<IntPtr>();
        foreach (var item in _stash.Items.Where(i => i.GroupId == groupId && i.Hidden).ToArray())
        {
            // 收起期间标识可能改过：展开前把当前标识记到成员上，Restore 会据此换窗口图标。
            _stash.SetSceneIcon(item, group.Badge);
            if (!_stash.Restore(item.Handle.ToInt64())) continue;
            shown++;
            restored.Add(item.Handle);
        }

        // 置前：优先收纳前的前台窗口；从快照拉起的组没有记录前台窗口，
        // 退回第一个刚还原的窗口——否则整组会停在 z 序最下面（看着像「没打开」）。
        if (shown > 0)
        {
            IntPtr focus = SceneFocusRules.Pick(group.ForegroundBefore, restored, IsEligibleActivationTarget);
            if (focus != IntPtr.Zero) _gateway.Activate(focus);
        }

        if (shown > 0) Changed?.Invoke();
        Diagnostics.Info("scene", $"展开 group={groupId}「{group.Name}」 个数={shown}");
        return shown;
    }

    /// <summary>
    /// 把单个已接入的窗口重新收进收纳区。
    /// 往「收起中」的场景追加窗口时用：新窗口本来是可见的，直接接入会让整组状态翻成「已展开」，
    /// 而其它成员还留在收纳区，胶囊语义就乱了。
    /// </summary>
    public bool StashSingle(IntPtr hwnd) => _stash.HideAgain(hwnd.ToInt64());

    /// <summary>
    /// 把某一组重新收起（保留组记录，位置按当前状态刷新）。
    /// monitorDevice 非空时只收这块屏幕上的成员：跨屏场景（旧快照还原 / 用户把窗口拖到副屏）
    /// 不会因为一块屏上的操作整组消失。null = 整组收起（点胶囊这种明确的整场景操作）。
    /// </summary>
    public int StashGroup(int groupId, string monitorDevice = null)
    {
        var group = _groups.FirstOrDefault(g => g.Id == groupId);
        if (group == null) return 0;

        int hidden = 0;
        foreach (var item in _stash.Items.Where(i => i.GroupId == groupId && !i.Hidden).ToArray())
        {
            // 按屏隔离：收紧时再问一次窗口当前在哪块屏，跨屏成员留在原屏幕。
            if (!string.IsNullOrEmpty(monitorDevice) &&
                !DisplayRules.BelongsTo(monitorDevice, _gateway.MonitorOf(item.Handle))) continue;
            if (_stash.HideAgain(item.Handle.ToInt64())) hidden++;
        }

        if (hidden > 0) Changed?.Invoke();
        if (hidden > 0 || !string.IsNullOrEmpty(monitorDevice))
        {
            Diagnostics.Info("scene",
                $"收起 group={groupId}「{group.Name}」 screen={monitorDevice ?? "(整组)"} 个数={hidden}");
        }
        return hidden;
    }

    /// <summary>删除场景时，把运行时组整体交回桌面（不最小化、不关闭）。</summary>
    public int Release(int groupId)
    {
        var group = _groups.FirstOrDefault(g => g.Id == groupId);
        if (group == null) return 0;

        // 只还原被隐藏的窗口；已经可见的窗口保持原位，不能因为删除场景而跳动。
        int shown = 0;
        foreach (var item in _stash.Items.Where(i => i.GroupId == groupId && i.Hidden).ToArray())
        {
            if (_stash.Restore(item.Handle.ToInt64())) shown++;
        }
        // 场景没了，场景图标也要跟着摘掉（对已可见窗口同样生效）。
        foreach (var item in _stash.Items.Where(i => i.GroupId == groupId).ToArray())
            _stash.ClearSceneIcon(item);

        _stash.RemoveGroup(groupId);
        _groups.Remove(group);
        Changed?.Invoke();
        return shown;
    }

    /// <summary>展开全部组（组都保留）。monitorDevice 必填：只展开这块屏幕上的场景。</summary>
    public int ExpandAll(string monitorDevice)
    {
        if (string.IsNullOrEmpty(monitorDevice)) return 0;

        int shown = 0;
        // 从旧到新依次还原：最后激活的是最新一组的前台窗口。
        foreach (var group in Groups()
                     .Where(g => !g.Expanded)
                     .Where(g => DisplayRules.BelongsTo(monitorDevice, MonitorOfGroup(g.Id)))
                     .OrderBy(g => g.Id))
            shown += ExpandGroup(group.Id);
        return shown;
    }

    /// <summary>程序退出 / 崩溃兜底：把窗口交回任务栏（最小化、可见）。</summary>
    public int ReleaseAll()
    {
        int released = _stash.ReleaseAll();
        _groups.Clear();
        if (released > 0) Changed?.Invoke();
        return released;
    }

    /// <param name="nowUtc">只给单测用：宽限期需要把时间推快，生产路径传 null。</param>
    public int Prune(DateTime? nowUtc = null)
    {
        int changed = _stash.Prune(nowUtc);
        int before = _groups.Count;
        SyncGroups();
        // changed 包含「被外部还原」的窗口：组还在，但展开状态和计数都变了，界面必须刷新。
        if (changed > 0 || _groups.Count != before) Changed?.Invoke();
        return changed;
    }

    /// <summary>
    /// 这块屏幕上是否还有不属于任何场景的窗口（规则里的条件卡片要用）。
    /// 多显示器下按屏判断：另一块屏上有窗口不算，避免在空屏幕上误判「该新建场景」。
    /// 屏幕未知时返回 false（fail-closed）。
    /// </summary>
    public bool HasUntrackedVisibleWindows(string monitorDevice)
    {
        if (string.IsNullOrEmpty(monitorDevice)) return false;

        foreach (var candidate in _gateway.Enumerate(includeMinimized: false))
        {
            if (!DisplayRules.BelongsTo(monitorDevice, _gateway.MonitorOf(candidate.Handle))) continue;
            if (!_stash.Contains(candidate.Handle.ToInt64())) return true;
        }
        return false;
    }

    /// <summary>窗口全被外部关掉后，把空组从列表里去掉。</summary>
    private void SyncGroups()
    {
        if (_groups.Count == 0) return;
        var alive = new HashSet<int>(_stash.Items.Select(i => i.GroupId));
        _groups.RemoveAll(g => !alive.Contains(g.Id));
    }

    private bool IsEligibleActivationTarget(IntPtr hwnd)
        => hwnd != IntPtr.Zero && _gateway.IsWindow(hwnd) &&
           !WindowEnumerator.IsOwnProcessWindow(hwnd) && !WindowEnumerator.IsShellWindow(hwnd);
}
