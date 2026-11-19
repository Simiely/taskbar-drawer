using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>
/// 两层窗口身份（见 <c>docs/design/window-identity.md</c> 第四节）。
///
/// <code>
/// L1 应用身份 = aumid:… | exe:…            // 决定「启动谁」与「同程序归组」
/// L2 窗口身份 = (L1, 窗口类名, 簇内位次)     // 决定「是不是同一个窗口」
/// 标题：只用于显示 + 配对时的弱加权
/// </code>
///
/// 为什么标题不能当身份：它是窗口上最易变的属性——浏览器切一个标签页就变了，
/// 于是「同 exe + 同标题」的旧键认不回原窗口，场景重新打开时窗口互相换位。
///
/// 位次不按 <c>EnumWindows</c> 顺序（不稳定），按保存时的矩形排序：
/// 同 (L1, 类名) 的窗口按 (Top, Left) 升序排名。只要布局不变位次就不变，
/// 重开场景后按同样规则再算一遍就能对上——这是唯一能跨重启稳定复现的序。
///
/// 全是纯函数：类名 / 矩形 / 枚举结果由调用方读好传进来，所以能直接单测。
/// </summary>
public static class WindowIdentity
{
    public const string AumidPrefix = "aumid:";
    public const string ExePrefix = "exe:";

    /// <summary>
    /// L1：有 AUMID 用 AUMID，否则退回规范化后的 exe 全路径；两者都为空返回空串
    /// （空串表示「没有身份」，读取端会走旧的 exe + 标题 + 矩形路径）。
    /// </summary>
    public static string AppId(string aumid, string exePath)
    {
        if (!string.IsNullOrWhiteSpace(aumid)) return AumidPrefix + aumid.Trim();
        string path = NormalizePath(exePath);
        return path.Length == 0 ? "" : ExePrefix + path;
    }

    /// <summary>路径规范化：斜杠统一、小写。空路径返回空串（这类窗口既不进快照也不参与配对）。</summary>
    public static string NormalizePath(string path)
        => string.IsNullOrWhiteSpace(path) ? "" : path.Replace('\\', '/').ToLowerInvariant();

    /// <summary>L2 的字符串形式，用于去重 / 抑制集合。三段缺一段就返回空串。</summary>
    public static string WindowKey(string appId, string className, int ordinal)
    {
        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(className)) return "";
        return appId + "\n" + className + "\n" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 宽松身份键：应用 + 窗口类名 + 标题。
    /// 用于「这个窗口在不在快照里」这类只需要近似的判断（追加列表的「已在场景」）——
    /// 那里两边不是同一批窗口，位次作为相对量不可比，刻意不用它。
    /// </summary>
    public static string LooseKey(string appId, string className, string title)
    {
        if (string.IsNullOrEmpty(appId)) return "";
        return appId + "\n" + (className ?? "") + "\n" + (title ?? "").Trim();
    }

    /// <summary>
    /// 运行时成员 → 配对候选。详情窗的「已打开」判定与移除时的 Detach 必须用同一套身份，
    /// 否则一个说"是它"、另一个说"找不到"，用户就会看到「移除的窗口下次又打开」。
    /// </summary>
    public static DisplayRules.RestoreCandidate Candidate(StashedWindow item)
    {
        var rect = item.Placement.rcNormalPosition;
        if (rect.IsEmpty) NativeMethods.GetWindowRect(item.Handle, out rect);

        return new DisplayRules.RestoreCandidate
        {
            Handle = item.Handle.ToInt64(),
            Title = item.Title,
            MonitorDevice = item.MonitorDevice,
            Rect = rect,
            AppId = AppId(null, item.ProcessPath),
            ClassName = NativeMethods.ClassName(item.Handle)
        };
    }

