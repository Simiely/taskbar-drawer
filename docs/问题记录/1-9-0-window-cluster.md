---
id: docs.problems.1-9-0-window-cluster
title: 问题：浏览器插件弹窗 / 软件对话框被当成窗口收进场景（1.9.0）
summary: "候选窗口枚举缺了「被拥有的窗口」这一条判据，弹窗成了场景成员，占条目、污染配对、关场景时连坐主窗口。修法是「窗口簇」：只收无 owner 的簇主，owned 弹窗跟着隐藏但不进场景。"
symptom: "浏览器扩展弹窗 / 软件对话框被当成场景成员，关闭场景时主窗口一起被关。"
cause: "候选窗口过滤漏了 owner；有 owner 的窗口是弹窗 / 对话框，不是独立窗口。"
detect: "打开浏览器扩展弹窗后收纳，检查场景成员列表和关闭场景的连坐行为。"
avoid: "准入只看 GW_OWNER（WS_EX_APPWINDOW 豁免），不要照抄 Alt+Tab 的 GetLastActivePopup。"
recover: "按窗口簇处理：只收簇主，owned 弹窗随簇主隐藏 / 恢复但不进场景；SW_HIDE 时显式隐藏弹窗。"
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
# 问题：浏览器插件弹窗 / 软件对话框被当成窗口收进场景（1.9.0）

**TL;DR**：候选窗口枚举缺了「被拥有的窗口」这一条判据，弹窗成了场景成员，占条目、污染配对、关场景时连坐主窗口。修法是「窗口簇」：只收无 owner 的簇主，owned 弹窗跟着隐藏但不进场景。

- 问题：浏览器里点开扩展弹窗后收纳，弹窗变成场景里的一个成员；之后关闭场景，主窗口被一起关掉。
- 根因：`WindowEnumerator` 只挡了工具窗、cloaked、无标题、shell 类，漏了 owner。对话框 / 弹出层的标志就是有 owner——Chromium 的扩展弹窗源码里就是用 `GetWindow(child, GW_OWNER) == owner` 判断归属的。
- 为什么不能照抄 Alt+Tab 判据：微软给的 Alt+Tab 算法会在有可见对话框时把**对话框**当成这个簇的代表、把主窗口排除。对切换器是对的，对收纳正好相反。
- 解决：准入只看 `GetWindow(hwnd, GW_OWNER) == 0`（`WS_EX_APPWINDOW` 豁免，**不是** `GA_ROOTOWNER`——见 1.13.4）；配对候选、追加列表同样排除 owned；快照落盘前再判一次。**必须连带处理隐藏**：`SW_HIDE` 主窗口不会联动 owned 窗口（系统只在最小化 / 销毁时联动），所以收纳时把同簇可见弹窗一起藏（`StashedWindow.OwnedHidden`），展开 / 删除场景 / 退出时放回，崩溃恢复时也补放一次。关闭只对簇主发 `WM_CLOSE`，并跳过「根 owner 也在本组」的句柄。
- 预防：凡是「这个窗口算不算用户的窗口」的判断，只有一个出处（`WindowEnumerator.ExclusionReason`），枚举和 `--probe` 共用；新增一条规则时同步给 `--probe` 加一类原因，误杀才能一眼定位。
- 一个反直觉的点：`WS_EX_APPWINDOW` 的语义是「当作没有 owner」。WinForms 默认窗体会带上这个样式，所以「有 owner 但要上任务栏」的窗口本来就该算窗口——豁免不是补丁，是系统语义。
