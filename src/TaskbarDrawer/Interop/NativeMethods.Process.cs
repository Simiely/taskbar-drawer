using System.Runtime.InteropServices;

namespace TaskbarDrawer.Interop;

/// <summary>
/// 进程父子关系：只用来回答「这个窗口的进程是不是某个场景成员的同一进程 / 子进程」。
/// 用 Toolhelp32 快照而不是 WMI：一次枚举拿到全部父子关系，开销在毫秒级，也不需要额外权限。
/// </summary>
public static partial class NativeMethods
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr InvalidSnapshot = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    /// <summary>一个进程的父子关系与可执行文件名（只取文件名，够用来识别 shell / 启动器）。</summary>
    public readonly struct ProcessLink
    {
        public uint ParentId { get; init; }
        public string ExeName { get; init; }
    }

    /// <summary>枚举所有进程的 pid → (父 pid, exe 文件名)。失败时返回空表（调用方按「无关」处理）。</summary>
    public static Dictionary<uint, ProcessLink> ProcessLinks()
    {
        var map = new Dictionary<uint, ProcessLink>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidSnapshot || snapshot == IntPtr.Zero) return map;

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return map;
            do
            {
                if (entry.th32ProcessID == 0) continue;
                map[entry.th32ProcessID] = new ProcessLink
                {
                    ParentId = entry.th32ParentProcessID,
                    ExeName = entry.szExeFile ?? ""
                };
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return map;
    }
}
