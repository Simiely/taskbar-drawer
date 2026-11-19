---
id: docs.problems.1-14-0-reattach
title: 问题：场景里的程序关掉再打开，运行中的场景认不回来（1.14.0）
summary: "运行时组只记句柄，窗口销毁后记录被移出，快照项还在；程序重开是新句柄，不会自动回到组里。重启本程序后反而正常——场景变回「已关闭」，点开时走的是还原配对。"
symptom: "场景展开时关掉其中一个程序，重新打开后新窗口不跟场景收起 / 展开，胶囊计数也不含它。"
cause: "运行时组只记 hwnd，窗口销毁后记录被 Prune 移出；快照项还在，但新句柄不会自动回组。"
detect: "展开场景后关掉一个成员再重开，看日志是否有「自动认回重新打开的程序」，胶囊计数是否恢复。"
avoid: "新建 / 重开的窗口要先 ClaimForeground 认领再走单窗口判定；认领不要用位次。"
recover: "SceneReattach.Missing / Match + EVENT_OBJECT_SHOW 驱动，展开入口 ReclaimVisible 兜底。"
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
# 问题：场景里的程序关掉再打开，运行中的场景认不回来（1.14.0）

**TL;DR**：运行时组只记句柄，窗口销毁后记录被移出，快照项还在；程序重开是新句柄，不会自动回到组里。重启本程序后反而正常——场景变回「已关闭」，点开时走的是还原配对。

- 现象：场景展开时关掉其中一个程序（日志 `窗口已销毁，移出记录`），重新打开后新窗口不跟场景一起收起 / 展开，胶囊计数也不含它。
- 根因：`StashAllService` 只持有 hwnd；`Prune` 销毁记录后，运行态与快照之间没有「等它回来」的桥。
- 解决：`Core/SceneReattach` 算出快照里缺了哪一项、把新窗口认回缺失项；`SceneService.TryReattach` 由全局 `EVENT_OBJECT_SHOW` 驱动（150 ms 合并），`ReclaimVisible` 在展开入口（`Restore` / `ExpandLatest` / `ExpandAll`）兜底。配对刻意不用位次——少一个成员后位次会整体前移，会把还开着的认成被关掉的。
- 竞态：单窗口模式必须先 `ClaimForeground` 再判定，否则新窗口会被当成「场景外窗口」，把整个场景收回胶囊。
- 边界：收起中的场景不在 SHOW 时接入（避免把用户刚打开的程序藏掉），只在展开时认领。
