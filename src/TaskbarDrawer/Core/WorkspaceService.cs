using System.IO;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>
/// 场景：把一组窗口的「软件 + 位置」存成快照，运行中可收起 / 展开，关闭后可一键还原。
/// 运行时组只是场景的运行态；每次收纳都会立刻保存成场景，所以两者始终一一对应。
/// 只有「关闭窗口」（或用户自己点 ×）才真的关窗口，关完后胶囊还在，再点一次全部拉起来。
/// </summary>
public sealed partial class WorkspaceService
{
    private readonly List<SavedGroup> _groups;

    /// <summary>启动时旧数据迁移是否还没落盘（等 AppShell 订阅 SaveFailed 后再写）。</summary>
    private bool _migrationsPending;

    /// <summary>启动时加载场景文件的问题（损坏 / 无权限）；正常时为 null。</summary>
    public string LoadWarning { get; }

    public WorkspaceService()
    {
        _groups = SavedGroupStore.Load(out string warning);
        LoadWarning = warning;

        // 1.6.7 及更早保存的是 emoji 图标：一次性换成「颜色 / 数字 / 位置」的纯色标识。
        int migrated = MigrateLegacyBadges();

        // 1.6.4 及更早保存的副屏矩形多算了一次显示器原点（详见 DisplayRules.TryRepairLegacyRect）。
        // 升级后第一次启动就把这些场景修好，用户不用把每个场景重新存一遍。
        int repaired = RepairLegacyRects();

        // 旧版移除记录按原样保存标题：控制台 / 终端标题常带尾随空格，和新的去重键对不上。
        int normalized = NormalizeSuppressedKeys();

        Diagnostics.Info("scene",
            $"加载场景 {_groups.Count} 个，迁移旧图标 {migrated} 个，修复旧版副屏坐标 {repaired} 个窗口，归一移除记录 {normalized} 条");
        // 迁移结果先不落盘：构造函数里 SaveFailed 还没有订阅者，写失败会静默。
        // 由 AppShell 订阅后调用 PersistPendingMigrations()，失败才能提示用户。
        _migrationsPending = repaired > 0 || migrated > 0 || normalized > 0;
    }

    /// <summary>把启动时旧数据迁移的结果落盘；由 AppShell 在订阅 SaveFailed 之后调用。</summary>
    public void PersistPendingMigrations()
    {
        if (!_migrationsPending) return;
        _migrationsPending = false;
        Persist();
    }

    /// <summary>把旧数据里的移除记录去掉两端空白：与 <see cref="SceneEdit.Key"/> 的新规则对齐。</summary>
    private int NormalizeSuppressedKeys()
    {
        int changed = 0;
        foreach (var group in _groups)
        {
            if (group.Suppressed == null)
            {
                group.Suppressed = new List<string>();
                continue;
            }

            int before = group.Suppressed.Count;
            for (int i = 0; i < group.Suppressed.Count; i++)
            {
                string value = group.Suppressed[i];
                string trimmed = value?.Trim();
                if (!string.Equals(trimmed, value, StringComparison.Ordinal))
                    group.Suppressed[i] = trimmed ?? "";
            }

            group.Suppressed.RemoveAll(string.IsNullOrEmpty);
            changed += before - group.Suppressed.Count;
        }
        return changed;
    }

    /// <summary>旧 emoji 图标 → 场景标识；返回迁移了多少个场景。</summary>
    private int MigrateLegacyBadges()
    {
        int migrated = 0;
        foreach (var group in _groups)
        {
            if (group.Badge == null)
            {
                group.Badge = SceneBadge.FromLegacyEmoji(group.Emoji);
                migrated++;
            }
            else
            {
                group.Badge = group.Badge.Normalized();
            }

            // 1.6.7 调色板里的灰色已换成黑色：旧场景一次性跟着迁移。
            if (string.Equals(group.Badge.Color, SceneBadge.LegacyGray, StringComparison.OrdinalIgnoreCase))
            {
                group.Badge.Color = SceneBadge.DefaultPalette[^1];
                migrated++;
            }

            // 旧字段读进来就丢掉：之后再落盘不会把它写回去。
            group.Emoji = null;
        }
        return migrated;
    }

    /// <summary>把旧版本保存的副屏矩形换算回屏幕坐标；返回修复条数。</summary>
    private int RepairLegacyRects()
    {
        var monitors = NativeMethods.AllMonitors();
        int repaired = 0;
        foreach (var group in _groups)
        {
            foreach (var window in group.Windows)
            {
                if (window.Width <= 0 || window.Height <= 0) continue;

                var saved = NativeMethods.RECT.FromLTRB(
                    window.Left, window.Top, window.Left + window.Width, window.Top + window.Height);
                NativeMethods.RECT fixedRect = saved;
                bool fixedThis = false;

                if (!string.IsNullOrEmpty(window.MonitorDevice) &&
                    NativeMethods.TryGetMonitorOfDevice(window.MonitorDevice, out var monitor))
                {
                    fixedThis = DisplayRules.TryRepairLegacyRect(saved, monitor.rcMonitor, out fixedRect);
                }
                else if (monitors.All(m => DisplayRules.VisibleRatio(saved, m.rcMonitor) == 0))
                {
                    // 更早的快照没记设备名：只有整个矩形完全不在任何屏幕上时，
                    // 才按各屏原点试一次——被旧版多加的正是显示器原点。
                    foreach (var candidate in monitors)
                    {
                        if (!DisplayRules.TryRepairLegacyRect(saved, candidate.rcMonitor, out fixedRect)) continue;
                        fixedThis = true;
                        break;
                    }
                }

                if (!fixedThis) continue;

                window.Left = fixedRect.Left;
                window.Top = fixedRect.Top;
                window.Width = fixedRect.Width;
                window.Height = fixedRect.Height;
                repaired++;
                Diagnostics.Info("scene",
                    $"旧坐标修复 scene={group.Name} exe={Path.GetFileName(window.ExePath)} " +
                    $"「{Brief(window.Title)}」 {saved} → {fixedRect} " +
                    $"device={window.MonitorDevice ?? "(未记录，按屏幕原点推断)"}");
            }
        }
        return repaired;
    }

