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
| [剩余待办的调研与实施建议](../design/backlog-plan.md) | decision | active | @simiely | normal | `docs/handoff.md` |
| [窗口识别与弹窗归属（终版方案）](../design/window-identity.md) | decision | active | @simiely | normal | `src/TaskbarDrawer/Core/WindowEnumerator.cs` |

## Reports

| 页面 | status | owner | tier | source |
|---|---|---|---|---|
| [第 1 轮走查报告](../walkthrough/report-1.md) | active | @simiely | normal | `docs/walkthrough/scenarios.md` |

## 全部页面

| id | 标题 | type | kind | status |
|---|---|---|---|---|
| `docs.design.backlog-plan` | [剩余待办的调研与实施建议](../design/backlog-plan.md) | explanation | decision | active |
| `docs.design.window-identity` | [窗口识别与弹窗归属（终版方案）](../design/window-identity.md) | explanation | decision | active |
| `docs.handoff` | [交接：进度、待办与发布路径](../handoff.md) | reference | note | active |
| `docs.index` | [文档索引](../index.md) | reference | index | active |
| `docs.walkthrough.capabilities` | [走查能力清单](../walkthrough/capabilities.md) | reference | note | active |
| `docs.walkthrough.report1` | [第 1 轮走查报告](../walkthrough/report-1.md) | reference | report | active |
| `docs.walkthrough.scenarios` | [走查素材与覆盖矩阵](../walkthrough/scenarios.md) | reference | note | active |
