using System.Diagnostics;
using System.IO;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>
/// 场景：把一组窗口的「软件 + 位置」存成快照，运行中可收起 / 展开，关闭后可一键还原。
/// 运行时组只是场景的运行态；每次收纳都会立刻保存成场景，所以两者始终一一对应。
/// 只有「关闭窗口」（或用户自己点 ×）才真的关窗口，关完后胶囊还在，再点一次全部拉起来。
/// </summary>
public sealed class WorkspaceService
{
    private sealed class TopWindow
    {
        public IntPtr Handle;
        public string Path;
        public string Title;
        public bool Visible;
        public bool ToolWindow;
    }

    private readonly List<SavedGroup> _groups;

    /// <summary>启动时加载场景文件的问题（损坏 / 无权限）；正常时为 null。</summary>
    public string LoadWarning { get; }

    public WorkspaceService()
    {
        _groups = SavedGroupStore.Load(out string warning);
        LoadWarning = warning;
    }

    /// <summary>落盘失败：场景仍在内存里可用，但重启后会丢，必须让用户知道。</summary>
    public event Action<string> SaveFailed;

    private void Persist()
    {
        if (SavedGroupStore.Save(_groups)) return;
        SaveFailed?.Invoke("场景保存失败（磁盘满 / 无权限），本次运行改动的场景重启后会丢失");
    }

    public IReadOnlyList<SavedGroupSummary> Summaries()
        => _groups
            .OrderBy(g => g.CreatedAt)
            .Select(g => new SavedGroupSummary
            {
                Id = g.Id,
                Name = g.Name,
                Count = g.Windows.Count,
                Titles = g.Windows.Select(w => w.Title).ToArray()
            })
            .ToArray();

    public SavedGroup Find(string id) => _groups.FirstOrDefault(g => g.Id == id);

    /// <summary>场景详情：编辑器列窗口用。找不到返回 null。</summary>
    public SavedGroupDetail Detail(string savedId)
    {
        var group = Find(savedId);
        if (group == null) return null;
        return new SavedGroupDetail
        {
            Id = group.Id,
            Name = group.Name,
            Windows = group.Windows
                .Select((window, index) => SceneEdit.Describe(index, window))
                .ToArray()
        };
    }

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

        string key = SceneEdit.Key(removed);
        if (key.Length > 0 && !group.Suppressed.Contains(key)) group.Suppressed.Add(key);
        Persist();

