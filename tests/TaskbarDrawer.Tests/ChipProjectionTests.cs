using TaskbarDrawer.Core;
using TaskbarDrawer.UI;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>胶囊投影是纯函数：不依赖窗口，直接验证文案与筛选规则。</summary>
public class ChipProjectionTests
{
    private static StashGroupSummary Group(
        int id, int count = 2, string name = null, string savedId = null, bool expanded = false,
        params string[] titles) => new()
    {
        Id = id,
        Count = count,
        Name = name,
        SavedId = savedId,
        Expanded = expanded,
        Titles = titles.Length == 0 ? new[] { "窗口一", "窗口二" } : titles
    };

    private static SavedGroupSummary Saved(
        string id, string name = "场景一", int count = 2, params string[] titles) => new()
    {
        Id = id,
        Name = name,
        Count = count,
        Titles = titles.Length == 0 ? new[] { "窗口一", "窗口二" } : titles
    };

    [Fact]
    public void Dormant_只有没有运行时组的场景才出现()
    {
        var groups = new[] { Group(1, savedId: "s1") };
        var saved = new[] { Saved("s1"), Saved("s2") };

        var dormant = ChipProjection.Dormant(groups, saved);

        Assert.Single(dormant);
        Assert.Equal("s2", dormant[0].Id);
    }

    [Fact]
    public void Chips_普通编组用组号和窗口数()
    {
        var chips = ChipProjection.Chips(new[] { Group(3, count: 2) }, Array.Empty<SavedGroupSummary>());

        var chip = Assert.Single(chips);
        Assert.Equal("3·2", chip.Text);
        Assert.Equal(3, chip.GroupId);
        Assert.False(chip.Dormant);
        Assert.Contains("第 3 组", chip.Tooltip);
    }

    [Fact]
    public void Chips_已保存的编组带星号()
    {
        var chips = ChipProjection.Chips(
            new[] { Group(1, name: "工作", savedId: "s1") }, Array.Empty<SavedGroupSummary>());

        Assert.Equal("★工作", Assert.Single(chips).Text);
    }

    [Fact]
    public void Chips_已关闭场景标记为休眠()
    {
        var chips = ChipProjection.Chips(Array.Empty<StashGroupSummary>(), new[] { Saved("s1", "写作") });

        var chip = Assert.Single(chips);
        Assert.Equal("★写作", chip.Text);
        Assert.True(chip.Dormant);
        Assert.Equal(0, chip.GroupId);
        Assert.Contains("已关闭", chip.Tooltip);
    }

    [Fact]
    public void ShortName_超过六个字截断()
    {
        Assert.Equal("一二三四五六…", ChipProjection.ShortName("一二三四五六七"));
        Assert.Equal("场景", ChipProjection.ShortName(" "));
        Assert.Equal("工作", ChipProjection.ShortName("工作"));
    }

    [Fact]
    public void RuntimeTooltip_最多列三个标题()
    {
        var group = Group(1, count: 5, titles: new[] { "甲", "乙", "丙", "丁" });
        string tooltip = ChipProjection.RuntimeTooltip(group);

        Assert.Contains("甲", tooltip);
        Assert.Contains("丙", tooltip);
        Assert.DoesNotContain("丁", tooltip);
        Assert.Contains("…", tooltip);
    }

    [Fact]
    public void RuntimeTooltip_跟随展开状态()
    {
        // 收起时提示「左键展开」，展开时提示「左键收起」。
        Assert.Contains("左键展开", ChipProjection.RuntimeTooltip(Group(1)));
        Assert.Contains("左键收起", ChipProjection.RuntimeTooltip(Group(1, expanded: true)));
    }
}
