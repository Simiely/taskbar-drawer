---
id: docs.handoff
title: 交接：进度、待办与发布路径
summary: 接手必读：当前版本与提交、已完成主线、发布与验收流程、文档维护入口。
type: reference
kind: note
status: active
owner: "@simiely"
tier: normal
topics: [development]
source_type: doc
source: DEVELOPMENT.md
last_reviewed: 2026-11-19
related: []
---
# 交接：进度、待办与发布路径

> 只写「换人 / 换会话接手立刻要用」的信息。用户视角看 [README](../README.md)，架构与历史问题看 [DEVELOPMENT](../DEVELOPMENT.md)，硬规则与坑看 [AGENTS](../AGENTS.md)。

## 当前状态

- 版本 **1.15.3**（代码 `8e8d66a`；文档整理 `abaa907`；Release `v1.15.3` 已发布）
- 1.15.3：修「跨屏拖动后不触发收起」——拖动前后前台窗口没变，系统不发 `EVENT_SYSTEM_FOREGROUND`；新增 `EVENT_SYSTEM_MOVESIZEEND` 监听，拖动结束后按当前前台补一次判定
- 仓库维护：DEVELOPMENT 拆为 `docs/架构.md` + `docs/问题记录/`（一坑一篇）；补 LICENSE（MIT）、`tools/release.ps1`、tag 触发的 Release workflow 与 README SVG 示意图
- 1.15.2：死代码清理（无行为变化）——未读字段 / 未用参数 / 无消费者的属性 / 3 个废弃 Win32 常量 / 11 条无用 using；`IDE0051/0052/0005/0060` 已清零
- 1.15.1：单窗口模式按「每块屏幕完全独立」重做——场景有固定归属屏（`StashGroup.Screen` / `SavedGroup.MonitorDevice`，收纳时冻结、拖动成员不改变），前台在别的屏上时不再收起本屏场景；场景收起时成员拖到哪都一起收；胶囊按「任意屏有可见成员」算展开；只收起和当前场景同一块屏的其它场景
- 1.15.0（已被 1.15.1 取代）：全局收起 + 跨屏客人最小化；保留其中的「资源管理器当普通应用」修复
- 1.14.5：修父进程链误判——`explorer.exe` 等 shell / 启动器处截断，记事本 / 写字板 / 从任务栏启动的浏览器不再被当成场景同应用，场景恢复正常的收起与最小化
- 1.14.4：把「同应用但非场景成员」从归组降级为跳过——资源管理器 / 微信辅助窗口成为前台时单窗口模式直接不动作，不再出现 `关联组=1 最小化=1`
- 1.14.3：修「场景成员拖到另一块屏后，在那块屏切窗口，它的任务栏按钮跟着消失」——`DisplayRules.PrimaryMonitorOf` 取场景主屏；主屏不在这块屏的「客人」按普通窗口最小化（保留任务栏按钮），主屏上的收起逻辑不变
- 1.14.2：按 1.14.1 日志收尾——同进程归组只给通过准入的顶层应用窗口（弹窗 / 工具窗不再被 `关联组` 绕过忽略）；跨屏位次命中在保存屏有同程序窗口时跳过，避免把另一块屏的资源管理器挪过来；最小化候选改读 `rcNormalPosition`
- 1.14.1：修「微信打开图片 / 小程序把整个场景收回胶囊」——`Core/ProcessSceneLink` 按「同进程 → 父进程 → 祖父进程」把同应用的辅助窗口归到场景组，单窗口模式不再把它们当场景外窗口；辅助窗口不接入场景
- 1.14.0：修「场景里的程序关掉再打开认不回来」——新增 `Core/SceneReattach`（同标题 → 同程序最近位置的一对一配对 + 新窗口认领）、`SceneService.TryReattach` / `ReclaimVisible`、全局 `EVENT_OBJECT_SHOW` 监听（150 ms 合并），并在 `Restore` / `ExpandLatest` / `ExpandAll` 展开时兜底；单窗口模式先 `ClaimForeground` 再判定，避免把刚打开的程序当场景外窗口
- 1.13.9：按 11-18 / 11-19 实机日志修三处——① 还原场景时把「收在别的场景里」的隐藏窗口也算作已在运行，不再重复启动（`WorkspaceService.Restore` 的 busy 判定）；② `WinUIDesktopWin32WindowClass`（Win11 桌面）与 `OperationStatusWindow`（复制进度窗）加入 shell 黑名单，点桌面 / 复制文件不再误触发单窗口模式；③ 胶囊守卫间隔 250 → 120 ms，被任务栏压住后的复位空窗减半
- 版本 **1.13.8**（已入库，Release `v1.13.8` 同版）；入库前远端 main 为 `f9988a1`，本轮按仓库标准用 GitHub Data API 以它为父提交 Squash 入库。**计划 S1–S5 已全部完成**
- 1.13.8：同一场景再次点「详情」直接收起详情窗；点另一个场景仍切换内容并置前（`SceneEdit.ShouldCloseEditor` + 4 条单测）
- 1.13.7 修「移除的窗口又被关场景写回」：`Detach` 改用两层身份配对，`UpdateSnapshot` 只刷新位置不改成员集合；真实窗口夹具 8 项全过（隔离数据目录）
- 1.13.6 修「点场景详情误触外层卡片 toggle」；1.13.4 / 1.13.5 分别修浏览器扩展弹窗与本程序 UI 打开时的单窗口误动作（用户已确认浏览器弹窗正常）
- 1.13.0 完成计划 S5：`WorkspaceService` 拆四片（组合 / 快照 / 还原 / 编辑），纯机械搬迁，42 个成员逐个核对在位
- 1.12.0 完成计划 S4：状态机的窗口操作抽成 `IWindowGateway` 端口（`Win32WindowGateway` 收所有平台调用），`StashAllService` 迁移矩阵进单测（13 条），另用真实网关实机夹具验 20 项（坑 15）
- 1.11.0 完成计划 S3：窗口身份两层化（`AppId` / `ClassName` / `Ordinal`），配对改成「位次优先」，新增 `Core/WindowIdentity` 纯规则模块；旧快照不迁移、自动退回旧路径（坑 14）
- 1.10.0 完成计划 S2：状态对账从 2.5 s 轮询改成窗口事件驱动（按 PID 装 `DESTROY/SHOW/HIDE` 钩子，150 ms 合并），图标重贴独立成 5 s 定时器；「关闭窗口」改为延时复核，按实际关掉的个数报（坑 13）
- 1.9.1 完成计划 S1：`HelpWindow` 高度上限改按目标屏算（走查 W-13 结案）；顺带发现「按钮是 `Border`、UIA 里不存在」，`Ui.ActionButton` / `CardButton` / `DangerButton` 全部换成真 `Button` + 自定义模板，并给三个窗的关键控件补了 `AutomationId`（FlaUI 前提，坑 12）
- 本地维护：1.9.0 落地 [窗口识别方案](design/window-identity.md) 的 P1 + P2——弹窗按「窗口簇」处理（不再进场景、随簇主一起隐藏 / 恢复、关闭只针对簇主），详情窗能区分「已打开 / 未打开」；P3（两层身份 + 位次配对）已由 1.11.0 完成，1.13.7 又补上移除 / 关场景写回路径
- 1.8.1 走查修复：18 条链路 → 13 个问题、修 11 个，见 [walkthrough/report-1.md](walkthrough/report-1.md)
- 1.8.0 结构：`AppShell` 拆三片（`AppShell.cs` 组合根 / `.Commands.cs` 命令 / `.Wiring.cs` 接线）
- 1.7.1 胶囊 z 序：事件驱动 + 250 ms 守卫，实测 74–176 ms
- 验收基线：`dotnet build` 0 警告 0 错误、`dotnet test` **164/164**、`REPO CHECK: PASS`
- 主线一句话：`AppShell` 是组合根（三片），收纳走 `StashAllService`（运行时组），快照走 `WorkspaceService`，两者靠 `SavedGroup.Id` / `StashGroup.SavedId` 关联；窗口隐藏与图标装饰在 `WindowStash`；**场景回到屏幕必须走 `FinishSceneOpen` 收尾**（坑 10）；**弹窗不是窗口，按窗口簇处理**（坑 11）

