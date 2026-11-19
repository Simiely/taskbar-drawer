using TaskbarDrawer.Core;
using TaskbarDrawer.Interop;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>
/// 两层窗口身份：位次计算与配对优先级。
/// 第 1 级（同应用 + 同类名 + 同位次）是「浏览器切过标签页也能认回同一个窗口」的修复点，
/// 所以这里刻意构造「标题已经对不上、位置和位次对得上」的用例，锁死它优先于标题。
/// </summary>
public class WindowIdentityTests
{
    private const string Chrome = @"C:\Program Files\Chrome\chrome.exe";
    private const string ChromeClass = "Chrome_WidgetWin_1";

    [Fact]
    public void AppId_Uses_Exe_When_No_Aumid_And_Normalizes_Path()
    {
        Assert.Equal("exe:c:/program files/chrome/chrome.exe", WindowIdentity.AppId(null, Chrome));
        // 反斜杠 / 大小写不同但指向同一个程序：必须归一成同一个 L1。
        Assert.Equal(
            WindowIdentity.AppId(null, Chrome),
            WindowIdentity.AppId(null, @"c:/program files/chrome/chrome.exe"));
    }

    [Fact]
    public void AppId_Prefers_Aumid_And_Is_Empty_Without_Any_Identity()
    {
        Assert.Equal("aumid:Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
            WindowIdentity.AppId("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", Chrome));
        Assert.Equal("", WindowIdentity.AppId(null, null));
        Assert.Equal("", WindowIdentity.AppId("  ", "  "));
    }

    [Fact]
    public void LooseKey_Ignores_Title_Padding_But_Keeps_Class()
    {
        // 追加列表用宽松键判断「已在场景」：标题两端空白不该造成误判，
        // 类名不同（主窗口 / 开发者工具）必须区分开。
        Assert.Equal(
            WindowIdentity.LooseKey("exe:c:/a.exe", "ClassA", "  标题 "),
            WindowIdentity.LooseKey("exe:c:/a.exe", "ClassA", "标题"));
        Assert.NotEqual(
            WindowIdentity.LooseKey("exe:c:/a.exe", "ClassA", "标题"),
            WindowIdentity.LooseKey("exe:c:/a.exe", "ClassB", "标题"));
        // 没有 L1 就没有身份：调用方据此退回 exe + 标题键。
        Assert.Equal("", WindowIdentity.LooseKey(null, "ClassA", "标题"));
    }

    [Fact]
    public void AssignOrdinals_Sorts_By_Position_Within_Same_App_And_Class()
    {
        // 枚举顺序（2,1,3）是故意打乱的：位次绝不能跟着枚举顺序走。
        var second = Candidate(2, "乙", 0, 0);
        var first = Candidate(1, "甲", 0, -100);   // 更靠上
        var third = Candidate(3, "丙", 1200, 0);   // 更靠右

        var ordinals = WindowIdentity.AssignOrdinals(new[] { second, first, third });

        Assert.Equal(1, ordinals[2]);
        Assert.Equal(0, ordinals[1]);
        Assert.Equal(2, ordinals[3]);
    }

    [Fact]
    public void AssignOrdinals_Is_Per_Class()
    {
        // 主窗口与开发者工具类名不同：各自从 0 开始，不会互相挤位次。
        var main = Candidate(1, "主窗口", 0, 0);
        var devtools = Candidate(2, "开发者工具", 0, 0, className: "Chrome_DevTools");

        var ordinals = WindowIdentity.AssignOrdinals(new[] { main, devtools });

        Assert.Equal(0, ordinals[1]);
        Assert.Equal(0, ordinals[2]);
    }

    [Fact]
    public void AssignOrdinals_Groups_By_Monitor_Before_Coordinates()
    {
        // 同一应用开在两块屏：保存侧读的是工作区坐标、还原侧读的是屏幕坐标，
        // 直接比 Top / Left 会把两块屏上的窗口排出相反次序。显示器必须排在坐标前面。
        var onSecondary = Candidate(1, "副屏窗口", 0, 100, monitor: @"\\.\DISPLAY2");
        var onPrimary = Candidate(2, "主屏窗口", -1920, 100, monitor: @"\\.\DISPLAY1");

        var ordinals = WindowIdentity.AssignOrdinals(new[] { onSecondary, onPrimary });

        Assert.Equal(0, ordinals[2]);   // 主屏那个先（DISPLAY1 < DISPLAY2）
        Assert.Equal(1, ordinals[1]);
    }

    [Fact]
    public void Pair_Prefers_Ordinal_Over_Title_When_Tab_Was_Switched()
    {
        // 用户切过标签页：两侧标题都变了。位置与位次仍能对上。
        var left = Candidate(1, "切换后的新标题", 0, 0);
        var right = Candidate(2, "另一个窗口", 2000, 0);
        var candidates = new[] { left, right };
        var ordinals = WindowIdentity.AssignOrdinals(candidates);

        var saved = Saved(ordinal: 0, title: "保存时的旧标题", left: 0, top: 0);

        // 旧实现（标题优先）会挑中索引 1 或者退回最近矩形；现在必须按位次挑 0。
        Assert.Equal(0, WindowIdentity.Pair(candidates, saved, ordinals));
    }

