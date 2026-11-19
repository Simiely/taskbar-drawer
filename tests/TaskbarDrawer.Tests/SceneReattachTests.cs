using TaskbarDrawer.Core;
using TaskbarDrawer.Interop;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>
/// 「场景成员的程序关掉又重开」的认领规则：快照里缺了哪一项、
/// 桌面上新出现的窗口能不能认成它。纯规则，不建真实窗口。
/// </summary>
public class SceneReattachTests
{
    private static SavedWindow Saved(
        string exe, string title, int left = 0, string className = "Cls") => new()
    {
        ExePath = exe,
        Title = title,
        ClassName = className,
        AppId = WindowIdentity.AppId(null, exe),
        Left = left,
        Top = 0,
        Width = 100,
        Height = 100
    };

    private static DisplayRules.RestoreCandidate Live(
        long handle, string exe, string title, int left = 0, string className = "Cls") => new()
    {
        Handle = handle,
        Title = title,
        ClassName = className,
        AppId = WindowIdentity.AppId(null, exe),
        Rect = NativeMethods.RECT.FromLTRB(left, 0, left + 100, 100),
        MonitorDevice = @"\\.\DISPLAY1"
    };

    private static SceneReattach.Candidate New(
        string exe, string title, int left = 0, string className = "Cls") => new()
    {
        Title = title,
        ClassName = className,
        ProcessPath = exe,
        Rect = NativeMethods.RECT.FromLTRB(left, 0, left + 100, 100)
    };

    [Fact]
    public void Missing_全部还在时为空()
    {
        var saved = new[] { Saved(@"C:\A.exe", "甲"), Saved(@"C:\B.exe", "乙") };
        var live = new[]
        {
            Live(1, @"C:\A.exe", "甲"),
            Live(2, @"C:\B.exe", "乙")
        };

        Assert.Empty(SceneReattach.Missing(saved, live));
    }

    [Fact]
    public void Missing_同程序两个窗口只开了一个时另一个算缺失()
    {
        var saved = new[]
        {
            Saved(@"C:\chrome.exe", "甲", left: 0),
            Saved(@"C:\chrome.exe", "乙", left: 500)
        };
        var live = new[] { Live(2, @"C:\chrome.exe", "乙", left: 500) };

        var missing = SceneReattach.Missing(saved, live);

        Assert.Single(missing);
        Assert.Equal("甲", missing[0].Title);
    }

    [Fact]
    public void Missing_标题变了按保存位置认回()
    {
        // 浏览器切标签页 / 编辑器换文件：标题变了，但窗口还在原位。
        var saved = new[] { Saved(@"C:\chrome.exe", "旧标题", left: 300) };
        var live = new[] { Live(1, @"C:\chrome.exe", "新标题", left: 300) };

        Assert.Empty(SceneReattach.Missing(saved, live));
    }

    [Fact]
    public void Missing_运行时成员为空时全部缺失()
    {
        var saved = new[] { Saved(@"C:\A.exe", "甲"), Saved(@"C:\B.exe", "乙") };

        Assert.Equal(2, SceneReattach.Missing(saved, Array.Empty<DisplayRules.RestoreCandidate>()).Count);
    }

    [Fact]
    public void Match_同标题优先()
    {
        var missing = new[]
        {
            Saved(@"C:\chrome.exe", "甲", left: 0),
            Saved(@"C:\chrome.exe", "乙", left: 500)
        };

        Assert.Equal(1, SceneReattach.Match(missing, New(@"C:\chrome.exe", "乙", left: 100)));
    }

    [Fact]
    public void Match_同程序只有一个缺失项时标题不同也认()
    {
        var missing = new[] { Saved(@"C:\doubao.exe", "旧标题") };

        Assert.Equal(0, SceneReattach.Match(missing, New(@"C:\doubao.exe", "新标题")));
    }

    [Fact]
    public void Match_同程序多个缺失项且标题不同时不猜()
    {
        var missing = new[]
        {
            Saved(@"C:\chrome.exe", "甲", left: 0),
            Saved(@"C:\chrome.exe", "乙", left: 500)
        };

        Assert.Equal(-1, SceneReattach.Match(missing, New(@"C:\chrome.exe", "丙", left: 260)));
    }

    [Fact]
    public void Match_不同程序不认()
    {
        var missing = new[] { Saved(@"C:\A.exe", "甲") };

        Assert.Equal(-1, SceneReattach.Match(missing, New(@"C:\B.exe", "甲")));
    }

    [Fact]
    public void Match_窗口类名不同不认()
    {
        var missing = new[] { Saved(@"C:\chrome.exe", "甲", className: "Chrome_WidgetWin_1") };

        Assert.Equal(-1, SceneReattach.Match(
            missing, New(@"C:\chrome.exe", "甲", className: "Chrome_WidgetWin_0")));
    }

    [Fact]
    public void Match_旧快照没有AppId时按exe路径认()
    {
        // 1.11 之前的快照没有 AppId / 类名：退回规范化后的 exe 路径比较。
        var legacy = new SavedWindow { ExePath = @"C:\A.exe", Title = "甲" };

        Assert.Equal(0, SceneReattach.Match(new[] { legacy }, New("c:/a.EXE", "甲")));
    }
}
