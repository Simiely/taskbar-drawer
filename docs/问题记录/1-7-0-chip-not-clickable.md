---
id: docs.problems.1-7-0-chip-not-clickable
title: 问题：胶囊「看得见但点不到」，持续 0–30 s
summary: "胶囊与任务栏共用 topmost 波段，且几何上落在任务栏矩形内；点一次任务栏，Explorer 就被抬到胶囊之上，输入全落到 `MSTaskListWClass`。"
symptom: "胶囊截图和任务栏一致但点不到，持续 0–30 秒。"
cause: "只比较矩形变化，任务栏被点击后同波段更靠前，胶囊没被重新抬起。"
detect: "点一次任务栏，再点胶囊，确认点击是否落到 MSTaskListWClass。"
avoid: "贴任务栏 / 屏幕边缘的自绘窗口要按输入命中判断可点性。"
recover: "2 秒 tick 用 WindowFromPoint 体检并强制重贴；1.7.1 起改为事件驱动 + 守卫。"
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
# 问题：胶囊「看得见但点不到」，持续 0–30 s

**TL;DR**：胶囊与任务栏共用 topmost 波段，且几何上落在任务栏矩形内；点一次任务栏，Explorer 就被抬到胶囊之上，输入全落到 `MSTaskListWClass`。

- 问题：失效期间截图逐像素一致（任务栏半透明，胶囊从 DWM 合成里透出来），用户会以为程序卡死，甚至误点到别的任务栏按钮。
- 根因：`SetWindowPos(HWND_TOPMOST)` 只保证在 topmost 波段内，任务栏被点击后也在同一波段里更靠前；原实现只在矩形变化时才重贴。
- 解决：每个 2 s tick 先做 z 序体检——取胶囊中心点 `WindowFromPoint`，命中不是自己且根窗口是 `Shell_TrayWnd` → 判定被盖，强制完整重贴；未被盖不重绘，避免闪烁。
- 预防：任何「贴任务栏 / 贴屏幕边缘」的自绘窗口，都要按「输入命中」而不是「矩形坐标」判断自己是否可点。
- 效果：自愈耗时从 10.1 / 20.0 / 27.2 s 降到 700 / 1500 / 1600 ms。

> 1.7.1 起驱动方式改为事件 + 自适应守卫（见上一节），这段「2 s tick 体检」只作为兜底保留；上表里的 700–1600 ms 就是轮询周期的代价。
