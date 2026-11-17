using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>
/// 场景的唯一写入口与生命周期。场景的名字 / 标识 / 关联同时存在于快照（<see cref="SavedGroup"/>）
/// 和运行时组（<see cref="StashGroup"/>），所有修改都必须走这里：
/// 只改一边会让胶囊、面板和下次启动后的场景对不上。
/// 生命周期只有四个动词：New / Restore / Stash / Dissolve。
/// </summary>
public sealed class SceneService
{
    private readonly WorkspaceService _workspace;
    private readonly StashAllService _stash;

    public SceneService(WorkspaceService workspace, StashAllService stash)
    {
        _workspace = workspace;
        _stash = stash;
    }

    /// <summary>
    /// 把运行时组保存成新场景并建立关联（<see cref="StashGroup.SavedId"/>）。
    /// 读不到程序路径时把窗口交回桌面，不留下没有快照的孤儿组；返回 null 表示没存成。
    /// </summary>
    public SavedGroup New(int groupId)
    {
        var saved = _workspace.SaveFrom(_stash, groupId, null);
        if (saved == null)
        {
            _stash.Release(groupId);
            return null;
        }

        _stash.LinkSaved(groupId, saved.Id, saved.Name, saved.Badge);
        return saved;
    }

    /// <summary>展开仍保留的运行时组（场景没有被关闭）。</summary>
    public int Restore(int groupId) => _stash.ExpandGroup(groupId);

    /// <summary>
    /// 打开已关闭的场景：已经在跑的窗口摆回保存位置，缺失的程序启动后接管。
    /// 回调由上层提供：启动的窗口在后台线程出现，摆位完成后要回到 UI 线程接入运行时组。
    /// </summary>
    public (int moved, int launched, int busy, int failed) Restore(
        SavedGroup group,
        IReadOnlySet<long> managed,
        Func<IntPtr, bool> adopt,
        Action<IntPtr> adoptLater,
        Action onComplete = null)
        => _workspace.Restore(group, managed, adopt, adoptLater, onComplete);

    /// <summary>收起场景（快照与运行时组都保留）；monitorDevice 非空时只收这块屏幕上的成员。</summary>
    public int Stash(int groupId, string monitorDevice = null) => _stash.StashGroup(groupId, monitorDevice);

    /// <summary>
    /// 解除场景。keepSnapshot = true：关闭窗口、快照保留（之后还能按原位打开）；
    /// false：窗口交回桌面并删除快照。两种情况的用户可见差异只在这里表达一次。
    /// </summary>
    public SceneDissolveResult Dissolve(string savedId, bool keepSnapshot)
    {
        var saved = _workspace.Find(savedId);
        if (saved == null) return new SceneDissolveResult { Found = false };

        var badge = SceneBadge.Coerce(saved.Badge);
        int groupId = _stash.GroupIdOfSaved(savedId);

        if (keepSnapshot)
        {
            // 没有运行时组 = 窗口已经自己关了：快照本来就在，不需要再关一次。
            if (groupId == 0)
            {
                return new SceneDissolveResult
                {
                    Found = true,
                    Name = saved.Name,
                    Badge = badge,
                    MonitorDevice = _workspace.MonitorOfSaved(saved)
                };
            }

            // 关之前按当前位置再存一次，下次打开才是用户最后摆放的样子。
            _workspace.UpdateSnapshot(saved, _stash, groupId);
            string monitor = _stash.MonitorOfGroup(groupId) ?? _workspace.MonitorOfSaved(saved);
            int closed = _workspace.Close(_stash, groupId);
            return new SceneDissolveResult
            {
                Found = true,
                Name = saved.Name,
                Badge = badge,
                MonitorDevice = monitor,
                GroupId = groupId,
                Closed = closed
            };
        }

        int released = groupId != 0 ? _stash.Release(groupId) : 0;
        bool deleted = _workspace.Delete(savedId);
        return new SceneDissolveResult
        {
            Found = true,
            Name = saved.Name,
            Badge = badge,
            MonitorDevice = _workspace.MonitorOfSaved(saved),
            GroupId = groupId,
            Released = released,
            SnapshotDeleted = deleted
        };
    }

    /// <summary>重命名场景：快照与运行中的胶囊同时改。</summary>
    public bool Rename(string savedId, string name)
    {
        if (!_workspace.Rename(savedId, name)) return false;
        _stash.RenameSaved(savedId, name.Trim());
        return true;
    }

    /// <summary>改场景标识：快照落盘，已展开成员窗口的任务栏图标同步重画。</summary>
    public bool SetBadge(string savedId, SceneBadge badge)
    {
        var normalized = SceneBadge.Coerce(badge);
        if (!_workspace.SetBadge(savedId, normalized)) return false;
        _stash.SetBadge(savedId, normalized);
        return true;
    }

