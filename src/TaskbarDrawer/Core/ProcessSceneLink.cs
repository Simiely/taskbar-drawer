using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>
/// 前台窗口是场景成员的「同应用辅助窗口」时，仍按该场景处理。
///
/// 微信的图片查看器是 Weixin.exe 自己的 Qt 顶层窗口，小程序宿主 WeChatAppEx.exe 是
/// Weixin.exe 拉起的子进程。它们都不是场景成员（没有 owner、也不是工具窗），
/// 单窗口模式会把它们当成「切到了另一个应用」，于是把整个场景收回胶囊——
/// 用户看到的就是「打开微信图片 / 小程序，其它窗口全没了」。
///
/// 这里只回答「这个窗口的进程属于哪个场景」：同进程 → 父进程 → 祖父进程（最多两层），
/// 父链对不上时再看可执行文件是否相同（飞书的媒体中心 / 图片视频查看器是一组兄弟进程，
/// 父进程是主进程、并不在场景里，只靠父链认不出来）。
/// 不做成员接入——辅助窗口不该进快照、不该占用场景计数、也不该跟场景一起收起 / 展开。
/// </summary>
public static class ProcessSceneLink
{
    /// <summary>进程表缓存：父子关系在一个进程生命周期内不变，10 s 刷新一次。</summary>
    private static Dictionary<uint, NativeMethods.ProcessLink> _processes = new();
    private static DateTime _processesAt;

