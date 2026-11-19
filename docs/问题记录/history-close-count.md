---
id: docs.problems.history-close-count
title: 问题：关闭窗口数量虚高（1 个窗口报 5 个）
summary: "WinForms 程序的隐藏辅助窗口也通过了候选过滤，关闭计数按命中数返回。"
symptom: "只关 1 个窗口却提示关了 5 个。"
cause: "隐藏辅助窗口也通过候选过滤，计数按命中数返回。"
detect: "关闭一个带隐藏辅助窗口的程序，核对提示数字与用户可见窗口数。"
avoid: "计数口径与文案口径一致，隐藏窗口不算关掉。"
recover: "分别统计 visible / hidden，优先返回可见数量。"
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
# 问题：关闭窗口数量虚高（1 个窗口报 5 个）

**TL;DR**：WinForms 程序的隐藏辅助窗口也通过了候选过滤，关闭计数按命中数返回。

- 问题：toast / 反馈里的数字和用户感知不一致。
- 根因：只统计了「发送了 WM_CLOSE 的窗口」，没区分可见 / 隐藏。
- 解决：分别统计 visible / hidden，优先返回可见数量；反馈文案只报用户能看到的窗口。
- 预防：计数口径要跟文案口径一致，隐藏窗口不算「关掉了几个」。
