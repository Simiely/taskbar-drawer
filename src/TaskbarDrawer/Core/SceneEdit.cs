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
        => window == null ? "" : Key(window.ExePath, window.Title);

    /// <summary>
    /// 两层身份键（AppId + 窗口类名 + 位次）：新快照用它。
    /// 标题会变（浏览器切标签页就变），exe 又太粗（同程序多窗口分不开），
    /// 所以去重 / 移除抑制都改用这个；拿不到身份字段时退回 <see cref="Key(SavedWindow)"/>。
    /// </summary>
    public static string IdentityKey(SavedWindow window)
    {
        if (window == null) return "";
        string identity = WindowIdentity.WindowKey(window.AppId, window.ClassName, window.Ordinal);
        return identity.Length > 0 ? identity : Key(window);
    }

    /// <summary>去重键的字符串形式：exe 路径 + 标题。路径为空时返回空串。</summary>
    public static string Key(string exePath, string title)
    {
        string exe = Normalize(exePath);
        if (exe.Length == 0) return "";
        // 标题两端的空白不可见，但会让同一窗口的键对不上（控制台 / 终端标题常见）。
        return exe + "\n" + (title ?? "").Trim();
    }

    /// <summary>
    /// 同一场景再次点「详情」时，应当把当前详情窗收起；点另一个场景仍走切换 / 置前。
    /// 纯判断放这里，AppShell 只负责执行关闭，避免把「同一个场景」的比较散进 UI 接线。
    /// </summary>
    public static bool ShouldCloseEditor(string openSavedId, string clickedSavedId)
        => !string.IsNullOrEmpty(openSavedId) &&
           string.Equals(openSavedId, clickedSavedId, StringComparison.Ordinal);

    /// <summary>场景编辑窗口里的一行。</summary>
    /// <param name="isOpen">这一行此刻是否对应着运行中的窗口（详情窗据此标「已打开 / 未打开」）。</param>
    public static SavedWindowItem Describe(int index, SavedWindow window, bool isOpen = false) => new()
    {
        Index = index,
        Title = window?.Title ?? "",
        ProcessName = window?.ProcessName ?? "",
        ExePath = window?.ExePath ?? "",
        Maximized = window != null && window.Maximized,
        IsOpen = isOpen,
        AppId = window?.AppId ?? "",
        ClassName = window?.ClassName ?? ""
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

        // 两种键都登记：新快照有身份键，旧快照只有 exe + 标题键；
        // 只登记一种会让「旧快照 + 新追加」判不出重复。
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in windows)
        {
            AddKeys(keys, window);
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

            string legacy = Key(window);
            string identity = IdentityKey(window);
            if (legacy.Length == 0 || keys.Contains(legacy) || keys.Contains(identity))
            {
                skipped++;
                continue;
            }

            windows.Add(window);
            AddKeys(keys, window);
            // 同一个键可能被移除过多次（同程序同名窗口）：只放行一次，剩下的仍要挡住。
            // 旧快照两种键相同，只能清一次，否则一次追加会把两条抑制都吃掉。
            suppressed?.Remove(identity);
            if (!string.Equals(identity, legacy, StringComparison.Ordinal)) suppressed?.Remove(legacy);
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

        // 按出现次数抑制：同一个程序可能有两个同名窗口，移除其中一个时
        // 不能把另一个也一起过滤掉。
        var blocked = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string key in suppressed)
        {
            if (string.IsNullOrEmpty(key)) continue;
            blocked[key] = blocked.TryGetValue(key, out int count) ? count + 1 : 1;
        }

        foreach (var window in captured)
        {
            // 新键优先、旧键兜底：新快照移除时记的是身份键，旧快照记的是 exe + 标题键。
            // 两种键相同时只消费一次，否则一个抑制会被扣两遍。
            if (Consume(blocked, IdentityKey(window))) continue;
            if (Consume(blocked, Key(window))) continue;
            visible.Add(window);
        }
        return visible;
    }

    private static string Normalize(string path) => WindowIdentity.NormalizePath(path);

    /// <summary>登记一个窗口的两种键（身份键与 exe + 标题键），供去重使用。</summary>
    private static void AddKeys(HashSet<string> keys, SavedWindow window)
    {
        string legacy = Key(window);
        if (legacy.Length > 0) keys.Add(legacy);
        string identity = IdentityKey(window);
        if (identity.Length > 0) keys.Add(identity);
    }

    /// <summary>抑制计数扣一次；没有这条抑制返回 false。</summary>
    private static bool Consume(Dictionary<string, int> blocked, string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (!blocked.TryGetValue(key, out int count) || count <= 0) return false;
        blocked[key] = count - 1;
        return true;
    }
}
