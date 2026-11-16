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
}
