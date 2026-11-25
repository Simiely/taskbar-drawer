using System.Diagnostics;

namespace TaskbarDrawer.Core;

/// <summary>
/// 单窗口模式的排除项：命中的应用不参与单窗口模式——它成为前台时不收起场景、
/// 也不因它最小化别的窗口；它自己的窗口同样不会被最小化。
///
/// 内置项只有资源管理器（默认勾选、不可删除）。其余项按应用记录：
/// 优先用 exe 全路径，读不到路径时退回进程文件名，这样程序关掉重开仍然有效。
/// </summary>
public sealed class WindowExclusion
{
    /// <summary>内置项（资源管理器）：不可删除，只能勾选 / 取消。</summary>
    public bool BuiltIn { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>exe 全路径；读不到时为 ""。</summary>
    public string Path { get; set; } = "";

    /// <summary>进程文件名（带 .exe），路径读不到时用它兜底匹配。</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>列表里显示的名字。</summary>
    public string DisplayName { get; set; } = "";

    public const string ExplorerProcess = "explorer.exe";

    /// <summary>内置的资源管理器排除项。</summary>
    public static WindowExclusion Explorer(bool enabled = true) => new()
    {
        BuiltIn = true,
        Enabled = enabled,
        ProcessName = ExplorerProcess,
        DisplayName = "资源管理器"
    };

    /// <summary>按应用建一条用户排除项（path / processName 至少有一个非空）。</summary>
    public static WindowExclusion ForApp(string path, string processName)
    {
        string name = NormalizeProcessName(processName);
        string full = (path ?? "").Trim();
        if (name.Length == 0) name = FileNameOf(full);
        return new WindowExclusion
        {
            Enabled = true,
            Path = full,
            ProcessName = name,
            DisplayName = DisplayNameOf(full, name)
        };
    }

    /// <summary>
    /// 把任意来源的列表整理成可落盘、可比较的形式：
    /// 资源管理器固定排第一（不可删除），空项 / 重复项丢掉，显示名与文件名补齐。
    /// </summary>
    /// <param name="explorerEnabledWhenMissing">没有内置项时资源管理器的默认勾选状态（旧配置迁移用）。</param>
    public static List<WindowExclusion> Normalize(
        IEnumerable<WindowExclusion> rules, bool explorerEnabledWhenMissing = true)
    {
        var source = rules?.Where(rule => rule != null).ToArray() ?? Array.Empty<WindowExclusion>();
        var builtIn = source.FirstOrDefault(rule => rule.BuiltIn);
        var result = new List<WindowExclusion>
        {
            Explorer(builtIn?.Enabled ?? explorerEnabledWhenMissing)
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in source)
        {
            if (rule.BuiltIn) continue;

            string path = (rule.Path ?? "").Trim();
            string name = NormalizeProcessName(rule.ProcessName);
            if (name.Length == 0) name = FileNameOf(path);
            if (path.Length == 0 && name.Length == 0) continue;

            string key = path.Length > 0
                ? WindowIdentity.NormalizePath(path)
                : name.ToLowerInvariant();
            if (!seen.Add(key)) continue;

            result.Add(new WindowExclusion
            {
                Enabled = rule.Enabled,
                Path = path,
                ProcessName = name,
                DisplayName = string.IsNullOrWhiteSpace(rule.DisplayName)
                    ? DisplayNameOf(path, name)
                    : rule.DisplayName.Trim()
            });
        }
        return result;
    }

    /// <summary>
    /// 这套规则是否命中一个窗口。前台判定与候选最小化共用这一条，避免两处语义分叉。
    /// </summary>
    public static bool MatchesAny(
        IReadOnlyList<WindowExclusion> rules, bool explorerWindow, string exePath, string processName)
    {
        if (rules == null || rules.Count == 0) return false;

        string path = WindowIdentity.NormalizePath(exePath);
        string name = NormalizeProcessName(processName);
        if (path.Length > 0 && name.Length == 0) name = FileNameOf(path);

        foreach (var rule in rules)
        {
            if (rule == null || !rule.Enabled) continue;

            // 资源管理器按窗口类名认（Win11 的文件夹窗口是 CabinetWClass）：
            // explorer.exe 还画着桌面 / 任务栏，那些窗口在枚举前就已经被排除了。
            if (rule.BuiltIn)
            {
                if (explorerWindow) return true;
                continue;
            }

            string rulePath = WindowIdentity.NormalizePath(rule.Path);
            if (rulePath.Length > 0 && path.Length > 0)
            {
                // 两边路径都读得到：只按全路径比。同名的另一个程序不是同一个应用。
                if (string.Equals(rulePath, path, StringComparison.Ordinal)) return true;
                continue;
            }

            // 至少有一边读不到全路径（管理员进程等）：退回进程文件名。
            string ruleName = NormalizeProcessName(rule.ProcessName);
            if (ruleName.Length == 0) ruleName = FileNameOf(rule.Path);
            if (ruleName.Length > 0 && name.Length > 0 &&
                string.Equals(ruleName, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 排除列表里是否已经有这个应用（忽略勾选状态，只看应用本身）。
    /// 用于「添加已打开的窗口」去重：关掉的项再点一次添加不应产生第二条。
    /// </summary>
    public static bool ContainsApp(
        IReadOnlyList<WindowExclusion> rules, string exePath, string processName)
    {
        string path = WindowIdentity.NormalizePath(exePath);
        string name = NormalizeProcessName(processName);
        if (name.Length == 0) name = FileNameOf(path);
        if (string.Equals(name, ExplorerProcess, StringComparison.OrdinalIgnoreCase)) return true;

        if (rules == null) return false;
        foreach (var rule in rules)
        {
            if (rule == null || rule.BuiltIn) continue;

            string rulePath = WindowIdentity.NormalizePath(rule.Path);
            if (rulePath.Length > 0 && path.Length > 0)
            {
                if (string.Equals(rulePath, path, StringComparison.Ordinal)) return true;
                continue;
            }

            string ruleName = NormalizeProcessName(rule.ProcessName);
            if (ruleName.Length == 0) ruleName = FileNameOf(rule.Path);
            if (ruleName.Length > 0 &&
                string.Equals(ruleName, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 读取进程的 exe 全路径与文件名。全路径受权限限制（管理员进程读不到），
    /// 文件名仍然拿得到，匹配时用文件名兜底。
    /// </summary>
    public static (string Path, string Name) ProcessInfoOf(uint pid)
    {
        if (pid == 0) return ("", "");
        try
        {
            using var process = Process.GetProcessById((int)pid);
            string name = NormalizeProcessName(process.ProcessName);
            string path = "";
            try { path = process.MainModule?.FileName ?? ""; } catch { /* 权限不足，只留文件名 */ }
            return (path, name);
        }
        catch
        {
            return ("", "");
        }
    }

    /// <summary>进程文件名规范化：去掉目录、补 .exe、统一小写（"C:\x\Chrome" → "chrome.exe"）。</summary>
    public static string NormalizeProcessName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        string file = name.Trim();
        int slash = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
        if (slash >= 0) file = file[(slash + 1)..];
        if (file.Length == 0) return "";
        return file.Contains('.') ? file.ToLowerInvariant() : file.ToLowerInvariant() + ".exe";
    }

    /// <summary>全路径里的文件名（小写）；空路径返回空串。</summary>
    public static string FileNameOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return System.IO.Path.GetFileName(path.Trim()).ToLowerInvariant(); }
        catch { return ""; }
    }

    /// <summary>列表显示名：exe 文件名去掉扩展名（保留原始大小写）；都取不到时用进程名。</summary>
    public static string DisplayNameOf(string path, string processName)
    {
        string file = "";
        if (!string.IsNullOrWhiteSpace(path))
        {
            try { file = System.IO.Path.GetFileName(path.Trim()); } catch { /* 非法路径按空处理 */ }
        }
        if (file.Length == 0)
        {
            string raw = (processName ?? "").Trim();
            int slash = Math.Max(raw.LastIndexOf('\\'), raw.LastIndexOf('/'));
            if (slash >= 0) raw = raw[(slash + 1)..];
            file = raw;
        }
        if (file.Length == 0) return "";
        return System.IO.Path.GetFileNameWithoutExtension(file);
    }
}

/// <summary>
/// 排除管理里「添加已打开的窗口」的一条候选：按应用聚合，一个应用一行。
/// </summary>
public sealed class ExclusionCandidate
{
    public string Path { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public string DisplayName { get; init; } = "";

    /// <summary>这个应用此刻打开的窗口数（只用于显示）。</summary>
    public int WindowCount { get; init; }

    /// <summary>已经在排除列表里：按钮禁用，避免重复添加。</summary>
    public bool Excluded { get; init; }
}
