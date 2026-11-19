---
id: docs.index
title: 文档索引
summary: 项目入口：用户说明、项目规则、开发文档与版本历史的导航。
type: reference
kind: index
status: active
owner: "@simiely"
tier: normal
topics: [governance]
source_type: doc
source: README.md
last_reviewed: 2026-11-19
related: []
---
# 文档索引

按读者选择入口，本页只做导航，不复制内容。

- [README](../README.md) — 用户视角：功能、系统要求、快速开始、命令行
- [AGENTS](../AGENTS.md) — 项目规则：技术栈、关键坑、约定、入口规则、常用命令
- [DEVELOPMENT](../DEVELOPMENT.md) — 开发视角：项目概览、架构总览、问题索引
- [架构说明](架构.md) — 分层、组合根、场景服务、单窗口模式与关键数据流
- [问题记录（索引）](_generated/pitfalls.md) — 14 个已解决问题的 TL;DR / 根因 / 方案
- [交接](handoff.md) — 接手入口：当前状态、优化待办、未落地方案、发布与验收
- [CHANGELOG](../CHANGELOG.md) — 版本历史

走查（scenario-walkthrough 方法）：

- [能力清单](walkthrough/capabilities.md) — 真实能力 C-01…C-22 与入口，素材生成的唯一依据
- [素材与覆盖矩阵](walkthrough/scenarios.md) — 18 条操作链 S-01…S-18 + 矩阵对账
- [第 1 轮报告](walkthrough/report-1.md) — 13 个问题、已修 11、实机回归步骤

设计决策：

- [窗口识别模型改造建议](design/window-identity.md) — 两层身份（AUMID + 类名 + 序号）、弹窗准入三道闸、详情窗状态标识
- [剩余待办的调研与实施建议](design/backlog-plan.md) — 9 项待办逐项检索后的取舍：做 5 项、暂缓 1 项、不做 2 项，含顺序与验收

新增跨模块文档时放在 `docs/` 并带 frontmatter，格式见 `_template.md`。
