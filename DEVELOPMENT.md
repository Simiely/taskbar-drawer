# DEVELOPMENT.md · 开发说明

> 面向开发者（自己）。主文件只保留项目概览、架构总览、问题索引与维护规则；
> 详细架构见 [docs/架构.md](docs/架构.md)，一坑一篇见 [docs/问题记录/](docs/问题记录/)。
> 现行规则与坑以 [AGENTS](AGENTS.md) 的「关键坑」为准，本节只保留背景与历史决策。

## 一、项目概览

- **做什么**：把一组窗口收进任务栏胶囊、原位展开；每次收纳都自动存成场景，关掉后还能按原位置拉起。
- **不做什么**：不做窗口缩略图、不做虚拟桌面、不替换任务栏、不接管窗口管理（删除场景即完全交还系统）。
- **状态**：v1.15.5，个人自用 + 开源；Win11 24H2。1.6.x–1.13.8 的按屏、胶囊 z 序、弹窗窗口簇、窗口身份、事件驱动、移除修复均已落地；1.14.x 补场景成员自动认回与微信辅助窗口；1.15.x 单窗口模式改为按屏独立、场景归属屏固定，并补跨屏拖动判定；1.15.4 补飞书同 exe 辅助窗口归组与「资源管理器不计入」开关；1.15.5 辅助窗口打开时保留宿主主窗口。
- **约束**：Win11 不允许 DeskBand / 往任务栏塞子窗口，胶囊只能是自己画的 topmost 小窗。

## 二、架构说明

四层：`Interop/`（P/Invoke）→ `Core/`（纯逻辑）→ `UI/`（WPF 纯代码）→ `AppShell*.cs`（唯一组合根）。

- `AppShell` 三片：`AppShell.cs` 组合根与生命周期、`AppShell.Commands.cs` 命令实现、`AppShell.Wiring.cs` 入口接线。
- `WorkspaceService` 四片：组合与持久化、`Snapshot` 抓窗口、`Restore` 还原编排、`Editing` 只改快照。
- 状态机 `StashAllService` 通过 `IWindowGateway` 碰窗口；场景快照与运行时组靠 `SavedId` 关联。
- 详细分层、核心对象、数据流、单窗口模式（按屏独立 / 归属屏固定 / `MOVESIZEEND`）见 [docs/架构.md](docs/架构.md)。

## 三、关键问题与方案

> 完整索引由 `docs/问题记录/` 的 `kind: pitfall` 页面自动生成：[踩坑索引](docs/_generated/pitfalls.md)。

| 版本 | 问题 | 记录 |
| --- | --- | --- |
| 1.13.7 | 移除的窗口又被关场景写回快照（1.13.7） | [记录](docs/问题记录/1-13-7-remove-window.md) |
| 1.14.0 | 场景里的程序关掉再打开，运行中的场景认不回来（1.14.0） | [记录](docs/问题记录/1-14-0-reattach.md) |
| 1.11.0 | 浏览器切过标签页，场景重开时两个窗口互相换位（1.11.0） | [记录](docs/问题记录/1-11-0-identity.md) |
| 1.10.0 | 窗口关掉了，胶囊上的数字要等 2.5 s；「已关闭 N 个」也是假的（1.10.0） | [记录](docs/问题记录/1-10-0-events.md) |
| 1.9.1 | 补 AutomationId 补了个寂寞——Border 在 UIA 里是透明的（1.9.1） | [记录](docs/问题记录/1-9-1-uia-button.md) |
| 1.9.0 | 浏览器插件弹窗 / 软件对话框被当成窗口收进场景（1.9.0） | [记录](docs/问题记录/1-9-0-window-cluster.md) |
| 1.8.1 | 单窗口模式打开场景后，其它窗口不最小化、场景还在 z 序最下面（1.8.1） | [记录](docs/问题记录/1-8-1-scene-open.md) |
| 1.7.1 | 胶囊「被任务栏压住」长达一个轮询周期（1.7.1） | [记录](docs/问题记录/1-7-1-chip-zorder.md) |
| 1.7.0 | 胶囊「看得见但点不到」，持续 0–30 s | [记录](docs/问题记录/1-7-0-chip-not-clickable.md) |
| 1.1.1 | 保存场景后，原编组胶囊消失，切组变成「关闭再打开」（历史记录，1.1.1 已修） | [记录](docs/问题记录/1-1-1-scene-capsule.md) |
| 历史 | 关闭场景把同程序的其它窗口一起关了（下载弹窗） | [记录](docs/问题记录/history-close-wrong-windows.md) |
| 历史 | 关闭窗口数量虚高（1 个窗口报 5 个） | [记录](docs/问题记录/history-close-count.md) |
| 历史 | Explorer 重启后胶囊被压住；6 组以上无法直达 | [记录](docs/问题记录/history-explorer-overflow.md) |
| 1.2.1 | 编组胶囊展开后，再点没反应（历史记录，1.2.1 已修；GroupChip 已在 1.5.0 删除） | [记录](docs/问题记录/1-2-1-group-chip.md) |

## 四、数据文件与调试

- `%LOCALAPPDATA%\TaskbarDrawer\settings.json`、`saved-groups.json`、`recovery.json`、`logs\`
- `--probe`：确认候选窗口集合（判断「为什么这个窗口没被收进去」）
- `dotnet test tests/TaskbarDrawer.Tests`：规则引擎、状态机、身份配对等回归测试（当前 164/164）
- 面板「场景」区块：点整行收起 / 展开；「关闭窗口」真关；「详情」在面板旁打开侧窗，移除 / 追加立即同步运行时组
- 发布：`pwsh tools/release.ps1` 构建 / 测试 / 打包；刻意保持框架依赖，zip 约 260 KB，完整步骤见 [交接](docs/handoff.md)

## 五、文档维护

| 场景 | 动作 |
| --- | --- |
| 改功能 | 更新 [架构说明](docs/架构.md) 对应小节 |
| 踩坑并解决 | 在 `docs/问题记录/` 新建一页（TL;DR / 问题 / 根因 / 解决 / 预防），或更新已有页 |
| 发版 | `CHANGELOG.md` 加版本节 + 版本号 bump + 更新 `AGENTS.md` 基线行 |
| 约定变化 | 更新 `AGENTS.md` 或按需拆到 `rules/` |
