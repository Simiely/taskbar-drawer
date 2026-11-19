---
id: docs.problems.1-10-0-events
title: 问题：窗口关掉了，胶囊上的数字要等 2.5 s；「已关闭 N 个」也是假的（1.10.0）
summary: "状态变化只有一个 2.5 s 轮询在驱动，而「关闭窗口」报的是**投递出去**的 `WM_CLOSE` 个数——目标程序弹一个保存框，一个都没关，提示照样说「已关闭 3 个窗口」。两件事要的是同一批信号，合在一个钩子里做。"
symptom: "结束场景成员的进程后胶囊计数要等 2.5 秒；关闭窗口弹保存框却仍报「已关闭 N 个」。"
cause: "状态只靠 2.5 秒轮询；关闭把 PostMessage 投递成功当成已关闭。"
detect: "从任务管理器结束一个成员，计时胶囊计数变化；关闭一个会弹保存框的窗口，看提示数字。"
avoid: "状态变化用事件驱动；关闭数量必须延时复核实际结果。"
recover: "按 PID 订阅 DESTROY / SHOW / HIDE（150 ms 合并）；CloseReport 250 / 800 ms 复核。"
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
# 问题：窗口关掉了，胶囊上的数字要等 2.5 s；「已关闭 N 个」也是假的（1.10.0）

**TL;DR**：状态变化只有一个 2.5 s 轮询在驱动，而「关闭窗口」报的是**投递出去**的 `WM_CLOSE` 个数——目标程序弹一个保存框，一个都没关，提示照样说「已关闭 3 个窗口」。两件事要的是同一批信号，合在一个钩子里做。

- 问题一：场景收起中，从任务管理器结束一个成员进程，胶囊计数最多要等 2.5 s 才变。
- 问题二：点「关闭窗口」时，程序弹「是否保存」，提示已经报出「已关闭 3 个窗口」——数字来自 `PostMessage` 的返回值，不是实际结果。
- 根因：唯一的信号源是 `DispatcherTimer` 轮询；关闭路径把「投递成功」当成了「已关闭」。
- 解决一：按被跟踪进程订阅 `EVENT_OBJECT_DESTROY / SHOW / HIDE`。**一个进程一个钩子**，因为 `SetWinEventHook` 的 `idProcess` 是注册期过滤——装成一个全局钩子等于把所有菜单 / 提示框的显示隐藏都收进回调。事件合并 150 ms 再对账（一次最小化会连着发好几条）。
- 解决二：`Close` 返回投出去的句柄，`CloseReport.Measure` 在 250 ms 后复核；还有活着的再等 800 ms，按实际数字报「已关闭 2 个，1 个仍在等待响应」。
- **不能一刀切全事件化**：图标重贴没有对应事件——应用换 favicon 走 `WM_SETICON`，它不发 WinEvent，SHELL 层的 `EVENT_OBJECT_NAMECHANGE` 也不是每个程序都发。所以 `Prune()` 只留状态对账，`ReapplyIcons()` 交给 5 s 定时器。把没有事件的活儿硬塞进事件钩子，结果是「钩子装了、图标还是不刷新」。
- 两个实测出来的细节：① 事件是投递到**安装线程的消息队列**的，必须在有消息循环的线程上装（UI 线程），测试进程里装不了，所以过滤规则抽成了纯函数 `Classify`；② 一个进程会有一堆自己的辅助窗口（WPF `HwndWrapper`、浏览器标签页），关掉一个进程能收到十几条 `DESTROY`，必须按「这个句柄在不在记录里」先筛一遍，否则每关一个程序都在白白触发对账。
- 实测延迟：`SW_HIDE` → Hide 5 ms、`SW_SHOWNA` → Show 5 ms、`WM_CLOSE` → 销毁 16–24 ms；**窗口已被收起（`SW_HIDE`）再从任务管理器强杀 → 5 ms 收到 `DESTROY`**（这才是真实场景：收起中的成员没有可见性变化，只测可见窗口等于没测）。加 150 ms 合并窗口后仍远小于旧的 2.5 s。
