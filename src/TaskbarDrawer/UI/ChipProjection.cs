#nullable enable

using TaskbarDrawer.Core;

namespace TaskbarDrawer.UI;

/// <summary>
/// 胶囊与面板的只读投影：把运行时组 / 场景摘要变成界面文案。
/// 用户只认「场景」：运行时组是场景的运行态，已关闭的场景只剩快照。
/// 无状态、不引用任何控件，所以可以直接单测，不必起窗口。
/// </summary>
public static class ChipProjection
{
    /// <summary>已保存、但当前没有对应运行时组的场景（只剩快照）。</summary>
    public static SavedGroupSummary[] Dormant(
        IReadOnlyList<StashGroupSummary> groups, IReadOnlyList<SavedGroupSummary> saved)
    {
        var live = new HashSet<string>(groups.Where(g => g.SavedId != null).Select(g => g.SavedId));
        return saved.Where(s => !live.Contains(s.Id)).ToArray();
    }

    /// <summary>
    /// 胶囊顺序：运行中的场景靠托盘（当前工作），已关闭的场景依次排在外侧。
    /// 每个场景只有一枚胶囊——运行时组和快照共用它，不会重复出现。
    /// </summary>
    public static IReadOnlyList<ChipEntry> Chips(
        IReadOnlyList<StashGroupSummary> groups, IReadOnlyList<SavedGroupSummary> saved)
    {
        var chips = new List<ChipEntry>();

        foreach (var group in groups)
        {
            chips.Add(new ChipEntry
            {
                Text = Theme.SceneIcon + ShortName(group.Name),
                Tooltip = RuntimeTooltip(group),
                Highlight = group.Expanded,
                GroupId = group.Id,
                SavedId = group.SavedId,
                Count = group.Count,
                Name = group.Name
            });
        }

        // 已保存、但当前一个窗口都没有的场景：胶囊继续在，点一下重新拉起。
        foreach (var group in saved)
        {
            chips.Add(new ChipEntry
            {
                Text = Theme.SceneIcon + ShortName(group.Name),
                Tooltip = SavedTooltip(group),
                Highlight = false,
                Dormant = true,
                SavedId = group.Id,
                Count = group.Count,
                Name = group.Name
            });
        }

        return chips;
    }

    /// <summary>胶囊上的名字：最多 6 个字，超出省略。</summary>
    public static string ShortName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "场景";
        return name.Length > 6 ? name[..6] + "…" : name;
    }

    public static string RuntimeTooltip(StashGroupSummary group)
    {
        string head = string.IsNullOrEmpty(group.Name)
            ? $"{Theme.SceneIcon} 场景 {group.Id} · {group.Count} 个窗口 · {group.State}"
            : $"{Theme.SceneIcon} {group.Name} · {group.Count} 个窗口 · {group.State}";

        var lines = new List<string> { head };
        foreach (string title in group.Titles.Take(3))
            lines.Add(title.Length > 26 ? title[..26] + "…" : title);
        if (group.Count > 3) lines.Add("…");
        lines.Add(group.Expanded ? "左键收起 · 右键管理" : "左键展开 · 右键管理");
        return string.Join("\n", lines);
    }

    public static string SavedTooltip(SavedGroupSummary group)
    {
        var lines = new List<string> { $"{Theme.SceneIcon} {group.Name} · {group.Count} 个窗口 · 已关闭" };
        foreach (string title in group.Titles.Take(3))
        {
            string text = title ?? "";
            lines.Add(text.Length > 26 ? text[..26] + "…" : text);
        }
        if (group.Count > 3) lines.Add("…");
        lines.Add("左键打开并还原窗口位置 · 右键管理");
        return string.Join("\n", lines);
    }
}
