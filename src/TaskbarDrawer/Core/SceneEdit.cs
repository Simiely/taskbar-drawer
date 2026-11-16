namespace TaskbarDrawer.Core;

/// <summary>
/// 场景快照的纯编辑逻辑：移除 / 追加 / 过滤 / 去重键。
/// 不读写文件、不碰窗口，所以可以直接单测；落盘与关联运行时组由 <see cref="WorkspaceService"/> 负责。
/// </summary>
public static class SceneEdit
{
    /// <summary>
    /// 去重键：规范化 exe 路径 + 窗口标题。
    /// 空路径返回空串，表示这一项不参与去重 / 过滤（读不到程序路径的窗口无法再启动，也不会进快照）。
    /// </summary>
    public static string Key(SavedWindow window)
    {
        if (window == null) return "";
        string exe = Normalize(window.ExePath);
        if (exe.Length == 0) return "";
        return exe + "\n" + (window.Title ?? "");
    }

    /// <summary>场景编辑窗口里的一行。</summary>
    public static SavedWindowItem Describe(int index, SavedWindow window) => new()
    {
        Index = index,
        Title = window?.Title ?? "",
        ProcessName = window?.ProcessName ?? "",
        ExePath = window?.ExePath ?? "",
        Maximized = window != null && window.Maximized
    };

    /// <summary>
    /// 从快照里移除第 index 个窗口。越界或只剩一个时返回 false：
    /// 场景至少保留一个窗口，整个场景不要了请走「删除场景」。
    /// </summary>
    public static bool RemoveAt(List<SavedWindow> windows, int index, out SavedWindow removed)
    {
        removed = null;
        if (windows == null || windows.Count <= 1) return false;
        if (index < 0 || index >= windows.Count) return false;
        removed = windows[index];
        windows.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// 把 incoming 合并进场景：已存在的跳过，同一批里的重复也只算一次；
    /// 重新加入的项会从 suppressed 里删掉，否则下次关闭场景时又会被过滤掉。
    /// </summary>
    public static (int added, int skipped) Merge(
        List<SavedWindow> windows, List<string> suppressed, IReadOnlyList<SavedWindow> incoming)
    {
        if (windows == null) return (0, 0);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in windows)
        {
            string key = Key(window);
            if (key.Length > 0) keys.Add(key);
        }

        int added = 0;
        int skipped = 0;
        if (incoming == null) return (0, 0);

        foreach (var window in incoming)
        {
            if (window == null)
            {
                skipped++;
                continue;
            }

            string key = Key(window);
            if (key.Length == 0 || !keys.Add(key))
            {
                skipped++;
                continue;
            }

            windows.Add(window);
            suppressed?.Remove(key);
            added++;
        }

        return (added, skipped);
    }

    /// <summary>过滤掉用户从场景里移除过的窗口：关闭场景时不能再把它们写回快照。</summary>
    public static List<SavedWindow> Visible(
        IReadOnlyList<SavedWindow> captured, IReadOnlyCollection<string> suppressed)
    {
        var visible = new List<SavedWindow>();
        if (captured == null) return visible;
        if (suppressed == null || suppressed.Count == 0)
        {
            visible.AddRange(captured);
            return visible;
        }

        var blocked = new HashSet<string>(suppressed, StringComparer.Ordinal);
        foreach (var window in captured)
        {
            string key = Key(window);
            if (key.Length > 0 && blocked.Contains(key)) continue;
            visible.Add(window);
        }
        return visible;
    }

    private static string Normalize(string path)
        => string.IsNullOrWhiteSpace(path) ? "" : path.Replace('\\', '/').ToLowerInvariant();
}
