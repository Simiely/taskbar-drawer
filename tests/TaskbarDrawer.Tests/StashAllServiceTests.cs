using TaskbarDrawer.Core;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>
/// 运行时状态机的迁移矩阵：StashAll → ExpandGroup → StashGroup → Release → Adopt → Detach。
/// 这些迁移以前只能靠实机点一遍，现在由 <see cref="FakeWindowGateway"/> 在内存里跑，
/// 断言「句柄集合 + Hidden 标志 + 组是否还在 + Changed 触发次数」。
///
/// 真实性边界：假实现只模拟「谁可见、谁被藏、位置写到哪」，不模拟 UIPI / DPI / 图标，
/// 那部分靠实机夹具验收，不要在这里假装测过。
/// </summary>
public class StashAllServiceTests
{
    private const string Screen = FakeWindowGateway.Display1;

    private static (StashAllService service, FakeWindowGateway gateway, List<string> changes) New()
    {
        var gateway = new FakeWindowGateway();
        var service = new StashAllService(icons: null, gateway: gateway);
        var changes = new List<string>();
        service.Changed += () => changes.Add("changed");
        return (service, gateway, changes);
    }

    [Fact]
    public void StashAll_只收目标屏幕上的窗口()
    {
        var (service, gateway, changes) = New();
        gateway.Add(1, "甲");
        gateway.Add(2, "乙");
        gateway.Add(3, "副屏窗口", FakeWindowGateway.Display2);

        int hidden = service.StashAll(out int groupId, Screen);

        Assert.Equal(2, hidden);
        Assert.NotEqual(0, groupId);
        Assert.Equal(2, service.Count);
        Assert.True(gateway.Get(new IntPtr(1)).Visible == false);
        Assert.True(gateway.Get(new IntPtr(2)).Visible == false);
        // 另一块屏上的窗口一根手指都不能碰。
        Assert.True(gateway.Get(new IntPtr(3)).Visible);
        Assert.Single(service.Groups());
        Assert.Equal(2, service.Groups()[0].Count);
        Assert.Single(changes);
    }

    [Fact]
    public void StashAll_没有可收窗口时不建组()
    {
        var (service, gateway, changes) = New();
        gateway.Add(1, "只看不收", FakeWindowGateway.Display2);

        int hidden = service.StashAll(out int groupId, Screen);

        Assert.Equal(0, hidden);
        Assert.Equal(0, groupId);
        Assert.Empty(service.Groups());
        Assert.Empty(changes);
    }

    [Fact]
    public void StashAll_跳过提权窗口并计数()
    {
        var (service, gateway, _) = New();
        gateway.Add(1, "普通窗口");
        gateway.Add(2, "管理员窗口").Elevated = true;

        int hidden = service.StashAll(out _, Screen);

        Assert.Equal(1, hidden);
        // 硬收管理员窗口只会留下一个点不开的空胶囊：必须跳过并让界面能说明原因。
        Assert.Equal(1, service.LastBlockedByElevation);
        Assert.True(gateway.Get(new IntPtr(2)).Visible);
    }

    [Fact]
    public void StashAll_连带隐藏同簇弹窗()
    {
        var (service, gateway, _) = New();
        var main = gateway.Add(1, "主窗口");
        var popup = gateway.Add(9, "保存对话框");
        main.Popups.Add(popup.Handle);

        service.StashAll(out _, Screen);

        // SW_HIDE 主窗口不会联动 owned 窗口（系统只在最小化 / 销毁时联动），必须显式藏。
        Assert.False(popup.Visible);
        // 弹窗不是成员：不计入窗口数、不进快照。
        Assert.Equal(1, service.Count);
    }

    [Fact]
    public void ExpandGroup_恢复成员并置前_组仍然保留()
    {
        var (service, gateway, _) = New();
        gateway.Add(1, "甲");
        gateway.Add(2, "乙");
        service.StashAll(out int groupId, Screen);
        gateway.Activated.Clear();

        int shown = service.ExpandGroup(groupId);

        Assert.Equal(2, shown);
        Assert.True(gateway.Get(new IntPtr(1)).Visible);
        Assert.True(gateway.Get(new IntPtr(2)).Visible);
        // 组还在，只是状态变成「已展开」——这是「展开 → 再收起」能反复用的前提。
        var group = Assert.Single(service.Groups());
        Assert.True(group.Expanded);
        // 展开后必须置前，否则用户会觉得「场景没打开」（1.8.1 的回归点）。
        Assert.Single(gateway.Activated);
    }

