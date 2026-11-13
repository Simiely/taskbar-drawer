#nullable enable

namespace TaskbarDrawer.Core.Rules;

/// <summary>执行一条流之前先算好的事实。条件是纯查表，不在执行过程中再去问系统。</summary>
public sealed class RuleFacts
{
    public bool HasUntrackedWindows { get; init; }
    public bool HasExpandedGroup { get; init; }
    public bool ChipOnScreen { get; init; }
    public bool SceneRunning { get; init; }

    /// <summary>触发源：被点击的编组号（0 = 没有）。</summary>
    public int GroupId { get; init; }

    /// <summary>触发源：被点击的场景 Id（null = 不是场景）。</summary>
    public string? SavedId { get; init; }
}

/// <summary>
/// 规则解释器：拿着当前规则走树，条件和动作都是查表调用。
/// 不用反射、不用表达式树、不编译代码——启动零成本，行为完全可预期。
/// </summary>
public sealed class RuleRunner
{
    /// <summary>单次执行最多走多少张卡：配置写错时宁可停下，也不要让一次点击跑出几百个动作。</summary>
    private const int MaxSteps = 64;

    /// <summary>分支最大嵌套层数，防止异常配置把栈打穿。</summary>
    private const int MaxDepth = 8;

    private readonly Func<RuleSet> _rules;
    private readonly Dictionary<string, Func<RuleFacts, bool>> _conditions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action<RuleFacts, RuleStep>> _actions = new(StringComparer.Ordinal);

    public RuleRunner(Func<RuleSet> rules)
    {
        _rules = rules;
    }

    public void On(string kind, Func<RuleFacts, bool> handler) => _conditions[kind] = handler;

    public void Do(string kind, Action<RuleFacts, RuleStep> handler) => _actions[kind] = handler;

    /// <summary>按当前规则执行某个入口。没有任何流或卡片时静默返回。</summary>
    public void Run(RuleTriggerKind trigger, RuleFacts facts)
    {
        var flow = _rules().Flow(trigger);
        if (flow == null) return;

        var state = new RunState();
        Execute(flow.Steps, facts, 0, state);
    }

    private void Execute(List<RuleStep>? steps, RuleFacts facts, int depth, RunState state)
    {
        if (steps == null || depth > MaxDepth) return;

        foreach (var step in steps)
        {
            if (state.Steps >= MaxSteps) return;
            state.Steps++;

            var definition = RuleCatalog.Find(step.Kind);
            if (definition == null) continue;

            if (definition.IsCondition)
            {
                Execute(Evaluate(step, facts) ? step.Then : step.Else, facts, depth + 1, state);
                continue;
            }

            if (!_actions.TryGetValue(step.Kind, out var action)) continue;

            // 逐卡隔离：某张卡失败只丢这一步，不让一次点击整体失败。
            try
            {
                action(facts, step);
            }
            catch (Exception ex)
            {
                Diagnostics.Write("rules." + step.Kind, ex);
            }
        }
    }

    private bool Evaluate(RuleStep step, RuleFacts facts)
    {
        if (!_conditions.TryGetValue(step.Kind, out var condition)) return false;
        try
        {
            return condition(facts);
        }
        catch (Exception ex)
        {
            Diagnostics.Write("rules." + step.Kind, ex);
            return false;
        }
    }

    private sealed class RunState
    {
        public int Steps;
    }
}
