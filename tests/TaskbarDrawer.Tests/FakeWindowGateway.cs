#nullable disable

using TaskbarDrawer.Core;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Tests;

/// <summary>
/// 内存里的窗口世界：替代真实 Win32，让 <see cref="StashAllService"/> 的迁移矩阵能在测试进程里跑完。
/// 只实现状态机真正会调的动作，其余细节（图标、DPI、UIPI）不建模——它们由实机夹具验收。
/// </summary>
internal sealed class FakeWindowGateway : IWindowGateway
{
    public const string Display1 = @"\\.\DISPLAY1";
    public const string Display2 = @"\\.\DISPLAY2";

    private readonly Dictionary<long, FakeWindow> _windows = new();

    public sealed class FakeWindow
    {
        public IntPtr Handle { get; set; }
        public string Title { get; set; } = "";
        public string ClassName { get; set; } = "FakeWindowClass";
        public uint ProcessId { get; set; } = 100;
        public string ProcessName { get; set; } = "app";
        public string ProcessPath { get; set; } = @"C:\app.exe";
        public string Monitor { get; set; } = Display1;
        public bool Visible { get; set; } = true;
        public bool ToolWindow { get; set; }
        public bool Cloaked { get; set; }
        public bool Elevated { get; set; }
        public bool Hung { get; set; }
        public bool Minimized { get; set; }
        public NativeMethods.RECT Rect { get; set; } = NativeMethods.RECT.FromLTRB(0, 0, 800, 600);

        /// <summary>这个窗口的 owned 弹窗（收纳时应当一起藏、展开时一起放回）。</summary>
        public List<IntPtr> Popups { get; } = new();
    }

    /// <summary>被置前过的窗口：展开场景后应当有且只有一个。</summary>
    public List<IntPtr> Activated { get; } = new();

    /// <summary>被写过位置的窗口。</summary>
    public List<IntPtr> Applied { get; } = new();

    public IntPtr ForegroundWindow { get; set; }

    public FakeWindow Add(int handle, string title, string monitor = Display1,
        string exe = @"C:\app.exe", uint pid = 100)
    {
        var window = new FakeWindow
        {
            Handle = new IntPtr(handle),
            Title = title,
            Monitor = monitor,
            ProcessPath = exe,
            ProcessId = pid,
            Rect = NativeMethods.RECT.FromLTRB(handle, 0, handle + 800, 600)
        };
        _windows[handle] = window;
        return window;
    }

    /// <summary>模拟窗口被销毁（任务管理器结束进程 / 程序自己退出）。</summary>
    public void Destroy(IntPtr hwnd) => _windows.Remove(hwnd.ToInt64());

    public FakeWindow Get(IntPtr hwnd)
        => _windows.TryGetValue(hwnd.ToInt64(), out var window) ? window : null;

    public bool IsWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && _windows.ContainsKey(hwnd.ToInt64());

    public bool IsVisible(IntPtr hwnd) => Get(hwnd)?.Visible == true;

    public bool IsHung(IntPtr hwnd) => Get(hwnd)?.Hung == true;

    public IntPtr Foreground() => ForegroundWindow;

    public string MonitorOf(IntPtr hwnd) => Get(hwnd)?.Monitor;

    public IReadOnlyList<WindowCandidate> Enumerate(bool includeMinimized)
        => _windows.Values
            .Where(w => w.Visible && !w.Cloaked && !w.ToolWindow && w.Title.Length > 0)
            .Where(w => includeMinimized || !w.Minimized)
            .Select(w => new WindowCandidate
            {
                Handle = w.Handle,
                Title = w.Title,
                ClassName = w.ClassName,
                ProcessId = w.ProcessId,
                Rect = w.Rect
            })
            .ToList();

    public WindowFacts Describe(IntPtr hwnd)
    {
        var window = Get(hwnd);
        if (window == null) return null;

        return new WindowFacts
        {
            Handle = window.Handle,
            Title = window.Title,
            ClassName = window.ClassName,
            ProcessId = window.ProcessId,
            ProcessName = window.ProcessName,
            ProcessPath = window.ProcessPath,
            ProcessStartTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Visible = window.Visible,
            ToolWindow = window.ToolWindow,
            Cloaked = window.Cloaked,
            Elevated = window.Elevated,
            Rect = window.Rect
        };
    }

    public WindowPlacement CapturePlacement(IntPtr hwnd)
    {
        var window = Get(hwnd);
        if (window == null) return default;

        return new WindowPlacement
        {
            Value = new NativeMethods.WINDOWPLACEMENT
            {
                length = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>(),
                showCmd = NativeMethods.SW_SHOWNORMAL,
                rcNormalPosition = window.Rect
            },
            Maximized = false,
            MonitorDevice = window.Monitor
        };
    }

    public void ApplyPlacement(IntPtr hwnd, WindowPlacement placement)
    {
        Applied.Add(hwnd);
        var window = Get(hwnd);
        if (window == null) return;
        window.Rect = placement.Value.rcNormalPosition;
    }

    public void SetVisibility(IntPtr hwnd, WindowVisibility visibility)
    {
        var window = Get(hwnd);
        if (window == null) return;

        bool visible = visibility != WindowVisibility.Hidden;
        window.Visible = visible;
        window.Minimized = visibility == WindowVisibility.Minimized;
    }

    public IReadOnlyList<IntPtr> OwnedPopups(IntPtr hwnd) => Get(hwnd)?.Popups ?? new List<IntPtr>();

    public void ShowOwnedPopup(IntPtr hwnd)
    {
        var window = Get(hwnd);
        if (window == null) return;
        window.Visible = true;
    }

    public void Activate(IntPtr hwnd) => Activated.Add(hwnd);
}
