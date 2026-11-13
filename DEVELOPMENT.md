# DEVELOPMENT.md · 开发说明

> 面向开发者（自己）。结构固定三块：项目概览 → 架构说明 → 关键问题与方案。
> 超过 200 行再拆 `docs/架构.md`、`docs/问题记录/`（一坑一篇，格式与 knowledge-base 一致）。

## 一、项目概览

- **做什么**：把一组窗口收进任务栏胶囊、原位展开；把软件 + 窗口位置存成场景，关掉后还能按原位置拉起。
- **不做什么**：不做窗口缩略图、不做虚拟桌面、不替换任务栏、不接管窗口管理（取消编组即完全交还系统）。
- **状态**：v1.3.0，个人自用 + 开源；单显示器 1920×1080 / Win11 24H2 为主验证环境。
- **约束**：Win11 不允许 DeskBand / 往任务栏塞子窗口，胶囊只能是自己画的 topmost 小窗。

## 二、架构说明

### 2.1 进程与生命周期（`Program.cs`）

- 命名 Mutex（`Local\TaskbarDrawer.SingleInstance`）保证单实例；第二个实例 `Set` 命名事件 `Local\TaskbarDrawer.ShowPanel`，由已运行实例唤起主面板。
- `ShutdownMode.OnExplicitShutdown`：关面板 ≠ 退出，只能从托盘退出。
- `Diagnostics`：全局 `DispatcherUnhandledException` → 记日志 → 退出；`RegisterRescue` 在崩溃时交还隐藏窗口；`--probe` 挂到父控制台只读打印候选窗口。

### 2.2 分层

| 层 | 目录 | 职责 |
| --- | --- | --- |
| Interop | `Interop/` | `NativeMethods.Window`（枚举 / 样式 / `WINDOWPLACEMENT` / 显示器与 DPI / `WindowFromPoint` / `GetAncestor`）、`Input`（`RegisterHotKey`、前台激活）、`Dwm`（cloaked 判定） |
| 核心 | `Core/` | 纯逻辑，不引用 WPF 控件 |
| 界面 | `UI/` | WPF 纯代码窗口与托盘菜单 |
| 装配 | `AppShell.cs` | 唯一组合根：建服务、接事件、`UpdateSurface` 同步胶囊与面板 |

核心对象：

- `WindowEnumerator` / `WindowInfo`：候选窗口过滤（可见、有标题、非工具窗、非 cloaked、非本程序、非 shell）。
- `WindowStash`：`Capture(hwnd)` 记录 `WINDOWPLACEMENT` 并转屏幕坐标 → `SW_HIDE`；`Restore` 还原；`Track` / `Refresh` 供「可见窗口接入编组」与「保存前刷新位置」用。
- `StashAllService`：运行时组状态机（`StashAll` / `ExpandGroup` / `ToggleGroup` / `StashGroup` / `CloseGroup` / `Prune` / `Adopt` / `LinkSaved`）。
- `SavedGroups`：场景模型 + `saved-groups.json` 原子写（临时文件 + `File.Move`）。
- `WorkspaceService`：场景的保存 / 更新快照 / 关闭 / 还原；还原时已有窗口直接摆位，缺失的 `Process.Start` 后在 12 s 内轮询新窗口并摆位。
- `SingleWindowMode` / `WindowActivator` / `WindowRecovery` / `HotkeyManager` / `AppSettings` / `AutoStart`：单窗口、抢前台、崩溃恢复、热键、配置、自启。
- `Rules/RuleModel`（`RuleSet` / `RuleFlow` / `RuleStep`）：规则树 + 写死在代码里的默认树，不落盘。
- `Rules/RuleCatalog`：卡片目录（Kind / 分类 / 标题 / 说明 / 可用入口 / 参数定义）。
- `Rules/RuleRunner` + `RuleFacts`：解释器。条件与动作都是查表委托，无反射 / 无表达式树 / 无动态编译。

界面对象：