    /// <summary>
    /// 分配簇内位次：同 (AppId, 类名) 内按 (显示器, Top, Left, 标题, 句柄) 升序排名，从 0 开始。
    ///
    /// 显示器必须排在坐标前面：保存侧用的是 <c>rcNormalPosition</c>（工作区坐标），
    /// 还原侧用的是 <c>GetWindowRect</c>（屏幕坐标），跨屏直接比 Top / Left 会把
    /// 同一个应用在两块屏上的窗口排出相反的次序。按设备名先分组，坐标就只在同一块屏内比较。
    /// 标题只用来打破「两个窗口叠在同一位置」的平局（新开的窗口常见），不参与主排序。
    /// </summary>
    /// <returns>句柄 → 位次。</returns>
    public static Dictionary<long, int> AssignOrdinals(IReadOnlyList<DisplayRules.RestoreCandidate> candidates)
    {
        var ordinals = new Dictionary<long, int>();
        if (candidates == null || candidates.Count == 0) return ordinals;

        var groups = new Dictionary<string, List<DisplayRules.RestoreCandidate>>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (candidate == null) continue;
            string key = (candidate.AppId ?? "") + "\n" + (candidate.ClassName ?? "");
            if (!groups.TryGetValue(key, out var bucket))
            {
                bucket = new List<DisplayRules.RestoreCandidate>();
                groups[key] = bucket;
            }
            bucket.Add(candidate);
        }

