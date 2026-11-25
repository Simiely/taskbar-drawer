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

    /// <summary>
    /// 单窗口模式的排除列表：内置资源管理器（不可删除，默认勾选）+ 用户添加的应用。
    /// 列表由 <see cref="WindowExclusion.Normalize"/> 整理，读取端永远拿到至少一项。
    /// </summary>
    public List<WindowExclusion> SingleWindowExclusions { get; set; } = new();

    /// <summary>v5 的旧字段：迁移到排除列表后仍然写回，降级运行时行为不至于突变。</summary>
    public bool ExcludeExplorerFromSingleWindow { get; set; } = true;

    /// <summary>主面板上次关闭时的位置（DIP）。HasPanelPosition 为 false 时按光标所在屏右下角弹出。</summary>
    public bool HasPanelPosition { get; set; }
    public double PanelLeft { get; set; }
    public double PanelTop { get; set; }

    public uint HotkeyModifiers { get; set; } =
        NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;
    public uint HotkeyVirtualKey { get; set; } = NativeMethods.VK_D;

    /// <summary>场景图标里程序原图标占画布边长的比例（0.55–1.0）。</summary>
    public double SceneAppIconSize { get; set; } = SceneIconMetrics.DefaultAppIconScale;

    /// <summary>场景标识占画布边长的比例（0.25–0.65），叠在原图标左上角或右上角。</summary>
    public double SceneBadgeSize { get; set; } = SceneIconMetrics.DefaultBadgeScale;

    /// <summary>场景标识的 8 色调色板（#RRGGBB）；可在场景编辑里逐个改色，恢复默认即还原这组值。</summary>
    public List<string> SceneBadgeColors { get; set; } = new(SceneBadge.DefaultPalette);

    private const int CurrentVersion = 6;

    /// <summary>当前生效的排除列表：内置资源管理器永远在第一位。</summary>
    public IReadOnlyList<WindowExclusion> Exclusions()
        => WindowExclusion.Normalize(SingleWindowExclusions, ExcludeExplorerFromSingleWindow);

    /// <summary>
    /// 数据目录，默认 <c>%LOCALAPPDATA%\TaskbarDrawer</c>。
    /// 可用环境变量 <c>TASKBAR_DRAWER_HOME</c> 覆盖：自动化夹具靠它把配置与场景隔离到临时目录——
    /// 否则跑一次就会把测试场景写进用户真实的 <c>saved-groups.json</c>。
    /// （不能靠改 <c>LOCALAPPDATA</c> 实现：<see cref="Environment.GetFolderPath"/> 走 shell API，不认这个环境变量。）
    /// </summary>
    public static string Folder
    {
        get
        {
            string custom = Environment.GetEnvironmentVariable("TASKBAR_DRAWER_HOME");
            if (!string.IsNullOrWhiteSpace(custom)) return custom;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarDrawer");
        }
    }

    public static string FilePath => Path.Combine(Folder, "settings.json");

    /// <summary>
    /// 当前生效的调色板：永远返回 8 个合法颜色。缺失 / 非法的槽位退回默认值，
    /// 这样手改配置文件或旧版本没有这个字段时，编辑器里仍然是一组能用的颜色。
    /// </summary>
    public IReadOnlyList<string> BadgePalette()
    {
        var defaults = SceneBadge.DefaultPalette;
        var result = new string[defaults.Count];
        for (int i = 0; i < defaults.Count; i++)
        {
            string stored = SceneBadgeColors != null && i < SceneBadgeColors.Count ? SceneBadgeColors[i] : null;
            result[i] = SceneBadge.TryNormalizeColor(stored, out string normalized) ? normalized : defaults[i];
        }
        return result;
    }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
            if (settings == null) return new AppSettings();

            // 旧版本存的是 Ctrl+Alt+S（用户反馈不生效），v2 起升级到 Ctrl+Alt+Shift+D。
            if (settings.Version < 2)
            {
                settings.HotkeyModifiers =
                    NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;
                settings.HotkeyVirtualKey = NativeMethods.VK_D;
            }

            // v3 调整了两个尺寸的默认值。旧默认值可能已被启动时的 Save 落盘，
            // 只把恰好等于旧默认值的项换成新默认值，用户手动调过的保持不动。
            if (settings.Version < 3)
            {
                if (Math.Abs(settings.SceneAppIconSize - 0.86) < 0.0001) settings.SceneAppIconSize = 0.80;
                if (Math.Abs(settings.SceneBadgeSize - 0.46) < 0.0001) settings.SceneBadgeSize = 0.60;
            }

            // v4 再次调整默认值（90% / 55%）：同样只替换恰好等于旧默认值的项。
            if (settings.Version < 4)
            {
                if (Math.Abs(settings.SceneAppIconSize - 0.80) < 0.0001)
                    settings.SceneAppIconSize = SceneIconMetrics.DefaultAppIconScale;
                if (Math.Abs(settings.SceneBadgeSize - 0.60) < 0.0001)
                    settings.SceneBadgeSize = SceneIconMetrics.DefaultBadgeScale;
            }

            // v5 新增「资源管理器不计入单窗口模式」，旧配置没有这个字段（反序列化是 false），
            // 按新默认值打开；用户在面板里手动关掉后会随 Version=5 原样保留。
            if (settings.Version < 5) settings.ExcludeExplorerFromSingleWindow = true;

            // v6 把单个开关升级成排除列表：资源管理器的勾选状态从旧字段带过来，
            // 用户之后加的别的应用都写在这里。手改配置漏掉内置项时由 Normalize 补齐。
            if (settings.Version < 6)
                settings.SingleWindowExclusions = new List<WindowExclusion>
                {
                    WindowExclusion.Explorer(settings.ExcludeExplorerFromSingleWindow)
                };
            settings.SingleWindowExclusions =
                WindowExclusion.Normalize(settings.SingleWindowExclusions, settings.ExcludeExplorerFromSingleWindow);
            settings.ExcludeExplorerFromSingleWindow = settings.SingleWindowExclusions[0].Enabled;

            if (settings.Version < CurrentVersion) settings.Version = CurrentVersion;

            // 热键字段非法时回落到默认组合，避免注册一个 VK=0 的假热键。
            if (settings.HotkeyVirtualKey == 0)
            {
                settings.HotkeyModifiers =
                    NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;
                settings.HotkeyVirtualKey = NativeMethods.VK_D;
            }

            // 图标尺寸只允许各自区间：手改配置文件写出离谱值时退回默认，避免图标画成一团。
            if (settings.SceneAppIconSize < SceneIconMetrics.MinAppIconScale ||
                settings.SceneAppIconSize > SceneIconMetrics.MaxAppIconScale)
                settings.SceneAppIconSize = SceneIconMetrics.DefaultAppIconScale;
            if (settings.SceneBadgeSize < SceneIconMetrics.MinBadgeScale ||
                settings.SceneBadgeSize > SceneIconMetrics.MaxBadgeScale)
                settings.SceneBadgeSize = SceneIconMetrics.DefaultBadgeScale;

            // 调色板补齐成 8 个合法颜色：缺项 / 非法值都落回默认，写回文件时就是干净的。
            settings.SceneBadgeColors = settings.BadgePalette().ToList();
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
        SingleWindowExclusions =
            WindowExclusion.Normalize(SingleWindowExclusions, ExcludeExplorerFromSingleWindow);
        ExcludeExplorerFromSingleWindow = SingleWindowExclusions[0].Enabled;
        SceneBadgeColors = BadgePalette().ToList();
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
