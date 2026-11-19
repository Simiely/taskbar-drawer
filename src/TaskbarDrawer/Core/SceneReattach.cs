using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>
/// 「场景成员的程序被关掉又重新打开」时的自动认领规则。
///
/// 运行时组只记录句柄：用户关掉某个程序，句柄被 <see cref="WindowStash.Prune"/> 移出，
/// 快照里那一项却还在。程序重新打开后是一个新句柄，不在运行时组里，于是：
/// 场景展开着时它不跟着收起 / 展开，胶囊上的计数也对不上。
/// 这组纯函数负责回答两个问题：
///   1. 快照里哪些项现在没有对应的运行窗口（<see cref="Missing"/>）；
///   2. 新出现的这个窗口能不能认成其中某一项（<see cref="Match"/>）。
///
/// 不读窗口、不落盘：快照、运行时成员、新窗口事实都由调用方读好传进来，所以能直接单测。
/// </summary>
public static class SceneReattach
{
    /// <summary>一个刚出现在桌面上的窗口（已通过准入过滤）。</summary>
    public sealed class Candidate
    {
        public IntPtr Handle { get; init; }
        public string Title { get; init; } = "";
        public string ClassName { get; init; } = "";
        public string ProcessPath { get; init; } = "";
        public NativeMethods.RECT Rect { get; init; }
    }

    /// <summary>
    /// 快照里没有对应运行成员的项。按「同标题 → 同程序最近位置」做一对一配对，
    /// 不会把两个快照项配到同一个窗口上（这是判断「缺了哪一个」的关键）。
    ///
    /// 刻意不用位次：运行时组少了一个成员后，剩下窗口的位次会整体前移，
    /// 用位次配对会把「还开着的那个」认成被关掉的那一项。
    /// </summary>
    public static List<SavedWindow> Missing(
        IReadOnlyList<SavedWindow> saved, IReadOnlyList<DisplayRules.RestoreCandidate> live)
    {
        var missing = new List<SavedWindow>();
        if (saved == null || saved.Count == 0) return missing;

        var remaining = live == null
            ? new List<DisplayRules.RestoreCandidate>()
            : live.ToList();

        // 第一遍只认同标题：标题没变是最强信号。
        // 不能边标题边距离地做——否则排在前面的「甲」会先把位置最近的「乙」抢走，
        // 真正开着的「乙」反而被算成缺失。
        var unpaired = new List<SavedWindow>();
        foreach (var item in saved)
        {
            int index = FindLiveByTitle(remaining, item);
            if (index < 0) unpaired.Add(item);
            else remaining.RemoveAt(index);
        }

        // 第二遍：标题变过的按保存位置找最近的那个。
        foreach (var item in unpaired)
        {
            int index = FindLiveByDistance(remaining, item);
            if (index < 0) missing.Add(item);
            else remaining.RemoveAt(index);
        }
        return missing;
    }

    /// <summary>
    /// 这个新窗口能认成 <paramref name="missing"/> 里的哪一项（-1 = 不认，保持原状）。
    ///
    /// 保守规则：先按 exe（+ 类名）收窄，再要求标题一致；只有「这个程序只剩一个缺失项」时
    /// 才允许标题不同（浏览器 / 资源管理器多窗口时标题不一致就宁可不认，避免互相抢窗口）。
    /// </summary>
    public static int Match(IReadOnlyList<SavedWindow> missing, Candidate window)
    {
        if (missing == null || missing.Count == 0 || window == null) return -1;

        string candidateApp = WindowIdentity.AppId(null, window.ProcessPath);
        if (candidateApp.Length == 0) return -1;

        var sameApp = new List<int>();
        for (int i = 0; i < missing.Count; i++)
        {
            var saved = missing[i];
            if (saved == null) continue;
            if (!SameApp(saved, candidateApp)) continue;
            if (!SameClass(saved, window)) continue;
            sameApp.Add(i);
        }
        if (sameApp.Count == 0) return -1;

        // 同标题优先；多个同标题（同程序同名窗口）时取位置最近的那个。
        string title = (window.Title ?? "").Trim();
        int best = -1;
        long bestDistance = long.MaxValue;
        foreach (int i in sameApp)
        {
            if (!string.Equals((missing[i].Title ?? "").Trim(), title, StringComparison.OrdinalIgnoreCase))
                continue;
            long distance = Distance(missing[i], window.Rect);
            if (distance >= bestDistance) continue;
            best = i;
            bestDistance = distance;
        }
        if (best >= 0) return best;

        // 标题变了：只有唯一一个同程序缺失项时才敢认。
        return sameApp.Count == 1 ? sameApp[0] : -1;
    }

    private static int FindLiveByTitle(List<DisplayRules.RestoreCandidate> live, SavedWindow saved)
    {
        string title = (saved.Title ?? "").Trim();
        if (title.Length == 0) return -1;

        for (int i = 0; i < live.Count; i++)
        {
            if (!SameApp(saved, live[i].AppId)) continue;
            if (!SameClass(saved, live[i].ClassName)) continue;
            if (string.Equals((live[i].Title ?? "").Trim(), title, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static int FindLiveByDistance(List<DisplayRules.RestoreCandidate> live, SavedWindow saved)
    {
        var savedRect = NativeMethods.RECT.FromLTRB(
            saved.Left, saved.Top, saved.Left + saved.Width, saved.Top + saved.Height);

        int best = -1;
        long bestDistance = long.MaxValue;
        for (int i = 0; i < live.Count; i++)
        {
            if (!SameApp(saved, live[i].AppId)) continue;
            if (!SameClass(saved, live[i].ClassName)) continue;

            long distance = DisplayRules.RectDistance(live[i].Rect, savedRect);
            if (distance >= bestDistance) continue;
            best = i;
            bestDistance = distance;
        }
        return best;
    }

    /// <summary>
    /// 同应用判定：新快照有 <see cref="SavedWindow.AppId"/> 就按它比（大小写 / 斜杠已在
    /// 生成 AppId 时归一）；旧快照没有身份字段，退回规范化后的 exe 路径。
    /// </summary>
    private static bool SameApp(SavedWindow saved, string candidateApp)
    {
        string expected = string.IsNullOrEmpty(saved.AppId)
            ? WindowIdentity.AppId(null, saved.ExePath)
            : saved.AppId;
        return expected.Length > 0 &&
               string.Equals(expected, candidateApp, StringComparison.Ordinal);
    }

    private static bool SameClass(SavedWindow saved, Candidate window)
        => SameClass(saved.ClassName, window.ClassName);

    private static bool SameClass(SavedWindow saved, string className)
        => SameClass(saved.ClassName, className);

    private static bool SameClass(string savedClass, string candidateClass)
        => string.IsNullOrEmpty(savedClass) || string.IsNullOrEmpty(candidateClass) ||
           string.Equals(savedClass, candidateClass, StringComparison.OrdinalIgnoreCase);

    private static long Distance(SavedWindow saved, NativeMethods.RECT rect)
        => DisplayRules.RectDistance(
            NativeMethods.RECT.FromLTRB(saved.Left, saved.Top, saved.Left + saved.Width, saved.Top + saved.Height),
            rect);
}
