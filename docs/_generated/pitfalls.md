---
id: docs.generated.pitfalls
title: 踩坑索引
summary: 从 kind=pitfall 页面自动生成的失败模式索引；勿手改。
type: reference
kind: index
status: active
owner: "@platform"
source_type: doc
source: schema/taxonomy.json
last_reviewed: 2026-11-11
related: []
---

# 踩坑索引

> 生成文件，勿手改。运行 `python tools/repo-kit/generate.py --root . --write` 重建。
> 每个坑的权威内容在它自己的页面；本页只做导航。

## 未解决（0）

（无）

## 已解决（14）

| 严重度 | 坑 | 症状 | 适用 |
|---|---|---|---|
| high | [问题：浏览器切过标签页，场景重开时两个窗口互相换位（1.11.0）](../问题记录/1-11-0-identity.md) | 同程序两个窗口切换标签页后收起并重开，两个窗口回到对方的位置，甚至重复启动。 | `docs.architecture` |
| high | [问题：移除的窗口又被关场景写回快照（1.13.7）](../问题记录/1-13-7-remove-window.md) | 从场景详情移除窗口后，关闭场景再打开，被移除的窗口又出现在场景里。 | `docs.architecture` |
| high | [问题：场景里的程序关掉再打开，运行中的场景认不回来（1.14.0）](../问题记录/1-14-0-reattach.md) | 场景展开时关掉其中一个程序，重新打开后新窗口不跟场景收起 / 展开，胶囊计数也不含它。 | `docs.architecture` |
| high | [问题：胶囊「被任务栏压住」长达一个轮询周期（1.7.1）](../问题记录/1-7-1-chip-zorder.md) | 点任务栏 / 切窗口后胶囊短暂看得见点不到，点击被任务栏吃掉。 | `docs.architecture` |
| high | [问题：单窗口模式打开场景后，其它窗口不最小化、场景还在 z 序最下面（1.8.1）](../问题记录/1-8-1-scene-open.md) | 单窗口模式点胶囊打开场景，其它窗口不动，场景窗口停在 z 序最下面。 | `docs.architecture` |
| high | [问题：浏览器插件弹窗 / 软件对话框被当成窗口收进场景（1.9.0）](../问题记录/1-9-0-window-cluster.md) | 浏览器扩展弹窗 / 软件对话框被当成场景成员，关闭场景时主窗口一起被关。 | `docs.architecture` |
| high | [问题：关闭场景把同程序的其它窗口一起关了（下载弹窗）](../问题记录/history-close-wrong-windows.md) | 关闭场景把同程序不在组内的下载 / 设置窗口一起关了，触发「取消所有传输」。 | `docs.architecture` |
| medium | [问题：保存场景后，原编组胶囊消失，切组变成「关闭再打开」（历史记录，1.1.1 已修）](../问题记录/1-1-1-scene-capsule.md) | 保存场景后原编组胶囊消失，点场景胶囊走关闭再打开而不是隐藏 / 展开。 | `docs.architecture` |
| medium | [问题：窗口关掉了，胶囊上的数字要等 2.5 s；「已关闭 N 个」也是假的（1.10.0）](../问题记录/1-10-0-events.md) | 结束场景成员的进程后胶囊计数要等 2.5 秒；关闭窗口弹保存框却仍报「已关闭 N 个」。 | `docs.architecture` |
| medium | [问题：编组胶囊展开后，再点没反应（历史记录，1.2.1 已修；GroupChip 已在 1.5.0 删除）](../问题记录/1-2-1-group-chip.md) | README 承诺再点收起，实际点击无反应。 | `docs.architecture` |
| medium | [问题：胶囊「看得见但点不到」，持续 0–30 s](../问题记录/1-7-0-chip-not-clickable.md) | 胶囊截图和任务栏一致但点不到，持续 0–30 秒。 | `docs.architecture` |
| medium | [问题：补 AutomationId 补了个寂寞——Border 在 UIA 里是透明的（1.9.1）](../问题记录/1-9-1-uia-button.md) | 给面板 / 详情窗的点击控件挂 AutomationId，UIA 树里一个都找不到。 | `docs.architecture` |
| low | [问题：关闭窗口数量虚高（1 个窗口报 5 个）](../问题记录/history-close-count.md) | 只关 1 个窗口却提示关了 5 个。 | `docs.architecture` |
| low | [问题：Explorer 重启后胶囊被压住；6 组以上无法直达](../问题记录/history-explorer-overflow.md) | Explorer 重启后胶囊被压住；场景超过 7 个后只能看到 +N，无法直达。 | `docs.architecture` |

## 按适用范围

### `docs.architecture`

- [问题：浏览器切过标签页，场景重开时两个窗口互相换位（1.11.0）](../问题记录/1-11-0-identity.md)（high）
- [问题：移除的窗口又被关场景写回快照（1.13.7）](../问题记录/1-13-7-remove-window.md)（high）
- [问题：场景里的程序关掉再打开，运行中的场景认不回来（1.14.0）](../问题记录/1-14-0-reattach.md)（high）
- [问题：胶囊「被任务栏压住」长达一个轮询周期（1.7.1）](../问题记录/1-7-1-chip-zorder.md)（high）
- [问题：单窗口模式打开场景后，其它窗口不最小化、场景还在 z 序最下面（1.8.1）](../问题记录/1-8-1-scene-open.md)（high）
- [问题：浏览器插件弹窗 / 软件对话框被当成窗口收进场景（1.9.0）](../问题记录/1-9-0-window-cluster.md)（high）
- [问题：关闭场景把同程序的其它窗口一起关了（下载弹窗）](../问题记录/history-close-wrong-windows.md)（high）
- [问题：保存场景后，原编组胶囊消失，切组变成「关闭再打开」（历史记录，1.1.1 已修）](../问题记录/1-1-1-scene-capsule.md)（medium）
- [问题：窗口关掉了，胶囊上的数字要等 2.5 s；「已关闭 N 个」也是假的（1.10.0）](../问题记录/1-10-0-events.md)（medium）
- [问题：编组胶囊展开后，再点没反应（历史记录，1.2.1 已修；GroupChip 已在 1.5.0 删除）](../问题记录/1-2-1-group-chip.md)（medium）
- [问题：胶囊「看得见但点不到」，持续 0–30 s](../问题记录/1-7-0-chip-not-clickable.md)（medium）
- [问题：补 AutomationId 补了个寂寞——Border 在 UIA 里是透明的（1.9.1）](../问题记录/1-9-1-uia-button.md)（medium）
- [问题：关闭窗口数量虚高（1 个窗口报 5 个）](../问题记录/history-close-count.md)（low）
- [问题：Explorer 重启后胶囊被压住；6 组以上无法直达](../问题记录/history-explorer-overflow.md)（low）
