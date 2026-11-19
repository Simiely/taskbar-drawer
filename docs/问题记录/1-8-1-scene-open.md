---
id: docs.problems.1-8-1-scene-open
title: 问题：单窗口模式打开场景后，其它窗口不最小化、场景还在 z 序最下面（1.8.1）
summary: "恢复、置前、单窗口过滤三件事各写了一半，缺一个统一收尾点；而置前依赖的 `ForegroundBefore` 对「从快照拉起的组」恒为 0。"
symptom: "单窗口模式点胶囊打开场景，其它窗口不动，场景窗口停在 z 序最下面。"
cause: "批量恢复期间 SuspendTemporarily 压掉前台事件；ForegroundBefore 对 Adopt 组为 0；最小化用 SW_MINIMIZE 抢焦点。"
detect: "单窗口模式下展开已关闭场景，看场景是否置前、其它窗口是否最小化。"
avoid: "状态恢复与恢复后的动作放在同一个收尾函数；被抑制的事件必须显式补跑。"
recover: "FinishSceneOpen（置前 + ApplyNow）+ SceneFocusRules.Pick + SW_SHOWMINNOACTIVE。"
applies_to: ["docs.architecture"]
severity: high
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
# 问题：单窗口模式打开场景后，其它窗口不最小化、场景还在 z 序最下面（1.8.1）

**TL;DR**：恢复、置前、单窗口过滤三件事各写了一半，缺一个统一收尾点；而置前依赖的 `ForegroundBefore` 对「从快照拉起的组」恒为 0。

- 问题：单窗口模式下点胶囊打开一个已关闭 / 已收起的场景，场景窗口出来了，但屏幕上其它窗口纹丝不动，而且场景窗口停在最下面（看着像没打开）。
- 根因一：`ToggleScene` / `RestoreScene` / `ExpandLatest` 开头都会 `SingleWindowMode.SuspendTemporarily()`（800 ms），批量还原期间的前台事件全被压掉；而单窗口模式的过滤**只**由前台事件驱动，抑制期过后系统不会再给事件 → 「其它窗口最小化」永远不发生。
- 根因二：`ExpandGroup` 只在 `IsEligibleActivationTarget(group.ForegroundBefore)` 成立时激活窗口。`ForegroundBefore` 是 `init`，只有 `StashAll` 建组时才写；`Adopt`（从快照拉起窗口时建组）不写，恒为 `IntPtr.Zero` → 这类场景**永远**不置前，再收起再展开也一样。
- 根因三：最小化用的是 `SW_MINIMIZE`，它「会激活 Z 序里的下一个顶层窗口」，一轮下来把焦点从场景窗口抢走。
- 解决：新增 `AppShell.FinishSceneOpen(groupId)`（置前 + `SingleWindowMode.ApplyNow`）作为唯一收尾点，三处调用 + 启动出来的窗口在接管时补一次；置前目标改由纯规则 `SceneFocusRules.Pick` 决定（优先 `ForegroundBefore`，否则第一个刚还原的窗口）；最小化改 `SW_SHOWMINNOACTIVE`。
- 预防：**「状态恢复」和「恢复之后该发生什么」要放在同一个收尾函数里**，否则抑制窗口（`SuspendTemporarily`）会把两者一起吃掉。凡是「只在事件里做的事」，被抑制之后必须有显式的补跑入口——`ApplyNow` 就是那个入口。
- 附带修掉的状态机问题：往「收起中」的场景追加窗口会让整组翻成「已展开」（新增 `StashSingle` 保持收起）；快照写盘失败仍建场景（违反坑 4，改为 `SaveFrom` 返回 null 并放回窗口）。
