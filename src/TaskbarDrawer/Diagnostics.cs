using System.IO;
using System.Text;
using TaskbarDrawer.Core;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer;

/// <summary>
/// 诊断日志：默认写在<b>程序所在目录</b>的 logs\app-yyyyMMdd.log（按天一个文件）。
/// 程序目录不可写（例如装在 Program Files 又没提权）时退回
/// %LOCALAPPDATA%\TaskbarDrawer\logs，并在首行说明实际路径。
/// 超过 1 天的旧日志在启动与跨天时自动删除；写日志失败保持静默，绝不影响主流程。
/// </summary>
public static class Diagnostics
{
    private static readonly object WriteLock = new();
    private static Action _rescue;
    private static string _folder;
    private static string _day;

    /// <summary>日志目录；首次使用时探测可写性（程序目录优先）。</summary>
    public static string Folder
    {
        get
        {
            lock (WriteLock)
            {
                return _folder ??= ResolveFolder();
            }
        }
    }

    public static void Init()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            RunRescue();
            Write("unhandled", e.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Write("unobserved", e.Exception);
        };

        // 先落一行启动横幅再打环境：用户复测后只需要把这一个文件发回来。
        Info("app", $"==== 启动 {Version()} pid={Environment.ProcessId} 提权={NativeMethods.IsCurrentProcessElevated} ====");
        Info("app", $"程序目录={AppContext.BaseDirectory} 日志目录={Folder} 参数={string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");
        Info("app", $"OS={Environment.OSVersion} 64位={Environment.Is64BitProcess} CPU={Environment.ProcessorCount} 屏幕={DescribeScreens()}");
    }

    public static void RegisterRescue(Action rescue) => _rescue = rescue;

    public static void HandleDispatcherException(Exception exception)
    {
        RunRescue();
        Write("dispatcher", exception);
    }

    private static void RunRescue()
    {
        try { _rescue?.Invoke(); } catch { /* 崩溃路径不再抛异常 */ }
    }

    /// <summary>记录一条关键决策（启动 / 收纳 / 恢复 / 展开 / 单窗口）。</summary>
    public static void Info(string scope, string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        Append($"[{scope}] {message}");
    }

    /// <summary>记录一次「已就地隔离」的局部异常：调用方会继续运行，这里只负责留证据。</summary>
    public static void Write(string scope, Exception exception)
    {
        if (exception == null) return;
        Append($"[{scope}] {exception}");
    }

    private static void Append(string line)
    {
        lock (WriteLock)
        {
            try
            {
                string folder = _folder ??= ResolveFolder();
                string day = DateTime.Now.ToString("yyyyMMdd");
                if (!string.Equals(day, _day, StringComparison.Ordinal))
                {
                    // 跨天：换新文件，顺手清掉超过 1 天的旧日志（一天只做一次）。
                    _day = day;
                    DeleteExpired(folder);
                }

                string file = Path.Combine(folder, $"app-{day}.log");
                File.AppendAllText(file,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId:D3}] {line}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
                // 日志写不进去时保持静默：这条路径可能在处理异常，不能反过来把进程带走。
            }
        }
    }

    /// <summary>只保留最近 1 天的日志文件；更早的整份删除（含旧版 error-*.log）。</summary>
    private static void DeleteExpired(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            DateTime cutoff = DateTime.Now.AddDays(-1);
            foreach (var file in new DirectoryInfo(folder).GetFiles("*.log"))
            {
                try
                {
                    if (file.LastWriteTime < cutoff) file.Delete();
                }
                catch
                {
                    // 正被别的实例 / 查看器占用：下一轮再删。
                }
            }
        }
        catch
        {
            // 目录读不动不影响写当前日志。
        }
    }

    /// <summary>程序目录优先，不可写时退回 LocalAppData；返回实际选中的目录。</summary>
    private static string ResolveFolder()
    {
        string local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarDrawer", "logs");
        foreach (string candidate in new[] { Path.Combine(AppContext.BaseDirectory, "logs"), local })
        {
            if (CanWrite(candidate)) return candidate;
        }
        return local;
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Version()
    {
        var attribute = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
            typeof(Diagnostics).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
        return string.IsNullOrWhiteSpace(attribute?.InformationalVersion) ? "unknown" : attribute.InformationalVersion;
    }

    /// <summary>显示器布局：设备名 / 全屏矩形 / 工作区 / DPI / 是否主屏。跨屏问题先看这一行。</summary>
    private static string DescribeScreens()
    {
        var parts = new List<string>();
        try
        {
            foreach (var monitor in NativeMethods.AllMonitors())
            {
                parts.Add($"{monitor.szDevice} bounds={monitor.rcMonitor} work={monitor.rcWork} " +
                          $"dpi={NativeMethods.DpiOfDevice(monitor.szDevice)} primary={NativeMethods.IsPrimary(monitor)}");
            }
        }
        catch
        {
            // 显示器信息只用于诊断，读不到就少一行。
        }
        return parts.Count == 0 ? "(未读到)" : string.Join(" | ", parts);
    }
}
