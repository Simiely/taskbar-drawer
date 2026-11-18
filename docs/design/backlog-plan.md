---
id: docs.design.backlog-plan
title: 剩余待办的调研与实施建议
summary: 对 9 项未完成待办逐项检索验证后给出结论：做 5 项、暂缓 1 项、不做 2 项，并给出依赖顺序与验收方式。
type: explanation
kind: decision
status: active
owner: "@simiely"
tier: normal
topics: [development]
source_type: doc
source: docs/handoff.md
last_reviewed: 2026-11-18
related: []
---
# 剩余待办的调研与实施建议

> 每项都先查外部依据再下结论。结论只有三种：**做**（给方案）、**暂缓**（给触发条件）、**不做**（给理由）。

## 一、待办现状

| 项 | 原描述 | 结论 |
| --- | --- | --- |
| B-1 | `Prune` 改事件驱动替代 2.5 s 轮询 | 做，但**不能一刀切**（见 2.1） |
| B-2 | W-12「已关闭 N 个窗口」计数口径 | 做，**与 B-1 合并**（同一个钩子） |
| B-3 | 窗口识别 P3：两层身份 + 位次配对 | 做，**排在所有重构之前** |
| B-4 | 引入 `IWindowGateway`，让状态机可单测 | 做，但**缩小到最小端口** |
| B-5 | 拆分 `WorkspaceService` | **暂缓**，等 B-3 落地 |
| B-6 | 拆分 `MainPanelWindow` / `SceneEditorWindow` | **暂缓**，只在下次改这两个窗口时顺手拆 |
| B-7 | W-13 `HelpWindow` 高度上限 | 直接做（1 行） |
| B-8 | （已知限制）胶囊只贴主任务栏 | **不做**（见 2.6） |
| B-9 | （隐含）用 FlaUI / WinAppDriver 做 E2E | **不建套件**，但先补 `AutomationId` |

另：「任务栏残留圆点」在 P0 已修完，handoff 里那节是过期描述，本次一并清理。

## 二、逐项调研

### 2.1 B-1 Prune 事件驱动：能事件化三分之二，图标重贴必须留轮询

**外部依据**

- Raymond Chen 两篇文章确认：`SetWinEventHook` 用 `WINEVENT_OUTOFCONTEXT` 时不需要注入 DLL，能收到 32 / 64 位乃至提权进程的窗口事件；同时反复强调「别挂太宽的钩子，否则会被事件刷爆、白耗 CPU」，应尽量按进程 / 线程收窄。
- Chromium 自己的 `NativeWindowOcclusionTrackerWin` 就是这套做法：全局订阅 `EVENT_SYSTEM_*`，`EVENT_OBJECT_DESTROY / HIDE` 全局订阅后用已知窗口集合过滤，`EVENT_OBJECT_LOCATIONCHANGE` **按 PID 逐个订阅**（因为它事件量太大）。

**`Prune` 现在同时干三件事**，其中只有两件有对应事件：

| 职责 | 有无事件 | 结论 |
| --- | --- | --- |
| (a) 检测被外部销毁的窗口 | `EVENT_OBJECT_DESTROY` | 事件驱动 |
| (b) 检测被外部还原（又变可见） | `EVENT_OBJECT_SHOW` / `HIDE` | 事件驱动 |
| (c) 每 2.5 s 重贴场景图标 | **没有**——应用换 favicon 走 `WM_SETICON`，不产生无障碍事件 | 保留轮询，放宽到 5 s |

**方案**：新增一个监听器，订阅 `EVENT_OBJECT_DESTROY .. EVENT_OBJECT_SHOW`，`idProcess` 只传「当前被跟踪窗口所属的 PID」（通常 1–3 个）；回调里用 hwnd 集合过滤，命中才走逻辑。`(c)` 拆成独立 5 s 定时器。钩子回调运行在 UI 线程，必须沿用现有 `Safe(scope, …)` 就地隔离 + 防重入。

**收益**：销毁 / 还原的检测延迟从 2.5 s 降到毫秒级；跨进程 `WM_GETICON` 次数减少约一半（5 s vs 2.5 s）。**风险**：钩子挂宽了会刷 CPU，所以必须按 PID 收窄，这一点照 Chromium 的做法即可。

### 2.2 B-2 关闭计数：唯一可靠信号就是 DESTROY 事件

