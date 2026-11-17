using System.Text.Json.Serialization;

namespace TaskbarDrawer.Core;

/// <summary>
/// 场景标识：叠在任务栏窗口图标角上的小圆标。
/// 数字 1–9 可选（白色粗体居中），不选数字就是纯色圆点；颜色默认八色预设（可自行改色），位置左上或右上（默认右上）。
/// 纯几何形状在小尺寸下比 emoji 清楚，也不依赖任何图片资源。
/// </summary>
public sealed class SceneBadge
{
    public const string DefaultColor = "#E5484D";

    /// <summary>1.6.7 调色板里的灰色；1.6.8 起换成黑色，旧数据读取时一次性替换。</summary>
    public const string LegacyGray = "#8B8D98";

    /// <summary>默认调色板：饱和度接近，缩到 16 px 仍能彼此区分；用户可在场景编辑里逐个改色。</summary>
    public static readonly IReadOnlyList<string> DefaultPalette = new[]
    {
        "#E5484D", // 红
        "#F76B15", // 橙
        "#F5B301", // 黄
        "#46A758", // 绿
        "#12A594", // 青
        "#0A6CDE", // 蓝
        "#8E4EC6", // 紫
        "#000000"  // 黑
    };

    public static SceneBadge Default => new();

    /// <summary>圆底色（#RRGGBB；默认取调色板里的颜色，也可以是自己改出来的任意颜色）。</summary>
    public string Color { get; set; } = DefaultColor;

    /// <summary>0 = 纯色圆点；1–9 = 圆底上写这个数字。</summary>
    public int Number { get; set; }

    /// <summary>true = 叠在图标右上角（默认）；false = 左上角。</summary>
    public bool Right { get; set; } = true;

    /// <summary>把外部数据夹回合法范围：颜色必须是 #RRGGBB（统一大写），数字只留 0–9。</summary>
    public SceneBadge Normalized()
    {
        return new SceneBadge
        {
            Color = NormalizeColor(Color),
            Number = Number is >= 0 and <= 9 ? Number : 0,
            Right = Right
        };
    }

    /// <summary>校准颜色写法：合法时返回大写 #RRGGBB，非法时退回默认色。</summary>
    public static string NormalizeColor(string color)
        => TryNormalizeColor(color, out string normalized) ? normalized : DefaultColor;

    /// <summary>只接受 #RRGGBB（或省略 # 的六位十六进制），统一转成大写。</summary>
    public static bool TryNormalizeColor(string color, out string normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(color)) return false;

        string value = color.Trim();
        if (value.Length == 6) value = "#" + value;
        if (value.Length != 7 || value[0] != '#') return false;

        for (int i = 1; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i])) return false;
        }

        normalized = value.ToUpperInvariant();
        return true;
    }

    /// <summary>null 也当作默认标识：界面上永远有一个可画的圆点。</summary>
    public static SceneBadge Coerce(SceneBadge badge) => badge?.Normalized() ?? Default;

    public bool SameAs(SceneBadge other)
        => other != null && other.Color == Color && other.Number == Number && other.Right == Right;

    /// <summary>文字里用的标识（托盘菜单 / 提示）：无数字是 ●，有数字是 ①–⑨。不参与落盘。</summary>
    [JsonIgnore]
    public string Glyph => Glyphs[Number is >= 0 and <= 9 ? Number : 0];

    public static string GlyphOf(SceneBadge badge) => badge?.Normalized().Glyph ?? "";

    /// <summary>文字里的标识前缀（● / ① + 空格）；没有标识时为空。</summary>
    public static string PrefixOf(SceneBadge badge)
    {
        string glyph = GlyphOf(badge);
        return glyph.Length == 0 ? "" : glyph + " ";
    }

    private static readonly string[] Glyphs =
    {
        "●", "①", "②", "③", "④", "⑤", "⑥", "⑦", "⑧", "⑨"
    };

    /// <summary>
    /// 1.6.7 及更早版本保存的 emoji 图标 → 最接近的标识颜色。
    /// 只做一次性迁移；迁移后保存的是纯色圆点，不再出现 emoji。
    /// </summary>
    public static SceneBadge FromLegacyEmoji(string emoji)
        => new()
        {
            Color = LegacyColors.TryGetValue(emoji ?? "", out string color) ? color : DefaultColor
        };

    private static readonly Dictionary<string, string> LegacyColors = new(StringComparer.Ordinal)
    {
        ["🔥"] = "#E5484D",
        ["⭐"] = "#F5B301",
        ["❤️"] = "#E5484D",
        ["⚡"] = "#F5B301",
        ["🎯"] = "#E5484D",
        ["🔒"] = "#F5B301",
        ["💡"] = "#F5B301",
        ["📍"] = "#E5484D",
        ["☕"] = "#F76B15",
        ["👻"] = "#8E4EC6",
        ["💎"] = "#0A6CDE",
        ["🍀"] = "#46A758",
        // 1.6.6 及更早版本的预设：统一退回黑色圆点。
        ["💾"] = "#000000",
        ["🎬"] = "#000000",
        ["💼"] = "#000000",
        ["🧪"] = "#000000",
        ["📊"] = "#000000",
        ["🎨"] = "#000000",
        ["🌐"] = "#000000",
        ["📝"] = "#000000",
        ["🔧"] = "#000000",
        ["🎮"] = "#000000",
        ["🚀"] = "#000000",
        ["📁"] = "#000000"
    };
}

/// <summary>
/// 场景窗口的文字前缀（1.6.6 的旧机制，1.6.7 起改为合成窗口图标）。
/// 只保留 <see cref="Strip"/>：清理升级前残留在窗口标题里的前缀；
/// 应用自己的标题即使以别的 emoji 开头，也不会被我们摘掉。
/// </summary>
public static class SceneTitle
{
    /// <summary>1.6.7 用过的预设图标。</summary>
    private static readonly string[] Current =
    {
        "🔥", "⭐", "❤️", "⚡", "🎯", "🔒",
        "💡", "📍", "☕", "👻", "💎", "🍀"
    };

    /// <summary>1.6.6 及更早版本用过的图标。</summary>
    private static readonly string[] Legacy =
    {
        "💾", "🎬", "💼", "🧪", "📊", "🎨", "🌐", "📝", "🔧", "🎮", "🚀", "📁"
    };

    /// <summary>摘掉任意一个已知前缀（当前预设 + 旧版预设）；没有前缀时原样返回。</summary>
    public static string Strip(string title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        foreach (string emoji in Current.Concat(Legacy))
        {
            string prefix = emoji + " ";
            if (title.StartsWith(prefix, StringComparison.Ordinal)) return title[prefix.Length..];
        }
        return title;
    }
}
