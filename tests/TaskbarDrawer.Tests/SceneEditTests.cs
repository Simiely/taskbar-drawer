using TaskbarDrawer.Core;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>场景编辑是纯函数：不落盘、不碰窗口，直接验证移除 / 合并 / 过滤规则。</summary>
public class SceneEditTests
{
    private static SavedWindow Window(string exe, string title, string process = "proc") => new()
    {
        ExePath = exe,
        ProcessName = process,
        Title = title
    };

    [Fact]
    public void RemoveAt_移除指定窗口()
    {
        var windows = new List<SavedWindow> { Window(@"C:\A.exe", "甲"), Window(@"C:\B.exe", "乙") };

        Assert.True(SceneEdit.RemoveAt(windows, 0, out var removed));

        Assert.Equal("甲", removed.Title);
        Assert.Single(windows);
        Assert.Equal("乙", windows[0].Title);
    }

    [Fact]
    public void RemoveAt_越界或只剩一个时拒绝()
    {
        var single = new List<SavedWindow> { Window(@"C:\A.exe", "甲") };
        Assert.False(SceneEdit.RemoveAt(single, 0, out _));
        Assert.Single(single);

        var two = new List<SavedWindow> { Window(@"C:\A.exe", "甲"), Window(@"C:\B.exe", "乙") };
        Assert.False(SceneEdit.RemoveAt(two, 2, out _));
        Assert.False(SceneEdit.RemoveAt(two, -1, out _));
        Assert.Equal(2, two.Count);
    }

    [Fact]
    public void Merge_追加新窗口并跳过重复()
    {
        var windows = new List<SavedWindow> { Window(@"C:\A.exe", "甲") };
        var suppressed = new List<string>();
        var incoming = new[]
        {
            Window(@"C:\A.exe", "甲"),
            Window(@"C:\B.exe", "乙"),
            Window(@"C:\B.exe", "乙")
        };

        var (added, skipped) = SceneEdit.Merge(windows, suppressed, incoming);

        Assert.Equal(1, added);
        Assert.Equal(2, skipped);
        Assert.Equal(2, windows.Count);
    }

    [Fact]
    public void Merge_路径大小写和斜杠归一()
    {
        var windows = new List<SavedWindow> { Window(@"C:\Apps\A.exe", "甲") };

        var (added, skipped) = SceneEdit.Merge(
            windows, new List<string>(), new[] { Window("c:/apps/a.EXE", "甲") });

        Assert.Equal(0, added);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void Merge_重新加入时清除suppressed()
    {
        var removed = Window(@"C:\A.exe", "甲");
        string key = SceneEdit.Key(removed);
        var windows = new List<SavedWindow> { Window(@"C:\B.exe", "乙") };
        var suppressed = new List<string> { key };

        var (added, _) = SceneEdit.Merge(windows, suppressed, new[] { removed });

        Assert.Equal(1, added);
        Assert.DoesNotContain(key, suppressed);
    }

    [Fact]
    public void Visible_过滤被移除的窗口()
    {
        var a = Window(@"C:\A.exe", "甲");
        var b = Window(@"C:\B.exe", "乙");

        var visible = SceneEdit.Visible(new[] { a, b }, new[] { SceneEdit.Key(b) });

        Assert.Single(visible);
        Assert.Equal("甲", visible[0].Title);
    }

    [Fact]
    public void Visible_全部被过滤时返回空()
    {
        var a = Window(@"C:\A.exe", "甲");

        Assert.Empty(SceneEdit.Visible(new[] { a }, new[] { SceneEdit.Key(a) }));
    }

    [Fact]
    public void Visible_没有suppressed时原样返回()
    {
        var a = Window(@"C:\A.exe", "甲");
        var b = Window(@"C:\B.exe", "乙");

        Assert.Equal(2, SceneEdit.Visible(new[] { a, b }, Array.Empty<string>()).Count);
        Assert.Equal(2, SceneEdit.Visible(new[] { a, b }, null!).Count);
    }

    [Fact]
    public void Key_空值不抛异常()
    {
        Assert.Equal("", SceneEdit.Key(null!));
        Assert.Equal("", SceneEdit.Key(new SavedWindow()));
        Assert.Equal("", SceneEdit.Key(new SavedWindow { ExePath = "  " }));
    }

    [Fact]
    public void Describe_透传字段()
    {
        var item = SceneEdit.Describe(3, new SavedWindow
        {
            Title = "标题",
            ProcessName = "app",
            ExePath = @"C:\app.exe",
            Maximized = true
        });

        Assert.Equal(3, item.Index);
        Assert.Equal("标题", item.Title);
        Assert.Equal("app", item.ProcessName);
        Assert.Equal(@"C:\app.exe", item.ExePath);
        Assert.True(item.Maximized);
    }
}
