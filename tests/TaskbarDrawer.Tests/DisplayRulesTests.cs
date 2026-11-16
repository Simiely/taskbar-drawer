using TaskbarDrawer.Core;
using TaskbarDrawer.Interop;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>
/// 多显示器规则是纯函数：本机单屏没法实机复现跨屏问题，这些用例是唯一的回归护栏。
/// 重点覆盖「屏幕未知时 fail-closed」与「目标屏被拔掉后夹回工作区」两条硬约束。
/// </summary>
public class DisplayRulesTests
{
    private static NativeMethods.RECT Rect(int left, int top, int width, int height)
        => NativeMethods.RECT.FromLTRB(left, top, left + width, top + height);

    [Fact]
    public void BelongsTo_Requires_A_Known_Target_Screen()
    {
        Assert.True(DisplayRules.BelongsTo(@"\\.\DISPLAY1", @"\\.\DISPLAY1"));
        Assert.False(DisplayRules.BelongsTo(@"\\.\DISPLAY1", @"\\.\DISPLAY2"));
        Assert.False(DisplayRules.BelongsTo(@"\\.\DISPLAY1", null));
    }

    [Theory]
    [InlineData("")]
    public void BelongsTo_Fails_Closed_When_Target_Is_Unknown(string target)
    {
        // 旧实现把「不知道哪块屏」当成「所有屏」，一次判定失败会把所有屏幕的窗口收进同一个场景。
        Assert.False(DisplayRules.BelongsTo(target, @"\\.\DISPLAY1"));
        Assert.False(DisplayRules.BelongsTo(target, null));
    }

    [Fact]
    public void BelongsTo_Fails_Closed_When_Target_Is_Null()
    {
        Assert.False(DisplayRules.BelongsTo(null, @"\\.\DISPLAY1"));
        Assert.False(DisplayRules.BelongsTo(null, null));
    }

    [Fact]
    public void VisibleRatio_Computes_Intersection()
    {
        var work = Rect(1920, 0, 1920, 1040);
        Assert.Equal(1.0, DisplayRules.VisibleRatio(Rect(2000, 100, 800, 600), work), 3);
        Assert.Equal(0.0, DisplayRules.VisibleRatio(Rect(0, 0, 800, 600), work), 3);
        Assert.Equal(0.5, DisplayRules.VisibleRatio(Rect(1520, 100, 800, 600), work), 3);
    }

    [Fact]
    public void ClampToWorkArea_Pulls_Window_Back_From_Missing_Monitor()
    {
        // 保存位置在已经被拔掉的副屏（x = 1920 起），最近的工作区是主屏。
        var work = Rect(0, 0, 1920, 1040);
        var clamped = DisplayRules.ClampToWorkArea(Rect(2400, 200, 800, 600), work);

        Assert.Equal(work.Right, clamped.Right);
        Assert.Equal(200, clamped.Top);
        Assert.Equal(800, clamped.Width);
    }

    [Fact]
    public void ClampToWorkArea_Bounds_Size_And_Keeps_It_Inside()
    {
        var work = Rect(0, 0, 1280, 720);
        var clamped = DisplayRules.ClampToWorkArea(Rect(-300, -200, 2000, 1200), work);

        Assert.Equal(work.Left, clamped.Left);
        Assert.Equal(work.Top, clamped.Top);
        Assert.Equal(work.Width, clamped.Width);
        Assert.Equal(work.Height, clamped.Height);
    }

    [Fact]
    public void ClampToWorkArea_Leaves_Rects_Alone_When_Either_Side_Is_Empty()
    {
        var empty = default(NativeMethods.RECT);
        Assert.True(DisplayRules.ClampToWorkArea(empty, Rect(0, 0, 1920, 1080)).IsEmpty);

        var rect = Rect(10, 10, 100, 100);
        Assert.Equal(rect, DisplayRules.ClampToWorkArea(rect, empty));
    }

    [Fact]
    public void NextRotation_Picks_Next_Older_Group_Then_Wraps()
    {
        int[] descending = { 9, 7, 4, 2 };

        Assert.Equal(7, DisplayRules.NextRotation(descending, 9));
        Assert.Equal(2, DisplayRules.NextRotation(descending, 4));
        // 已经是最旧一组：回到最新，形成轮转。
        Assert.Equal(9, DisplayRules.NextRotation(descending, 2));
        // 指针失效（比如那组已经被删掉）：从最新开始。
        Assert.Equal(9, DisplayRules.NextRotation(descending, 100));
        Assert.Equal(0, DisplayRules.NextRotation(Array.Empty<int>(), 5));
    }

