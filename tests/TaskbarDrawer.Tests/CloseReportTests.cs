using TaskbarDrawer.Core;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>
/// 关闭场景的提示必须按「实际关掉几个」说，不能按「发了几个 WM_CLOSE」说。
/// 三种分支各锁一条：全关掉、部分还在等、一个都没投出去。
/// </summary>
public class CloseReportTests
{
    private static CloseReport Measure(int handlersCount, int alive)
    {
        var handles = new List<IntPtr>();
        for (int i = 0; i < handlersCount; i++) handles.Add(new IntPtr(0x100 + i));
        // 前 alive 个当作还活着。
        return CloseReport.Measure(handles, hwnd => hwnd.ToInt64() - 0x100 < alive);
    }

    [Fact]
    public void All_Closed_Reports_Plain_Count()
    {
        var report = Measure(3, alive: 0);

        Assert.Equal(3, report.Requested);
        Assert.Equal(3, report.Closed);
        Assert.Equal(0, report.Waiting);
        Assert.Equal("已关闭 3 个窗口", report.DescribeGroup());
        Assert.Equal("🔴已关闭「写文档」· 3 个窗口", report.DescribeScene("写文档", "🔴"));
    }

    [Fact]
    public void Still_Running_Windows_Are_Reported_As_Waiting()
    {
        // 目标程序弹了保存框：三个请求都发出去过，但只有两个真的关了。
        var report = Measure(3, alive: 1);

        Assert.Equal(3, report.Requested);
        Assert.Equal(2, report.Closed);
        Assert.Equal(1, report.Waiting);
        Assert.Equal("已关闭 2 个窗口，1 个仍在等待响应", report.DescribeGroup());
        Assert.Equal("已关闭「写文档」· 2 个窗口，1 个仍在等待响应", report.DescribeScene("写文档", null));
    }

    [Fact]
    public void Nothing_Requested_Says_So_Instead_Of_Zero()
    {
        var report = Measure(0, alive: 0);

        Assert.Equal(0, report.Requested);
        Assert.Equal("没有可关闭的窗口", report.DescribeGroup());
        Assert.Equal("「写文档」没有可关闭的窗口", report.DescribeScene("写文档", "🔴"));
    }

    [Fact]
    public void Closed_Count_Never_Exceeds_Requested()
    {
        // 防御：传进来一个矛盾的数（关了 5 个但只发了 3 个请求）不能算出负数等待数。
        var report = new CloseReport(3, 5);

        Assert.Equal(3, report.Closed);
        Assert.Equal(0, report.Waiting);
    }
}
