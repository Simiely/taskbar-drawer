using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskbarDrawer.Core;

/// <summary>场景里的一个窗口：程序路径 + 保存时的位置。</summary>
public sealed class SavedWindow
{
    /// <summary>exe 全路径；还原 / 关闭都靠它认人。</summary>
    public string ExePath { get; set; }

    public string ProcessName { get; set; }
    public string Title { get; set; }

    /// <summary>屏幕坐标（物理像素），与 WINDOWPLACEMENT 的还原矩形一致。</summary>
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>
    /// 保存时窗口所在显示器（设备名，如 \\.\DISPLAY1）。
    /// 还原时只匹配同一块屏的窗口：否则屏 1 的场景会把屏 2 上同程序的窗口挪过来。
    /// 旧快照没有该字段（null）时按保存矩形推断。
    /// </summary>
    public string MonitorDevice { get; set; }

    public bool Maximized { get; set; }
}

/// <summary>
/// 场景（工作区快照）：一组软件 + 各自的窗口位置，跨启动保留。
/// 与运行时组不同，它不持有窗口句柄：关掉软件后这条记录仍然在。
/// </summary>
public sealed class SavedGroup
{
    public string Id { get; set; }
    public string Name { get; set; }

    /// <summary>场景标识（颜色 + 可选数字 + 角上位置）；旧数据没有该字段时由旧 emoji 迁移得到。</summary>
    public SceneBadge Badge { get; set; }

    /// <summary>1.6.7 及更早版本的 emoji 图标，只用于读取旧数据，迁移后不再写回。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Emoji { get; set; }

    public DateTime CreatedAt { get; set; }
    public List<SavedWindow> Windows { get; set; } = new();

    /// <summary>
    /// 用户从场景里移除过的窗口（去重键，见 <see cref="SceneEdit.Key"/>）。
    /// 场景正在运行时会话快照由运行时组整份覆盖，靠这个集合把移除过的项挡住；
    /// 重新追加同名窗口时会从集合里删掉。旧版 JSON 没有该字段，反序列化为空列表。
    /// </summary>
    public List<string> Suppressed { get; set; } = new();
}

/// <summary>场景编辑窗口里的一行窗口。</summary>
public sealed class SavedWindowItem
{
    public int Index { get; init; }
    public string Title { get; init; }
    public string ProcessName { get; init; }
    public string ExePath { get; init; }
    public bool Maximized { get; init; }
}

/// <summary>场景详情里「追加窗口」的一项：这块屏幕上的一个窗口及它的归属状态。</summary>
public sealed class AppendableWindowItem
{
    public long Handle { get; init; }
    public string Title { get; init; }
    public string ProcessName { get; init; }
    public bool Minimized { get; init; }

    /// <summary>已经在当前场景里：运行中的场景看运行时组，已关闭的场景看快照。</summary>
    public bool InScene { get; init; }

    /// <summary>已经被另一个场景接管，不能直接追加。</summary>
    public bool InOtherScene { get; init; }

    public bool CanAppend => !InScene && !InOtherScene;
}

/// <summary>枚举到的一个顶层窗口（只读），用来组装追加候选。</summary>
public sealed class WindowSnapshot
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; }
    public string ProcessName { get; init; }
    public string ExePath { get; init; }
    public string MonitorDevice { get; init; }
    public bool Minimized { get; init; }
}

/// <summary>场景详情：编辑器只读展示 + 由 <see cref="WorkspaceService"/> 组装。</summary>
public sealed class SavedGroupDetail
{
    public string Id { get; init; }
    public string Name { get; init; }
    public SceneBadge Badge { get; init; }
    public IReadOnlyList<SavedWindowItem> Windows { get; init; } = Array.Empty<SavedWindowItem>();
}

/// <summary>面板与胶囊用的只读摘要（只列「当前没有窗口」的场景）。</summary>
public sealed class SavedGroupSummary
{
    public string Id { get; init; }
    public string Name { get; init; }
    public SceneBadge Badge { get; init; }
    public int Count { get; init; }

    public IReadOnlyList<string> Titles { get; init; } = Array.Empty<string>();

    public string State => "已关闭";
}

/// <summary>场景落盘：%LOCALAPPDATA%\TaskbarDrawer\saved-groups.json。</summary>
internal static class SavedGroupStore
{
    public static string FilePath => Path.Combine(AppSettings.Folder, "saved-groups.json");

    public static List<SavedGroup> Load(out string warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(FilePath)) return new List<SavedGroup>();
            var groups = JsonSerializer.Deserialize<List<SavedGroup>>(File.ReadAllText(FilePath));
            return groups ?? new List<SavedGroup>();
        }
        catch
        {
            // 文件损坏 / 无权限：先备份一份再按「还没有场景」处理，
            // 避免下一次保存直接把用户原来的场景文件覆盖掉。
            string backup = FilePath + ".corrupt";
            try { File.Copy(FilePath, backup, true); } catch { }
            warning = $"场景文件读取失败，已备份为 {Path.GetFileName(backup)}；本次按空场景列表启动";
            return new List<SavedGroup>();
        }
    }

    /// <summary>原子落盘；返回 false 表示这次没写进去（磁盘满 / 被占用 / 无权限）。</summary>
    public static bool Save(List<SavedGroup> groups)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(groups, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, true);
            return true;
        }
        catch
        {
            // 写不进去只影响下次启动能否恢复场景，本次运行照常；由上层提示用户。
            return false;
        }
    }
}
