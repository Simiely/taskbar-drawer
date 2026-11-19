---
id: docs.problems.1-7-1-chip-zorder
title: 问题：胶囊「被任务栏压住」长达一个轮询周期（1.7.1）
summary: "任务栏和胶囊在同一个 topmost 波段里抢 z 序，旧实现只在每 2 s 的一轮体检里发现被压住，期间胶囊看得见但点不到；改成事件驱动后最坏 ≤250 ms。"
symptom: "点任务栏 / 切窗口后胶囊短暂看得见点不到，点击被任务栏吃掉。"
cause: "胶囊与任务栏同属 topmost 波段，旧实现只在 2 秒体检时复位。"
detect: "点任务栏后用 WindowFromPoint 查胶囊中心点，看是否命中 Shell_TrayWnd。"
avoid: "与系统 UI 抢层级必须用输入命中判断；驱动必须事件化，轮询只做兜底。"
recover: "EVENT_SYSTEM_FOREGROUND + Explorer 的 LOCATIONCHANGE + 120 ms 守卫；只抬 z 序，几何变了才重排。"
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
# 问题：胶囊「被任务栏压住」长达一个轮询周期（1.7.1）

**TL;DR**：任务栏和胶囊在同一个 topmost 波段里抢 z 序，旧实现只在每 2 s 的一轮体检里发现被压住，期间胶囊看得见但点不到；改成事件驱动后最坏 ≤250 ms。

- 问题：用户点任务栏 / 切窗口之后，胶囊有时会「灰一下」甚至点不动，点击被任务栏吃掉，可能误触相邻的任务栏按钮。
- 根因：不是判定错，是节奏错。`WindowFromPoint` 命中测试能正确识别「被任务栏或其子窗口压住」，但入口只有 `DispatcherTimer` 的 2 s 心跳，而任务栏抬自己发生在点击 / 激活 / 改几何这些瞬间。
- 为什么不换更硬的方案：DeskBand 在 Win11 已被移除；`SetWindowBand` 对普通进程永远返回 0x5，`CreateWindowInBand` 只允许 `ZBID_DEFAULT` / `ZBID_DESKTOP`，`ZBID_UIACCESS` 需要 UIAccess token（要签名证书 + 受信安装路径）；把胶囊 `SetParent` 到 `Shell_TrayWnd` 不受支持，Explorer 重建任务栏就丢。浮窗抢 z 序是唯一可行路径，只能把竞速赢下来。
- 解决：`EVENT_SYSTEM_FOREGROUND` 事件立即体检；按 Explorer 进程订阅 `EVENT_OBJECT_LOCATIONCHANGE` 覆盖几何变化；事件后 120 ms 自适应守卫 16 轮；复位用 `SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE` 只抬 z 序，把「抬起来」和「重新布局」彻底分开。
- 预防：任何「与系统 UI 抢层级」的窗口，判断标准应是「输入命中」（`WindowFromPoint`）而不是「矩形坐标」；而驱动必须是**事件**，轮询只能当兜底——轮询周期就是用户能感知到的故障时长。
- 刻意不做：被开始菜单 / 通知中心盖住时不抢，那属于正常遮挡；硬抢会让胶囊浮在浮层上面。
