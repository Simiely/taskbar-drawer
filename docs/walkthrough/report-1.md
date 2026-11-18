---
id: docs.walkthrough.report1
title: 第 1 轮走查报告
summary: 18 条素材链走查结果：13 个问题（5 个用户可见功能缺陷，其中 2 个为用户实报），12 条已修，1 条记录在案。
type: reference
kind: report
status: active
owner: "@simiely"
tier: normal
topics: [development]
source_type: doc
source: docs/walkthrough/scenarios.md
last_reviewed: 2026-11-18
related: []
---
# 第 1 轮走查报告

## 总体结论

18 条链路：**12 条通顺、4 条不通（真缺陷）、2 条不可执行（本机单屏 / 需注入注册表）**。
共发现 **13 个问题**：🔴 3 个功能缺陷、🟡 7 个、⚪ 3 个。已修 12 个（1.8.1 修 11 个，W-13 在 1.9.1 结案），1 个记录在案（见「未修」）。

用户实报的两条（S-01 / S-02）**都已复现并定位到根因**，且不是同一处代码。

## 问题清单

| 编号 | 级别 | 问题 | 位置 | 受影响链路 | 风险(L×I) | 影响 |
| --- | --- | --- | --- | --- | --- | --- |
| W-01 | 🔴 | 单窗口模式下打开 / 展开场景后，屏幕上的其它窗口**不会被最小化**（模式看起来没生效） | `AppShell.Commands.cs`（`ToggleScene`/`RestoreScene`/`ExpandLatest`）、`SingleWindowMode.cs` | S-01 S-02 S-04 S-11 | 5×5=25 | 用户实报① |
| W-02 | 🔴 | 从快照打开的场景**停在 z 序最下面**；之后再收起 / 展开也一样 | `StashAllService.ExpandGroup`（只认 `ForegroundBefore`，Adopt 建的组该值为 0）、`WorkspaceService.Restore`（从不置前） | S-01 S-02 S-03 S-05 | 5×5=25 | 用户实报② |
| W-03 | 🔴 | 单窗口模式最小化别的窗口用 `SW_MINIMIZE`，它会「激活 Z 序里的下一个顶层窗口」，把焦点从场景窗口抢走 | `SingleWindowMode.cs` `Apply` | S-04 S-11 | 4×4=16 | 置前后又被顶下去 |
| W-04 | 🟡 | 往「收起中」的场景追加窗口后，整组状态翻成「已展开」，而其它成员还在收纳区 | `SceneService.AppendWindows` | S-07 | 3×4=12 | 胶囊语义错乱（点它变成"收起"） |
| W-05 | 🟡 | 快照写盘失败时仍建立场景并关联胶囊，窗口已被藏起来——违反 AGENTS 坑 4「保存失败要放回桌面」 | `WorkspaceService.SaveFrom` | S-10 | 3×5=15 | 重启后场景消失、窗口像是丢了 |
| W-06 | 🟡 | 详情窗里**按下禁用按钮会把整个窗口拖走**（「移除」、追加列表的「已在场景」大量是禁用态） | `Ui.IsInteractive` | S-06 S-18 | 4×2=8 | 误操作、窗口乱跳 |
| W-07 | 🟡 | 托盘「按场景切换」在菜单展开时被重建，从可见下拉里移除子项会把菜单在鼠标底下收起来 | `TrayService.SetScenes` | S-02 | 3×3=9 | 2.5 s 的 Prune 就可能触发 |
| W-08 | 🟡 | 开机自启用整串命令比较：升级 / 换目录后显示「未开启」，而注册表里的旧命令仍会拉起旧副本 | `AutoStart.IsEnabled` | C-16 | 3×3=9 | 显示的开关状态是假的 |
| W-09 | 🟡 | 详情窗高度上限在 `OnSourceInitialized` 里按主屏算，4K 主屏 + 1080p 副屏时能把窗口拉出屏幕底部 | `SceneEditorWindow.PlaceBeside` | S-18 | 2×3=6 | 底部按钮点不到 |
| W-10 | ⚪ | 主面板提示仍写「任务栏留一枚 ⭐ 胶囊」，1.6.7 起早已是彩色圆点 | `MainPanelWindow.cs:556` | C-01 | 1×1=1 | 找不到那个星 |
| W-11 | ⚪ | 改场景标识失败时详情窗不回滚，会长期显示一个没存下来的标识 | `AppShell.Commands.SetSceneBadge` | S-15 | 2×2=4 | 视觉不一致 |
| W-12 | 🟡 | 「已关闭 N 个窗口」的 N 是**发出 `WM_CLOSE` 的个数**，目标程序弹保存框时窗口一个没关 | `WorkspaceService.Close` + 文案 | S-09 | 3×2=6 | 文案失真（未修，见下） |
| W-13 | ⚪ | 说明窗的高度上限同样在定位前算（窗口固定尺寸、不可拉，影响低） | `HelpWindow` | C-19 | 1×2=2 | 极端 DPI 下偏小（**1.9.1 已修**） |

## 已修（12 条）

- **W-01**：`SingleWindowMode` 新增 `ApplyNow(anchor)`——忽略「刚恢复、先别动」的抑制窗口，立刻按指定窗口应用一次；`AppShell` 新增 `FinishSceneOpen(groupId)`，在「展开运行中场景 / 打开已关闭场景 / 展开最近」三处收尾时调用；启动出来的窗口在 `AdoptRestoredWindow` 里补一次。
  *根因*：批量恢复期间的 `SuspendTemporarily()` 把前台事件全压掉了，而系统之后不会再给事件——「避免误最小化」和「该最小化」被同一段抑制一起取消了，且没有任何补偿动作。
