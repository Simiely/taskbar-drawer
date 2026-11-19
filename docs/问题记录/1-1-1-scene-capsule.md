---
id: docs.problems.1-1-1-scene-capsule
title: 问题：保存场景后，原编组胶囊消失，切组变成「关闭再打开」（历史记录，1.1.1 已修）
summary: "曾把场景作为第二套模型并行展示，出现了两枚语义不同的胶囊；同一个组的操作路径被拆成「运行时切换」和「场景关闭 / 启动」。1.5.0 起进一步收敛：收纳即建场景，运行时组只作为场景的运行态存在。"
symptom: "保存场景后原编组胶囊消失，点场景胶囊走关闭再打开而不是隐藏 / 展开。"
cause: "运行时组与场景快照没有身份关联，界面把两者当独立条目；点击分支只按是否场景决定行为。"
detect: "收纳一组窗口后保存场景，观察胶囊数量与点击行为。"
avoid: "同一实体只能有一个界面入口；持久化变化要以附着方式合并到运行时模型。"
recover: "StashGroup 增加 SavedId / Name，LinkSaved 关联但不新建胶囊；全关后 dormant，点击启动并 Adopt 回组。"
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
# 问题：保存场景后，原编组胶囊消失，切组变成「关闭再打开」（历史记录，1.1.1 已修）

**TL;DR**：曾把场景作为第二套模型并行展示，出现了两枚语义不同的胶囊；同一个组的操作路径被拆成「运行时切换」和「场景关闭 / 启动」。1.5.0 起进一步收敛：收纳即建场景，运行时组只作为场景的运行态存在。

- 问题：存完场景，原组胶囊不显示；点场景胶囊是 `WM_CLOSE` 后重启进程，而不是普通组的隐藏 / 展开。
- 根因：运行时组（句柄）与场景快照（json）没有身份关联，`UpdateSurface` 把两者都当独立条目渲染；点击分支只按「是否场景」决定行为。
- 解决：`StashGroup` 增加 `SavedId` / `Name`，保存时 `LinkSaved` 关联但不新建胶囊；场景窗口全关后胶囊转为 dormant（「已关闭」），点击才启动并按位置摆回，随后 `Adopt` 接回运行时组。
- 预防：同一实体只能有一个界面入口；持久化层的变化要以「附着」的方式合并到运行时模型，不要并行渲染。
