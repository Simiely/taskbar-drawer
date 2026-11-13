#nullable enable

namespace TaskbarDrawer.Core.Rules;

/// <summary>规则流由哪个用户入口触发。入口固定三个，界面上不可增删。</summary>
public enum RuleTriggerKind
{
    Hotkey,
    GroupChip,
    SceneChip
}

/// <summary>
/// 规则树里的一步。同一类型承担两种角色：普通卡片只填 Kind / Args，
/// 条件卡片再用 Then / Else 装两个分支。不做继承：执行器只按 Kind 查表，不需要类型判断。
/// </summary>
public sealed class RuleStep
{
    public string Kind { get; set; } = "";

    /// <summary>卡片参数（目前只有「显示提示」用到 text）。</summary>
    public Dictionary<string, string>? Args { get; set; }

    public List<RuleStep>? Then { get; set; }
    public List<RuleStep>? Else { get; set; }
}

/// <summary>一个触发器对应一条流；入口固定，所以流的数量也是固定的。</summary>
public sealed class RuleFlow
{
    public RuleTriggerKind Trigger { get; set; }
    public List<RuleStep> Steps { get; set; } = new();
}

/// <summary>
/// 全部规则：入口固定，流程写死在 <see cref="CreateDefault"/> 里，运行期只读。
/// </summary>
public sealed class RuleSet
{
    public List<RuleFlow> Flows { get; set; } = new();

    public RuleFlow? Flow(RuleTriggerKind trigger) => Flows.FirstOrDefault(f => f.Trigger == trigger);

    /// <summary>
    /// 默认规则 = 产品手感。热键沿用改造前的三段式；编组胶囊为「点一下展开、再点收回」。
    /// 热键：有未分组窗口 → 收一组；否则屏幕上还有展开的组 → 全收起；否则展开最近一组。
    /// </summary>
    public static RuleSet CreateDefault()
    {
        return new RuleSet
        {
            Flows = new List<RuleFlow>
            {
                new()
                {
                    Trigger = RuleTriggerKind.Hotkey,
                    Steps = new List<RuleStep>
                    {
                        new()
                        {
                            Kind = RuleCatalog.ConditionHasUntracked,
                            Then = new List<RuleStep>
                            {
                                new() { Kind = RuleCatalog.ActionStashNew }
                            },
                            Else = new List<RuleStep>
                            {
                                new()
                                {
                                    Kind = RuleCatalog.ConditionHasExpandedGroup,
                                    Then = new List<RuleStep>
                                    {
                                        new() { Kind = RuleCatalog.ActionCollapseExpanded }
                                    },
                                    Else = new List<RuleStep>
                                    {
                                        new() { Kind = RuleCatalog.ActionExpandLatest }
                                    }
                                }
                            }
                        }
                    }
                },
                new()
                {
                    Trigger = RuleTriggerKind.GroupChip,
                    Steps = new List<RuleStep>
                    {
                        new() { Kind = RuleCatalog.ActionToggleGroup }
                    }
                },
                new()
                {
                    Trigger = RuleTriggerKind.SceneChip,
                    Steps = new List<RuleStep>
                    {
                        new()
                        {
                            Kind = RuleCatalog.ConditionSceneRunning,
                            Then = new List<RuleStep>
                            {
                                new() { Kind = RuleCatalog.ActionToggleGroup }
                            },
                            Else = new List<RuleStep>
                            {
                                new() { Kind = RuleCatalog.ActionRestoreScene }
                            }
                        }
                    }
                }
            }
        };
    }
}
