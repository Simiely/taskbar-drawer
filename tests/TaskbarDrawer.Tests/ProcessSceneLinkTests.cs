using TaskbarDrawer.Core;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>
/// 「场景应用的辅助窗口」判定：同进程 → 父进程 → 祖父进程，最多两层。
/// 典型现场：微信图片查看器（同 Weixin.exe）、小程序宿主（WeChatAppEx.exe，Weixin.exe 的子进程）。
/// </summary>
public class ProcessSceneLinkTests
{
    private static StashedWindow Member(int group, uint pid) => new()
    {
        Handle = new IntPtr(pid * 10 + (uint)group),
        GroupId = group,
        ProcessId = pid
    };

    private static Func<uint, uint> Tree(params (uint pid, uint parent)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.pid, p => p.parent);
        return pid => map.TryGetValue(pid, out uint parent) ? parent : 0;
    }

    [Fact]
    public void 同一进程的辅助窗口归入场景()
    {
        var members = new[] { Member(1, 100) };

        Assert.Equal(1, ProcessSceneLink.Resolve(100, members, _ => 0));
    }

    [Fact]
    public void 子进程的窗口归入父进程所在的场景()
    {
        // 微信小程序宿主 WeChatAppEx.exe 由 Weixin.exe 拉起。
        var members = new[] { Member(1, 100) };

        Assert.Equal(1, ProcessSceneLink.Resolve(200, members, Tree((200, 100))));
    }

    [Fact]
    public void 祖父进程也能认到_但最多两层()
    {
        var members = new[] { Member(1, 100) };

        Assert.Equal(1, ProcessSceneLink.Resolve(300, members, Tree((300, 200), (200, 100))));
        Assert.Equal(0, ProcessSceneLink.Resolve(
            400, members, Tree((400, 300), (300, 200), (200, 100))));
    }

    [Fact]
    public void 无关进程不归入场景()
    {
        var members = new[] { Member(1, 100) };

        Assert.Equal(0, ProcessSceneLink.Resolve(999, members, _ => 1));
    }

    [Fact]
    public void 同一进程属于两个场景时不猜()
    {
        // 所有资源管理器窗口共用一个 explorer.exe：两个场景都含资源管理器时，不能随便归给其中一个。
        var members = new[] { Member(1, 100), Member(2, 100) };

        Assert.Equal(0, ProcessSceneLink.Resolve(100, members, _ => 0));
    }

    [Fact]
    public void Shell启动器自己的窗口不算场景辅助窗口()
    {
        var members = new[] { Member(1, 100) };

        // 另一个资源管理器窗口与场景里的资源管理器同进程（explorer.exe），但它就是普通应用：
        // 必须返回 0，让单窗口模式照常收起场景，而不是「跳过前台」什么都不做。
        Assert.Equal(0, ProcessSceneLink.ResolveForProcess(100, "explorer.exe", members, _ => 0));
        // 不是 shell 的同进程窗口仍按辅助窗口处理（微信图片查看器）。
        Assert.Equal(1, ProcessSceneLink.ResolveForProcess(100, "Weixin.exe", members, _ => 0));
    }

    [Fact]
    public void Shell启动器不能作为同应用的锚点()
    {
        // 从桌面 / 任务栏启动的程序父进程都是 explorer.exe：沿父链走会把它当成场景同应用。
        Assert.True(ProcessSceneLink.IsShellLauncher("explorer.exe"));
        Assert.True(ProcessSceneLink.IsShellLauncher("RuntimeBroker.exe"));
        Assert.False(ProcessSceneLink.IsShellLauncher("Weixin.exe"));
        Assert.False(ProcessSceneLink.IsShellLauncher("WeChatAppEx.exe"));
        Assert.False(ProcessSceneLink.IsShellLauncher(""));
    }
}
