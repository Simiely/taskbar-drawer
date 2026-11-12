using System.IO;
using System.Text.Json;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

/// <summary>设置持久化：%LOCALAPPDATA%\TaskbarDrawer\settings.json。</summary>
public sealed class AppSettings
{
    /// <summary>配置版本：v4.1 起只保留一枚热键，旧配置的 Ctrl+Alt+S 会被迁移掉。</summary>
    public int Version { get; set; }

    public bool SingleWindowMode { get; set; }
    public bool AutoStart { get; set; }
    public bool ShowedWelcome { get; set; }

    /// <summary>主面板上次关闭时的位置（DIP）。HasPanelPosition 为 false 时按光标所在屏右下角弹出。</summary>
    public bool HasPanelPosition { get; set; }
    public double PanelLeft { get; set; }
    public double PanelTop { get; set; }

    public uint HotkeyModifiers { get; set; } =
        NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;
    public uint HotkeyVirtualKey { get; set; } = NativeMethods.VK_D;

    private const int CurrentVersion = 2;

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarDrawer");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
            if (settings == null) return new AppSettings();

            // 旧版本存的是 Ctrl+Alt+S（用户反馈不生效），升级到 Ctrl+Alt+Shift+D。
            if (settings.Version < CurrentVersion)
            {
                settings.HotkeyModifiers =
                    NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;
                settings.HotkeyVirtualKey = NativeMethods.VK_D;
                settings.Version = CurrentVersion;
            }

            // 热键字段非法时回落到默认组合，避免注册一个 VK=0 的假热键。
            if (settings.HotkeyVirtualKey == 0)
            {
                settings.HotkeyModifiers =
                    NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;
                settings.HotkeyVirtualKey = NativeMethods.VK_D;
            }
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Version = CurrentVersion;
        try
        {
            Directory.CreateDirectory(Folder);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, true);
        }
        catch
        {
            // 设置写不进去不影响本次运行（下次启动回到上次的状态）。
        }
    }
}
