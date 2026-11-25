using TaskbarDrawer.Core;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>
/// 排除列表的规则：内置资源管理器固定第一且不可删，用户项按应用匹配；
/// 路径读不到时退回进程文件名，程序关掉重开窗口仍然有效。
/// </summary>
public class WindowExclusionTests
{
    private static WindowExclusion Chrome(string path = @"C:\Apps\chrome.exe")
        => WindowExclusion.ForApp(path, "chrome");

    [Fact]
    public void Normalize_内置项固定排第一_重复应用只留一条()
    {
        var rules = WindowExclusion.Normalize(new[]
        {
            WindowExclusion.ForApp(@"C:\Apps\chrome.exe", "chrome"),
            WindowExclusion.ForApp(@"c:\apps\Chrome.exe", "chrome"),
            new WindowExclusion { Path = "", ProcessName = "" }
        });

        Assert.Equal(2, rules.Count);
        Assert.True(rules[0].BuiltIn);
        Assert.Equal("chrome", rules[1].DisplayName);
        Assert.Equal(@"C:\Apps\chrome.exe", rules[1].Path);
    }

    [Fact]
    public void Normalize_保留内置项的关闭状态()
    {
        var rules = WindowExclusion.Normalize(new[] { WindowExclusion.Explorer(enabled: false) });
        Assert.Single(rules);
        Assert.False(rules[0].Enabled);
    }

    [Fact]
    public void 资源管理器规则只命中文件夹窗口()
    {
        var rules = new[] { WindowExclusion.Explorer() };

        Assert.True(WindowExclusion.MatchesAny(
            rules, explorerWindow: true, @"C:\Windows\explorer.exe", "explorer.exe"));
        // 桌面 / 任务栏也是 explorer.exe，但它们不是文件夹窗口，不能靠进程名误伤。
        Assert.False(WindowExclusion.MatchesAny(
            rules, explorerWindow: false, @"C:\Windows\explorer.exe", "explorer.exe"));
        Assert.False(WindowExclusion.MatchesAny(
            rules, explorerWindow: false, @"C:\Apps\chrome.exe", "chrome.exe"));
    }

    [Fact]
    public void 关闭的内置项不再排除资源管理器()
    {
        var rules = new[] { WindowExclusion.Explorer(enabled: false) };
        Assert.False(WindowExclusion.MatchesAny(rules, true, "", ""));
    }

    [Fact]
    public void 用户项按exe全路径命中_大小写与斜杠不敏感()
    {
        var rules = new[] { WindowExclusion.Explorer(), Chrome() };

        Assert.True(WindowExclusion.MatchesAny(rules, false, @"c:/apps/CHROME.EXE", "chrome"));
        // 两边路径都读得到：同名但不同目录的是另一个程序，不能误伤。
        Assert.False(WindowExclusion.MatchesAny(rules, false, @"D:\Tools\chrome.exe", "chrome"));
    }

    [Fact]
    public void 读不到全路径时退回进程文件名()
    {
        var rules = new[] { WindowExclusion.Explorer(), Chrome() };

        // 管理员进程的 MainModule 读不到：只剩进程名，仍要认出同一个应用。
        Assert.True(WindowExclusion.MatchesAny(rules, false, "", "chrome"));
        Assert.False(WindowExclusion.MatchesAny(rules, false, "", "notepad"));
    }

    [Fact]
    public void 取消勾选的用户项不再命中()
    {
        var chrome = Chrome();
        chrome.Enabled = false;

        Assert.False(WindowExclusion.MatchesAny(
            new[] { chrome }, false, @"C:\Apps\chrome.exe", "chrome"));
    }

    [Fact]
    public void 添加去重_路径可读时同名不同目录算两个应用()
    {
        var rules = new List<WindowExclusion> { WindowExclusion.Explorer(), Chrome() };

        Assert.True(WindowExclusion.ContainsApp(rules, @"C:\Apps\chrome.exe", "chrome"));
        Assert.False(WindowExclusion.ContainsApp(rules, @"D:\Portable\chrome.exe", "chrome"));
        // 路径读不到时只能按名字认重，避免同一个应用加两条。
        Assert.True(WindowExclusion.ContainsApp(rules, "", "chrome"));
    }

    [Fact]
    public void 资源管理器永远算已存在_不允许重复添加()
    {
        var rules = new List<WindowExclusion> { WindowExclusion.Explorer() };
        Assert.True(WindowExclusion.ContainsApp(rules, @"C:\Windows\explorer.exe", "explorer.exe"));
    }

    [Fact]
    public void 进程名规范化统一带exe小写()
    {
        Assert.Equal("chrome.exe", WindowExclusion.NormalizeProcessName("Chrome"));
        Assert.Equal("chrome.exe", WindowExclusion.NormalizeProcessName(@"C:\Apps\CHROME.EXE"));
        Assert.Equal("", WindowExclusion.NormalizeProcessName("  "));
    }
}