    [Fact]
    public void MonitorOf_Prefers_Saved_Device_Over_Rect()
    {
        var window = new SavedWindow
        {
            Left = 5000, Top = 5000, Width = 800, Height = 600,
            MonitorDevice = @"\\.\DISPLAY2"
        };
        Assert.Equal(@"\\.\DISPLAY2", DisplayRules.MonitorOf(window));
    }

    [Fact]
    public void MonitorOf_Returns_Null_When_Nothing_Is_Known()
    {
        var window = new SavedWindow { MonitorDevice = null, Width = 0, Height = 0 };
        Assert.Null(DisplayRules.MonitorOf(window));
        Assert.Null(DisplayRules.MonitorOf(Array.Empty<SavedWindow>()));
    }

    [Fact]
    public void PickRestoreCandidate_Prefers_Same_Title_On_Target_Screen()
    {
        // 同程序两个窗口：枚举顺序是 B、A，保存项要 A。旧实现取第一个，会把 A 摆到 B 的位置。
        var candidates = new[]
        {
            Candidate(1, "B", @"\\.\DISPLAY1", 1920, 0),
            Candidate(2, "A", @"\\.\DISPLAY1", 0, 0)
        };
        var saved = Saved("A", @"\\.\DISPLAY1", 0, 0, 900, 700);

        Assert.Equal(1, DisplayRules.PickRestoreCandidate(candidates, saved));
    }

    [Fact]
    public void PickRestoreCandidate_Moves_Title_Match_Back_From_Other_Screen()
    {
        // 程序重启后系统把窗口开在主屏：标题相同就认它，按屏过滤会得到「没有窗口可摆」。
        var candidates = new[] { Candidate(7, "A", @"\\.\DISPLAY1", 100, 100) };
        var saved = Saved("A", @"\\.\DISPLAY2", 2100, 100, 900, 700);

        Assert.Equal(0, DisplayRules.PickRestoreCandidate(candidates, saved));
    }

    [Fact]
    public void PickRestoreCandidate_Falls_Back_To_Nearest_Rect()
    {
        var candidates = new[]
        {
            Candidate(1, "别的标题", @"\\.\DISPLAY2", 3000, 500),
            Candidate(2, "另一个标题", @"\\.\DISPLAY2", 2200, 120)
        };
        var saved = Saved("期望标题", @"\\.\DISPLAY2", 2100, 100, 900, 700);

        Assert.Equal(1, DisplayRules.PickRestoreCandidate(candidates, saved));
        Assert.Equal(-1, DisplayRules.PickRestoreCandidate(Array.Empty<DisplayRules.RestoreCandidate>(), saved));
    }

    private static DisplayRules.RestoreCandidate Candidate(
        long handle, string title, string device, int left, int top)
        => new()
        {
            Handle = handle,
            Title = title,
            MonitorDevice = device,
            Rect = Rect(left, top, 900, 700)
        };

    private static SavedWindow Saved(string title, string device, int left, int top, int width, int height)
        => new()
        {
            Title = title,
            MonitorDevice = device,
            Left = left,
            Top = top,
            Width = width,
            Height = height
        };

    [Fact]
    public void TryRepairLegacyRect_Fixes_Secondary_Monitor_Offset()
    {
        // 1.6.4 及更早：副屏矩形被多加了一次显示器原点。旧数据 (4020,100)-(5020,800)
        // 其实是副屏上的 (2100,100)-(3100,800)。
        var bounds = Rect(1920, 0, 1920, 1080);
        var legacy = Rect(4020, 100, 1000, 700);

        Assert.True(DisplayRules.TryRepairLegacyRect(legacy, bounds, out var repaired));
        Assert.Equal(Rect(2100, 100, 1000, 700), repaired);
    }

    [Fact]
    public void TryRepairLegacyRect_Leaves_Valid_Secondary_Rect_Alone()
    {
        var bounds = Rect(1920, 0, 1920, 1080);
        Assert.False(DisplayRules.TryRepairLegacyRect(Rect(2100, 100, 1000, 700), bounds, out _));
    }

    [Fact]
    public void TryRepairLegacyRect_Leaves_Straddling_Window_Alone()
    {
        // 窗口右半边在桌面外、左半边在副屏：这是用户故意摆的位置，不能当成旧数据平移。
        var bounds = Rect(1920, 0, 1920, 1080);
        Assert.False(DisplayRules.TryRepairLegacyRect(Rect(3300, 100, 2000, 700), bounds, out _));
    }

    [Fact]
    public void TryRepairLegacyRect_Does_Not_Touch_Primary_Monitor_Rects()
    {
        // 主屏原点为 (0,0)，旧数据本来就是对的：任何合法矩形都不能被搬走。
        var bounds = Rect(0, 0, 1920, 1080);
        Assert.False(DisplayRules.TryRepairLegacyRect(Rect(100, 100, 900, 700), bounds, out _));
    }
}