## 待办（按收益排序）

P0：

- [x] 修复残留圆点：`WindowStash.RemoveSceneIcon` 按大 / 小图标槽位分别判断与恢复；启动时清理失效 HICON；`Prune` 释放已销毁窗口的图标句柄
- [x] 新增 `SceneService` 作为场景唯一写入口，合并 `SavedGroup` / `StashGroup` 的 Name / Badge / SavedId 双写，以及两份 `SetBadge` / `RemapBadgeColors`
- [x] 生命周期收敛为四动词 New / Restore / Stash / Dissolve：删除 `StashAllService.ToggleGroup` 与 `AppShell.CloseScene`，`ToggleScene` 只做状态分派（落到 Restore / Stash），`Dissolve` 用 `keepSnapshot` 区分关闭窗口 / 删除场景
- [x] 图标 / 标识尺寸的默认值与范围收敛到单一常量源（`Core/SceneIconMetrics`，设置 / 绘制 / 面板 / 夹取共用）
- [x] 规则引擎边界确认：规则层只服务 Hotkey / SceneChip 两个状态不明确的入口；托盘 / 面板是显式命令，统一走 `SceneService` 命令层，不接入 `RuleRunner`。已删除无消费者的 `ForTrigger` / `TriggerTitle` / `TriggerHint`，以及 `RuleCardDefinition` 的 `Title` / `Description` / `ArgKey` / `ArgLabel` / `ArgDefault`；`Find`（解释器依赖）、`OnlyTriggers` / `AppliesTo`（规则约束测试）保留，`RuleStep.Args` 运行时参数保留（Toast 动作仍读 `text`）
- [x] 胶囊被任务栏压住（1.7.1）：根因不是判定错而是驱动节奏——原来的 2 s 心跳就是用户能感知的故障时长。改为事件驱动（前台切换 + 任务栏几何）+ 120 ms 自适应守卫（1.13.9 从 250 ms 收紧），复位只抬 z 序。**结论：DeskBand 在 Win11 已移除、`SetWindowBand` 对普通进程永远 0x5、`CreateWindowInBand` 只放行 DEFAULT / DESKTOP、`SetParent` 到任务栏不受支持——浮窗抢 z 序是唯一可行路径，不要再去试「换逻辑」**

