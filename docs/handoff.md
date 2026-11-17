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
last_reviewed: 2026-11-17
related: []
---
# 交接：进度、待办与发布路径

> 只写「换人 / 换会话接手立刻要用」的信息。用户视角看 [README](../README.md)，架构与历史问题看 [DEVELOPMENT](../DEVELOPMENT.md)，硬规则与坑看 [AGENTS](../AGENTS.md)。

## 当前状态

- 版本 **1.7.0**；main 最新提交 `1561390`（2026-11-17，1.7.0 已入库）
- 本地维护：1.7.0 已落地（说明窗口、详情侧窗、顶部拖动区、移除 / 追加修复）；`dotnet build` 0 警告 0 错误、`dotnet test` 77/77
- 本地复验（2026-11-17）：发布 exe 502,392 B；本机 UIA + 真实鼠标 E2E 27 项全过：场景还原 / 收起 / 展开、移除窗口后运行时组同步、追加全部窗口并即时接入、说明窗口、详情侧窗初始 620 / 可拉高、顶部拖动区；滚动条拖动（点 10 px 判定区、避开 3 px 视觉细条）/ 滚轮滚动单独验证通过；最终构建冒烟测试通过（面板 400×550、说明 620×720）
- 本轮完成：场景标识默认右上角（已有场景保留保存时的选择）；主面板顶部拖动条 + 空白 / 文字处可拖（按钮 / 卡片 / 滑块不抢）；面板内长说明移入「说明」窗口（SVG 重绘的矢量示意图 + 编号标注，不引入 SVG 运行时）；场景详情改为面板旁非模态侧窗（初始约半屏、窗内滚动条、右下角 ◢ 可调大小）；移除窗口同时从运行时组摘掉（标题尾随空格 / 大小写归一、同名窗口按次数抑制）；追加改为这块屏幕全部窗口并即时接入运行时组；面板 / 详情 / 说明统一深色细滚动条（透明轨道、圆角拇指、三态变亮）
- 审计清理：删除 `NativeMethods.MonitorDeviceOfPoint`、`StashedWindow.OriginalIconOwned*`、`AppendableWindowItem.MonitorDevice`、`SceneAppendResult` 的 4 个未读计数属性；架构结论：主线清晰，`AppShell` / `WorkspaceService` / 两个大 UI 窗口需要后续拆分，窗口定位 / 路径归一 / 徽标双写存在重复实现
- 入库前走查修正：启动迁移落盘延后到 `SaveFailed` 订阅之后；详情窗调整手柄在捕获丢失 / 按键松开时停止；说明图资源缺失或 XAML 解析失败写日志；旧运行时组兜底关闭收口到 `SceneService.CloseLegacy`；`.gitignore` 补齐 Python 缓存；README / 交接日期同步
- 验证结果：`dotnet build` 0 警告 0 错误；`dotnet test` 77 项全过
- 主线一句话：`AppShell` 是组合根，收纳走 `StashAllService`（运行时组），快照走 `WorkspaceService`，两者靠 `SavedGroup.Id` / `StashGroup.SavedId` 关联；窗口隐藏与图标装饰在 `WindowStash`

## 待办（按收益排序）

P0：

