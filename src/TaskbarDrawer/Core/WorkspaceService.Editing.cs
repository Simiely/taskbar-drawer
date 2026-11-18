namespace TaskbarDrawer.Core;

/// <summary>
/// 场景编辑：只改快照数据（移除 / 追加 / 改名 / 标识 / 删除），不碰任何窗口。
/// 运行中的场景由 <see cref="SceneService"/> 负责同步到运行时组，这一片只管落盘那一侧。
/// </summary>
public sealed partial class WorkspaceService
{
    /// <summary>
    /// 从场景快照里移除一个窗口。只改快照：正在运行的窗口不关也不动，下次打开场景生效。
    /// </summary>
    public SceneEditResult RemoveWindow(string savedId, int index)
    {
        var group = Find(savedId);
        if (group == null) return new SceneEditResult { Error = "场景已经不存在" };

        if (!SceneEdit.RemoveAt(group.Windows, index, out var removed))
        {
            return new SceneEditResult
            {
                Error = group.Windows.Count <= 1
                    ? "场景至少保留一个窗口；整个场景不要了请用「删除场景」"
                    : "这一项已经不存在，列表可能已刷新"
            };
        }

        // 记身份键（AppId + 类名 + 位次）：同程序两个同名窗口只有它能分开。
        // 旧快照没有身份字段时 IdentityKey 会退回 exe + 标题键，与旧数据保持兼容。
        string key = SceneEdit.IdentityKey(removed);
        // 同名窗口可能被逐个移除：每次都记一次，过滤时按出现次数抵消。
        if (key.Length > 0) group.Suppressed.Add(key);
        Persist();

        return new SceneEditResult
        {
            Ok = true,
            Title = string.IsNullOrWhiteSpace(removed.Title)
                ? string.IsNullOrWhiteSpace(removed.ProcessName) ? "窗口" : removed.ProcessName
                : removed.Title,
            Count = group.Windows.Count,
            Removed = removed
        };
    }

    /// <summary>
    /// 把一组窗口追加进场景快照：已存在的跳过；只改快照，下次打开场景生效。
    /// </summary>
    public SceneAppendResult AppendWindows(string savedId, IReadOnlyList<SavedWindow> incoming)
    {
        var group = Find(savedId);
        if (group == null) return new SceneAppendResult { Error = "场景已经不存在" };

        var (added, skipped) = SceneEdit.Merge(group.Windows, group.Suppressed, incoming);
        if (added == 0)
        {
            return new SceneAppendResult
            {
                Error = skipped > 0
                    ? $"这 {skipped} 个窗口已经在场景里了"
                    : "没有可追加的窗口"
            };
        }

        Persist();
        return new SceneAppendResult { Ok = true, Added = added };
    }

    /// <summary>自动命名：取最小的未被占用的「场景 N」，删除场景后编号可以复用。</summary>
    private string NextSceneName()
    {
        for (int index = 1; ; index++)
        {
            string candidate = $"场景 {index}";
            if (_groups.All(g => g.Name != candidate)) return candidate;
        }
    }

    public bool Rename(string id, string name)
    {
        var group = Find(id);
        if (group == null || string.IsNullOrWhiteSpace(name)) return false;
        group.Name = name.Trim();
        Persist();
        return true;
    }

    /// <summary>改场景标识（颜色 / 数字 / 位置）。只改快照；运行中的窗口由 StashAllService 同步。</summary>
    public bool SetBadge(string id, SceneBadge badge)
    {
        var group = Find(id);
        if (group == null) return false;
        group.Badge = SceneBadge.Coerce(badge);
        Persist();
        return true;
    }

    /// <summary>
    /// 调色板换了颜色：所有用旧颜色的场景一起换成新颜色（同时批量恢复默认也走这里）。
    /// 返回受影响场景数；映射为「同时生效」，不会因为链式替换把颜色改乱。
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
            group.Badge.Color = next;
            changed++;
        }

        if (changed > 0) Persist();
        return changed;
    }

    public bool Delete(string id)
    {
        var group = Find(id);
        if (group == null) return false;
        _groups.Remove(group);
        Persist();
        return true;
    }
}