    /// <summary>调色板改色 / 恢复默认：快照与运行时组用同一张映射表，避免链式替换。</summary>
    public void RemapBadgeColors(Func<string, string> remap)
    {
        _workspace.RemapBadgeColors(remap);
        _stash.RemapBadgeColors(remap);
    }

    /// <summary>关闭没有关联快照的旧运行时组（正常路径走 Dissolve）；保留 SceneService 唯一写入口。</summary>
    public int CloseLegacy(int groupId) => _workspace.Close(_stash, groupId);

    /// <summary>
    /// 从场景移除一个窗口：快照删一项；场景正在运行时同时从运行时组摘掉，
    /// 收起中的窗口会放回桌面（不能让它既不可见又不在任务栏）。
    /// </summary>
    public SceneEditResult RemoveWindow(string savedId, int index)
    {
        var result = _workspace.RemoveWindow(savedId, index);
        if (!result.Ok) return result;

        bool detached = _stash.Detach(savedId, result.Removed, out bool restored);
        return new SceneEditResult
        {
            Ok = true,
            Title = result.Title,
            Count = result.Count,
            Removed = result.Removed,
            Detached = detached,
            Restored = restored
        };
    }

    /// <summary>
    /// 把若干窗口追加进场景：写入快照；场景正在运行时立刻接入运行时组，
    /// 不用等关闭再打开。属于其它场景 / 另一块屏幕的窗口会被跳过并计数。
    /// </summary>
    public SceneAppendResult AppendWindows(string savedId, IReadOnlyList<long> handles)
    {
        var saved = _workspace.Find(savedId);
        if (saved == null) return new SceneAppendResult { Error = "场景已经不存在" };
        if (handles == null || handles.Count == 0) return new SceneAppendResult { Error = "没有选择要追加的窗口" };

        int targetGroup = _stash.GroupIdOfSaved(savedId);
        string sceneMonitor = targetGroup != 0
            ? _stash.MonitorOfGroup(targetGroup)
            : _workspace.MonitorOfSaved(saved);

        var incoming = new List<(IntPtr Handle, SavedWindow Window)>();
        int otherScene = 0;
        int otherScreen = 0;

        foreach (long raw in handles.Distinct())
        {
            var hwnd = new IntPtr(raw);
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) continue;

            int owner = _stash.GroupIdOf(hwnd);
            if (owner != 0 && owner != targetGroup)
            {
                otherScene++;
                continue;
            }

            string windowMonitor = NativeMethods.MonitorDeviceOf(hwnd);
            if (sceneMonitor != null && windowMonitor != null &&
                !string.Equals(sceneMonitor, windowMonitor, StringComparison.Ordinal))
            {
                otherScreen++;
                continue;
            }

            var captured = _workspace.CaptureWindow(_stash, hwnd);
            if (captured == null) continue;

            incoming.Add((hwnd, captured));
        }

        if (incoming.Count == 0)
        {
            string reason = otherScene > 0
                ? "这些窗口属于其它场景，不能直接追加"
                : otherScreen > 0
                    ? "不能把另一块屏幕上的窗口加进来"
                    : "这些窗口已经关闭或暂时无法追加";
            return new SceneAppendResult { Error = reason };
        }

        var merge = _workspace.AppendWindows(savedId, incoming.Select(i => i.Window).ToArray());

        int adopted = 0;
        if (targetGroup != 0)
        {
            foreach (var item in incoming)
            {
                if (_stash.GroupIdOf(item.Handle) != 0) continue;
                if (_stash.Adopt(item.Handle, savedId, saved.Name, saved.Badge)) adopted++;
            }
        }

        if (!merge.Ok && adopted == 0)
            return new SceneAppendResult { Error = merge.Error };

        return new SceneAppendResult
        {
            Ok = true,
            Added = merge.Added,
            Adopted = adopted
        };
    }
}

/// <summary>解除场景的结果；界面据此决定提示文案，不用再自己判断快照 / 运行时组的状态。</summary>
public sealed class SceneDissolveResult
{
    public bool Found { get; init; }
    public string Name { get; init; }
    public SceneBadge Badge { get; init; }

    /// <summary>提示出现在哪块屏幕（场景归属屏）。</summary>
    public string MonitorDevice { get; init; }

    /// <summary>关联的运行时组号；0 = 没有打开的窗口。</summary>
    public int GroupId { get; init; }

    /// <summary>关闭窗口模式里实际发出 WM_CLOSE 的数量。</summary>
    public int Closed { get; init; }

    /// <summary>删除场景模式里交回桌面的窗口数。</summary>
    public int Released { get; init; }

    public bool SnapshotDeleted { get; init; }
}
