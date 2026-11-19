---
id: docs.generated.index
title: 生成索引
summary: 从 frontmatter 自动生成的导航；勿手改。
type: reference
kind: index
status: active
owner: "@platform"
source_type: doc
source: schema/taxonomy.json
last_reviewed: 2026-11-11
related: []
---

# 生成索引

> 生成文件，勿手改。运行 `python tools/repo-kit/generate.py --root . --write` 重建。

## 按 type

### reference

| 页面 | kind | status | owner | tier | source |
|---|---|---|---|---|---|
| [交接：进度、待办与发布路径](../handoff.md) | note | active | @simiely | normal | `DEVELOPMENT.md` |
| [文档索引](../index.md) | index | active | @simiely | normal | `README.md` |
| [走查能力清单](../walkthrough/capabilities.md) | note | active | @simiely | normal | `README.md` |
| [走查素材与覆盖矩阵](../walkthrough/scenarios.md) | note | active | @simiely | normal | `docs/walkthrough/capabilities.md` |

### explanation

| 页面 | kind | status | owner | tier | source |
|---|---|---|---|---|---|
| [架构说明](../架构.md) | note | active | @simiely | normal | `DEVELOPMENT.md` |
| [剩余待办的调研与实施建议](../design/backlog-plan.md) | decision | active | @simiely | normal | `docs/handoff.md` |
| [窗口识别与弹窗归属（终版方案）](../design/window-identity.md) | decision | active | @simiely | normal | `src/TaskbarDrawer/Core/WindowEnumerator.cs` |

## Reports

| 页面 | status | owner | tier | source |
|---|---|---|---|---|
| [第 1 轮走查报告](../walkthrough/report-1.md) | active | @simiely | normal | `docs/walkthrough/scenarios.md` |

## Pitfalls

| 坑 | severity | 症状 | applies_to | status |
|---|---|---|---|---|
| [问题：浏览器切过标签页，场景重开时两个窗口互相换位（1.11.0）](../问题记录/1-11-0-identity.md) | high | 同程序两个窗口切换标签页后收起并重开，两个窗口回到对方的位置，甚至重复启动。 | `docs.architecture` | resolved |
| [问题：移除的窗口又被关场景写回快照（1.13.7）](../问题记录/1-13-7-remove-window.md) | high | 从场景详情移除窗口后，关闭场景再打开，被移除的窗口又出现在场景里。 | `docs.architecture` | resolved |
| [问题：场景里的程序关掉再打开，运行中的场景认不回来（1.14.0）](../问题记录/1-14-0-reattach.md) | high | 场景展开时关掉其中一个程序，重新打开后新窗口不跟场景收起 / 展开，胶囊计数也不含它。 | `docs.architecture` | resolved |
| [问题：胶囊「被任务栏压住」长达一个轮询周期（1.7.1）](../问题记录/1-7-1-chip-zorder.md) | high | 点任务栏 / 切窗口后胶囊短暂看得见点不到，点击被任务栏吃掉。 | `docs.architecture` | resolved |
| [问题：单窗口模式打开场景后，其它窗口不最小化、场景还在 z 序最下面（1.8.1）](../问题记录/1-8-1-scene-open.md) | high | 单窗口模式点胶囊打开场景，其它窗口不动，场景窗口停在 z 序最下面。 | `docs.architecture` | resolved |
| [问题：浏览器插件弹窗 / 软件对话框被当成窗口收进场景（1.9.0）](../问题记录/1-9-0-window-cluster.md) | high | 浏览器扩展弹窗 / 软件对话框被当成场景成员，关闭场景时主窗口一起被关。 | `docs.architecture` | resolved |
| [问题：关闭场景把同程序的其它窗口一起关了（下载弹窗）](../问题记录/history-close-wrong-windows.md) | high | 关闭场景把同程序不在组内的下载 / 设置窗口一起关了，触发「取消所有传输」。 | `docs.architecture` | resolved |
| [问题：关闭窗口数量虚高（1 个窗口报 5 个）](../问题记录/history-close-count.md) | low | 只关 1 个窗口却提示关了 5 个。 | `docs.architecture` | resolved |
| [问题：Explorer 重启后胶囊被压住；6 组以上无法直达](../问题记录/history-explorer-overflow.md) | low | Explorer 重启后胶囊被压住；场景超过 7 个后只能看到 +N，无法直达。 | `docs.architecture` | resolved |
| [问题：保存场景后，原编组胶囊消失，切组变成「关闭再打开」（历史记录，1.1.1 已修）](../问题记录/1-1-1-scene-capsule.md) | medium | 保存场景后原编组胶囊消失，点场景胶囊走关闭再打开而不是隐藏 / 展开。 | `docs.architecture` | resolved |
| [问题：窗口关掉了，胶囊上的数字要等 2.5 s；「已关闭 N 个」也是假的（1.10.0）](../问题记录/1-10-0-events.md) | medium | 结束场景成员的进程后胶囊计数要等 2.5 秒；关闭窗口弹保存框却仍报「已关闭 N 个」。 | `docs.architecture` | resolved |
| [问题：编组胶囊展开后，再点没反应（历史记录，1.2.1 已修；GroupChip 已在 1.5.0 删除）](../问题记录/1-2-1-group-chip.md) | medium | README 承诺再点收起，实际点击无反应。 | `docs.architecture` | resolved |
| [问题：胶囊「看得见但点不到」，持续 0–30 s](../问题记录/1-7-0-chip-not-clickable.md) | medium | 胶囊截图和任务栏一致但点不到，持续 0–30 秒。 | `docs.architecture` | resolved |
| [问题：补 AutomationId 补了个寂寞——Border 在 UIA 里是透明的（1.9.1）](../问题记录/1-9-1-uia-button.md) | medium | 给面板 / 详情窗的点击控件挂 AutomationId，UIA 树里一个都找不到。 | `docs.architecture` | resolved |

