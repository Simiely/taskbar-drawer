using TaskbarDrawer.Core;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>
/// 「展开场景后该把哪个窗口放到最前」是纯规则：置前动作本身要碰 Win32，
/// 但「选谁」可以脱离窗口测。这里锁死两条语义：
/// 1) 有收纳前的前台窗口就优先用它（用户离开时看的就是它）；
/// 2) 没有记录时退回场景里的第一个可用窗口——不退回就会整组停在 z 序最下面。
/// </summary>
public class SceneFocusRulesTests
{
    private static readonly Func<IntPtr, bool> AllUsable = _ => true;

    [Fact]
    public void Uses_Foreground_Window_Recorded_At_Stash_Time()
    {
        var before = new IntPtr(0x11);
        var restored = new[] { new IntPtr(0x21), new IntPtr(0x22) };

        Assert.Equal(before, SceneFocusRules.Pick(before, restored, AllUsable));
    }

    [Fact]
    public void Falls_Back_To_First_Restored_Window_When_Nothing_Was_Recorded()
    {
        // 从快照拉起的场景由 Adopt 现场建组，ForegroundBefore 是 0：
        // 这条兜底就是「打开场景后窗口在最下面」的修复点。
        var restored = new[] { new IntPtr(0x31), new IntPtr(0x32) };

        Assert.Equal(new IntPtr(0x31), SceneFocusRules.Pick(IntPtr.Zero, restored, AllUsable));
    }

    [Fact]
    public void Skips_Recorded_Window_That_Is_No_Longer_Usable()
    {
        var before = new IntPtr(0x41);
        var restored = new[] { new IntPtr(0x51), new IntPtr(0x52) };
        Func<IntPtr, bool> usable = hwnd => hwnd != before;

        Assert.Equal(new IntPtr(0x51), SceneFocusRules.Pick(before, restored, usable));
    }

    [Fact]
    public void Returns_Zero_When_No_Window_Is_Usable()
    {
        var restored = new[] { new IntPtr(0x61) };

        Assert.Equal(IntPtr.Zero, SceneFocusRules.Pick(IntPtr.Zero, restored, _ => false));
        Assert.Equal(IntPtr.Zero, SceneFocusRules.Pick(IntPtr.Zero, Array.Empty<IntPtr>(), AllUsable));
        Assert.Equal(IntPtr.Zero, SceneFocusRules.Pick(IntPtr.Zero, null, AllUsable));
    }

    [Fact]
    public void Tolerates_Missing_Eligibility_Check()
    {
        // 传 null 时宁可什么都不做，也不能把窗口句柄当激活目标乱用。
        Assert.Equal(IntPtr.Zero, SceneFocusRules.Pick(new IntPtr(0x71), new[] { new IntPtr(0x72) }, null));
    }
}