- `TaskbarChipLayer`：胶囊层。每 2 s 重贴任务栏、体检 z 序；最多 7 枚，多出的进溢出槽（逐组可点）。
- `TaskbarChipWindow`：单枚胶囊（深色圆角、数量、tooltip）。
- `MainPanelWindow`：主面板（编组 / 场景 / 开关 / 热键）。
- `Theme` / `Ui`：深色画刷与通用控件工厂（`Frozen` / `Text` / `Clickable`），窗口只引用，不再各自定义颜色。
- `TrayService`（托盘 + 深色菜单）、`TextPromptWindow`（重命名）、`ToastWindow`、`DarkMenuRenderer`、`AppIcon`。

### 2.3 关键数据流

```
收纳：热键 → StashAll → 收集可见窗口 → WindowStash.Capture + SW_HIDE → 新组 → UpdateSurface → 胶囊
展开：胶囊 / 面板 / 热键 → ExpandGroup → 逐句柄 Restore → 前台还给收纳前窗口
存场景：面板「存储编组」→ WorkspaceService.SaveFrom(组内窗口) → saved-groups.json → LinkSaved(组, 场景 id, 名字)
关场景：「关闭窗口」→ 有 SavedId：UpdateSnapshot + 逐句柄 PostMessage(WM_CLOSE)；无 SavedId：取消编组
开场景：场景胶囊 → Restore → 已有窗口摆位 / 缺失的启动 + 轮询 → Adopt 接回运行时编组
入口事件：热键 / 胶囊点击 → 算 RuleFacts（一次枚举）→ RuleRunner 走规则树 → 命中动作 → 调上面这些方法
```

术语只有两个：**编组**（运行时、按句柄）、**场景**（持久化、按 exe 路径 + 位置）。同一个窗口可以同时属于多个编组。

## 三、关键问题与方案

### 问题：胶囊「看得见但点不到」，持续 0–30 s

**TL;DR**：胶囊与任务栏共用 topmost 波段，且几何上落在任务栏矩形内；点一次任务栏，Explorer 就被抬到胶囊之上，输入全落到 `MSTaskListWClass`。

- 问题：失效期间截图逐像素一致（任务栏半透明，胶囊从 DWM 合成里透出来），用户会以为程序卡死，甚至误点到别的任务栏按钮。
- 根因：`SetWindowPos(HWND_TOPMOST)` 只保证在 topmost 波段内，任务栏被点击后也在同一波段里更靠前；原实现只在矩形变化时才重贴。
- 解决：每个 2 s tick 先做 z 序体检——取胶囊中心点 `WindowFromPoint`，命中不是自己且根窗口是 `Shell_TrayWnd` → 判定被盖，强制完整重贴；未被盖不重绘，避免闪烁。
- 预防：任何「贴任务栏 / 贴屏幕边缘」的自绘窗口，都要按「输入命中」而不是「矩形坐标」判断自己是否可点。
- 效果：自愈耗时从 10.1 / 20.0 / 27.2 s 降到 700 / 1500 / 1600 ms。

### 问题：保存场景后，原编组胶囊消失，切组变成「关闭再打开」

**TL;DR**：曾把场景作为第二套模型并行展示，出现了两枚语义不同的胶囊；同一个组的操作路径被拆成「运行时切换」和「场景关闭 / 启动」。

- 问题：存完场景，原组胶囊不显示；点场景胶囊是 `WM_CLOSE` 后重启进程，而不是普通组的隐藏 / 展开。
- 根因：运行时组（句柄）与场景快照（json）没有身份关联，`UpdateSurface` 把两者都当独立条目渲染；点击分支只按「是否场景」决定行为。
- 解决：`StashGroup` 增加 `SavedId` / `Name`，保存时 `LinkSaved` 关联但不新建胶囊；场景窗口全关后胶囊转为 dormant（「已关闭」），点击才启动并按位置摆回，随后 `Adopt` 接回运行时组。
- 预防：同一实体只能有一个界面入口；持久化层的变化要以「附着」的方式合并到运行时模型，不要并行渲染。

