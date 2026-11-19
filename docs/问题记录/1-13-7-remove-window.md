---
id: docs.problems.1-13-7-remove-window
title: 问题：移除的窗口又被关场景写回快照（1.13.7）
summary: "移除是两个动作——从运行时组 `Detach`、从快照 `RemoveWindow`。只要其中一处还在用「exe + 标题」匹配，标题一变就会漏；而 `UpdateSnapshot` 整份覆盖成员集合，又会把这次漏掉放大成永久写回。"
symptom: "从场景详情移除窗口后，关闭场景再打开，被移除的窗口又出现在场景里。"
cause: "Detach 用 exe + 标题匹配，标题变化后认不回目标；UpdateSnapshot 从运行时组整份重抓并覆盖成员集合，把漏掉的窗口写回快照。"
detect: "改一下成员窗口标题，从详情移除，再关闭 / 重开场景，检查 saved-groups.json 是否仍有该窗口。"
avoid: "移除与配对必须共用 WindowIdentity.Pair；UpdateSnapshot 只刷新位置，永不改 group.Windows。"
recover: "Detach 复用两层身份配对；UpdateSnapshot 只刷新矩形 / 最大化 / 显示器。"
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
# 问题：移除的窗口又被关场景写回快照（1.13.7）

**TL;DR**：移除是两个动作——从运行时组 `Detach`、从快照 `RemoveWindow`。只要其中一处还在用「exe + 标题」匹配，标题一变就会漏；而 `UpdateSnapshot` 整份覆盖成员集合，又会把这次漏掉放大成永久写回。

- 用户现场是旧快照：`AppId` / `ClassName` 为 `null`，`Suppressed` 是 `exe\n标题`。同程序两个资源管理器窗口、标题又变了时，`Detach` 认不回目标，运行时组里仍挂着它
- 关场景时 `UpdateSnapshot` 从运行时组整份重抓写入快照，于是刚从详情窗移除的项又被写回 `saved-groups.json`；下次开场景自然还在
- 修法分两层：`Detach` 复用 `WindowIdentity.Candidate` / `AssignOrdinals` / `Pair`（与还原配对、详情窗「已打开」判定同一套规则）；`UpdateSnapshot` 降级为**只刷新矩形 / 最大化 / 显示器**，永不改 `group.Windows`
- 兼容：旧快照没有身份字段时，`Pair` 与 `SceneEdit.IdentityKey` 都会退回旧键 / 矩形，行为与 1.11.0 之前一致；`Suppressed` 双键并存，旧数据不需要迁移
- 验证：真实窗口夹具用 `TASKBAR_DRAWER_HOME` 隔离数据，改标题后 `Detach` 仍摘对成员；再故意把窗口 `Adopt` 回旧运行时组跑 `UpdateSnapshot`，磁盘快照仍只有 1 项
