---
id: docs.generated.backlinks
title: 反向链接
summary: 从 related 与 pitfall 的 applies_to 自动派生的反向链接；勿手改。
type: reference
kind: index
status: active
owner: "@platform"
source_type: doc
source: schema/taxonomy.json
last_reviewed: 2026-11-11
related: []
---

# 反向链接

> 生成文件，勿手改。方向性关系只存一侧，本页是派生结果；对称关系按规范边去重。
> `applies_to` 是 pitfall 的专用字段，可指向页面 id 或技能 name；技能没有页面可链，故只出现在目标列。

| 目标 id / 技能 | 来源 id | 关系 | 来源页面 |
|---|---|---|---|
| `docs.architecture` | [问题：保存场景后，原编组胶囊消失，切组变成「关闭再打开」（历史记录，1.1.1 已修）](../问题记录/1-1-1-scene-capsule.md) | applies-to（反向：has-pitfall；来自 applies_to 字段） | `docs/问题记录/1-1-1-scene-capsule.md` |
| `docs.architecture` | [问题：窗口关掉了，胶囊上的数字要等 2.5 s；「已关闭 N 个」也是假的（1.10.0）](../问题记录/1-10-0-events.md) | applies-to（反向：has-pitfall；来自 applies_to 字段） | `docs/问题记录/1-10-0-events.md` |
| `docs.architecture` | [问题：浏览器切过标签页，场景重开时两个窗口互相换位（1.11.0）](../问题记录/1-11-0-identity.md) | applies-to（反向：has-pitfall；来自 applies_to 字段） | `docs/问题记录/1-11-0-identity.md` |
| `docs.architecture` | [问题：移除的窗口又被关场景写回快照（1.13.7）](../问题记录/1-13-7-remove-window.md) | applies-to（反向：has-pitfall；来自 applies_to 字段） | `docs/问题记录/1-13-7-remove-window.md` |
| `docs.architecture` | [问题：场景里的程序关掉再打开，运行中的场景认不回来（1.14.0）](../问题记录/1-14-0-reattach.md) | applies-to（反向：has-pitfall；来自 applies_to 字段） | `docs/问题记录/1-14-0-reattach.md` |
| `docs.architecture` | [问题：编组胶囊展开后，再点没反应（历史记录，1.2.1 已修；GroupChip 已在 1.5.0 删除）](../问题记录/1-2-1-group-chip.md) | applies-to（反向：has-pitfall；来自 applies_to 字段） | `docs/问题记录/1-2-1-group-chip.md` |
| `docs.architecture` | [问题：胶囊「看得见但点不到」，持续 0–30 s](../问题记录/1-7-0-chip-not-clickable.md) | applies-to（反向：has-pitfall；来自 applies_to 字段） | `docs/问题记录/1-7-0-chip-not-clickable.md` |
| `docs.architecture` | [问题：胶囊「被任务栏压住」长达一个轮询周期（1.7.1）](../问题记录/1-7-1-chip-zorder.md) | applies-to（反向：has-pitfall；来自 applies_to 字段） | `docs/问题记录/1-7-1-chip-zorder.md` |
| `docs.architecture` | [问题：单窗口模式打开场景后，其它窗口不最小化、场景还在 z 序最下面（1.8.1）](../问题记录/1-8-1-scene-open.md) | applies-to（反向：has-pitfall；来自 applies_to 字段） | `docs/问题记录/1-8-1-scene-open.md` |
| `docs.architecture` | [问题：浏览器插件弹窗 / 软件对话框被当成窗口收进场景（1.9.0）](../问题记录/1-9-0-window-cluster.md) | applies-to（反向：has-pitfall；来自 applies_to 字段） | `docs/问题记录/1-9-0-window-cluster.md` |
| `docs.architecture` | [问题：补 AutomationId 补了个寂寞——Border 在 UIA 里是透明的（1.9.1）](../问题记录/1-9-1-uia-button.md) | applies-to（反向：has-pitfall；来自 applies_to 字段） | `docs/问题记录/1-9-1-uia-button.md` |
| `docs.architecture` | [问题：关闭窗口数量虚高（1 个窗口报 5 个）](../问题记录/history-close-count.md) | applies-to（反向：has-pitfall；来自 applies_to 字段） | `docs/问题记录/history-close-count.md` |
| `docs.architecture` | [问题：关闭场景把同程序的其它窗口一起关了（下载弹窗）](../问题记录/history-close-wrong-windows.md) | applies-to（反向：has-pitfall；来自 applies_to 字段） | `docs/问题记录/history-close-wrong-windows.md` |
| `docs.architecture` | [问题：Explorer 重启后胶囊被压住；6 组以上无法直达](../问题记录/history-explorer-overflow.md) | applies-to（反向：has-pitfall；来自 applies_to 字段） | `docs/问题记录/history-explorer-overflow.md` |
