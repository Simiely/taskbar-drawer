#nullable enable

namespace TaskbarDrawer.Core.Rules;

public enum RuleCardCategory
{
    Condition,
    Action
}

/// <summary>一张卡片的静态定义：怎么显示、哪些入口能用、有没有可编辑参数。</summary>
public sealed class RuleCardDefinition
{
    public string Kind { get; init; } = "";
    public RuleCardCategory Category { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>空 = 三个入口都能用；非空 = 只在这些入口下出现（避免出现永远不成立的卡）。</summary>
    public RuleTriggerKind[] OnlyTriggers { get; init; } = Array.Empty<RuleTriggerKind>();

    /// <summary>可编辑参数的键；null 表示这张卡没有参数。</summary>
    public string? ArgKey { get; init; }
    public string ArgLabel { get; init; } = "";
    public string ArgDefault { get; init; } = "";

    public bool IsCondition => Category == RuleCardCategory.Condition;

    public bool AppliesTo(RuleTriggerKind trigger) =>
        OnlyTriggers.Length == 0 || OnlyTriggers.Contains(trigger);
}

/// <summary>
/// 卡片目录。新增一种行为 = 这里加一行 + RuleRunner 里注册一个处理函数，没有别的注册表。
/// </summary>
public static class RuleCatalog
{
    // 条件
    public const string ConditionHasUntracked = "cond.hasUntracked";
    public const string ConditionHasExpandedGroup = "cond.hasExpandedGroup";
    public const string ConditionChipOnScreen = "cond.chipOnScreen";
    public const string ConditionSceneRunning = "cond.sceneRunning";

    // 动作
    public const string ActionStashNew = "act.stashNew";
    public const string ActionExpandLatest = "act.expandLatest";
    public const string ActionCollapseExpanded = "act.collapseExpanded";
    public const string ActionExpandAll = "act.expandAll";
    public const string ActionExpandGroup = "act.expandGroup";
    public const string ActionToggleGroup = "act.toggleGroup";
    public const string ActionRestoreScene = "act.restoreScene";
    public const string ActionOpenPanel = "act.openPanel";
    public const string ActionToast = "act.toast";

    private static readonly RuleTriggerKind[] ChipTriggers =
    {
        RuleTriggerKind.GroupChip,
        RuleTriggerKind.SceneChip
    };

    public static IReadOnlyList<RuleTriggerKind> Triggers { get; } = new[]
    {
        RuleTriggerKind.Hotkey,
        RuleTriggerKind.GroupChip,
        RuleTriggerKind.SceneChip
    };

    public static IReadOnlyList<RuleCardDefinition> All { get; } = new[]
    {
        new RuleCardDefinition
        {
            Kind = ConditionHasUntracked,
            Category = RuleCardCategory.Condition,
            Title = "屏幕上有未分组窗口？",
            Description = "可见、不属于任何编组的窗口数量大于 0。",
            // 这个事实只在热键入口算过；放到胶囊入口会永远读到 false。
            OnlyTriggers = new[] { RuleTriggerKind.Hotkey }
        },
        new RuleCardDefinition
        {
            Kind = ConditionHasExpandedGroup,
            Category = RuleCardCategory.Condition,
            Title = "有已展开的编组？",
            Description = "至少有一组窗口正摆在桌面上。",
            OnlyTriggers = new[] { RuleTriggerKind.Hotkey }
        },
        new RuleCardDefinition
        {
            Kind = ConditionChipOnScreen,
            Category = RuleCardCategory.Condition,
            Title = "编组已在屏幕上？",
            Description = "被点击的这组窗口当前正展开着。",
            OnlyTriggers = new[] { RuleTriggerKind.GroupChip }
        },
        new RuleCardDefinition
        {
            Kind = ConditionSceneRunning,
            Category = RuleCardCategory.Condition,
            Title = "场景窗口正在运行？",
            Description = "被点击的场景当前还有窗口开着；否则就是只剩快照的已关闭场景。",
            OnlyTriggers = new[] { RuleTriggerKind.SceneChip }
        },

        new RuleCardDefinition
        {
            Kind = ActionStashNew,
            Category = RuleCardCategory.Action,
            Title = "收纳为新编组",
            Description = "把屏幕上的窗口收成新的一组。"
        },
        new RuleCardDefinition
        {
            Kind = ActionExpandLatest,
            Category = RuleCardCategory.Action,
            Title = "展开最近一组",
            Description = "恢复最近一次收纳的那组窗口。"
        },
        new RuleCardDefinition
        {
            Kind = ActionCollapseExpanded,
            Category = RuleCardCategory.Action,
            Title = "收起所有已展开编组",
            Description = "把当前摆在桌面上的各组一起收回胶囊。"
        },
        new RuleCardDefinition
        {
            Kind = ActionExpandAll,
            Category = RuleCardCategory.Action,
            Title = "展开全部编组",
            Description = "所有收起的组一起放出来。"
        },
        new RuleCardDefinition
        {
            Kind = ActionExpandGroup,
            Category = RuleCardCategory.Action,
            Title = "展开这个编组",
            Description = "只展开被点击的那一组。",
            OnlyTriggers = ChipTriggers
        },
        new RuleCardDefinition
        {
            Kind = ActionToggleGroup,
            Category = RuleCardCategory.Action,
            Title = "展开 / 收起这个编组",
            Description = "按当前状态切换被点击的那一组。",
            OnlyTriggers = ChipTriggers
        },
        new RuleCardDefinition
        {
            Kind = ActionRestoreScene,
            Category = RuleCardCategory.Action,
            Title = "打开 / 还原这个场景",
            Description = "按保存的位置启动并摆回窗口。",
            OnlyTriggers = new[] { RuleTriggerKind.SceneChip }
        },
        new RuleCardDefinition
        {
            Kind = ActionOpenPanel,
            Category = RuleCardCategory.Action,
            Title = "打开主面板",
            Description = "显示主面板窗口。"
        },
        new RuleCardDefinition
        {
            Kind = ActionToast,
            Category = RuleCardCategory.Action,
            Title = "显示提示",
            Description = "在屏幕底部弹一条轻提示。",
            ArgKey = "text",
            ArgLabel = "提示文案",
            ArgDefault = "已执行"
        }
    };

    public static RuleCardDefinition? Find(string kind) =>
        All.FirstOrDefault(d => string.Equals(d.Kind, kind, StringComparison.Ordinal));

    /// <summary>某个入口下可以选的卡片（保持目录顺序：先条件后动作）。</summary>
    public static IEnumerable<RuleCardDefinition> ForTrigger(RuleTriggerKind trigger) =>
        All.Where(d => d.AppliesTo(trigger));

    /// <summary>入口在界面上显示的名字，同时也是它的固定说明。</summary>
    public static string TriggerTitle(RuleTriggerKind trigger) => trigger switch
    {
        RuleTriggerKind.Hotkey => "全局热键",
        RuleTriggerKind.GroupChip => "编组胶囊点击",
        _ => "场景胶囊点击"
    };

    public static string TriggerHint(RuleTriggerKind trigger) => trigger switch
    {
        RuleTriggerKind.Hotkey => "按快捷键时",
        RuleTriggerKind.GroupChip => "左键点未保存的编组胶囊时",
        _ => "左键点场景胶囊时"
    };
}
