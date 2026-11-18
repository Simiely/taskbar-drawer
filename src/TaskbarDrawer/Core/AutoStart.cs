using Microsoft.Win32;

namespace TaskbarDrawer.Core;

/// <summary>开机自启：写 HKCU\...\Run（设置项仍是事实来源，写入失败如实回滚）。</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TaskbarDrawer";

    private static string Command
    {
        get
        {
            string exe = Environment.ProcessPath;
            return string.IsNullOrEmpty(exe) ? null : $"\"{exe}\" --autostart";
        }
    }

    public static bool IsSupported => !string.IsNullOrEmpty(Environment.ProcessPath);

    /// <summary>
    /// 是否已加入开机自启。
    /// 只看「这个值存在」——命令里含 exe 绝对路径，程序换目录 / 升级到新版本目录后字符串就对不上，
    /// 用全等比较会把「已自启」显示成「未开启」，而注册表里那条旧命令仍会在开机时拉起旧副本。
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把 Run 项刷新成当前 exe 路径：旧路径残留时开机拉起的是旧副本（用户升过级就会遇到）。
    /// 只在已经有这个值时才动，不会替用户打开自启。
    /// </summary>
    public static void Repair()
    {
        if (!IsSupported) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not string value) return;
            string command = Command;
            if (command != null && value != command) key.SetValue(ValueName, command);
        }
        catch
        {
            // 修不了不影响本次运行，下次启动再试。
        }
    }

    /// <summary>设置自启，返回写入后的真实状态（失败时保持原样）。</summary>
    public static bool Set(bool enabled)
    {
        if (!IsSupported) return false;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return false;
            if (enabled) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, false);
            return IsEnabled() == enabled;
        }
        catch
        {
            return IsEnabled();
        }
    }
}
