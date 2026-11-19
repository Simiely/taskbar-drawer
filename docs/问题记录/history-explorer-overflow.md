---
id: docs.problems.history-explorer-overflow
title: 问题：Explorer 重启后胶囊被压住；6 组以上无法直达
summary: "任务栏句柄变化时矩形可能完全相同，只比较 RECT 会跳过重贴；胶囊超过 7 枚后早期只能看到 `+N`，没有逐组入口。"
symptom: "Explorer 重启后胶囊被压住；场景超过 7 个后只能看到 +N，无法直达。"
cause: "矩形比较跳过重贴，任务栏句柄变化但几何相同；溢出槽没有逐组入口。"
detect: "重启 Explorer 后点胶囊；创建 8 个以上场景，检查溢出槽。"
avoid: "外部环境变化要比较身份（句柄），不能只比较几何；溢出要有逐项入口。"
recover: "变化检测加入 Shell_TrayWnd 句柄；溢出槽列出每组名字可点 / 右键。"
applies_to: ["docs.architecture"]
severity: low
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
# 问题：Explorer 重启后胶囊被压住；6 组以上无法直达

**TL;DR**：任务栏句柄变化时矩形可能完全相同，只比较 RECT 会跳过重贴；胶囊超过 7 枚后早期只能看到 `+N`，没有逐组入口。

- 解决：变化检测加入 `Shell_TrayWnd` 句柄比较，句柄变了强制重排；溢出槽列出每一组的名字，可直接点 / 右键。
- 预防：判断「外部环境是否变化」要比较身份（句柄），不能只比较几何。
