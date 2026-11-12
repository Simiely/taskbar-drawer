using System.IO;

namespace TaskbarDrawer;

/// <summary>
/// 未处理异常：先执行兜底（把收纳里的窗口交回任务栏），再把日志写到
/// %LOCALAPPDATA%\TaskbarDrawer\logs（滚动保留 10 份）。
/// </summary>
public static class Diagnostics
{
    private static readonly object WriteLock = new();
    private static Action _rescue;

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarDrawer", "logs");

    public static void Init()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            RunRescue();
            Write(e.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Write(e.Exception);
        };
    }

    public static void RegisterRescue(Action rescue) => _rescue = rescue;

    public static void HandleDispatcherException(Exception exception)
    {
        RunRescue();
        Write(exception);
    }

    private static void RunRescue()
    {
        try { _rescue?.Invoke(); } catch { /* 崩溃路径不再抛异常 */ }
    }

    /// <summary>
    /// 记录一次「已就地隔离」的局部异常：调用方会继续运行，这里只负责留证据。
    /// 与崩溃日志分开存放（error-*.log / crash-*.log），互不覆盖。
    /// </summary>
    public static void Write(string scope, Exception exception)
    {
        if (exception == null) return;

        // 多个回调可能在不同线程同时报错：串行化，避免滚动删除时互相打架。
        lock (WriteLock)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string file = Path.Combine(Folder, $"error-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
                File.WriteAllText(file,
                    $"{DateTime.Now:O}{Environment.NewLine}[{scope}]{Environment.NewLine}" +
                    $"{exception}{Environment.NewLine}");

                foreach (var old in new DirectoryInfo(Folder).GetFiles("error-*.log")
                             .OrderByDescending(f => f.CreationTime).Skip(10))
                {
                    old.Delete();
                }
            }
            catch
            {
                // 日志写不进去时保持静默：这里已经是在处理异常了。
            }
        }
    }

    private static void Write(Exception exception) => Write("unhandled", exception);
}
