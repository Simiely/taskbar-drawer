---
id: docs.handoff
title: 交接：进度、待办与发布路径
summary: 接手必读：当前版本与提交、十项架构优化待办、残留圆点的根因与未落地方案、发布与验收流程。
type: reference
kind: note
status: active
owner: "@simiely"
tier: normal
topics: [development]
source_type: doc
source: DEVELOPMENT.md
last_reviewed: 2026-11-18
related: []
---
# 交接：进度、待办与发布路径

> 只写「换人 / 换会话接手立刻要用」的信息。用户视角看 [README](../README.md)，架构与历史问题看 [DEVELOPMENT](../DEVELOPMENT.md)，硬规则与坑看 [AGENTS](../AGENTS.md)。

## 当前状态

- 版本 **1.13.8**（本提交入库，Release `v1.13.8` 同版）；入库前远端 main 为 `f9988a1`，本轮按仓库标准用 GitHub Data API 以它为父提交 Squash 入库。**计划 S1–S5 已全部完成**
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
- 验收基线：`dotnet build` 0 警告 0 错误、`dotnet test` **139/139**、`REPO CHECK: PASS`
- 主线一句话：`AppShell` 是组合根（三片），收纳走 `StashAllService`（运行时组），快照走 `WorkspaceService`，两者靠 `SavedGroup.Id` / `StashGroup.SavedId` 关联；窗口隐藏与图标装饰在 `WindowStash`；**场景回到屏幕必须走 `FinishSceneOpen` 收尾**（坑 10）；**弹窗不是窗口，按窗口簇处理**（坑 11）

## 待办（按收益排序）

P0：

- [x] 修复残留圆点：`WindowStash.RemoveSceneIcon` 按大 / 小图标槽位分别判断与恢复；启动时清理失效 HICON；`Prune` 释放已销毁窗口的图标句柄
- [x] 新增 `SceneService` 作为场景唯一写入口，合并 `SavedGroup` / `StashGroup` 的 Name / Badge / SavedId 双写，以及两份 `SetBadge` / `RemapBadgeColors`
- [x] 生命周期收敛为四动词 New / Restore / Stash / Dissolve：删除 `StashAllService.ToggleGroup` 与 `AppShell.CloseScene`，`ToggleScene` 只做状态分派（落到 Restore / Stash），`Dissolve` 用 `keepSnapshot` 区分关闭窗口 / 删除场景
- [x] 图标 / 标识尺寸的默认值与范围收敛到单一常量源（`Core/SceneIconMetrics`，设置 / 绘制 / 面板 / 夹取共用）
- [x] 规则引擎边界确认：规则层只服务 Hotkey / SceneChip 两个状态不明确的入口；托盘 / 面板是显式命令，统一走 `SceneService` 命令层，不接入 `RuleRunner`。已删除无消费者的 `ForTrigger` / `TriggerTitle` / `TriggerHint`，以及 `RuleCardDefinition` 的 `Title` / `Description` / `ArgKey` / `ArgLabel` / `ArgDefault`；`Find`（解释器依赖）、`OnlyTriggers` / `AppliesTo`（规则约束测试）保留，`RuleStep.Args` 运行时参数保留（Toast 动作仍读 `text`）
- [x] 胶囊被任务栏压住（1.7.1）：根因不是判定错而是驱动节奏——原来的 2 s 心跳就是用户能感知的故障时长。改为事件驱动（前台切换 + 任务栏几何）+ 250 ms 自适应守卫，复位只抬 z 序。**结论：DeskBand 在 Win11 已移除、`SetWindowBand` 对普通进程永远 0x5、`CreateWindowInBand` 只放行 DEFAULT / DESKTOP、`SetParent` 到任务栏不受支持——浮窗抢 z 序是唯一可行路径，不要再去试「换逻辑」**

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

- 源码主目录：`C:\Users\260803\Documents\Trae-CN\temp_20261112_01\taskbar-drawer`（交付目录，只放源码与四件套）
- 发布副本：`C:\Temp\td-release\repo`。同步时只覆盖 `src` / `tests` / `tools/make_icon.py` / 三份根文档，不要把 `bin` / `obj` / `publish` 带进去
- 步骤：同步文件 → `python tools/repo-kit/generate.py --root . --write` → `python tools/repo-kit/check_repo.py --root .` → 与远端逐 blob 比对 → 按 GitHub Data API 提交（token 从 Windows 凭据管理器 `git:https://github.com` 读取，不要写进文件）
- 网络限制：`github.com` / `git push` 在当前环境不通，`api.github.com` 与 `uploads.github.com` 的实际上传端点可通；所以用 Data API 把本轮本地提交 Squash 成**一个父提交为远端 main 的远端提交**，本地再把 `refs/heads/main` / `refs/remotes/origin/main` 对齐到同一个 SHA（旧本地提交先留 `backup/pre-api-YYYYMMDD` 分支）
- Release 命名沿用 `v{版本}`：`TaskbarDrawer v{版本}` + 资产 `TaskbarDrawer-{版本}-win-x64.zip`（框架依赖单文件 exe + 中文使用说明），正文按「主要变化 / 下载 / 验证」三段写
- 改完文档后把 `schema/agents.template.md` 的「文档基线」行更新成最新提交 hash，重新生成 `AGENTS.md`

## 验收清单

- `dotnet build src/TaskbarDrawer/TaskbarDrawer.csproj -c Release`：0 警告 0 错误
- `dotnet test tests/TaskbarDrawer.Tests/TaskbarDrawer.Tests.csproj -c Release`：当前基线 **139 项全过**
- 胶囊 z 序（1.7.1）：实机点几次任务栏 / 点任务栏按钮切几次窗口，看 `logs\app-*.log` 的 `[chip] 被任务栏压住 N 枚 → 已复位 来源=… 耗时=…ms`——`来源=前台切换` 且耗时几十毫秒为正常；出现 `(非事件触发)` 说明有一路事件没覆盖到，需要补触发源
- UI 用 UI Automation 找元素 + 真实鼠标点击验证；不要用模拟全局热键，会误触发收纳
- 配置检查：`settings.json` 的 `Version` 应为 4，尺寸 0.90 / 0.55，`SceneBadgeColors` 第 8 项 `#000000`
- 发布产物：`dotnet publish ... -p:PublishSingleFile=true -o publish`，单文件约 500 KB（框架依赖，含 SVG 重绘的矢量说明图）

## 文档职责

| 文件 | 写什么 | 不写什么 |
| --- | --- | --- |
| [README](../README.md) | 功能、系统要求、快速开始 | 内部实现与待办 |
| [DEVELOPMENT](../DEVELOPMENT.md) | 分层、数据流、历史问题背景 | 当前进度与下一步 |
| [AGENTS](../AGENTS.md) | 规则、关键坑、常用命令（生成文件） | 进度 |
| [CHANGELOG](../CHANGELOG.md) | 每个版本改了什么 | 未完成项 |
| 本页 | 当前状态、待办、未落地方案、发布与验收 | 重复功能说明 |
