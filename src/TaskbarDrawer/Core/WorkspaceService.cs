using System.Diagnostics;
using System.IO;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>
/// 场景：把一组窗口的「软件 + 位置」存成快照，之后一键关闭 / 一键还原。
/// 快照与运行时编组共用同一枚胶囊：存储编组只是给组起名并留底，平时仍按编组收起 / 展开；
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

    private readonly List<SavedGroup> _groups = SavedGroupStore.Load();

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
            Name = string.IsNullOrWhiteSpace(name) ? $"场景 {_groups.Count + 1}" : name.Trim(),
            CreatedAt = DateTime.Now,
            Windows = windows
        };
        _groups.Add(group);
        SavedGroupStore.Save(_groups);
        return group;
    }

    /// <summary>关闭前按当前实际位置再存一次，下次还原才是用户最后摆放的样子。</summary>
    public void UpdateSnapshot(SavedGroup group, StashAllService stash, int groupId)
    {
        var windows = CaptureWindows(stash, groupId);
        if (windows.Count == 0) return;
        group.Windows = windows;
        SavedGroupStore.Save(_groups);
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
    /// 之后这一组就按普通组收起 / 展开，不需要反复关掉再开。
    /// </summary>
    public (int moved, int launched) Restore(SavedGroup group, Action<IntPtr> onWindowReady)
    {
        var known = new HashSet<long>(TopLevelWindows().Select(w => w.Handle.ToInt64()));
        var used = new HashSet<long>();
        var windows = TopLevelWindows();
        var pending = new List<SavedWindow>();
        int moved = 0;

        foreach (var saved in group.Windows)
        {
            string exe = Normalize(saved.ExePath);
            if (exe == null) continue;

            var match = windows.FirstOrDefault(w =>
                w.Visible && w.Title.Length > 0 && !w.ToolWindow &&
                !used.Contains(w.Handle.ToInt64()) && Normalize(w.Path) == exe);
            if (match != null)
            {
                ApplyPlacement(match.Handle, saved);
                used.Add(match.Handle.ToInt64());
                onWindowReady?.Invoke(match.Handle);
                moved++;
            }
            else
            {
                pending.Add(saved);
            }
        }

        int launched = pending.Count(Launch);
        if (launched > 0) PositionLater(pending, known, onWindowReady);
        return (moved, launched);
    }

    public bool Rename(string id, string name)
    {
        var group = Find(id);
        if (group == null || string.IsNullOrWhiteSpace(name)) return false;
        group.Name = name.Trim();
        SavedGroupStore.Save(_groups);
        return true;
    }

    public bool Delete(string id)
    {
        var group = Find(id);
        if (group == null) return false;
        _groups.Remove(group);
        SavedGroupStore.Save(_groups);
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
                Maximized = item.Maximized
            });
        }
        return windows;
    }

    /// <summary>启动后等主窗口出现再摆位：Process.Start 返回时窗口往往还没建好。</summary>
    private static void PositionLater(List<SavedWindow> pending, HashSet<long> known, Action<IntPtr> onWindowReady)
    {
        Task.Run(() =>
        {
            var remaining = new List<SavedWindow>(pending);
            var deadline = DateTime.UtcNow.AddSeconds(12);

            while (remaining.Count > 0 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(450);
                foreach (var saved in remaining.ToArray())
                {
                    string exe = Normalize(saved.ExePath);
                    if (exe == null)
                    {
                        remaining.Remove(saved);
                        continue;
                    }

                    var match = TopLevelWindows().FirstOrDefault(w =>
                        w.Visible && w.Title.Length > 0 && !w.ToolWindow &&
                        !known.Contains(w.Handle.ToInt64()) && Normalize(w.Path) == exe);
                    if (match == null) continue;

                    ApplyPlacement(match.Handle, saved);
                    known.Add(match.Handle.ToInt64());
                    onWindowReady?.Invoke(match.Handle);
                    remaining.Remove(saved);
                }
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
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, saved.Left, saved.Top, saved.Width, saved.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
        if (saved.Maximized) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWMAXIMIZED);
    }

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
