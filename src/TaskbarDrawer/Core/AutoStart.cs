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

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value == Command;
        }
        catch
        {
            return false;
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