    [Fact]
    public void Pair_Uses_Second_Ordinal_For_Second_Saved_Window()
    {
        var candidates = new[]
        {
            Candidate(1, "甲", 0, 0),
            Candidate(2, "乙", 2000, 0)
        };
        var ordinals = WindowIdentity.AssignOrdinals(candidates);

        // 保存侧第二个窗口的位次是 1（右侧那个）：必须还给右侧，不能两个都往左窗塞。
        Assert.Equal(1, WindowIdentity.Pair(candidates, Saved(1, "乙", 2000, 0), ordinals));
    }

    [Fact]
    public void Pair_Falls_Back_To_Title_For_Old_Snapshot_Without_Identity()
    {
        // 旧快照没有 AppId / 类名 / 位次：跳过第 1 级，退回「同标题」的旧行为。
        var candidates = new[]
        {
            Candidate(1, "新标题", 0, 0),
            Candidate(2, "保存时的标题", 2000, 0)
        };
        var ordinals = WindowIdentity.AssignOrdinals(candidates);

        var saved = new SavedWindow
        {
            Title = "保存时的标题",
            Left = 0, Top = 0, Width = 900, Height = 700
        };

        Assert.Equal(1, WindowIdentity.Pair(candidates, saved, ordinals));
    }

    [Fact]
    public void Pair_Falls_Back_To_Title_When_Ordinals_Are_Missing()
    {
        var candidates = new[]
        {
            Candidate(1, "新标题", 0, 0),
            Candidate(2, "保存时的标题", 2000, 0)
        };

        Assert.Equal(1, WindowIdentity.Pair(candidates, Saved(0, "保存时的标题", 0, 0), null!));
    }

    [Fact]
    public void Pair_Requires_Same_Class_For_Ordinal_Match()
    {
        // 同一个程序的不同窗口类（主窗口 / 对话框宿主）：位次相同也不能算同一个窗口。
        var otherClass = Candidate(2, "别的类", 0, 0, className: "Other_Class");
        var candidates = new[] { Candidate(1, "主窗口", 2000, 0), otherClass };
        var ordinals = WindowIdentity.AssignOrdinals(candidates);

        // 位次 0 在两侧各有一个，但只有同类名的才命中；这里保存项是 ChromeClass，
        // 命中 0 号（位次 1）失败后退到标题/矩形，最终仍不该选 otherClass。
        Assert.NotEqual(1, WindowIdentity.Pair(candidates, Saved(0, "谁都不匹配", 0, 0), ordinals));
    }

    [Fact]
    public void Pair_Skips_Cross_Screen_Ordinal_When_Saved_Screen_Has_Same_App_Window()
    {
        // 日志 13:37:17 现场：资源管理器都最小化，位次 0 按显示器名落在主屏；
        // 保存项在副屏，副屏也有同程序窗口——不能因为主屏那个位次对上就把它挪过来。
        var primary = Candidate(1, "窗口", 0, 0, monitor: @"\\.\DISPLAY1");
        var secondary = Candidate(2, "窗口", -602, 0, monitor: @"\\.\DISPLAY2");
        var candidates = new[] { primary, secondary };
        var ordinals = WindowIdentity.AssignOrdinals(candidates);   // 1 → 0，2 → 1

        var saved = Saved(0, "保存时的标题", -602, 0, monitor: @"\\.\DISPLAY2");

        Assert.Equal(1, WindowIdentity.Pair(candidates, saved, ordinals));
    }

    [Fact]
    public void Pair_Keeps_Cross_Screen_Ordinal_When_No_Same_Screen_Candidate()
    {
        // 副屏没有同程序窗口（例如显示器被拔掉）：位次命中仍然有效，不能退化成「找不到」。
        var primary = Candidate(1, "窗口", 0, 0, monitor: @"\\.\DISPLAY1");
        var candidates = new[] { primary };
        var ordinals = WindowIdentity.AssignOrdinals(candidates);

        var saved = Saved(0, "保存时的标题", -602, 0, monitor: @"\\.\DISPLAY2");

        Assert.Equal(0, WindowIdentity.Pair(candidates, saved, ordinals));
    }

    private static DisplayRules.RestoreCandidate Candidate(
        long handle, string title, int left, int top, string className = ChromeClass,
        string? appId = null, string monitor = @"\\.\DISPLAY1")
        => new()
        {
            Handle = handle,
            Title = title,
            MonitorDevice = monitor,
            Rect = NativeMethods.RECT.FromLTRB(left, top, left + 800, top + 600),
            AppId = appId ?? WindowIdentity.AppId(null, Chrome),
            ClassName = className
        };

    private static SavedWindow Saved(int ordinal, string title, int left, int top,
        string monitor = @"\\.\DISPLAY1")
        => new()
        {
            Title = title,
            Left = left,
            Top = top,
            Width = 800,
            Height = 600,
            MonitorDevice = monitor,
            AppId = WindowIdentity.AppId(null, Chrome),
            ClassName = ChromeClass,
            Ordinal = ordinal
        };
}
