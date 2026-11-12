using System.IO;
using System.Text.Json;

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
    public DateTime CreatedAt { get; set; }
    public List<SavedWindow> Windows { get; set; } = new();
}

/// <summary>面板与胶囊用的只读摘要（只列「当前没有窗口」的场景）。</summary>
public sealed class SavedGroupSummary
{
    public string Id { get; init; }
    public string Name { get; init; }
    public int Count { get; init; }

    public IReadOnlyList<string> Titles { get; init; } = Array.Empty<string>();

    public string State => "已关闭";
}

/// <summary>场景落盘：%LOCALAPPDATA%\TaskbarDrawer\saved-groups.json。</summary>
internal static class SavedGroupStore
{
    public static string FilePath => Path.Combine(AppSettings.Folder, "saved-groups.json");

    public static List<SavedGroup> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<SavedGroup>();
            var groups = JsonSerializer.Deserialize<List<SavedGroup>>(File.ReadAllText(FilePath));
            return groups ?? new List<SavedGroup>();
        }
        catch
        {
            // 文件损坏 / 无权限：按「还没有场景」处理，不影响其它功能。
            return new List<SavedGroup>();
        }
    }

    public static void Save(List<SavedGroup> groups)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(groups, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, true);
        }
        catch
        {
            // 写不进去只影响下次启动能否恢复场景，本次运行照常。
        }
    }
}
