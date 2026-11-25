using TaskbarDrawer.Core;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>
/// 「点一下浏览器扩展弹窗，整个场景被收回胶囊」的判定规则。
///
/// 机理：点弹窗时系统把<b>弹窗</b>设为前台（MS「Window Features」：用户点击即设置前台窗口；
/// catch22 的停靠工具栏教程也写明「工具窗显示为活动、owner 变非活动」），而弹窗永远不是场景成员，
/// 于是「别的场景整组收回胶囊」会把弹窗所属的场景整个收走。
///
/// 两道兜底各锁一条：按簇主归组；簇主也对不上就别把这次前台变化当成窗口切换。
/// </summary>
public class SingleWindowModeRulesTests
{
    private static readonly IntPtr Popup = new(0x100);
    private static readonly IntPtr Browser = new(0x200);

    [Fact]
    public void 前台本身就是场景成员时直接用自己()
    {
        int group = SingleWindowMode.ResolveGroup(Popup, _ => 7, _ => Browser);
        Assert.Equal(7, group);
    }

    [Fact]
    public void 前台是弹窗时按窗口簇的簇主归组()
    {
        // 浏览器在组 3，弹窗自己不在任何组：应当判定为「用户还在用组 3」。
        int group = SingleWindowMode.ResolveGroup(
            Popup,
            hwnd => hwnd == Browser ? 3 : 0,
            _ => Browser);

        Assert.Equal(3, group);
    }

    [Fact]
    public void 簇主也不在场景里时仍返回零()
    {
        int group = SingleWindowMode.ResolveGroup(Popup, _ => 0, _ => Browser);
        Assert.Equal(0, group);
    }

    [Fact]
    public void 无owner的普通窗口簇主就是自己_不应误判()
    {
        // 普通顶层窗口的 GA_ROOTOWNER 返回自己：不能因此把自己再查一遍（会白跑一次回调）。
        int calls = 0;
        int group = SingleWindowMode.ResolveGroup(
            Popup,
            _ => { calls++; return 0; },
            hwnd => hwnd);

        Assert.Equal(0, group);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void 前台既无组又算不上窗口时忽略这次切换()
    {
        // 工具窗 / 被拥有的弹窗 / 无标题：按准入规则它连窗口都不算，不能因此收掉别的场景。
        Assert.True(SingleWindowMode.ShouldIgnoreForeground(0, "工具窗（不进 Alt+Tab）"));
        Assert.True(SingleWindowMode.ShouldIgnoreForeground(0, "被拥有的弹窗 / 对话框"));
    }

    [Fact]
    public void 前台属于某个场景时绝不能被忽略()
    {
        // 解析出组号就说明它（或它的簇主）是场景成员：必须照常处理。
        Assert.False(SingleWindowMode.ShouldIgnoreForeground(3, "工具窗（不进 Alt+Tab）"));
    }

    [Fact]
    public void 前台是正常窗口时照常处理()
    {
        // 真的切到了场景外的普通窗口：该收起别的场景、该最小化别的窗口。
        Assert.False(SingleWindowMode.ShouldIgnoreForeground(0, null));
    }

    [Fact]
    public void 资源管理器默认不计入单窗口模式()
    {
        // 开关打开 + 不属于任何场景：前台 / 候选都跳过，既不动它也不因它动别人。
        Assert.True(SingleWindowMode.ShouldSkipExplorerWindow(true, 0, isExplorer: true));
        // 属于场景的资源管理器仍按场景整体处理，不能跳过。
        Assert.False(SingleWindowMode.ShouldSkipExplorerWindow(true, 3, isExplorer: true));
    }

    [Fact]
    public void 关掉开关后资源管理器按普通窗口处理()
    {
        Assert.False(SingleWindowMode.ShouldSkipExplorerWindow(false, 0, isExplorer: true));
    }
}