`PostMessage(WM_CLOSE)` 只代表「请求已送达」。目标程序弹保存框、或者干脆拒绝关闭时，窗口一个都没少，而我们现在报「已关闭 3 个窗口」。

Raymond Chen 专门写过如何监听别的窗口被销毁：`SetWinEventHook(EVENT_OBJECT_DESTROY, …, pid, tid, WINEVENT_OUTOFCONTEXT)`，并在回调里比对 hwnd。**这正好是 B-1 要建的同一个钩子**，所以两项合并做：发出 `WM_CLOSE` 后收集 1.5 s 内的 DESTROY 事件，按 hwnd 命中计数，再给最终文案「已关闭 2 个，1 个仍在等待响应」。分成两次做会变成两套钩子，没有理由。

### 2.3 B-3 两层身份：唯一真正解决「标题一变就认不回来」的方案

上一轮已调研清楚（见 [window-identity.md](window-identity.md)）：AUMID 只对打包应用有效、桌面应用取不到；位次按保存时的矩形 `(Top, Left)` 排序才跨重启稳定。**结论不变，且应优先于任何重构**——它是用户可感知的痛点，而重构只是内部收益。

风险控制已定：旧快照字段为空即走旧路径，不迁移；新增纯规则（类名 + 位次计算）配单测；`PickRestoreCandidate` 的现有四级保留给旧数据。

### 2.4 B-4 IWindowGateway：做，但别做成巨型接口

**外部依据**：FlaUI / WinAppDriver 这类 UI 自动化测的是「屏幕上的控件」，看不见内部状态机；`StashAllService` 的收起 / 展开 / Adopt / Detach 迁移只能靠单元测试覆盖。这是标准的端口-适配器（seam）做法，没有更省的路。

**关键约束**：不要给 `NativeMethods` 每个函数都建接口——那会得到一个 30 多个方法的巨型接口，测试里全是没人读的桩。只抽**状态机真正依赖的动作**（约 7 个）：`IsWindow` / `Hide` / `Show` / `Restore` / `CapturePlacement` / `Track` / `Enumerate`。

**目标**：给 `StashAllService` 的状态迁移矩阵写测试（StashAll → ExpandGroup → StashGroup → Release → Adopt → Detach 的组合），断言「句柄集合 + Hidden 标志 + Changed 触发次数」。接口放 `Core`，Win32 实现在 `Interop`，测试用假 Gateway。

### 2.5 B-5 / B-6 两个拆分：都暂缓，理由不同

- **`WorkspaceService`（B-5）**：1.9.0 刚给它加了 `Owned` 过滤、`OpenKeys`、`Close` 的簇主逻辑，B-3 还会大改 `Restore` 与配对。此刻拆会把「行为变更」和「结构变更」混在一起，出问题分不清是哪边。**触发条件**：B-3 落地后、B-4 的单测到位时再拆。
- **`MainPanelWindow` / `SceneEditorWindow`（B-6）**：纯代码 WPF，没有自动化 UI 测试兜底，拆构建方法的收益低于回归风险。**触发条件**：下次要改这两个窗口的布局时顺手拆，不单独开一轮。

### 2.6 B-8 副屏任务栏胶囊：不做

**外部依据（两条实证）**

- 副屏任务栏类名是 `Shell_SecondaryTrayWnd`，但它**没有 `TrayNotifyWnd`**——一个正在维护的同类工具在 issue 里写得很清楚：它右侧锚点失效就是因为副屏没有通知区窗口，最后退化成「按 DPI 估算时钟宽度」。
- Win11 副屏的时钟是 XAML 渲染、没有经典子窗口句柄，要精确定位只能上 UI Automation（AutomationId `SystemTrayIcon`），而且随 locale / DPI 变化。

我们胶囊的锚点恰好就是 `TrayNotifyWnd`。要在副屏贴胶囊，等于要么引 UIA 找托盘图标、要么自己估算时钟宽度——**为一个「多一个入口」的收益引入 UIA 依赖和多套 DPI 分支，不划算**。保持现状：副屏用热键 / 主面板操作，README 已列为已知限制。

### 2.7 B-9 E2E：不建套件，但先补 AutomationId

**外部依据**

