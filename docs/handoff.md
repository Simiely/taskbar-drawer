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
last_reviewed: 2026-11-16
related: []
---
# 交接：进度、待办与发布路径

> 只写「换人 / 换会话接手立刻要用」的信息。用户视角看 [README](../README.md)，架构与历史问题看 [DEVELOPMENT](../DEVELOPMENT.md)，硬规则与坑看 [AGENTS](../AGENTS.md)。

## 当前状态

- 版本 **1.6.7**，main 最新提交 `51f3bfa`（2026-11-16 18:12，本页随该提交入库）
- 本轮已完成：场景标识改为彩色圆点 / 数字（可选 1–9、左上 / 右上）；8 个色块可右键改成任意颜色 + 「恢复默认」；第八色由灰改黑；图标 / 标识尺寸默认 90% / 55% 且两个滑块并排一行；`settings.json` 升到 v4
- 验证结果：`dotnet build` 0 警告 0 错误；`dotnet test` 71 项全过；`check_repo.py` PASS；远端 tree 与发布副本逐 blob 比对零差异
- 主线一句话：`AppShell` 是组合根，收纳走 `StashAllService`（运行时组），快照走 `WorkspaceService`，两者靠 `SavedGroup.Id` / `StashGroup.SavedId` 关联；窗口隐藏与图标装饰在 `WindowStash`

## 待办（按收益排序）

P0：

- [ ] 修复残留圆点：`WindowStash.RemoveSceneIcon` 按大 / 小图标槽位分别判断与恢复；启动时清理失效 HICON；`Prune` 释放已销毁窗口的图标句柄
- [ ] 新增 `SceneService` 作为场景唯一写入口，合并 `SavedGroup` / `StashGroup` 的 Name / Badge / SavedId 双写，以及两份 `SetBadge` / `RemapBadgeColors`
- [ ] 生命周期收敛为四动词 New / Restore / Stash / Dissolve，替换 `ToggleScene` / `RestoreSavedGroup` / `CloseScene` / `CloseRunningScene` / `DissolveScene` 五个重叠操作
- [ ] 图标 / 标识尺寸的默认值与范围收敛到单一常量源（现散在 `AppSettings` / `SceneIconPainter` / `MainPanelWindow` / `AppShell.SetSceneIconSizes` 四处）
- [ ] 规则引擎收口：面板 / 托盘也走同一命令表；清理 `RuleCatalog` 中生产代码无消费者的 `Find` / `ForTrigger` 元数据

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
- `dotnet test tests/TaskbarDrawer.Tests/TaskbarDrawer.Tests.csproj -c Release`：当前基线 71 项全过
- UI 用 UI Automation 找元素 + 真实鼠标点击验证；不要用模拟全局热键，会误触发收纳
- 配置检查：`settings.json` 的 `Version` 应为 4，尺寸 0.90 / 0.55，`SceneBadgeColors` 第 8 项 `#000000`
- 发布产物：`dotnet publish ... -p:PublishSingleFile=true -o publish`，单文件约 430 KB（框架依赖）

## 文档职责

| 文件 | 写什么 | 不写什么 |
| --- | --- | --- |
| [README](../README.md) | 功能、系统要求、快速开始 | 内部实现与待办 |
| [DEVELOPMENT](../DEVELOPMENT.md) | 分层、数据流、历史问题背景 | 当前进度与下一步 |
| [AGENTS](../AGENTS.md) | 规则、关键坑、常用命令（生成文件） | 进度 |
| [CHANGELOG](../CHANGELOG.md) | 每个版本改了什么 | 未完成项 |
| 本页 | 当前状态、待办、未落地方案、发布与验收 | 重复功能说明 |
