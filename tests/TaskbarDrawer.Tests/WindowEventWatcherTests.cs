using TaskbarDrawer.Core;
using TaskbarDrawer.Interop;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>
/// 窗口事件回调只放行「窗口对象本身」的销毁 / 显示 / 隐藏。
/// 钩子装不进测试进程，但这条过滤规则是纯函数，可以直接锁死：
/// 1) 菜单 / 提示框 / 输入法的 SHOW、HIDE 不能混进来，否则每弹一个菜单都触发一轮对账；
/// 2) 只有这三种事件有意义——位置变化走的是另一条通路（胶囊 z 序）。
/// </summary>
public class WindowEventWatcherTests
{
    private static readonly IntPtr Window = new(0x1234);

    [Fact]
    public void Accepts_Destroy_On_Window_Object()
    {
        Assert.True(WindowEventWatcher.Classify(
            NativeMethods.EVENT_OBJECT_DESTROY, Window, NativeMethods.OBJID_WINDOW, 0, out var kind));
        Assert.Equal(WindowEventKind.Destroy, kind);
    }

    [Fact]
    public void Accepts_Show_And_Hide()
    {
        Assert.True(WindowEventWatcher.Classify(
            NativeMethods.EVENT_OBJECT_SHOW, Window, NativeMethods.OBJID_WINDOW, 0, out var shown));
        Assert.Equal(WindowEventKind.Show, shown);

        Assert.True(WindowEventWatcher.Classify(
            NativeMethods.EVENT_OBJECT_HIDE, Window, NativeMethods.OBJID_WINDOW, 0, out var hidden));
        Assert.Equal(WindowEventKind.Hide, hidden);
    }

    [Fact]
    public void Rejects_Child_Objects()
    {
        // 菜单项 / 光标 / 滚动条：idObject 不是 OBJID_WINDOW，或者是窗口里的子对象（idChild != 0）。
        Assert.False(WindowEventWatcher.Classify(
            NativeMethods.EVENT_OBJECT_SHOW, Window, idObject: -8, idChild: 0, out _));
        Assert.False(WindowEventWatcher.Classify(
            NativeMethods.EVENT_OBJECT_SHOW, Window, NativeMethods.OBJID_WINDOW, idChild: 3, out _));
    }

    [Fact]
    public void Rejects_Null_Window_And_Unrelated_Events()
    {
        Assert.False(WindowEventWatcher.Classify(
            NativeMethods.EVENT_OBJECT_SHOW, IntPtr.Zero, NativeMethods.OBJID_WINDOW, 0, out _));
        // 位置变化由胶囊自己的钩子处理，这条通路不管。
        Assert.False(WindowEventWatcher.Classify(
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, Window, NativeMethods.OBJID_WINDOW, 0, out _));
    }
}