- WinAppDriver 自 2020 年 11 月起实质停更，1000+ open issues，最新版依赖已 EOL 的 .NET 5，用的还是废弃的 JSON Wire Protocol。**排除**。
- FlaUI 是当前唯一活跃选项：v5.0.0（2025-02）、MIT、支持 .NET 8、UIA2/UIA3 双后端，Win32 / WinForms / WPF / Store 都支持。

**但有个前提问题**：FlaUI 定位首选 `AutomationId`；我们是**纯代码 WPF，没有 `x:Name`，所以 AutomationId 全为空**，只能退回 `Name`（中文文案）定位——文案一改测试就碎。

所以真正的第一步不是写测试，而是**给关键控件补 `AutomationProperties.AutomationId`**（主面板的场景行 / 开关 / 滑块，详情窗的移除 / 追加 / 状态标记，说明窗口的关闭）。这件事本身就有回报：现在的人工 UIA 验证会更稳，以后想上 FlaUI 也有基础。

> **1.9.1 执行时发现的前提问题**：我们的「按钮」是带 `MouseLeftButtonUp` 的 `Border`，而 `Border` 没有 automation peer——挂上去的 `AutomationId` 在 UIA 树里**根本不存在**（离屏夹具实测 ControlView / RawView 都查不到）。所以真正的第一步是先把 `Ui.ActionButton` / `CardButton` / `DangerButton` 换成真 `Button` + 自定义模板，ID 才有落点。已完成，见 [DEVELOPMENT](../../DEVELOPMENT.md) 三、关键问题。

**仍然不做 E2E 套件**：托盘菜单、全局热键、任务栏胶囊这三类交互不适合自动化（会真的操作桌面、误触发收纳），继续用「手工 + 日志断言」。

## 三、建议顺序

| 阶段 | 内容 | 依赖 | 估时 |
| --- | --- | --- | --- |
| **S1** | ✅ 已完成（1.9.1）：B-7（`HelpWindow` 高度）+ 关键控件 `AutomationId`，含「`Border` → 真 `Button`」这一步前置改造 | 无 | 半天 |
| **S2** | ✅ 已完成（1.10.0）：B-1 + B-2 合并——按 PID 装 DESTROY/SHOW/HIDE 钩子，事件驱动 `Prune` + 真实关闭计数；图标重贴拆成 5 s 定时器 | 无 | 1 天 |
| **S3** | ✅ 已完成（1.11.0）：B-3 两层身份 + 位次配对（`WindowIdentity` + 13 条纯规则单测） | 无 | 2 天 |
| **S4** | ✅ 已完成（1.12.0）：最小 `IWindowGateway`（12 个动作）+ `StashAllService` 迁移矩阵 13 条单测 | 建议在 S3 之后（改动面收敛后再抽端口） | 2 天 |
| **S5** | ✅ 已完成（1.13.0）：B-5 拆 `WorkspaceService` 为四片（261 / 253 / 290 / 131 行） | 需 S4 的单测兜底 | 1 天 |

不做的：B-6（暂缓，随需求顺手）、B-8（不做）、B-9 E2E 套件（只做 AutomationId）。

## 四、验收

- **S1**（已达成）：`HelpWindow` 在 4K 主屏 + 1080p 副屏上贴副屏弹出，底部按钮完整可见；UIA 里能看到面板 / 详情窗关键控件的 AutomationId 非空——实测主面板 22 个、详情窗 14 个、说明窗 5 个，全部 `invoke=True`。
- **S2**（已达成）：事件实测 Hide/Show 5 ms、销毁 16–24 ms，加 150 ms 合并窗口后远小于 1 s；「关闭窗口」改为 250 ms 复核（还在等就再等 800 ms），提示按实际数字报「已关闭 2 个窗口，1 个仍在等待响应」。
- **S3**（规则部分已达成）：见 [window-identity.md](window-identity.md) 第九节 P3 验收。「切标签页后位次仍能认对」「跨屏位次不受坐标系影响」「旧快照退回旧行为」都有单测锁定；真实浏览器两窗口的实机回归待做。
- **S4**（已达成）：`dotnet test` 覆盖 6 条以上状态迁移（实际 13 条），测试进程不创建真实窗口；另用真实网关 + 真实窗口的实机夹具验了 20 项（隐藏 / 还原 / 位置一致 / 弹窗跟随 / 销毁清理）。
- **S5**（已达成）：拆分后 `dotnet test` 117/117 全绿，状态机测试一行未改——说明边界抽对了。