P1 / P2（详细调研与取舍见 [剩余待办调研与建议](design/backlog-plan.md)，不要再逐条重新评估）：

- [x] 拆分 `AppShell`（1.8.0）：1286 行 → `AppShell.cs` 365（组合根 / 生命周期 / 装配顺序）/ `AppShell.Commands.cs` 652（命令实现）/ `AppShell.Wiring.cs` 361（入口接线与窗口管理）。约 40 处事件订阅按入口收进 7 个 `Wire*`，装配顺序从「语句顺序」变成显式契约
- [x] **S1**（1.9.1）：`HelpWindow` 高度上限（W-13）+ 关键控件 `AutomationId`。**补 ID 之前必须先换控件**——`Border` 没有 automation peer，ID 挂上去 UIA 也读不到；现在是 `Button` + `ControlTemplate`，验收用临时离屏夹具（不入库）查 UIA 树（面板 22 / 详情窗 14 / 说明窗 5 个带 ID 元素），并导出 PNG 与原 `Border` 版对照外观；重建方法见 [DEVELOPMENT](../DEVELOPMENT.md) 三、关键问题
- [x] **S2**（1.10.0）：`Prune` 事件驱动 + 关闭计数合并（B-1 + B-2）。`Core/WindowEventWatcher` 按被跟踪进程装钩子（`idProcess` 是注册期过滤，一个进程一个钩子），事件合并 150 ms 后对账；图标重贴没有事件（`WM_SETICON` 不发 WinEvent），拆成独立 5 s 定时器；`WorkspaceService.Close` 改为交句柄、`CloseReport` 延时复核实际关掉几个。实测 Hide/Show 5 ms、销毁 16–24 ms
- [x] **S3**（1.11.0）：两层身份 + 位次配对。`SavedWindow` 加 `AppId`/`ClassName`/`Ordinal`；`WindowIdentity` 出 `AppId` / `AssignOrdinals` / `Pair` 三段纯规则；`SceneEdit.IdentityKey` 双键兼容旧 `Suppressed`；位次排序必须显示器优先（保存侧工作区坐标 / 还原侧屏幕坐标）。1.13.7 的真实窗口夹具已覆盖「标题失配 + 旧运行时组 + 关场景写回」这条路径
- [x] **S4**（1.12.0）：`IWindowGateway`（12 个动作，读操作折进 `Describe`、四种显示语义合成 `SetVisibility`）+ `StashAllServiceTests` 13 条（假网关，不建真实窗口）+ 真实网关实机夹具 20 项全过。`Prune` 增加可注入时钟 `nowUtc`（只给单测）
- [x] **S5**（1.13.0）：`WorkspaceService` 拆四片——`.cs` 组合与持久化 261 行 / `.Snapshot.cs` 抓窗口 253 行 / `.Restore.cs` 还原编排 290 行 / `.Editing.cs` 只改快照数据 131 行。拆分前先落地 S3（配对抽到 `WindowIdentity`）与 S4（窗口操作压到 `IWindowGateway`），所以这次只搬不改

明确不做：`MainPanelWindow` / `SceneEditorWindow` 分区拆分（暂缓，随需求顺手做）、副屏任务栏胶囊（B-8：副屏任务栏没有 `TrayNotifyWnd`，要做就得引 UIA 或估算时钟宽度，不划算）、FlaUI 全量 E2E 套件（B-9：WinAppDriver 已停更，FlaUI 可用但纯代码 WPF 没有 AutomationId，先补 ID 再谈）

## 走查（scenario-walkthrough）