    /// <summary>
    /// 不能作为「同应用」锚点的 shell / 启动器进程：几乎所有从桌面 / 任务栏启动的程序
    /// 父进程都是 explorer.exe，沿父链走到它会把记事本、写字板、浏览器全误判成场景成员。
    /// </summary>
    private static readonly HashSet<string> ShellLaunchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe",
        "svchost.exe",
        "services.exe",
        "wininit.exe",
        "userinit.exe",
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe",
        "conhost.exe",
        "runtimebroker.exe",
        "dllhost.exe",
        "taskhostw.exe",
        "sihost.exe",
        "ctfmon.exe",
        "searchhost.exe",
        "shellexperiencehost.exe",
        "startmenuexperiencehost.exe",
        "applicationframehost.exe"
    };

    /// <summary>
    /// 只能算「运行环境」、不能代表某个应用的 exe：它们同名的进程往往是互不相干的应用，
    /// 不能按「同 exe = 同应用」兜底归组，否则一个脚本窗口会把另一个脚本窗口认成辅助窗口。
    /// </summary>
    private static readonly HashSet<string> GenericHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "python.exe",
        "pythonw.exe",
        "node.exe",
        "java.exe",
        "javaw.exe",
        "dotnet.exe",
        "electron.exe",
        "wscript.exe",
        "cscript.exe",
        "mshta.exe"
    };

    /// <summary>
    /// 这个窗口的进程（或它的父 / 祖父进程）是不是某个场景成员的同一进程；
    /// 是就返回那个组号，否则 0。
    /// </summary>
    public static int GroupOf(IntPtr hwnd, IEnumerable<StashedWindow> members)
    {
        if (hwnd == IntPtr.Zero || members == null) return 0;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return 0;

        // 已销毁的成员不算：进程号可能已经被系统复用。只枚举一次，下面父链 / 同 exe 都要用。
        var live = members
            .Where(m => m.ProcessId != 0 && NativeMethods.IsWindow(m.Handle))
            .ToArray();
        return ResolveForProcess(pid, LinkOf(pid).ExeName, live, ParentOf, maxDepth: 2, exeOf: ExeOf);
    }

    /// <summary>
    /// 纯规则：先排除 shell / 启动器自己的窗口，再按进程链归组。
    ///
    /// 资源管理器的文件夹窗口与场景里的资源管理器<b>同进程</b>（explorer.exe），
    /// 但它就是一个普通应用：用户切到另一个资源管理器窗口时，单窗口模式必须照常
    /// 收起场景、最小化其它窗口。只有真正由场景应用拉起的辅助窗口（微信图片 / 小程序宿主）
    /// 才返回组号。
    /// </summary>
    internal static int ResolveForProcess(
        uint pid, string exeName, IEnumerable<StashedWindow> members,
        Func<uint, uint> parentOf, int maxDepth = 2, Func<uint, string> exeOf = null)
    {
        if (pid == 0) return 0;
        if (IsShellLauncher(exeName)) return 0;

        // 父链优先：同进程 / 子进程是最可靠的关系。
        int group = Resolve(pid, members, parentOf, maxDepth);
        if (group != 0) return group;

        // 父链对不上（飞书媒体中心与聊天窗口是兄弟进程，共同父进程不在场景里）时，
        // 再按「同一个 exe」兜底；同一 exe 属于多个场景时仍然不猜。
        if (exeOf == null || string.IsNullOrEmpty(exeName) || IsGenericHost(exeName)) return 0;
        return ResolveByExe(exeName, members, exeOf);
    }

    /// <summary>
    /// 前台窗口所在进程的父 / 祖父 pid：辅助窗口打开时，这些进程的窗口不能被最小化
    /// （飞书媒体中心的父进程就是飞书主进程，pid=19236 的主窗口在另一块屏上）。
    /// </summary>
    public static HashSet<uint> AncestorPids(IntPtr hwnd, int maxDepth = 2)
    {
        var result = new HashSet<uint>();
        if (hwnd == IntPtr.Zero) return result;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return AncestorsOf(pid, ParentOf, maxDepth);
    }

    internal static HashSet<uint> AncestorsOf(uint pid, Func<uint, uint> parentOf, int maxDepth = 2)
    {
        var result = new HashSet<uint>();
        if (pid == 0 || parentOf == null) return result;

        uint current = pid;
        for (int depth = 0; depth < maxDepth; depth++)
        {
            uint parent = parentOf(current);
            // 遇到 0（父进程已退出 / 是 shell 启动器）或环就停。
            if (parent == 0 || !result.Add(parent)) break;
            current = parent;
        }
        return result;
    }

    /// <summary>
    /// 纯规则：找 exe 文件名相同的场景成员，唯一组才返回，多个组不猜。
    /// </summary>
    internal static int ResolveByExe(
        string exeName, IEnumerable<StashedWindow> members, Func<uint, string> exeOf)
    {
        if (string.IsNullOrEmpty(exeName) || members == null || exeOf == null) return 0;

        int found = 0;
        foreach (var member in members)
        {
            if (member == null || member.ProcessId == 0) continue;
            string exe = exeOf(member.ProcessId);
            if (!string.Equals(exe, exeName, StringComparison.OrdinalIgnoreCase)) continue;
            if (found == 0) found = member.GroupId;
            else if (found != member.GroupId) return 0;
        }
        return found;
    }

    /// <summary>
    /// 纯规则：从 <paramref name="pid"/> 开始沿父进程链向上找，看哪个组里有同进程的成员。
    /// 同一进程被两个场景同时接管时返回 0（不猜）；找不到也返回 0。
    /// </summary>
    public static int Resolve(
        uint pid, IEnumerable<StashedWindow> members, Func<uint, uint> parentOf, int maxDepth = 2)
    {
        if (pid == 0 || members == null || parentOf == null) return 0;

        var byPid = new Dictionary<uint, int>();
        foreach (var member in members)
        {
            if (member == null || member.ProcessId == 0) continue;
            if (byPid.TryGetValue(member.ProcessId, out int existing))
            {
                // 同一进程属于多个场景（例如所有资源管理器窗口共用一个进程）：不猜。
                if (existing != member.GroupId) byPid[member.ProcessId] = 0;
            }
            else
            {
                byPid[member.ProcessId] = member.GroupId;
            }
        }

        uint current = pid;
        for (int depth = 0; depth <= maxDepth && current != 0; depth++)
        {
            if (byPid.TryGetValue(current, out int group)) return group;

            uint parent = parentOf(current);
            if (parent == 0 || parent == current) break;
            current = parent;
        }
        return 0;
    }

    private static uint ParentOf(uint pid)
    {
        uint parent = LinkOf(pid).ParentId;
        if (parent == 0) return 0;
        if (!_processes.TryGetValue(parent, out var parentLink)) return parent;

        // shell / 启动器只负责拉起进程，不代表「同一个应用」：在它这里截断父链。
        return IsShellLauncher(parentLink.ExeName) ? 0 : parent;
    }

    private static NativeMethods.ProcessLink LinkOf(uint pid)
    {
        if (DateTime.UtcNow - _processesAt > TimeSpan.FromSeconds(10)) RefreshProcesses();

        if (!_processes.TryGetValue(pid, out var link))
        {
            // 新启动的进程：快照里还没有它，刷新一次再查。
            RefreshProcesses();
            if (!_processes.TryGetValue(pid, out link))
            {
                _processes[pid] = default;   // 查不到就记住，别反复拍快照
                return default;
            }
        }
        return link;
    }

    private static void RefreshProcesses()
    {
        _processes = NativeMethods.ProcessLinks();
        _processesAt = DateTime.UtcNow;
    }

    private static string ExeOf(uint pid) => pid == 0 ? null : LinkOf(pid).ExeName;

    internal static bool IsShellLauncher(string exeName)
        => !string.IsNullOrEmpty(exeName) && ShellLaunchers.Contains(exeName);

    internal static bool IsGenericHost(string exeName)
        => !string.IsNullOrEmpty(exeName) && GenericHosts.Contains(exeName);
}