- [x] 修复残留圆点：`WindowStash.RemoveSceneIcon` 按大 / 小图标槽位分别判断与恢复；启动时清理失效 HICON；`Prune` 释放已销毁窗口的图标句柄
- [x] 新增 `SceneService` 作为场景唯一写入口，合并 `SavedGroup` / `StashGroup` 的 Name / Badge / SavedId 双写，以及两份 `SetBadge` / `RemapBadgeColors`
- [x] 生命周期收敛为四动词 New / Restore / Stash / Dissolve：删除 `StashAllService.ToggleGroup` 与 `AppShell.CloseScene`，`ToggleScene` 只做状态分派（落到 Restore / Stash），`Dissolve` 用 `keepSnapshot` 区分关闭窗口 / 删除场景
- [x] 图标 / 标识尺寸的默认值与范围收敛到单一常量源（`Core/SceneIconMetrics`，设置 / 绘制 / 面板 / 夹取共用）
- [x] 规则引擎边界确认：规则层只服务 Hotkey / SceneChip 两个状态不明确的入口；托盘 / 面板是显式命令，统一走 `SceneService` 命令层，不接入 `RuleRunner`。已删除无消费者的 `ForTrigger` / `TriggerTitle` / `TriggerHint`，以及 `RuleCardDefinition` 的 `Title` / `Description` / `ArgKey` / `ArgLabel` / `ArgDefault`；`Find`（解释器依赖）、`OnlyTriggers` / `AppliesTo`（规则约束测试）保留，`RuleStep.Args` 运行时参数保留（Toast 动作仍读 `text`）

P1：

- [ ] 拆分 `AppShell`（1000+ 行）：入口装配、命令实现、面板 / 托盘接线分离，约 40 处事件订阅集中注册
- [ ] 拆分 `WorkspaceService`（600+ 行）：快照存储、窗口恢复摆位、进程启动三段职责分离
- [ ] 引入 `IWindowGateway`，让 `WindowStash` / `StashAllService` 状态机可脱离 Win32 单测（目前三个核心状态机 0 测试）

P2：

- [ ] 拆分 `MainPanelWindow` / `SceneEditorWindow` 的分区构建方法，按区块提取
- [ ] `Prune` 改为事件驱动（`WinEventHook`）替代 2.5 s 轮询，避免周期性重贴图标

## 未落地：任务栏残留圆点

- 现象：个别窗口在场景收起 / 删除后，任务栏按钮上仍留着彩色圆点，或图标变成空白
- 根因一：`RemoveSceneIcon` 只用 `WindowIcon(big: true)` 判断图标是否属于本程序，随后无条件销毁 big / small 两个 HICON。应用若只换了大图标、小图标仍是我们的，任务栏（Win11 读小图标）就会残留或拿到已销毁句柄
- 根因二：进程被强杀时来不及还原图标，`%LOCALAPPDATA%` 里也没有「哪些窗口被改过图标」的记录，下次启动无人清理
- 方案：`NativeMethods` 增加只读单槽位的 `WindowIconSet(hwnd, big)` 与启动清理 `ClearStaleWindowIcons()`；`RemoveSceneIcon` 按槽位分别恢复；`AppShell.Initialize` 在 `WindowRecovery.Recover()` 后调用清理；`Prune` 对已销毁窗口释放 HICON。挂图标前的失效句柄校验已随 1.6.7 落地，可直接复用

## 发布流程

- 源码主目录：`C:\Users\260803\Documents\Trae-CN\temp_20261112_01\taskbar-drawer`（交付目录，只放源码与四件套）
- 发布副本：`C:\Temp\td-release\repo`。同步时只覆盖 `src` / `tests` / `tools/make_icon.py` / 三份根文档，不要把 `bin` / `obj` / `publish` 带进去
- 步骤：同步文件 → `python tools/repo-kit/generate.py --root . --write` → `python tools/repo-kit/check_repo.py --root .` → 与远端逐 blob 比对 → 用 `commit.ps1` 按 GitHub Data API 提交（token 从 Windows 凭据管理器 `git:https://github.com` 读取，不要写进文件）
- 本机 PATH 没有 `git`，不要用 `git` 命令；提交只传实际变更的文件，提交后再拉一次远端 tree 验证零差异
- 改完文档后把 `schema/agents.template.md` 的「文档基线」行更新成最新提交 hash，重新生成 `AGENTS.md`

## 验收清单

- `dotnet build src/TaskbarDrawer/TaskbarDrawer.csproj -c Release`：0 警告 0 错误
- `dotnet test tests/TaskbarDrawer.Tests/TaskbarDrawer.Tests.csproj -c Release`：当前基线 77 项全过
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