### 问题：关闭场景把同程序的其它窗口一起关了（下载弹窗）

**TL;DR**：关闭逻辑按 exe 路径枚举该程序所有顶层窗口并统一发 `WM_CLOSE`，把不属于本组的下载窗口也关了，浏览器 / 下载器各自弹出「这将取消所有传输」。

- 问题：用户只想关组内窗口，结果丢传输任务。
- 根因：场景只存了 exe 路径，关闭时用路径反查窗口；而同一程序可能有多个独立窗口（下载、设置、弹窗）。
- 解决：运行时组始终持有真实句柄，关闭只对这些 hwnd 发 `WM_CLOSE`；路径只用于「窗口已不存在时重新启动」。
- 预防：任何「关闭」动作的作用域必须是显式记录的对象，不能用模糊匹配反推。

### 问题：关闭窗口数量虚高（1 个窗口报 5 个）

**TL;DR**：WinForms 程序的隐藏辅助窗口也通过了候选过滤，关闭计数按命中数返回。

- 问题：toast / 反馈里的数字和用户感知不一致。
- 根因：只统计了「发送了 WM_CLOSE 的窗口」，没区分可见 / 隐藏。
- 解决：分别统计 visible / hidden，优先返回可见数量；反馈文案只报用户能看到的窗口。
- 预防：计数口径要跟文案口径一致，隐藏窗口不算「关掉了几个」。

### 问题：Explorer 重启后胶囊被压住；6 组以上无法直达

**TL;DR**：任务栏句柄变化时矩形可能完全相同，只比较 RECT 会跳过重贴；胶囊超过 7 枚后早期只能看到 `+N`，没有逐组入口。

- 解决：变化检测加入 `Shell_TrayWnd` 句柄比较，句柄变了强制重排；溢出槽列出每一组的名字，可直接点 / 右键。
- 预防：判断「外部环境是否变化」要比较身份（句柄），不能只比较几何。

### 问题：编组胶囊展开后，再点没反应

**TL;DR**：规则化时把「再点收起」丢了，默认流写成「未展开才展开」，展开状态落到空分支。

- 问题：README 承诺「再点 → 收起」，实际点击没有任何反馈，看起来像程序卡住。
- 根因：GroupChip 默认树用 `cond.chipOnScreen` 包住 `act.expandGroup`，`Then` 留空；改造前的手感没有被完整翻译成规则。
- 解决：默认流改成单张 `act.toggleGroup`；同时给 `cond.hasUntracked` / `cond.hasExpandedGroup` 补 `OnlyTriggers = Hotkey`，避免加进胶囊流后永远为 false。
- 预防：默认树是产品手感的一部分，改规则要对着 README 逐条核对；新增条件卡先确认「这个事实在哪几个入口算过」。

## 四、数据文件与调试

- `%LOCALAPPDATA%\TaskbarDrawer\settings.json`、`saved-groups.json`、`recovery.json`、`logs\`
- `--probe`：确认候选窗口集合（判断「为什么这个窗口没被收进去」）
- `dotnet test tests/TaskbarDrawer.Tests`：规则引擎回归测试（默认手感 / 步数与深度护栏 / 目录约束）
- 面板「编组」区块：点整行收起 / 展开；按钮与胶囊右键菜单一致
- 发布：`dotnet publish ... -p:PublishSingleFile=true`，刻意保持框架依赖以把 exe 压到约 320 KB

## 五、文档维护

| 场景 | 动作 |
| --- | --- |
| 改功能 | 更新本文件对应小节 |
| 踩坑并解决 | 追加「问题：…」一节（TL;DR / 问题 / 根因 / 解决 / 预防） |
| 发版 | `CHANGELOG.md` 加版本节 + 版本号 bump + 更新 `AGENTS.md` 基线行 |
| 约定变化 | 更新 `AGENTS.md` 或按需拆到 `rules/` |