- **W-02**：新增 `Core/SceneFocusRules.Pick`（纯规则 + 5 条单测）：优先 `ForegroundBefore`，不可用时退回场景里第一个刚还原的窗口；`ExpandGroup` 改用它，`FinishSceneOpen` 统一置前。
  *根因*：从快照拉起的组由 `Adopt` 现场创建，`ForegroundBefore` 恒为 0，而 `ExpandGroup` 只在它可用时才激活——所以这类场景**永远**不会置前。
- **W-03**：`SW_MINIMIZE` → `SW_SHOWMINNOACTIVE`（本项目 `ReleaseFallback` 早就是这个用法）。
- **W-04**：`StashAllService.StashSingle(hwnd)` + `AppendWindows` 在「追加前场景是收着的」时把新成员收进收纳区，保住「已收起」状态。
- **W-05**：`SaveFrom` 在 `Persist()` 失败时移除刚加的内存条并返回 null → `SceneService.New` 走 `Release` 把窗口放回桌面；提示文案改为「无法存成场景，窗口已放回桌面」。
- **W-06**：`Ui.ActionButton(enabled:false)` 打 `NoDragTag`，`IsInteractive` 认这个标记，不再把禁用按钮当空白处。
- **W-07**：菜单可见时只置脏（`_pendingScenes` + `_sceneDirty`），`_menu.Closed` 时再重建；重建逻辑抽成 `RebuildSceneItems`。
- **W-08**：`IsEnabled()` 改为「Run 值存在即开启」，新增 `AutoStart.Repair()` 在启动时把旧路径刷成当前 exe。
- **W-09**：高度上限移到 `PlaceBeside` 里、按目标屏工作区算。
- **W-10 / W-11**：文案修正；`SetBadge` 失败时 `RequestSurfaceUpdate` 回推真实数据。
- **W-13**：`HelpWindow` 的 `MaxHeight` 从 `OnSourceInitialized` 挪进 `PlaceOnMonitorOf`，按窗口实际落入的那块屏算，与详情窗 1.8.1 的修法对齐（1.9.1）。
- **W-14（新增，非走查项）**：把「补 AutomationId」这件事做成——发现按钮是 `Border`、在 UIA 树里不存在，`Ui.ActionButton` 换成真 `Button` + 自定义模板，并给面板 / 详情窗 / 说明窗 41 个关键控件挂上 ID（1.9.1，见 [DEVELOPMENT](../../DEVELOPMENT.md) 三、关键问题）。

## 覆盖矩阵结论

- 每行 ≥1 格、每列 ≥1 格（「离线」「多角色」两列写明理由，见素材文档）。
- **补的盲区**：原矩阵里「单窗口模式 × 打开已关闭场景」没有独立素材，正是用户踩到的地方（S-01）；「追加到收起中的场景」（S-07）也是补进去才发现的。
- **仍空**：C-16/17/18/22（注册表、UAC、DPI）只能代码审查，本机不便注入；S-14 多屏链需双显示器。

## 走查验证的良好点

- 隐藏 / 还原是 `SW_HIDE` + `WINDOWPLACEMENT`，坐标换算只扣 `rcWork - rcMonitor`（副屏那个坑有单测锁着）。
- 「关闭窗口」只对组内句柄发 `WM_CLOSE`，没有按 exe 路径扫。
- 按屏隔离统一走 `DisplayRules.BelongsTo`，屏幕未知时 fail-closed。
- 场景写入只有 `SceneService` 一个入口，快照与运行时组不会各改一边。
- 崩溃兜底三层（`RegisterRescue` / `ReleaseAll` / `recovery.json` + 身份校验）。
- 胶囊 z 序自愈（1.7.1）实测 74–176 ms，且只抬 z 序不做全量重排。

## 结论三问

1. **链路逻辑通顺吗？** 主线通顺；断在「场景回到屏幕之后」这一段——恢复、置前、单窗口过滤三件事各写了一半，没有统一收尾点。本轮补的 `FinishSceneOpen` 就是那个缺失的收尾点。
2. **是补丁叠补丁吗？** 有一处：最小化标志、置前规则、追加时的状态保持此前散在三个类里；现已分别收敛到 `SingleWindowMode.ApplyNow` / `SceneFocusRules` / `StashSingle`，各只有一个归属点。
3. **会无法操作 / 出 bug 吗？** 修复前：单窗口模式的「打开场景」等于失效（W-01），且场景看不见在最上层（W-02）；还有禁用按钮拖窗（W-06）这种误操作陷阱。修复后主链可走通，剩余风险见下。

## 未修（记录在案）

- **W-12**：精确计数需要在发出 `WM_CLOSE` 后延时用 `IsWindow` 复查，会把一次同步操作变成异步；当前文案在正常路径（程序直接关掉）是准确的，暂不改。

## 建议处理（按风险分）

1. W-01 / W-02 需**实机回归**（步骤见下），风险分最高且是用户实报。
2. W-05 建议手工验证一次（把 `%LOCALAPPDATA%\TaskbarDrawer` 设为只读后新建场景）。
3. 其余按 CHANGELOG 1.8.1 条目随版本回归。

### 实机回归步骤（单窗口模式）

1. 开启单窗口模式 → 打开桌面上两三个无关窗口（记事本等）
2. 点胶囊展开一个**已收起**的场景 → 期望：场景窗口在最前，其它窗口被最小化
3. 点一个无关窗口 → 期望：这个场景整组收回胶囊
4. 再点胶囊展开 → 期望：场景置前、无关窗口被最小化（这一步修前必失败：`ForegroundBefore` 为 0）
5. 关闭单窗口模式 → 重复 2 → 期望：只置前，其它窗口不动