    [Fact]
    public void StashGroup_再次收起_组保留且位置刷新()
    {
        var (service, gateway, _) = New();
        gateway.Add(1, "甲");
        service.StashAll(out int groupId, Screen);
        service.ExpandGroup(groupId);
        // 展开期间用户挪了窗口：收起前应按当前位置刷新记录。
        gateway.Get(new IntPtr(1)).Rect = TaskbarDrawer.Interop.NativeMethods.RECT.FromLTRB(300, 200, 1100, 800);
        gateway.Applied.Clear();

        int hidden = service.StashGroup(groupId);

        Assert.Equal(1, hidden);
        Assert.False(gateway.Get(new IntPtr(1)).Visible);
        var group = Assert.Single(service.Groups());
        Assert.False(group.Expanded);
        Assert.Equal(1, group.Count);
    }

    [Fact]
    public void ExpandNewest_在多组之间轮转()
    {
        var (service, gateway, _) = New();
        gateway.Add(1, "第一组窗口");
        service.StashAll(out int first, Screen);
        gateway.Add(2, "第二组窗口");
        service.StashAll(out int second, Screen);

        // 从最新一组开始，再点一次换到更旧的一组——否则热键永远在同一组上来回。
        // 返回值是「这次展开的窗口数」，组号看 LastExpandedGroupId。
        Assert.Equal(1, service.ExpandNewest(Screen));
        Assert.Equal(second, service.LastExpandedGroupId);
        Assert.Equal(1, service.ExpandNewest(Screen));
        Assert.Equal(first, service.LastExpandedGroupId);
        Assert.True(first != second);
    }

    [Fact]
    public void Adopt_接入运行中的窗口_不隐藏也不新建空组()
    {
        var (service, gateway, _) = New();
        gateway.Add(1, "已经开着的窗口");

        bool ok = service.Adopt(new IntPtr(1), "saved-1", "写文档", SceneBadge.Default);

        Assert.True(ok);
        // 从快照拉起的窗口本来就可见：接入时不能再藏一次，否则用户看到「打开了又消失」。
        Assert.True(gateway.Get(new IntPtr(1)).Visible);
        var group = Assert.Single(service.Groups());
        Assert.Equal("saved-1", group.SavedId);
        Assert.Equal(1, group.Count);
        Assert.True(group.Expanded);
    }

    [Fact]
    public void Adopt_已经被管着的窗口返回false()
    {
        var (service, gateway, _) = New();
        gateway.Add(1, "窗口");
        service.StashAll(out _, Screen);

        // 同一句柄重复接入会让一组里的计数翻倍，必须拒绝。
        Assert.False(service.Adopt(new IntPtr(1), "saved-1", "场景", SceneBadge.Default));
        Assert.Equal(1, service.Count);
    }

    [Fact]
    public void Release_把窗口交回桌面并删掉组()
    {
        var (service, gateway, _) = New();
        gateway.Add(1, "甲");
        service.StashAll(out int groupId, Screen);

        int shown = service.Release(groupId);

        Assert.Equal(1, shown);
        Assert.True(gateway.Get(new IntPtr(1)).Visible);
        Assert.Empty(service.Groups());
        Assert.Equal(0, service.Count);
    }

    [Fact]
    public void Detach_摘掉成员时把收起的窗口放回桌面()
    {
        var (service, gateway, _) = New();
        gateway.Add(1, "甲");
        gateway.Add(2, "乙");
        service.StashAll(out int groupId, Screen);
        service.LinkSaved(groupId, "saved-1", "写文档", SceneBadge.Default);

        bool ok = service.Detach("saved-1",
            new SavedWindow { ExePath = @"C:\app.exe", Title = "甲" }, out bool restored);

        Assert.True(ok);
        // 收起中的成员被摘掉时必须放回桌面：否则它既不在屏幕上、也不在任务栏。
        Assert.True(restored);
        Assert.True(gateway.Get(new IntPtr(1)).Visible);
        Assert.Equal(1, service.Count);
    }