- 第 1 轮已完成：[能力清单](walkthrough/capabilities.md) → [素材 + 矩阵](walkthrough/scenarios.md) → [报告](walkthrough/report-1.md)，修掉 11 个问题
- **第 2 轮待做**：① 实机回归 report-1 的 5 步单窗口复测；② 补 S-14 多屏链（需双显示器）；③ 开 1 个新探索会话（charter：`Explore 场景详情与追加列表 with 多窗口/最小化状态 to discover 状态残留与禁用态陷阱`）；④ 收敛判据见方法 C1（🔴 清零 + 两轮递减 + 回归全绿）

## 已落地：任务栏残留圆点（1.6.7 起）

- 现象（历史）：个别窗口在场景收起 / 删除后，任务栏按钮上仍留着彩色圆点，或图标变成空白
- 根因一：`RemoveSceneIcon` 只用大图标槽位判断图标是否属于本程序，随后无条件销毁 big / small 两个 HICON；Win11 任务栏读小图标，于是残留或拿到已销毁句柄
- 根因二：进程被强杀时来不及还原图标，下次启动无人清理
- 现状：`RemoveSceneIcon` 已按槽位分别判断与恢复；`WindowIconSlot` / `SmallestWindowIconIs` / `ClearStaleWindowIcons()` 已在 1.6.7 落地，启动时清理失效句柄；`Prune` 对已销毁窗口释放 HICON。本项无需再做

## 发布流程

- 源码目录：`C:\Users\260803\WorkBuddy\2026-11-18-09-32-50`（当前工作区，直接 git push）
- 本地发布：`powershell -NoProfile -ExecutionPolicy Bypass -File tools\release.ps1`（PowerShell 7 可用 `pwsh tools/release.ps1`）——校验版本 / 干净工作区 → build + test → `dotnet publish` 单文件 → 写 `使用说明.txt` + `LICENSE.txt` → `dist\TaskbarDrawer-<版本>-win-x64.zip`，输出 SHA256
- 版本与资产命名沿用 `v{版本}`：Release 标题 `TaskbarDrawer v{版本}`，资产 `TaskbarDrawer-{版本}-win-x64.zip`（框架依赖单文件 exe + 中文使用说明 + LICENSE），正文按「主要变化 / 下载 / 验证」三段写
- 发布：本地干净后 `git push origin main`；打 tag `v{版本}` 并推送，`.github/workflows/release.yml` 会自动构建/上传；也可以本地 `gh release create`（token 从 Windows 凭据管理器读取，不要写进文件）
- 文档基线：发版后把 `schema/agents.template.md` 的「文档基线」行更新成最新提交 hash，重新生成 `AGENTS.md`，并回填 knowledge-base `用户库/仓库盘点表.md`
- 历史备注：1.13.8 入库时 `git push` 不通，曾用 GitHub Data API Squash；现在普通 push 已可用，不再走那条路

## 验收清单

- `dotnet build src/TaskbarDrawer/TaskbarDrawer.csproj -c Release`：0 警告 0 错误
- `dotnet test tests/TaskbarDrawer.Tests/TaskbarDrawer.Tests.csproj -c Release`：当前基线 **164 项全过**
- `python tools/repo-kit/check_repo.py --root .`、`generate.py --check`、`pwsh tools/update-repo-kit.ps1 -Check`：全绿
- 胶囊 z 序（1.7.1）：实机点几次任务栏 / 点任务栏按钮切几次窗口，看 `logs\app-*.log` 的 `[chip] 被任务栏压住 N 枚 → 已复位 来源=… 耗时=…ms`——`来源=前台切换` 且耗时几十毫秒为正常；出现 `(非事件触发)` 说明有一路事件没覆盖到，需要补触发源
- UI 用 UI Automation 找元素 + 真实鼠标点击验证；不要用模拟全局热键，会误触发收纳
- 配置检查：`settings.json` 的 `Version` 应为 4，尺寸 0.90 / 0.55，`SceneBadgeColors` 第 8 项 `#000000`
- 发布产物：`dist\TaskbarDrawer-1.15.3-win-x64.zip`，约 260 KB（框架依赖；需 .NET 8 Desktop Runtime）

## 文档职责

| 文件 | 写什么 | 不写什么 |
| --- | --- | --- |
| [README](../README.md) | 功能、系统要求、快速开始 | 内部实现与待办 |
| [DEVELOPMENT](../DEVELOPMENT.md) | 项目概览、架构总览、问题索引 | 当前进度与下一步 |
| [架构说明](架构.md) | 分层、组合根、场景服务、单窗口模式、数据流 | 历史问题与待办 |
| [问题记录](_generated/pitfalls.md) | 一坑一篇的 TL;DR / 根因 / 方案 | 跨项目通用经验（提炼后进 knowledge-base） |
| [AGENTS](../AGENTS.md) | 规则、关键坑、常用命令（生成文件） | 进度 |
| [CHANGELOG](../CHANGELOG.md) | 每个版本改了什么 | 未完成项 |
| 本页 | 当前状态、待办、未落地方案、发布与验收 | 重复功能说明 |
