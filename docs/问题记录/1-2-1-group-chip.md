---
id: docs.problems.1-2-1-group-chip
title: 问题：编组胶囊展开后，再点没反应（历史记录，1.2.1 已修；GroupChip 已在 1.5.0 删除）
summary: "规则化时把「再点收起」丢了，默认流写成「未展开才展开」，展开状态落到空分支。"
symptom: "README 承诺再点收起，实际点击无反应。"
cause: "默认规则树用 cond.chipOnScreen 包住 act.expandGroup，展开后落到空分支。"
detect: "展开编组胶囊后再点一次，看是否收起。"
avoid: "默认规则树是产品手感，改规则要对着 README 逐条核对；条件卡先确认入口事实。"
recover: "默认流改成单张 act.toggleGroup；条件卡补 OnlyTriggers = Hotkey。"
applies_to: ["docs.architecture"]
severity: medium
type: explanation
kind: pitfall
status: resolved
owner: "@simiely"
tier: normal
confidence: high
topics: [development]
source_type: doc
source: DEVELOPMENT.md
last_reviewed: 2026-11-19
related: []
---
# 问题：编组胶囊展开后，再点没反应（历史记录，1.2.1 已修；GroupChip 已在 1.5.0 删除）

**TL;DR**：规则化时把「再点收起」丢了，默认流写成「未展开才展开」，展开状态落到空分支。

- 问题：README 承诺「再点 → 收起」，实际点击没有任何反馈，看起来像程序卡住。
- 根因：GroupChip 默认树用 `cond.chipOnScreen` 包住 `act.expandGroup`，`Then` 留空；改造前的手感没有被完整翻译成规则。
- 解决：默认流改成单张 `act.toggleGroup`；同时给 `cond.hasUntracked` / `cond.hasExpandedGroup` 补 `OnlyTriggers = Hotkey`，避免加进胶囊流后永远为 false。
- 预防：默认树是产品手感的一部分，改规则要对着 README 逐条核对；新增条件卡先确认「这个事实在哪几个入口算过」。
