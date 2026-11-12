namespace TaskbarDrawer.Core;

/// <summary>
/// 过滤输入。单独抽成纯结构，便于用固定数据做回归，不依赖真实窗口。
/// </summary>
public struct WindowFacts
{
    public bool Visible;
    public bool Cloaked;
    public bool ToolWindow;
    public bool OwnProcess;
    public bool HasTitle;
    public bool HasArea;

    public bool IsCandidate
        => Visible && !Cloaked && !ToolWindow && !OwnProcess && HasTitle && HasArea;
}

/// <summary>
/// 过滤链的唯一实现（规则来自 WindowDock 真机实测）：
/// `IsWindowVisible && !IsCloaked && !WS_EX_TOOLWINDOW && pid != self && title.Length>0 && 有面积`。
/// </summary>
public static class WindowFilter
{
    public static bool IsCandidate(in WindowFacts facts) => facts.IsCandidate;
}

public static class WindowIdentity
{
    /// <summary>程序身份：优先用规范化 exe 全路径；取不到路径时退回进程名。</summary>
    public static string FromProcess(string processPath, string processName)
    {
        if (!string.IsNullOrWhiteSpace(processPath))
            return "exe:" + processPath.Replace('\\', '/').ToLowerInvariant();
        return "name:" + (processName ?? "").ToLowerInvariant();
    }
}