        return new SceneEditResult
        {
            Ok = true,
            Title = string.IsNullOrWhiteSpace(removed.Title)
                ? string.IsNullOrWhiteSpace(removed.ProcessName) ? "窗口" : removed.ProcessName
                : removed.Title,
            Count = group.Windows.Count
        };
    }

    /// <summary>把窗口追加进场景快照：已存在的跳过；只改快照，下次打开场景生效。</summary>
    public SceneAppendResult AppendWindows(string savedId, IReadOnlyList<SavedWindow> incoming)
    {
        var group = Find(savedId);
        if (group == null) return new SceneAppendResult { Error = "场景已经不存在" };

        var (added, skipped) = SceneEdit.Merge(group.Windows, group.Suppressed, incoming);
        if (added == 0)
        {
            return new SceneAppendResult
            {
                Skipped = skipped,
                Error = skipped > 0
                    ? $"这 {skipped} 个窗口已经在场景里了"
                    : "没有可追加的窗口"
            };
        }

        Persist();
        return new SceneAppendResult { Ok = true, Added = added, Skipped = skipped };
    }

    /// <summary>把一个正在运行的其他场景追加进场景：只支持正在运行、且不是场景自己这一组。</summary>
    public SceneAppendResult AppendRunningScene(string savedId, StashAllService stash, int groupId)
    {
        var group = Find(savedId);
        if (group == null) return new SceneAppendResult { Error = "场景已经不存在" };
        if (stash == null || groupId <= 0) return new SceneAppendResult { Error = "这个场景已经不存在了" };
        if (stash.SavedIdOf(groupId) == savedId)
            return new SceneAppendResult { Error = "不能把场景自己这一组追加给自己" };

        var windows = CaptureWindows(stash, groupId);
        if (windows.Count == 0) return new SceneAppendResult { Error = "这个场景已经没有窗口" };
        return AppendWindows(savedId, windows);
    }

    /// <summary>
    /// 把运行时的一组窗口存成场景。位置按此刻的窗口刷新，
    /// 展开着的组也能拿到当前矩形，而不是上一次收起时的旧位置。
    /// </summary>
    public SavedGroup SaveFrom(StashAllService stash, int groupId, string name)
    {
        var windows = CaptureWindows(stash, groupId);
        if (windows.Count == 0) return null;

        var group = new SavedGroup
        {
            Id = Guid.NewGuid().ToString("N")[..6],
            Name = string.IsNullOrWhiteSpace(name) ? NextSceneName() : name.Trim(),
            CreatedAt = DateTime.Now,
            Windows = windows
        };
        _groups.Add(group);
        Persist();
        return group;
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

    /// <summary>关闭前按当前实际位置再存一次，下次还原才是用户最后摆放的样子。</summary>
    public void UpdateSnapshot(SavedGroup group, StashAllService stash, int groupId)
    {
        if (group == null) return;
        var captured = CaptureWindows(stash, groupId);
        if (captured.Count == 0) return;

        // 用户在场景编辑里移除过的窗口不能因为「关闭场景时整份覆盖」又写回来；
        // 全被移除时不覆盖，保留上一份快照而不是把场景清空。
        var visible = SceneEdit.Visible(captured, group.Suppressed);
        if (visible.Count == 0) return;

        group.Windows = visible;
        Persist();
    }

    /// <summary>
    /// 关掉这一组自己的窗口（WM_CLOSE，等价逐个点 ×）。
    /// 只认运行时组里记录的句柄，不按 exe 路径扫全系统——否则同一个程序里
    /// 不属于这一组的窗口（例如正在下载的浏览器）也会被一起关掉，
    /// 那些程序就会各自弹「这将取消所有传输」之类的确认框。
    /// </summary>
    public int Close(StashAllService stash, int groupId)
    {
        int closed = 0;
        var handles = stash.Items
            .Where(i => i.GroupId == groupId)
            .Select(i => i.Handle)
            .Distinct()
            .ToArray();

        foreach (IntPtr hwnd in handles)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) continue;
            if (NativeMethods.PostMessageW(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) closed++;
        }
        return closed;
    }

    /// <summary>
    /// 还原这一组：已经在跑的窗口直接挪回保存位置，没跑的程序启动后等它出窗口再摆位。
    /// 每摆好一个窗口回调一次（启动的窗口在后台线程回调），由上层接入运行时组，
    /// 之后这一组就按普通场景收起 / 展开，不需要反复关掉再开。
    /// excluded 是已经被其它场景管理的窗口句柄：它们不属于这次还原，绝不能挪走。
    /// </summary>
    public (int moved, int launched, int busy, int failed) Restore(
        SavedGroup group,
        IReadOnlySet<long> excluded,
        Func<IntPtr, bool> adopt,
        Action<IntPtr> adoptLater,
        Action onComplete = null)
    {
        // 只枚举一次：matches 与 known 用同一份快照，避免两次枚举之间窗口状态变化导致重复匹配。
        var windows = TopLevelWindows();
        var known = new HashSet<long>(windows.Select(w => w.Handle.ToInt64()));
        var used = new HashSet<long>();
        var pending = new List<SavedWindow>();
        int moved = 0;
        int busy = 0;
        int failed = 0;

        foreach (var saved in group.Windows)
        {
            string exe = Normalize(saved.ExePath);
            if (exe == null) continue;

            // 快照里记了屏幕：只接管同一块屏上的窗口，另一块屏的同程序窗口不动。
            // 那块屏已经拔掉时不再按屏限制，否则用户会看到「程序都没找到」。
            string expectedDevice = DisplayRules.MonitorOf(saved);
            bool deviceExists = expectedDevice == null ||
                                NativeMethods.TryGetMonitorOfDevice(expectedDevice, out _);

            bool SameExe(TopWindow w) =>
                w.Visible && w.Title.Length > 0 && !w.ToolWindow && Normalize(w.Path) == exe;

            bool OnTargetScreen(TopWindow w) =>
                !deviceExists || NativeMethods.MonitorDeviceOf(w.Handle) == expectedDevice;

            var match = windows.FirstOrDefault(w =>
                SameExe(w) && OnTargetScreen(w) &&
                !used.Contains(w.Handle.ToInt64()) &&
                (excluded == null || !excluded.Contains(w.Handle.ToInt64())));
            if (match != null)
            {
                ApplyPlacement(match.Handle, saved);
                used.Add(match.Handle.ToInt64());
                if (adopt == null || adopt(match.Handle)) moved++;
                else failed++;
                continue;
            }

            // 同类窗口存在，但已经在别的场景里，或只在另一块屏幕上：
            // 既不能挪它（会破坏别人的窗口位置），也不能再启动一个副本。
            if (windows.Any(w => SameExe(w) &&
                    ((excluded != null && excluded.Contains(w.Handle.ToInt64())) || !OnTargetScreen(w))))
            {
                busy++;
                continue;
            }

            pending.Add(saved);
        }

        // 只有真正启动成功的才值得等窗口出现；启动失败的项目等 12 s 也不会出现。
        var launchedPending = pending.Where(Launch).ToList();
        if (launchedPending.Count > 0)
            PositionLater(launchedPending, known, adoptLater, onComplete);
        else
            onComplete?.Invoke();

        return (moved, launchedPending.Count, busy, failed);
    }

    public bool Rename(string id, string name)
    {
        var group = Find(id);
        if (group == null || string.IsNullOrWhiteSpace(name)) return false;
        group.Name = name.Trim();
        Persist();
        return true;
    }

    public bool Delete(string id)
    {
        var group = Find(id);
        if (group == null) return false;
        _groups.Remove(group);
        Persist();
        return true;
    }

    private static List<SavedWindow> CaptureWindows(StashAllService stash, int groupId)
    {
        var windows = new List<SavedWindow>();
        foreach (var item in stash.Items.Where(i => i.GroupId == groupId).ToArray())
        {
            // 读不到 exe 路径的窗口（受保护进程）无法再启动，存进去只会制造点不开的条目。
            if (string.IsNullOrWhiteSpace(item.ProcessPath)) continue;

            stash.RefreshPlacement(item);
            var rect = item.Placement.rcNormalPosition;
            if (rect.IsEmpty) NativeMethods.GetWindowRect(item.Handle, out rect);

            windows.Add(new SavedWindow
            {
                ExePath = item.ProcessPath,
                ProcessName = item.ProcessName,
                Title = item.Title,
                Left = rect.Left,
                Top = rect.Top,
                Width = rect.Width,
                Height = rect.Height,
                MonitorDevice = item.MonitorDevice,
                Maximized = item.Maximized
            });
        }
        return windows;
    }

    /// <summary>启动后等主窗口出现再摆位：Process.Start 返回时窗口往往还没建好。</summary>
    private static void PositionLater(
        List<SavedWindow> pending, HashSet<long> known, Action<IntPtr> onWindowReady, Action onComplete)
    {
        Task.Run(() =>
        {
            try
            {
                var remaining = new List<SavedWindow>(pending);
                var deadline = DateTime.UtcNow.AddSeconds(12);

                while (remaining.Count > 0 && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(450);
                    // 每个 tick 只枚举一次：早先是每条等待记录各枚举一遍，等待项多时是 N 倍开销。
                    var windows = TopLevelWindows();
                    foreach (var saved in remaining.ToArray())
                    {
                        string exe = Normalize(saved.ExePath);
                        if (exe == null)
                        {
                            remaining.Remove(saved);
                            continue;
                        }

                        // 刚启动的窗口一律接管：系统通常把它开在主屏，不能按保存的屏幕过滤，
                        // 否则等待项永远匹配不上（摆位后窗口会被移到保存的那块屏）。
                        var match = windows.FirstOrDefault(w =>
                            w.Visible && w.Title.Length > 0 && !w.ToolWindow &&
                            !known.Contains(w.Handle.ToInt64()) && Normalize(w.Path) == exe);
                        if (match == null) continue;

                        ApplyPlacement(match.Handle, saved);
                        known.Add(match.Handle.ToInt64());
                        onWindowReady?.Invoke(match.Handle);
                        remaining.Remove(saved);
                    }
                }
            }
            finally
            {
                onComplete?.Invoke();
            }
        });
    }

    private static bool Launch(SavedWindow saved)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(saved.ExePath) || !File.Exists(saved.ExePath)) return false;
            Process.Start(new ProcessStartInfo
            {
                FileName = saved.ExePath,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Write("saved launch", ex);
            return false;
        }
    }

    private static void ApplyPlacement(IntPtr hwnd, SavedWindow saved)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        if (saved.Width > 0 && saved.Height > 0)
        {
            // 目标屏可能已经拔掉 / 分辨率变了：先夹回最近的工作区，否则窗口会落在屏幕外。
            var rect = DisplayRules.EnsureVisible(NativeMethods.RECT.FromLTRB(
                saved.Left, saved.Top, saved.Left + saved.Width, saved.Top + saved.Height));
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
        if (saved.Maximized) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWMAXIMIZED);
    }

    /// <summary>已关闭场景属于哪块屏幕：按快照里记录的显示器判断（旧快照退回矩形推断）。</summary>
    public string MonitorOfSaved(SavedGroup group) => group == null ? null : DisplayRules.MonitorOf(group.Windows);

    /// <summary>全部顶层窗口（含隐藏的，关闭组里收起来的窗口要用到）。</summary>
    private static List<TopWindow> TopLevelWindows()
    {
        var list = new List<TopWindow>();
        var paths = new Dictionary<uint, string>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (WindowEnumerator.IsOwnProcessWindow(hwnd)) return true;
            if (WindowEnumerator.IsShellWindow(hwnd)) return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!paths.TryGetValue(pid, out string path))
            {
                path = ProcessPathOf(pid);
                paths[pid] = path;
            }

            list.Add(new TopWindow
            {
                Handle = hwnd,
                Path = path,
                Title = NativeMethods.Title(hwnd),
                Visible = NativeMethods.IsWindowVisible(hwnd),
                ToolWindow = (NativeMethods.ExStyle(hwnd) & NativeMethods.WS_EX_TOOLWINDOW) != 0
            });
            return true;
        }, IntPtr.Zero);

        return list;
    }

    private static string ProcessPathOf(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string Normalize(string path)
        => string.IsNullOrWhiteSpace(path) ? null : path.Replace('\\', '/').ToLowerInvariant();
}

/// <summary>从场景移除一个窗口的结果；错误文案可直接展示给用户。</summary>
public sealed class SceneEditResult
{
    public bool Ok { get; init; }

    /// <summary>被移除窗口的显示名（标题为空时退回进程名）。</summary>
    public string Title { get; init; }

    /// <summary>移除后剩下的窗口数。</summary>
    public int Count { get; init; }

    public string Error { get; init; }
}

/// <summary>往场景追加窗口 / 其它场景的运行结果。</summary>
public sealed class SceneAppendResult
{
    public bool Ok { get; init; }
    public int Added { get; init; }
    public int Skipped { get; init; }
    public string Error { get; init; }
}
