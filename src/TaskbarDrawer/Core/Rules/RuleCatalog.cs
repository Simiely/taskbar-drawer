#nullable enable

namespace TaskbarDrawer.Core.Rules;

public enum RuleCardCategory
{
    Condition,
    Action
}

/// <summary>一张卡片的静态定义：类型 + 允许的入口。</summary>
public sealed class RuleCardDefinition
{
    public string Kind { get; init; } = "";
    public RuleCardCategory Category { get; init; }

    /// <summary>空 = 所有入口都能用；非空 = 只在这些入口下出现（避免出现永远不成立的卡）。</summary>
    public RuleTriggerKind[] OnlyTriggers { get; init; } = Array.Empty<RuleTriggerKind>();

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
    public const string ConditionHasExpandedScene = "cond.hasExpandedScene";
    public const string ConditionSceneRunning = "cond.sceneRunning";

    // 动作
    public const string ActionNewScene = "act.newScene";
    public const string ActionExpandLatest = "act.expandLatest";
    public const string ActionCollapseExpanded = "act.collapseExpanded";
    public const string ActionExpandAll = "act.expandAll";
    public const string ActionToggleScene = "act.toggleScene";
    public const string ActionRestoreScene = "act.restoreScene";
    public const string ActionOpenPanel = "act.openPanel";
    public const string ActionToast = "act.toast";

    public static IReadOnlyList<RuleTriggerKind> Triggers { get; } = new[]
    {
        RuleTriggerKind.Hotkey,
        RuleTriggerKind.SceneChip
    };

    public static IReadOnlyList<RuleCardDefinition> All { get; } = new[]
    {
        new RuleCardDefinition
        {
            Kind = ConditionHasUntracked,
            Category = RuleCardCategory.Condition,
            // 这个事实只在热键入口算过；放到胶囊入口会永远读到 false。
            OnlyTriggers = new[] { RuleTriggerKind.Hotkey }
        },
        new RuleCardDefinition
        {
            Kind = ConditionHasExpandedScene,
            Category = RuleCardCategory.Condition,
            OnlyTriggers = new[] { RuleTriggerKind.Hotkey }
        },
        new RuleCardDefinition
        {
            Kind = ConditionSceneRunning,
            Category = RuleCardCategory.Condition,
            OnlyTriggers = new[] { RuleTriggerKind.SceneChip }
        },

        new RuleCardDefinition
        {
            Kind = ActionNewScene,
            Category = RuleCardCategory.Action
        },
        new RuleCardDefinition
        {
            Kind = ActionExpandLatest,
            Category = RuleCardCategory.Action
        },
        new RuleCardDefinition
        {
            Kind = ActionCollapseExpanded,
            Category = RuleCardCategory.Action
        },
        new RuleCardDefinition
        {
            Kind = ActionExpandAll,
            Category = RuleCardCategory.Action
        },
        new RuleCardDefinition
        {
            Kind = ActionToggleScene,
            Category = RuleCardCategory.Action,
            OnlyTriggers = new[] { RuleTriggerKind.SceneChip }
        },
        new RuleCardDefinition
        {
            Kind = ActionRestoreScene,
            Category = RuleCardCategory.Action,
            OnlyTriggers = new[] { RuleTriggerKind.SceneChip }
        },
        new RuleCardDefinition
        {
            Kind = ActionOpenPanel,
            Category = RuleCardCategory.Action
        },
        new RuleCardDefinition
        {
            Kind = ActionToast,
            Category = RuleCardCategory.Action
        }
    };

    public static RuleCardDefinition? Find(string kind) =>
        All.FirstOrDefault(d => string.Equals(d.Kind, kind, StringComparison.Ordinal));
}
