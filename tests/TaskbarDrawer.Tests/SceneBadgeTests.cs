using TaskbarDrawer.Core;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>场景标识的取值约束、文字表示与旧数据迁移。</summary>
public class SceneBadgeTests
{
    [Fact]
    public void Normalized_接受任意合法颜色()
    {
        Assert.Equal("#123456", new SceneBadge { Color = "#123456" }.Normalized().Color);
        Assert.Equal("#000000", new SceneBadge { Color = "#000000" }.Normalized().Color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public void Normalized_非法颜色退回默认色(string input)
    {
        var badge = new SceneBadge { Color = input }.Normalized();
        Assert.Equal(SceneBadge.DefaultColor, badge.Color);
        Assert.Equal(SceneBadge.DefaultColor, new SceneBadge { Color = null }.Normalized().Color);
    }

    [Fact]
    public void DefaultPalette_第八色是黑色()
    {
        Assert.Equal(8, SceneBadge.DefaultPalette.Count);
        Assert.Equal("#000000", SceneBadge.DefaultPalette[7]);
        Assert.DoesNotContain(SceneBadge.LegacyGray, SceneBadge.DefaultPalette);
    }

    [Fact]
    public void Normalized_颜色大小写归一()
    {
        var badge = new SceneBadge { Color = "#0a6cde" }.Normalized();
        Assert.Equal("#0A6CDE", badge.Color);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(10, 0)]
    [InlineData(0, 0)]
    [InlineData(9, 9)]
    public void Normalized_数字只留0到9(int input, int expected)
    {
        var badge = new SceneBadge { Number = input }.Normalized();
        Assert.Equal(expected, badge.Number);
    }

    [Fact]
    public void Coerce_null得到默认标识()
    {
        var badge = SceneBadge.Coerce(null);
        Assert.Equal(SceneBadge.DefaultColor, badge.Color);
        Assert.Equal(0, badge.Number);
        Assert.True(badge.Right);
    }

    [Fact]
    public void Glyph_无数字是圆点_有数字是圈号()
    {
        Assert.Equal("●", SceneBadge.GlyphOf(new SceneBadge()));
        Assert.Equal("①", SceneBadge.GlyphOf(new SceneBadge { Number = 1 }));
        Assert.Equal("⑨", SceneBadge.GlyphOf(new SceneBadge { Number = 9 }));
        Assert.Equal("", SceneBadge.GlyphOf(null));
    }

    [Fact]
    public void Prefix_圆点或圈号后带空格()
    {
        Assert.Equal("● ", SceneBadge.PrefixOf(new SceneBadge()));
        Assert.Equal("① ", SceneBadge.PrefixOf(new SceneBadge { Number = 1 }));
        Assert.Equal("", SceneBadge.PrefixOf(null));
    }

    [Fact]
    public void FromLegacyEmoji_映射到最接近的颜色()
    {
        Assert.Equal("#E5484D", SceneBadge.FromLegacyEmoji("🔥").Color);
        Assert.Equal("#0A6CDE", SceneBadge.FromLegacyEmoji("💎").Color);
        Assert.Equal("#46A758", SceneBadge.FromLegacyEmoji("🍀").Color);
        // 1.6.6 的旧预设统一黑色；未知值退回默认色。
        Assert.Equal("#000000", SceneBadge.FromLegacyEmoji("💾").Color);
        Assert.Equal(SceneBadge.DefaultColor, SceneBadge.FromLegacyEmoji("🦄").Color);
        Assert.Equal(0, SceneBadge.FromLegacyEmoji("🔥").Number);
    }

    [Fact]
    public void SameAs_逐项比较()
    {
        var badge = new SceneBadge { Color = "#12A594", Number = 4, Right = true };
        Assert.True(badge.SameAs(new SceneBadge { Color = "#12A594", Number = 4, Right = true }));
        Assert.False(badge.SameAs(new SceneBadge { Color = "#12A594", Number = 4, Right = false }));
        Assert.False(badge.SameAs(null));
    }

    [Fact]
    public void SceneTitle_摘掉新旧预设前缀()
    {
        Assert.Equal("文档", SceneTitle.Strip("🔥 文档"));
        Assert.Equal("文档", SceneTitle.Strip("💾 文档"));
        Assert.Equal("🦄 文档", SceneTitle.Strip("🦄 文档"));
        Assert.Equal("文档", SceneTitle.Strip("文档"));
        Assert.Equal("", SceneTitle.Strip(null));
    }
}
