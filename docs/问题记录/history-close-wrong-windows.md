---
id: docs.problems.history-close-wrong-windows
title: 问题：关闭场景把同程序的其它窗口一起关了（下载弹窗）
summary: "关闭逻辑按 exe 路径枚举该程序所有顶层窗口并统一发 `WM_CLOSE`，把不属于本组的下载窗口也关了，浏览器 / 下载器各自弹出「这将取消所有传输」。"
symptom: "关闭场景把同程序不在组内的下载 / 设置窗口一起关了，触发「取消所有传输」。"
cause: "关闭按 exe 路径枚举该程序所有顶层窗口并发 WM_CLOSE。"
detect: "同程序开一个场景成员和一个下载窗口，关闭场景，确认下载窗口是否被关。"
avoid: "关闭动作的作用域必须是显式记录的句柄，不能用模糊匹配反推。"
recover: "运行时组只对组内 hwnd 发 WM_CLOSE；路径只用于重新启动。"
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
# 问题：关闭场景把同程序的其它窗口一起关了（下载弹窗）

**TL;DR**：关闭逻辑按 exe 路径枚举该程序所有顶层窗口并统一发 `WM_CLOSE`，把不属于本组的下载窗口也关了，浏览器 / 下载器各自弹出「这将取消所有传输」。

- 问题：用户只想关组内窗口，结果丢传输任务。
- 根因：场景只存了 exe 路径，关闭时用路径反查窗口；而同一程序可能有多个独立窗口（下载、设置、弹窗）。
- 解决：运行时组始终持有真实句柄，关闭只对这些 hwnd 发 `WM_CLOSE`；路径只用于「窗口已不存在时重新启动」。
- 预防：任何「关闭」动作的作用域必须是显式记录的对象，不能用模糊匹配反推。