        foreach (var bucket in groups.Values)
        {
            int ordinal = 0;
            foreach (var candidate in bucket
                         .OrderBy(c => c.MonitorDevice ?? "", StringComparer.OrdinalIgnoreCase)
                         .ThenBy(c => c.Rect.Top)
                         .ThenBy(c => c.Rect.Left)
                         .ThenBy(c => c.Title ?? "", StringComparer.OrdinalIgnoreCase)
                         .ThenBy(c => c.Handle))
            {
                ordinals[candidate.Handle] = ordinal++;
            }
        }
        return ordinals;
    }

    /// <summary>
    /// 保存侧：按 (AppId, 类名) 分组、按 (Top, Left, 标题) 排序，把位次写回快照项。
    /// 位次是相对量，必须等整组抓完才能分配——所以这里是「写回」而不是「返回」。
    /// </summary>
    public static void AssignOrdinals(IReadOnlyList<SavedWindow> windows)
    {
        if (windows == null) return;

        var groups = new Dictionary<string, List<SavedWindow>>(StringComparer.Ordinal);
        foreach (var window in windows)
        {
            if (window == null) continue;
            string key = (window.AppId ?? "") + "\n" + (window.ClassName ?? "");
            if (!groups.TryGetValue(key, out var bucket))
            {
                bucket = new List<SavedWindow>();
                groups[key] = bucket;
            }
            bucket.Add(window);
        }

        foreach (var bucket in groups.Values)
        {
            int ordinal = 0;
            foreach (var window in bucket
                         .OrderBy(w => w.MonitorDevice ?? "", StringComparer.OrdinalIgnoreCase)
                         .ThenBy(w => w.Top)
                         .ThenBy(w => w.Left)
                         .ThenBy(w => w.Title ?? "", StringComparer.OrdinalIgnoreCase))
            {
                window.Ordinal = ordinal++;
            }
        }
    }

    /// <summary>
    /// 从同应用的候选窗口里挑出「最像保存项」的那个，返回下标（-1 = 没有候选）。
    ///
    /// 优先级（替换原来的「标题优先」）：
    /// 1) 同 L1 + 同类名 + 同位次——最稳，浏览器多窗口靠这一级；
    /// 2) 同 L1 + 同标题（同屏优先，跨屏也认：程序重启后系统常把它开在主屏）；
    /// 3) 同屏矩形最近；4) 任意屏矩形最近。
    ///
    /// 旧快照没有 <see cref="SavedWindow.AppId"/> / <see cref="SavedWindow.ClassName"/>，
    /// 会自动跳过第 1 级、退回 2–4 级，也就是改造前的行为——旧数据不迁移，不猜。
    /// </summary>
    public static int Pair(
        IReadOnlyList<DisplayRules.RestoreCandidate> candidates, SavedWindow saved,
        IReadOnlyDictionary<long, int> ordinals = null)
    {
        if (candidates == null || candidates.Count == 0 || saved == null) return -1;

        string device = DisplayRules.MonitorOf(saved);
        var savedRect = NativeMethods.RECT.FromLTRB(
            saved.Left, saved.Top, saved.Left + saved.Width, saved.Top + saved.Height);

        int byOrdinal = PickByOrdinal(candidates, saved, ordinals, device, savedRect);
        if (byOrdinal >= 0) return byOrdinal;

        int byTitle = PickByTitle(candidates, saved, device);
        if (byTitle >= 0) return byTitle;

        return PickByDistance(candidates, saved, device, savedRect);
    }

    /// <summary>第 1 级：同应用 + 同类名 + 同位次。命中的如果有多个（位次算漏了），按同屏 / 最近收敛。</summary>
    private static int PickByOrdinal(
        IReadOnlyList<DisplayRules.RestoreCandidate> candidates, SavedWindow saved,
        IReadOnlyDictionary<long, int> ordinals, string device, NativeMethods.RECT savedRect)
    {
        if (ordinals == null) return -1;
        if (string.IsNullOrEmpty(saved.AppId) || string.IsNullOrEmpty(saved.ClassName)) return -1;

        int best = -1;
        bool bestOnScreen = false;
        long bestDistance = long.MaxValue;

        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (!SameApp(candidate, saved)) continue;
            if (!string.Equals(candidate.ClassName, saved.ClassName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!ordinals.TryGetValue(candidate.Handle, out int ordinal) || ordinal != saved.Ordinal) continue;

            bool onScreen = DisplayRules.BelongsTo(device, candidate.MonitorDevice);
            long distance = DisplayRules.RectDistance(candidate.Rect, savedRect);
            if (best < 0 || (onScreen && !bestOnScreen) || (onScreen == bestOnScreen && distance < bestDistance))
            {
                best = i;
                bestOnScreen = onScreen;
                bestDistance = distance;
            }
        }

        // 位次是「同 (L1, 类名) 窗口」的相对顺序，跨屏时同一组里的 0 号可能落在另一块屏
        // （AssignOrdinals 先按显示器名排序）。保存项明明有同屏的同程序窗口时，不能因为
        // 另一块屏的窗口位次对上就把它挪过来——交给标题 / 最近矩形两级，它们优先同屏。
        if (best >= 0 && !bestOnScreen && HasSameAppOnScreen(candidates, saved, device)) return -1;
        return best;
    }

    /// <summary>保存屏上有没有同应用、同类名的候选窗口（位次跳级前用它判断要不要放弃跨屏命中）。</summary>
    private static bool HasSameAppOnScreen(
        IReadOnlyList<DisplayRules.RestoreCandidate> candidates, SavedWindow saved, string device)
    {
        if (string.IsNullOrEmpty(device)) return false;

        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (!SameApp(candidate, saved)) continue;
            if (!string.IsNullOrEmpty(saved.ClassName) && !string.IsNullOrEmpty(candidate.ClassName) &&
                !string.Equals(candidate.ClassName, saved.ClassName, StringComparison.OrdinalIgnoreCase)) continue;
            if (DisplayRules.BelongsTo(device, candidate.MonitorDevice)) return true;
        }
        return false;
    }

    /// <summary>第 2 级：同应用 + 同标题。先找同屏的，再放宽到任意屏。</summary>
    private static int PickByTitle(
        IReadOnlyList<DisplayRules.RestoreCandidate> candidates, SavedWindow saved, string device)
    {
        string title = saved.Title?.Trim();
        if (string.IsNullOrEmpty(title)) return -1;

        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (!SameApp(candidate, saved)) continue;
                if (!string.Equals(candidate.Title?.Trim(), title, StringComparison.OrdinalIgnoreCase)) continue;
                if (pass == 0 && !DisplayRules.BelongsTo(device, candidate.MonitorDevice)) continue;
                return i;
            }
        }
        return -1;
    }

    /// <summary>第 3 / 4 级：同屏最近，再放宽到任意屏最近。</summary>
    private static int PickByDistance(
        IReadOnlyList<DisplayRules.RestoreCandidate> candidates, SavedWindow saved, string device,
        NativeMethods.RECT savedRect)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            int best = -1;
            long bestDistance = long.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (!SameApp(candidate, saved)) continue;
                if (pass == 0 && !DisplayRules.BelongsTo(device, candidate.MonitorDevice)) continue;
                long distance = DisplayRules.RectDistance(candidate.Rect, savedRect);
                if (distance >= bestDistance) continue;
                best = i;
                bestDistance = distance;
            }
            if (best >= 0) return best;
        }
        return -1;
    }

    /// <summary>旧快照没有 AppId：视作「同应用」，退回改造前的行为。</summary>
    private static bool SameApp(DisplayRules.RestoreCandidate candidate, SavedWindow saved)
        => string.IsNullOrEmpty(saved.AppId) ||
           string.Equals(candidate.AppId, saved.AppId, StringComparison.Ordinal);
}