    [Fact]
    public void Detach_旧快照标题失配时按位置找回成员()
    {
        // 真实事故：旧快照存的是资源管理器旧标题，运行时同 exe 有三扇窗且标题全变了，
        // 原先的 exe + 标题匹配直接放弃 → 运行中的成员没被摘掉 → 关闭场景时又被写回快照，
        // 用户看到「移除的窗口下次打开还在」。
        var (service, gateway, _) = New();
        var first = gateway.Add(11, "此电脑");
        var second = gateway.Add(22, "另一个资源管理器");
        service.Adopt(first.Handle, "saved-1", "场景", SceneBadge.Default);
        service.Adopt(second.Handle, "saved-1", "场景", SceneBadge.Default);

        var stale = new SavedWindow
        {
            ExePath = @"C://app.exe",
            Title = "很久以前的标题",
            Left = 22,
            Top = 0,
            Width = 800,
            Height = 600
        };

        bool ok = service.Detach("saved-1", stale, out _);

        Assert.True(ok);
        Assert.Equal(1, service.Count);
        // 摘掉的必须是位置对得上的那个，不能误伤同程序的另一个窗口。
        Assert.False(service.Contains(second.Handle));
        Assert.True(service.Contains(first.Handle));
    }

    [Fact]
    public void 场景归属屏在收纳时确定_拖动成员不改变()
    {
        var (service, gateway, _) = New();
        gateway.Add(1, "甲");
        service.StashAll(out int groupId, Screen);

        Assert.Equal(Screen, service.MonitorOfGroup(groupId));

        // 用户把成员拖到副屏：场景仍属于原来的屏。
        // 单窗口模式靠这条判定「副屏上的切换不收起主屏的场景」。
        service.ExpandGroup(groupId);
        gateway.Get(new IntPtr(1)).Monitor = FakeWindowGateway.Display2;

        Assert.Equal(Screen, service.MonitorOfGroup(groupId));
    }

    [Fact]
    public void Adopt_从快照打开的场景沿用快照的归属屏()
    {
        var (service, gateway, _) = New();
        gateway.Add(1, "已经开着的窗口", FakeWindowGateway.Display2);

        bool ok = service.Adopt(new IntPtr(1), "saved-1", "场景", SceneBadge.Default,
            FakeWindowGateway.Display2);

        Assert.True(ok);
        Assert.Equal(FakeWindowGateway.Display2, service.MonitorOfGroup(1));
    }

    [Fact]
    public void Groups_任意屏幕有可见成员就算展开()
    {
        var (service, gateway, _) = New();
        gateway.Add(1, "甲");
        gateway.Add(2, "乙");
        service.StashAll(out int groupId, Screen);
        service.LinkSaved(groupId, "saved-1", "场景", SceneBadge.Default);
        var guest = gateway.Add(3, "拖到副屏的成员", FakeWindowGateway.Display2);
        service.Adopt(guest.Handle, "saved-1", "场景", SceneBadge.Default, Screen);
        service.ExpandGroup(groupId);

        // 只收主屏这块屏上的成员（单窗口模式按屏处理）：副屏的成员还可见，场景仍算展开。
        int hidden = service.StashGroup(groupId, Screen);

        Assert.Equal(2, hidden);
        Assert.True(service.Groups()[0].Expanded);
        Assert.True(guest.Visible);
    }

    [Fact]
    public void Prune_清理已销毁的窗口()
    {
        var (service, gateway, _) = New();
        gateway.Add(1, "甲");
        gateway.Add(2, "乙");
        service.StashAll(out _, Screen);

        gateway.Destroy(new IntPtr(1));
        int changed = service.Prune();

        Assert.Equal(1, changed);
        Assert.Equal(1, service.Count);
        Assert.Single(service.Groups());
        Assert.Equal(1, service.Groups()[0].Count);
    }

    [Fact]
    public void Prune_外部还原的成员改回已展开_不删记录()
    {
        var (service, gateway, _) = New();
        gateway.Add(1, "甲");
        service.StashAll(out int groupId, Screen);
        // 用户从任务栏把窗口叫回来了：记录要留着（场景还能再收起），只是状态变化。
        gateway.Get(new IntPtr(1)).Visible = true;

        // 藏着的那一刻还在宽限期内，立刻 Prune 不会误判；把时间推过宽限期才认账。
        Assert.Equal(0, service.Prune());
        int changed = service.Prune(DateTime.UtcNow.AddSeconds(2));

        Assert.Equal(1, changed);
        Assert.Equal(1, service.Count);
        Assert.True(service.Groups()[0].Expanded);
        Assert.Equal(groupId, service.Groups()[0].Id);
    }
}
