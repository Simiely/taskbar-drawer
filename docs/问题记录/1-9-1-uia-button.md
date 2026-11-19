---
id: docs.problems.1-9-1-uia-button
title: 问题：补 AutomationId 补了个寂寞——Border 在 UIA 里是透明的（1.9.1）
summary: "计划里写的是「给关键控件补 `AutomationProperties.AutomationId`」，但本项目的按钮是带 `MouseLeftButtonUp` 的 `Border`。`Border` 没有 automation peer，UIA 树里根本不存在，挂上去的 ID 谁也读不到。先把控件换成真正的 `Button`，ID 才有意义。"
symptom: "给面板 / 详情窗的点击控件挂 AutomationId，UIA 树里一个都找不到。"
cause: "控件是带 MouseLeftButtonUp 的 Border，没有 automation peer。"
detect: "离屏夹具用 AutomationElement.FromHandle + ControlView / RawView 查找 AutomationId。"
avoid: "可点控件必须用真正的 Button，不要手搓 Border + MouseLeftButtonUp。"
recover: "Ui.ActionButton / CardButton / DangerButton 换成 Button + ControlTemplate。"
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
# 问题：补 AutomationId 补了个寂寞——Border 在 UIA 里是透明的（1.9.1）

**TL;DR**：计划里写的是「给关键控件补 `AutomationProperties.AutomationId`」，但本项目的按钮是带 `MouseLeftButtonUp` 的 `Border`。`Border` 没有 automation peer，UIA 树里根本不存在，挂上去的 ID 谁也读不到。先把控件换成真正的 `Button`，ID 才有意义。

- 问题：按计划给面板 / 详情窗补 ID，用 UIA 检查器看——一个都找不到。
- 定位方法（可复用，夹具本身不入库，按这段描述十几行就能重建）：写一个离屏夹具，同进程内用 UIA 客户端查自己（`AutomationElement.FromHandle` + `TreeWalker.ControlViewWalker` / `RawViewWalker` 遍历，`FindFirst(PropertyCondition(AutomationIdProperty, id))` 查找）。实测结果：`Border` + `AutomationId` + `Name` 在 **ControlView 和 RawView 里都找不到**（只有一个 `ControlType.Text` 子元素冒出文字）；`Button` 完整暴露 `ControlType.Button` + id + name，`TryGetCurrentPattern(InvokePattern.Pattern)` 为真，`Focusable=false` 也能 `Invoke` 且 Click 真的到达。
- 解决：`Ui.ActionButton` 改成返回 `Button` + 自定义 `ControlTemplate`（圆角底板 + `ContentPresenter`，`Padding` 走 `TemplateBinding` 到内容 `Margin`）。外观逐项对齐原 `Border`：底色 / `CornerRadius 8` / `Padding 11×5` / 悬停变色（触发放在 `Style` 里，局部 `Background` 仍是更高优先级，所以说明窗那个透明 `✕` 的自定义悬停照旧生效）/ 禁用 `Opacity 0.45` + 箭头光标。卡片行与危险按钮各自成一套样式（`CardButton` / `DangerButton`）。
- 嵌套按钮是安全的：卡片行现在是 `Button`，里面还套着「详情」。内层 `ButtonBase` 在 `MouseLeftButtonDown` 就把事件标记 handled 并捕获鼠标，外层收不到、`IsPressed` 不成立，点「详情」不会顺带触发整行开合。
- 视觉怎么验：`RenderTargetBitmap` 把三个窗导出 PNG，和「改造前的 `Border` 版」并排看。这比"应该差不多"可靠——按钮底色 / 圆角 / 内边距这种差异肉眼在小图上很难发现。
- 预防：UI 里凡是「可点的东西」，一律走 `Ui.ActionButton` / `Ui.CardButton` / `Ui.DangerButton`，不要手搓 `Border` + `MouseLeftButtonUp`。手搓的代价不是样式不一致，而是**这个控件对自动化和读屏永久隐身**。
