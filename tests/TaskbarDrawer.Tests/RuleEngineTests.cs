using TaskbarDrawer.Core.Rules;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>
/// 规则引擎的回归网：锁住默认手感、护栏和卡片目录约束。
/// 全部用假动作记录执行序列，不碰真实窗口。
/// </summary>
public sealed class RuleEngineTests
{
    private readonly List<string> _log = new();
    private RuleSet _rules = RuleSet.CreateDefault();

    private RuleRunner Runner()
    {
        var runner = new RuleRunner(() => _rules);
        runner.On(RuleCatalog.ConditionHasUntracked, facts => facts.HasUntrackedWindows);
        runner.On(RuleCatalog.ConditionHasExpandedGroup, facts => facts.HasExpandedGroup);
        runner.On(RuleCatalog.ConditionChipOnScreen, facts => facts.ChipOnScreen);
        runner.On(RuleCatalog.ConditionSceneRunning, facts => facts.SceneRunning);

        runner.Do(RuleCatalog.ActionStashNew, (_, _) => _log.Add("stashNew"));
        runner.Do(RuleCatalog.ActionExpandLatest, (_, _) => _log.Add("expandLatest"));
        runner.Do(RuleCatalog.ActionExpandAll, (_, _) => _log.Add("expandAll"));
        runner.Do(RuleCatalog.ActionCollapseExpanded, (_, _) => _log.Add("collapseExpanded"));
        runner.Do(RuleCatalog.ActionExpandGroup, (facts, _) => _log.Add($"expandGroup:{facts.GroupId}"));
        runner.Do(RuleCatalog.ActionToggleGroup, (facts, _) => _log.Add($"toggleGroup:{facts.GroupId}"));
        runner.Do(RuleCatalog.ActionRestoreScene, (facts, _) => _log.Add($"restoreScene:{facts.SavedId}"));
        runner.Do(RuleCatalog.ActionOpenPanel, (_, _) => _log.Add("openPanel"));
        runner.Do(RuleCatalog.ActionToast, (_, step) =>
            _log.Add("toast:" + (step.Args != null && step.Args.TryGetValue("text", out var text) ? text : "")));
        return runner;
    }

    // ---------- 默认手感 ----------

    [Fact]
    public void 热键_屏幕上有未分组窗口_收纳成新组()
    {
        Runner().Run(RuleTriggerKind.Hotkey, new RuleFacts { HasUntrackedWindows = true, HasExpandedGroup = true });
        Assert.Equal(new[] { "stashNew" }, _log);
    }

    [Fact]
    public void 热键_没有未分组窗口但有展开的组_全部收起()
    {
        Runner().Run(RuleTriggerKind.Hotkey, new RuleFacts { HasUntrackedWindows = false, HasExpandedGroup = true });
        Assert.Equal(new[] { "collapseExpanded" }, _log);
    }