## 全部页面

| id | 标题 | type | kind | status |
|---|---|---|---|---|
| `docs.architecture` | [架构说明](../架构.md) | explanation | note | active |
| `docs.design.backlog-plan` | [剩余待办的调研与实施建议](../design/backlog-plan.md) | explanation | decision | active |
| `docs.design.window-identity` | [窗口识别与弹窗归属（终版方案）](../design/window-identity.md) | explanation | decision | active |
| `docs.handoff` | [交接：进度、待办与发布路径](../handoff.md) | reference | note | active |
| `docs.index` | [文档索引](../index.md) | reference | index | active |
| `docs.problems.1-1-1-scene-capsule` | [问题：保存场景后，原编组胶囊消失，切组变成「关闭再打开」（历史记录，1.1.1 已修）](../问题记录/1-1-1-scene-capsule.md) | explanation | pitfall | resolved |
| `docs.problems.1-10-0-events` | [问题：窗口关掉了，胶囊上的数字要等 2.5 s；「已关闭 N 个」也是假的（1.10.0）](../问题记录/1-10-0-events.md) | explanation | pitfall | resolved |
| `docs.problems.1-11-0-identity` | [问题：浏览器切过标签页，场景重开时两个窗口互相换位（1.11.0）](../问题记录/1-11-0-identity.md) | explanation | pitfall | resolved |
| `docs.problems.1-13-7-remove-window` | [问题：移除的窗口又被关场景写回快照（1.13.7）](../问题记录/1-13-7-remove-window.md) | explanation | pitfall | resolved |
| `docs.problems.1-14-0-reattach` | [问题：场景里的程序关掉再打开，运行中的场景认不回来（1.14.0）](../问题记录/1-14-0-reattach.md) | explanation | pitfall | resolved |
| `docs.problems.1-2-1-group-chip` | [问题：编组胶囊展开后，再点没反应（历史记录，1.2.1 已修；GroupChip 已在 1.5.0 删除）](../问题记录/1-2-1-group-chip.md) | explanation | pitfall | resolved |
| `docs.problems.1-7-0-chip-not-clickable` | [问题：胶囊「看得见但点不到」，持续 0–30 s](../问题记录/1-7-0-chip-not-clickable.md) | explanation | pitfall | resolved |
| `docs.problems.1-7-1-chip-zorder` | [问题：胶囊「被任务栏压住」长达一个轮询周期（1.7.1）](../问题记录/1-7-1-chip-zorder.md) | explanation | pitfall | resolved |
| `docs.problems.1-8-1-scene-open` | [问题：单窗口模式打开场景后，其它窗口不最小化、场景还在 z 序最下面（1.8.1）](../问题记录/1-8-1-scene-open.md) | explanation | pitfall | resolved |
| `docs.problems.1-9-0-window-cluster` | [问题：浏览器插件弹窗 / 软件对话框被当成窗口收进场景（1.9.0）](../问题记录/1-9-0-window-cluster.md) | explanation | pitfall | resolved |
| `docs.problems.1-9-1-uia-button` | [问题：补 AutomationId 补了个寂寞——Border 在 UIA 里是透明的（1.9.1）](../问题记录/1-9-1-uia-button.md) | explanation | pitfall | resolved |
| `docs.problems.history-close-count` | [问题：关闭窗口数量虚高（1 个窗口报 5 个）](../问题记录/history-close-count.md) | explanation | pitfall | resolved |
| `docs.problems.history-close-wrong-windows` | [问题：关闭场景把同程序的其它窗口一起关了（下载弹窗）](../问题记录/history-close-wrong-windows.md) | explanation | pitfall | resolved |
| `docs.problems.history-explorer-overflow` | [问题：Explorer 重启后胶囊被压住；6 组以上无法直达](../问题记录/history-explorer-overflow.md) | explanation | pitfall | resolved |
| `docs.walkthrough.capabilities` | [走查能力清单](../walkthrough/capabilities.md) | reference | note | active |
| `docs.walkthrough.report1` | [第 1 轮走查报告](../walkthrough/report-1.md) | reference | report | active |
| `docs.walkthrough.scenarios` | [走查素材与覆盖矩阵](../walkthrough/scenarios.md) | reference | note | active |
