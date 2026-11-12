using System.IO;
using System.Text.Json;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>
/// 孤儿窗口回收。
/// 正常退出和程序崩溃都有 ReleaseAll 兜底，但「任务管理器结束进程」和断电没有机会执行任何代码，
/// 被 SW_HIDE 的窗口会停在「不在屏幕、也不在任务栏」的状态。
/// 所以把当前仍被隐藏的窗口写进一个小文件；下次启动时，仍然隐藏且身份对得上的窗口直接交回任务栏。
/// </summary>
public static class WindowRecovery
{
    private sealed class Entry
    {
        public long Handle { get; set; }
        public string Title { get; set; }
        public uint ProcessId { get; set; }
        public long ProcessStartTicks { get; set; }
    }

    private static string FilePath => Path.Combine(AppSettings.Folder, "recovery.json");

    private static string _lastSignature;

    /// <summary>
    /// 记录当前仍被隐藏的窗口。隐藏集合没变化就不写盘，避免每 2.5 s 一次无意义 IO。
    /// </summary>
    public static void Save(IReadOnlyCollection<StashedWindow> items)
    {
        var hidden = items.Where(i => i.Hidden).ToArray();
        string signature = string.Join(",", hidden.Select(i => i.Handle.ToInt64()).OrderBy(v => v));
        if (signature == _lastSignature) return;
        _lastSignature = signature;

        try
        {
            Directory.CreateDirectory(AppSettings.Folder);

            if (hidden.Length == 0)
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                return;
            }

            var entries = hidden.Select(i => new Entry
            {
                Handle = i.Handle.ToInt64(),
                Title = i.Title,
                ProcessId = i.ProcessId,
                ProcessStartTicks = i.ProcessStartTime?.Ticks ?? 0
            }).ToArray();

            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries));
            File.Move(temp, FilePath, true);
        }
        catch
        {
            // 落盘失败不影响收纳本身：最坏情况只是这一次强杀救不回来。
        }
    }

    /// <summary>
    /// 启动时回收上次遗留的隐藏窗口：仍然存在、进程身份和标题都对得上、且仍然不可见的才动。
    /// 句柄会被系统复用，所以不能只看句柄。
    /// </summary>
    public static int Recover()
    {
        if (!File.Exists(FilePath)) return 0;

        int recovered = 0;
        try
        {
            var entries = JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(FilePath));
            foreach (var entry in entries ?? Array.Empty<Entry>())
            {
                var hwnd = new IntPtr(entry.Handle);
                if (!NativeMethods.IsWindow(hwnd)) continue;

                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (entry.ProcessId != 0 && pid != entry.ProcessId) continue;
                if (!string.Equals(NativeMethods.Title(hwnd), entry.Title, StringComparison.Ordinal)) continue;

                // 已经可见：它自己回来了或用户已处理，不要再碰。
                if (NativeMethods.IsWindowVisible(hwnd)) continue;

                // 显示成最小化：窗口回到任务栏，但不抢焦点、不打断开机流程。
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWMINNOACTIVE);
                recovered++;
            }
        }
        catch
        {
            // 文件损坏 / 无权限：按「没有恢复记录」处理。
        }

        Clear();
        return recovered;
    }

    /// <summary>正常退出时调用：窗口已被主动放回任务栏，回收记录不再需要。</summary>
    public static void Clear()
    {
        _lastSignature = null;
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch
        {
            // 删不掉也不影响下次启动：下次会用可见性校验跳过已还原的窗口。
        }
    }
}