    /// <summary>落盘失败：场景仍在内存里可用，但重启后会丢，必须让用户知道。</summary>
    public event Action<string> SaveFailed;

    private bool Persist()
    {
        if (SavedGroupStore.Save(_groups)) return true;
        SaveFailed?.Invoke("场景保存失败（磁盘满 / 无权限），本次运行改动的场景重启后会丢失");
        return false;
    }

    public IReadOnlyList<SavedGroupSummary> Summaries()
        => _groups
            .OrderBy(g => g.CreatedAt)
            .Select(g => new SavedGroupSummary
            {
                Id = g.Id,
                Name = g.Name,
                Badge = SceneBadge.Coerce(g.Badge),
                Count = g.Windows.Count,
                Titles = g.Windows.Select(w => w.Title).ToArray()
            })
            .ToArray();

    public SavedGroup Find(string id) => _groups.FirstOrDefault(g => g.Id == id);

    /// <summary>场景详情：编辑器列窗口用。找不到返回 null。</summary>
    /// <param name="openKeys">
    /// 当前确实打开的成员（<see cref="SceneEdit.Key"/> 的集合），用来给每一行标「已打开 / 未打开」。
    /// 传 null 表示不做这项判定。
    /// </param>
    public SavedGroupDetail Detail(string savedId, IReadOnlyCollection<string> openKeys = null)
    {
        var group = Find(savedId);
        if (group == null) return null;

        var open = openKeys == null ? null : new HashSet<string>(openKeys, StringComparer.Ordinal);
        return new SavedGroupDetail
        {
            Id = group.Id,
            Name = group.Name,
            Badge = SceneBadge.Coerce(group.Badge),
            Windows = group.Windows
                .Select((window, index) => SceneEdit.Describe(index, window, IsOpen(window, open)))
                .ToArray()
        };
    }

    /// <summary>
    /// 快照项此刻是否对应着运行中的窗口：身份键（AppId + 类名 + 位次）命中就算，
    /// 旧快照没有身份字段，退回 exe + 标题键。两种都试是为了「切过标签页」也能认对。
    /// </summary>
    private static bool IsOpen(SavedWindow window, HashSet<string> open)
    {
        if (open == null || open.Count == 0) return false;
        if (open.Contains(SceneEdit.IdentityKey(window))) return true;
        return open.Contains(SceneEdit.Key(window));
    }

    /// <summary>已关闭场景属于哪块屏幕：优先场景级归属屏，旧快照退回成员窗口的位置。</summary>
    public string MonitorOfSaved(SavedGroup group)
        // 场景归属屏在创建时冻结：成员被拖到别的屏幕不改变它（单窗口模式按屏隔离的前提）。
        // 旧快照没有场景级字段，退回成员窗口的位置。
        => group == null ? null
            : !string.IsNullOrEmpty(group.MonitorDevice) ? group.MonitorDevice
            : DisplayRules.MonitorOf(group.Windows);

    private static string Normalize(string path)
        => string.IsNullOrWhiteSpace(path) ? null : path.Replace('\\', '/').ToLowerInvariant();

    /// <summary>日志里的标题截断：完整标题可能很长，前 40 字足够认出窗口。</summary>
    private static string Brief(string title)
        => string.IsNullOrEmpty(title) ? "" : title.Length <= 40 ? title : title[..40] + "…";
}

/// <summary>从场景移除一个窗口的结果；错误文案可直接展示给用户。</summary>
public sealed class SceneEditResult
{
    public bool Ok { get; init; }

    /// <summary>被移除窗口的显示名（标题为空时退回进程名）。</summary>
    public string Title { get; init; }

    /// <summary>移除后剩下的窗口数。</summary>
    public int Count { get; init; }

    /// <summary>被移除的快照项（用于同步运行中的场景）。</summary>
    public SavedWindow Removed { get; init; }

    /// <summary>是否同时从运行中的场景摘掉了这个窗口（场景没在运行时为 false）。</summary>
    public bool Detached { get; init; }

    /// <summary>摘掉时窗口原本收在收纳区，已经放回桌面。</summary>
    public bool Restored { get; init; }

    public string Error { get; init; }
}

/// <summary>往场景追加窗口的运行结果。</summary>
public sealed class SceneAppendResult
{
    public bool Ok { get; init; }

    /// <summary>写入快照的新窗口数。</summary>
    public int Added { get; init; }

    /// <summary>场景正在运行时，追加后立刻接入运行时组的窗口数。</summary>
    public int Adopted { get; init; }

    public string Error { get; init; }
}
