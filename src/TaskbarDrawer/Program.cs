using System.Text;
using System.Windows;
using TaskbarDrawer.Core;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer;

public static class Program
{
    private const string MutexName = @"Local\TaskbarDrawer.SingleInstance";

    /// <summary>第二个实例通过 Set 这个命名事件，让已运行的实例弹出主面板。</summary>
    public const string ShowPanelEventName = @"Local\TaskbarDrawer.ShowPanel";

    [STAThread]
    public static int Main(string[] args)
    {
        string first = args.Length > 0 ? args[0] : "";

        if (first is "--probe" or "--help" or "-h" or "/?")
        {
            AttachConsoleForOutput();
            return first == "--probe" ? RunProbe() : PrintUsage();
        }

        Diagnostics.Init();

        // 「以管理员身份重启」时旧实例还没退干净：等它释放单实例互斥体再接管。
        // 否则新实例会把自己当成「第二次启动」，唤起旧实例后自己退出，看起来像重启失败。
        bool elevatedRestart = first is "--elevated-restart";
        var mutex = AcquireSingleInstance(elevatedRestart, out bool createdNew);
        if (!createdNew)
        {
            mutex?.Dispose();
            AttachConsoleForOutput();
            Console.WriteLine("任务栏收纳已在运行，已唤起主面板。");
            SignalExistingInstance();
            return 0;
        }

        using (mutex)
        {
            // 常驻程序：只在托盘 + 任务栏小条里出现，主线程跑 WPF 消息循环。
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var shell = new AppShell();
            Diagnostics.RegisterRescue(shell.EmergencyRelease);
            app.DispatcherUnhandledException += (_, e) =>
            {
                Diagnostics.HandleDispatcherException(e.Exception);
                e.Handled = true;
                Environment.Exit(1);
            };

            shell.Initialize();
            return app.Run();
        }
    }

    /// <summary>
    /// 取单实例互斥体。waitForPrevious 为 true（提权重启）时最多等 10 秒，
    /// 让旧实例走完退出流程；普通启动只试一次，拿到已存在就返回 false。
    /// </summary>
    private static Mutex AcquireSingleInstance(bool waitForPrevious, out bool createdNew)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(waitForPrevious ? 10000 : 0);
        while (true)
        {
            var mutex = new Mutex(true, MutexName, out createdNew);
            if (createdNew) return mutex;
            mutex.Dispose();
            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(100);
        }
    }

    /// <summary>通知已运行的实例打开主面板（不依赖窗口广播，消息专用窗口收不到 HWND_BROADCAST）。</summary>
    private static void SignalExistingInstance()
    {
        // 已运行实例可能在启动早期还没创建事件：短暂重试，避免第二次启动静默无反应。
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowPanelEventName, out var signal))
                {
                    using (signal) signal.Set();
                    return;
                }
            }
            catch
            {
                // 唤起失败不影响「已在运行」这一事实。
                return;
            }

            Thread.Sleep(100);
        }
    }

    /// <summary>WinExe 没有自带控制台：挂到调用方的控制台上，让 --probe 的输出可见。</summary>
    private static void AttachConsoleForOutput()
    {
        try
        {
            NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 没有父控制台（例如从资源管理器启动）时忽略。
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine("任务栏收纳 TaskbarDrawer");
        Console.WriteLine("  TaskbarDrawer.exe             启动（常驻托盘；再次启动会唤起主面板）");
        Console.WriteLine("  TaskbarDrawer.exe --probe     只读打印候选窗口，不移动 / 不隐藏任何窗口");
        Console.WriteLine("  Ctrl+Alt+Shift+D               新建场景（收起当前显示器上的窗口）/ 没窗口时展开最近的场景");
        Console.WriteLine("  左键单击托盘图标               切换单窗口模式（双击打开主面板）");
        return 0;
    }

    private static int RunProbe()
    {
        var list = WindowEnumerator.Enumerate(includeMinimized: false);
        Console.WriteLine($"候选窗口 {list.Count} 个（可见 / 有标题 / 非工具窗 / 非 cloaked / 非本程序 / 非 shell）");
        Console.WriteLine("句柄        PID     类名                          矩形(屏幕坐标)             标题");
        foreach (var item in list.OrderBy(w => w.Rect.Left).ThenBy(w => w.Rect.Top))
        {
            string title = item.Title.Length > 40 ? item.Title[..40] + "…" : item.Title;
            Console.WriteLine(
                $"0x{item.Handle.ToInt64():X8}  {item.ProcessId,-6}  {item.ClassName,-28}  " +
                $"{item.Rect.Left},{item.Rect.Top} {item.Rect.Width}x{item.Rect.Height,-10}  {title}");
        }
        return 0;
    }
}
