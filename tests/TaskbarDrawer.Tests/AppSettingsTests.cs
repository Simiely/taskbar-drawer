using TaskbarDrawer.Core;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>调色板与默认尺寸：非法 / 缺失的色块都补齐成默认值，读取时不会得到半截调色板。</summary>
public class AppSettingsTests
{
    [Fact]
    public void 默认尺寸是90和55()
    {
        var settings = new AppSettings();
        Assert.Equal(0.90, settings.SceneAppIconSize);
        Assert.Equal(0.55, settings.SceneBadgeSize);
    }

    [Fact]
    public void BadgePalette_没有自定义时就是默认八色()
    {
        var palette = new AppSettings().BadgePalette();
        Assert.Equal(SceneBadge.DefaultPalette, palette);
        Assert.Equal("#000000", palette[7]);
    }

    [Fact]
    public void BadgePalette_自定义颜色原样保留_缺项与非法值补默认()
    {
        var settings = new AppSettings
        {
            SceneBadgeColors = new List<string> { "#abcdef", "red", "", "#12345" }
        };

        var palette = settings.BadgePalette();
        Assert.Equal(8, palette.Count);
        Assert.Equal("#ABCDEF", palette[0]);
        Assert.Equal(SceneBadge.DefaultPalette[1], palette[1]);
        Assert.Equal(SceneBadge.DefaultPalette[2], palette[2]);
        Assert.Equal(SceneBadge.DefaultPalette[3], palette[3]);
        Assert.Equal(SceneBadge.DefaultPalette[7], palette[7]);
    }

    [Fact]
    public void 资源管理器默认不计入单窗口模式()
    {
        Assert.True(new AppSettings().ExcludeExplorerFromSingleWindow);
    }
}