    [Fact]
    public void 热键_屏幕上什么都没有_展开最近一组()
    {
        Runner().Run(RuleTriggerKind.Hotkey, new RuleFacts());
        Assert.Equal(new[] { "expandLatest" }, _log);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 编组胶囊_无论是否展开_都是切换(bool onScreen)
    {
        Runner().Run(RuleTriggerKind.GroupChip, new RuleFacts { ChipOnScreen = onScreen, GroupId = 7 });
        Assert.Equal(new[] { "toggleGroup:7" }, _log);
    }

    [Fact]
    public void 场景胶囊_窗口在运行_切换编组()
    {
        Runner().Run(RuleTriggerKind.SceneChip,
            new RuleFacts { SceneRunning = true, GroupId = 2, SavedId = "scene-a" });
        Assert.Equal(new[] { "toggleGroup:2" }, _log);
    }

    [Fact]
    public void 场景胶囊_只剩快照_按位置还原()
    {
        Runner().Run(RuleTriggerKind.SceneChip,
            new RuleFacts { SceneRunning = false, SavedId = "scene-a" });
        Assert.Equal(new[] { "restoreScene:scene-a" }, _log);
    }

    // ---------- 运行时护栏 ----------

    [Fact]
    public void 空流不执行任何动作()
    {
        _rules = new RuleSet();
        Runner().Run(RuleTriggerKind.Hotkey, new RuleFacts { HasUntrackedWindows = true });
        Assert.Empty(_log);
    }

    [Fact]
    public void 未知卡片被跳过_后续卡片照常执行()
    {
        _rules = new RuleSet
        {
            Flows =
            {
                new RuleFlow
                {
                    Trigger = RuleTriggerKind.Hotkey,
                    Steps =
                    {
                        new RuleStep { Kind = "not.a.card" },
                        new RuleStep { Kind = RuleCatalog.ActionExpandAll }
                    }
                }
            }
        };

        Runner().Run(RuleTriggerKind.Hotkey, new RuleFacts());
        Assert.Equal(new[] { "expandAll" }, _log);
    }

    [Fact]
    public void 单次最多执行六十四步()
    {
        var steps = new List<RuleStep>();
        for (int i = 0; i < 100; i++) steps.Add(new RuleStep { Kind = RuleCatalog.ActionExpandAll });
        _rules = new RuleSet
        {
            Flows = { new RuleFlow { Trigger = RuleTriggerKind.Hotkey, Steps = steps } }
        };

        Runner().Run(RuleTriggerKind.Hotkey, new RuleFacts());
        Assert.Equal(64, _log.Count);
    }

    [Fact]
    public void 超过八层嵌套的分支不再执行()
    {
        RuleStep Action() => new() { Kind = RuleCatalog.ActionExpandAll };
        RuleStep Wrap(RuleStep inner) => new()
        {
            Kind = RuleCatalog.ConditionHasUntracked,
            Then = new List<RuleStep> { inner }
        };

        // 八层条件里放动作：动作深度为 8，仍在允许范围内。
        RuleStep eight = Action();
        for (int i = 0; i < 8; i++) eight = Wrap(eight);
        _rules = new RuleSet
        {
            Flows = { new RuleFlow { Trigger = RuleTriggerKind.Hotkey, Steps = { eight } } }
        };
        Runner().Run(RuleTriggerKind.Hotkey, new RuleFacts { HasUntrackedWindows = true });
        Assert.Single(_log);

        // 九层：动作在深度 9，被 MaxDepth 截断。
        _log.Clear();
        RuleStep nine = Action();
        for (int i = 0; i < 9; i++) nine = Wrap(nine);
        _rules = new RuleSet
        {
            Flows = { new RuleFlow { Trigger = RuleTriggerKind.Hotkey, Steps = { nine } } }
        };
        Runner().Run(RuleTriggerKind.Hotkey, new RuleFacts { HasUntrackedWindows = true });
        Assert.Empty(_log);
    }

    [Fact]
    public void 一张卡抛异常不影响后面的卡()
    {
        _rules = new RuleSet
        {
            Flows =
            {
                new RuleFlow
                {
                    Trigger = RuleTriggerKind.Hotkey,
                    Steps =
                    {
                        new RuleStep { Kind = RuleCatalog.ActionStashNew },
                        new RuleStep { Kind = RuleCatalog.ActionExpandAll }
                    }
                }
            }
        };

        var runner = new RuleRunner(() => _rules);
        runner.Do(RuleCatalog.ActionStashNew, (_, _) => throw new InvalidOperationException("boom"));
        runner.Do(RuleCatalog.ActionExpandAll, (_, _) => _log.Add("expandAll"));

        runner.Run(RuleTriggerKind.Hotkey, new RuleFacts());
        Assert.Equal(new[] { "expandAll" }, _log);
    }

    // ---------- 卡片目录约束 ----------

    [Theory]
    [InlineData(RuleCatalog.ConditionHasUntracked)]
    [InlineData(RuleCatalog.ConditionHasExpandedGroup)]
    public void 热键专属条件不能出现在胶囊入口(string kind)
    {
        var definition = RuleCatalog.Find(kind);
        Assert.NotNull(definition);
        Assert.True(definition!.AppliesTo(RuleTriggerKind.Hotkey));
        Assert.False(definition.AppliesTo(RuleTriggerKind.GroupChip));
        Assert.False(definition.AppliesTo(RuleTriggerKind.SceneChip));
    }

    [Fact]
    public void 入口专属卡片只在对应入口出现()
    {
        Assert.Equal(new[] { RuleTriggerKind.GroupChip },
            RuleCatalog.Find(RuleCatalog.ConditionChipOnScreen)!.OnlyTriggers);
        Assert.Equal(new[] { RuleTriggerKind.SceneChip },
            RuleCatalog.Find(RuleCatalog.ConditionSceneRunning)!.OnlyTriggers);
        Assert.Equal(new[] { RuleTriggerKind.SceneChip },
            RuleCatalog.Find(RuleCatalog.ActionRestoreScene)!.OnlyTriggers);
        Assert.False(RuleCatalog.Find(RuleCatalog.ActionExpandGroup)!.AppliesTo(RuleTriggerKind.Hotkey));
        Assert.False(RuleCatalog.Find(RuleCatalog.ActionToggleGroup)!.AppliesTo(RuleTriggerKind.Hotkey));
    }

    [Fact]
    public void 默认规则里的每张卡都在目录里()
    {
        foreach (var flow in RuleSet.CreateDefault().Flows)
            AssertAllCardsKnown(flow.Steps);
    }

    private static void AssertAllCardsKnown(List<RuleStep> steps)
    {
        foreach (var step in steps)
        {
            Assert.NotNull(RuleCatalog.Find(step.Kind));
            if (step.Then != null) AssertAllCardsKnown(step.Then);
            if (step.Else != null) AssertAllCardsKnown(step.Else);
        }
    }
}
