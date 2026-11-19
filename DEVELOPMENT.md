# DEVELOPMENT.md · 开发说明

> 面向开发者（自己）。结构固定三块：项目概览 → 架构说明 → 关键问题与方案。
> 超过 200 行再拆 `docs/架构.md`、`docs/问题记录/`（一坑一篇，格式与 knowledge-base 一致）。

## 一、项目概览

- **做什么**：把一组窗口收进任务栏胶囊、原位展开；每次收纳都自动存成场景，关掉后还能按原位置拉起。
- **不做什么**：不做窗口缩略图、不做虚拟桌面、不替换任务栏、不接管窗口管理（删除场景即完全交还系统）。
- **状态**：v1.7.1，个人自用 + 开源；Win11 24H2。1.6.0 的按屏主路径已在双显示器上验证；1.6.1 单屏机 22 项实机验证全部通过（收纳 / 展开原位 / 快照带屏 / 回收即时落盘 / 500 ms 强杀恢复 / 二次启动唤起面板 / 稳态占用）。1.6.2 修复双屏下场景整组收起会波及另一块屏的问题；1.6.3 修开关入口按屏；1.6.4 修提权重启；1.6.5 修副屏场景坐标并加诊断日志（用户双屏日志确认 `actual == target`）；1.6.6 放开场景窗口手动最小化、加场景 emoji 与任务栏标题前缀、修重命名弹窗按钮；1.6.7 把场景 emoji 换成叠在窗口图标角上的纯几何标识（颜色圆点 / 数字 1–9，位置左上或右上）、移除标题前缀、加两个尺寸滑块（并排一行，默认 90% / 55%）、8 色可自定义与「恢复默认」，并把胶囊改成任务栏按钮式透明 / 悬停灰底。跨屏行为仍以代码审查 + `DisplayRulesTests` 单测为准。1.7.0 做主面板 / 详情侧窗与说明窗口；1.7.1 把胶囊 z 序从 2 s 轮询改成事件驱动 + 自适应守卫（遮挡从最长一个周期降到 ≤250 ms，见「关键问题」）；1.8.0 把 `AppShell` 拆成三片；1.8.1 修单窗口模式不最小化 / 场景停在 z 序最下面等 11 个走查问题；1.9.0 弹窗按「窗口簇」处理；1.9.1 修说明窗高度上限，并把按钮从 `Border` 换成真 `Button`（`Border` 在 UIA 树里不存在）；1.10.0 剪枝改事件驱动、关闭计数按实际结果报；1.11.0 窗口身份换成两层（应用 + 类名 + 位次），配对不再以标题为准；1.12.0 把状态机的窗口操作抽成 `IWindowGateway` 端口，迁移矩阵进了单测；1.13.0 把 `WorkspaceService` 拆成四片（编辑 / 快照 / 还原 / 组合）。
- **约束**：Win11 不允许 DeskBand / 往任务栏塞子窗口，胶囊只能是自己画的 topmost 小窗。

## 二、架构说明

### 2.1 进程与生命周期（`Program.cs`）

- 命名 Mutex（`Local\TaskbarDrawer.SingleInstance`）保证单实例；第二个实例 `Set` 命名事件 `Local\TaskbarDrawer.ShowPanel`，由已运行实例唤起主面板。
- `ShutdownMode.OnExplicitShutdown`：关面板 ≠ 退出，只能从托盘退出。
- `Diagnostics`：全局 `DispatcherUnhandledException` → 记日志 → 退出；`RegisterRescue` 在崩溃时交还隐藏窗口；`--probe` 挂到父控制台只读打印候选窗口。

### 2.1b 组合根的三片（`AppShell*.cs`）

同一个 `partial class AppShell`，按「谁改什么」切开，改功能时只需要打开一片：

| 文件 | 放什么 | 不变量 |
| --- | --- | --- |
| `AppShell.cs` | 字段、`Initialize` 的装配顺序、消息宿主与热键注册、`Safe`、`RequestSurfaceUpdate` / `UpdateSurface`、唤起重启 `Exit` / `Dispose` | 装配顺序即依赖：服务 → 单窗口 → 胶囊 → 热键宿主 → 托盘 → 规则 → 热键注册 → 面板。托盘必须在 `RegisterHotkeys` 之前（注册失败要弹气泡），面板必须最后（要读已生效的热键文案与外观设置） |
| `AppShell.Commands.cs` | 每个用户动作的实现、规则卡片绑定、设置写入、反馈文案 | 只走 `SceneService` / `StashAllService` / `WorkspaceService`，不直接改控件；结尾统一 `RequestSurfaceUpdate()` |
| `AppShell.Wiring.cs` | `WireServices` / `WireTray` / `WirePanel` / `WireChips` / `WireSingleWindow` / `WireHotkeyHost` / `WireRules`，以及面板 / 详情 / 说明窗口的开关与数据推送 | 每个订阅就地包 `Safe(scope, …)`；面板隐藏时不建 UI，`SetScenes` 只记数据 |

拆分后单片 365 / 652 / 361 行（原 1286 行）。拆分是纯机械搬迁，行为零变化——验收标准就是「启动日志与实机操作和拆分前逐条一致」。

### 2.1c 场景服务的四片（`WorkspaceService*.cs`）

同一个 `partial class WorkspaceService`，同样按「谁改什么」切开：

| 文件 | 放什么 | 不变量 |
| --- | --- | --- |
| `WorkspaceService.cs` | 字段、构造、启动迁移（旧 emoji / 旧移除键 / 旧矩形）、`Persist` / `SaveFailed`、`Summaries` / `Find` / `Detail`、`MonitorOfSaved`、`Normalize` / `Brief` | 写盘只有一个出口（`Persist`）；启动迁移必须等 `AppShell` 订阅 `SaveFailed` 之后才落盘，否则失败无声 |
| `WorkspaceService.Snapshot.cs` | `CaptureWindow` / `EnumerateWindows` / `SaveFrom` / `UpdateSnapshot` / `CaptureWindows` / `ToSaved`，以及 `TopWindow` / `TopLevelWindows` / `OrdinalCandidates` / `ProcessPathOf` | 只读窗口、不改窗口；位次在整组抓完之后统一分配（`WindowIdentity.AssignOrdinals`） |
| `WorkspaceService.Restore.cs` | `Restore`（摆位 / 启动 / 等窗口）、`Close`、`PositionLater`、`Launch`、`ApplyPlacement` | 配对走 `WindowIdentity.Pair`、窗口操作走 `IWindowGateway`；这里只编排「先摆位还是先启动」与 12 s 等待期 |
| `WorkspaceService.Editing.cs` | `RemoveWindow` / `AppendWindows` / `Rename` / `SetBadge` / `RemapBadgeColors` / `Delete` / `NextSceneName` | 只改快照数据，不碰任何窗口；运行中场景的同步由 `SceneService` 负责 |

拆分后单片 261 / 253 / 290 / 131 行（原 829 行）。纯机械搬迁：42 个成员逐个核对在位、`dotnet test` 一行没改就全绿。

### 2.2 分层

| 层 | 目录 | 职责 |
| --- | --- | --- |
| Interop | `Interop/` | `NativeMethods.Window`（枚举 / 样式 / `WINDOWPLACEMENT` / 显示器与 DPI / `WindowFromPoint` / `GetAncestor`）、`Input`（`RegisterHotKey`、前台激活）、`Dwm`（cloaked 判定） |
| 核心 | `Core/` | 纯逻辑，不引用 WPF 控件 |
| 界面 | `UI/` | WPF 纯代码窗口与托盘菜单 |
| 装配 | `AppShell*.cs` | 唯一组合根，1.8.0 起按职责分三片（同一个 `partial class`）：`AppShell.cs` 组合根与生命周期、`AppShell.Commands.cs` 命令实现、`AppShell.Wiring.cs` 入口接线与窗口管理 |

核心对象：

- `WindowEnumerator` / `WindowInfo`：候选窗口过滤（可见、有标题、非工具窗、非 cloaked、非本程序、非 shell）。
- `WindowStash`：`Capture(hwnd)` 记录 `WINDOWPLACEMENT` 并转屏幕坐标 → `SW_HIDE`；`Restore` 还原；`Track` / `Refresh` 供「可见窗口接入场景」与「保存前刷新位置」用；`SetSceneIcon` / `ClearSceneIcon` 给成员窗口挂 / 摘场景合成图标。
- `StashAllService`：运行时状态机（`StashAll` / `ExpandGroup` / `ToggleGroup` / `StashGroup` / `Release` / `Prune` / `Adopt` / `LinkSaved` / `SetBadge`）。运行时组 = 场景的运行态。
- `WindowEventWatcher`：按**被跟踪进程**订阅 `EVENT_OBJECT_DESTROY / SHOW / HIDE`，替代轮询做状态对账（一个进程一个钩子，`idProcess` 是注册期过滤）。只放行 `OBJID_WINDOW` 且句柄在记录里的事件——过滤规则抽成纯函数 `Classify`，钩子本身在测试进程里装不了。
- `NativeMethods.RootOwner` / `WalkOwners`：自己沿 `GW_OWNER` 走 owner 链找簇主（见 1.13.4，`GA_ROOTOWNER` 在 `WS_POPUP` 上会断链）。
- `IWindowGateway` / `Win32WindowGateway`：状态机碰窗口的唯一入口。读操作折进 `Describe`，四种显示语义合成 `SetVisibility`；坐标换算、矩形校正、同簇弹窗链都在 Win32 实现里。单测注入假实现，状态迁移不再需要真实窗口。
- `CloseReport`：关闭场景的实测结果（请求数 / 实际关掉数 / 仍在等待数）+ 提示文案，纯规则、单测覆盖。
- `WindowIdentity`：两层窗口身份的纯规则——`AppId`（L1）、`AssignOrdinals`（簇内位次，显示器优先排序）、`Pair`（四级配对）。旧快照没有身份字段时自动退回标题 + 矩形，行为与改造前一致。
- `SavedGroups`：场景模型（含 `SceneBadge`）+ `saved-groups.json` 原子写（临时文件 + `File.Move`）。
- `SceneBadge` / `SceneTitle`：场景标识（颜色 / 数字 / 位置）的取值约束、文字表示、旧 emoji 迁移，以及旧标题前缀清理的纯函数，单测覆盖。
- `ISceneIconPainter` / `SceneIconPainter`：把场景标识与程序原图标合成一张 HICON（彩色圆底 + 可选白色数字，叠在图标左上 / 右上角），按窗口 DPI 出 32 / 16 px 两档，Set 到窗口后任务栏按钮显示「角标 + 程序图标」；纯几何绘制，不依赖 emoji 字体或位图资源。原图标与标识的大小分别由 `AppSettings.SceneAppIconSize`（0.55–1.0，默认 0.90）/ `SceneBadgeSize`（0.25–0.65，默认 0.55）控制；8 色调色板存在 `AppSettings.SceneBadgeColors`，右上角可自定义改色，改色 / 恢复默认通过 `WorkspaceService.RemapBadgeColors` + `StashAllService.RemapBadgeColors` 同时刷新快照与运行中的图标；面板拖动滑块时 `StashAllService.RefreshSceneIcons` 立即重画。
- `WorkspaceService`：场景的保存 / 更新快照 / 关闭 / 还原；还原时已有窗口直接摆位，缺失的 `Process.Start` 后在 12 s 内轮询新窗口并摆位。
- `SingleWindowMode` / `WindowActivator` / `WindowRecovery` / `HotkeyManager` / `AppSettings` / `AutoStart`：单窗口、抢前台、崩溃恢复、热键、配置、自启。
- `Rules/RuleModel`（`RuleSet` / `RuleFlow` / `RuleStep`）：规则树 + 写死在代码里的默认树，不落盘。
- `Rules/RuleCatalog`：卡片目录（Kind / 分类 / 标题 / 说明 / 可用入口 / 参数定义）。
- `Rules/RuleRunner` + `RuleFacts`：解释器。条件与动作都是查表委托，无反射 / 无表达式树 / 无动态编译。

界面对象：

- `TaskbarChipLayer`：胶囊层。全量体检每 2 s 一次（兜底），z 序靠 `EVENT_SYSTEM_FOREGROUND` 与 Explorer 进程的 `EVENT_OBJECT_LOCATIONCHANGE` 事件驱动 + 120 ms 自适应守卫（16 轮后停）；被压住时只抬 z 序，几何变了才重排。最多 7 枚，多出的进溢出槽（逐场景可点）。
- `TaskbarChipWindow`：单枚胶囊（深色圆角、数量、tooltip）。
- `MainPanelWindow`：主面板（场景列表 / 开关 / 热键）；顶部拖动条与空白 / 文字处可拖，长说明统一收进 `HelpWindow`。
- `SceneEditorWindow`：场景详情侧窗（面板旁、非模态、初始约半屏、窗内滚动、右下角可调大小；移除窗口 / 追加这块屏幕上的全部窗口 / 重命名 / 删除）。只读数据 + 抛事件，写操作全部回 `AppShell` → `WorkspaceService` / `SceneService`。
- `HelpWindow`：使用说明（内嵌 `Assets/help-panel.svg` / `help-scene.svg` 重绘的矢量示意图；构建前转成 WPF 图形 XAML + 原生文本 XAML，不引入 SVG 运行时）。改 SVG 后需用 SharpVectors 转换工具重新生成 `help-*.xaml` / `help-*-labels.xaml`，当前提交只保留生成后的 XAML。
- `Theme` / `Ui`：深色画刷、统一字体 / 文本工厂、场景标识圆点控件与深色细滚动条样式，窗口只引用，不再各自定义颜色和字体。
- `TrayService`（托盘 + 深色菜单）、`TextPromptWindow`（重命名）、`ToastWindow`、`DarkMenuRenderer`、`AppIcon`。

### 2.3 关键数据流

```
收纳：热键 → 算当前显示器 → StashAll(out groupId, 显示器) → 收集这块屏的可见窗口 → WindowStash.Capture + SW_HIDE → 新组
      → WorkspaceService.SaveFrom(组内窗口) → saved-groups.json → LinkSaved(组, 场景 id, 名字) → UpdateSurface → 胶囊
展开：胶囊 / 面板 / 热键 → ExpandGroup → 逐句柄 Restore → 前台还给收纳前窗口
按屏：ExpandNewest / ExpandAll / CollapseExpandedScenes 都带显示器条件；场景归属用 StashedWindow.MonitorDevice 判断
改场景：场景行「详情」→ SceneEditorWindow（同一场景再点一次详情 = 收起；点另一个场景 = 切换内容并置前）→ RemoveWindow（快照 + 从运行时组 Detach）/ AppendWindows（枚举全部窗口 + Adopt）→ saved-groups.json 与运行时组立即同步
关场景：「关闭窗口」→ UpdateSnapshot + 逐句柄 PostMessage(WM_CLOSE) → 胶囊转为已关闭
删场景：「删除场景」→ StashAllService.Release（隐藏的窗口还原，可见的不动）→ Delete 快照
开场景：场景胶囊 → Restore → 已有窗口摆位 / 缺失的启动 + 轮询 → Adopt 接回运行时组
入口事件：热键 / 场景胶囊点击 → 算 RuleFacts（一次枚举）→ RuleRunner 走规则树 → 命中动作 → 调上面这些方法
```

术语只有两个：**场景**（持久化 + 运行态，标识为彩色圆点 / 数字）与**单窗口模式**。运行时组是场景的运行态，靠 `SavedId` 与快照一一对应；正常路径下每个运行时组都有 `SavedId`。

场景快照里的 `Suppressed` 记录用户移除过的窗口（新快照用 `SceneEdit.IdentityKey` = `AppId + 类名 + 位次`；旧快照没有身份字段时退回 `SceneEdit.Key` = 规范化 exe 路径 + 去两端空白的标题，按出现次数生效）。移除时 `SceneService` 会同时把窗口从运行时组摘掉（收起中的先放回桌面）；**关闭场景时 `UpdateSnapshot` 只刷新位置，不再改成员集合**（1.13.7），所以移除项不可能被整份重抓写回来；重新追加同一窗口时只清除一条抑制记录。

## 三、关键问题与方案

> 现行规则与坑以 [AGENTS](AGENTS.md) 的「关键坑」为准；本节保留问题背景与历史决策，不重复维护细节。

### 问题：移除的窗口又被关场景写回快照（1.13.7）

**TL;DR**：移除是两个动作——从运行时组 `Detach`、从快照 `RemoveWindow`。只要其中一处还在用「exe + 标题」匹配，标题一变就会漏；而 `UpdateSnapshot` 整份覆盖成员集合，又会把这次漏掉放大成永久写回。

- 用户现场是旧快照：`AppId` / `ClassName` 为 `null`，`Suppressed` 是 `exe\n标题`。同程序两个资源管理器窗口、标题又变了时，`Detach` 认不回目标，运行时组里仍挂着它
- 关场景时 `UpdateSnapshot` 从运行时组整份重抓写入快照，于是刚从详情窗移除的项又被写回 `saved-groups.json`；下次开场景自然还在
- 修法分两层：`Detach` 复用 `WindowIdentity.Candidate` / `AssignOrdinals` / `Pair`（与还原配对、详情窗「已打开」判定同一套规则）；`UpdateSnapshot` 降级为**只刷新矩形 / 最大化 / 显示器**，永不改 `group.Windows`
- 兼容：旧快照没有身份字段时，`Pair` 与 `SceneEdit.IdentityKey` 都会退回旧键 / 矩形，行为与 1.11.0 之前一致；`Suppressed` 双键并存，旧数据不需要迁移
- 验证：真实窗口夹具用 `TASKBAR_DRAWER_HOME` 隔离数据，改标题后 `Detach` 仍摘对成员；再故意把窗口 `Adopt` 回旧运行时组跑 `UpdateSnapshot`，磁盘快照仍只有 1 项

### 问题：场景里的程序关掉再打开，运行中的场景认不回来（1.14.0）

**TL;DR**：运行时组只记句柄，窗口销毁后记录被移出，快照项还在；程序重开是新句柄，不会自动回到组里。重启本程序后反而正常——场景变回「已关闭」，点开时走的是还原配对。

- 现象：场景展开时关掉其中一个程序（日志 `窗口已销毁，移出记录`），重新打开后新窗口不跟场景一起收起 / 展开，胶囊计数也不含它。
- 根因：`StashAllService` 只持有 hwnd；`Prune` 销毁记录后，运行态与快照之间没有「等它回来」的桥。
- 解决：`Core/SceneReattach` 算出快照里缺了哪一项、把新窗口认回缺失项；`SceneService.TryReattach` 由全局 `EVENT_OBJECT_SHOW` 驱动（150 ms 合并），`ReclaimVisible` 在展开入口（`Restore` / `ExpandLatest` / `ExpandAll`）兜底。配对刻意不用位次——少一个成员后位次会整体前移，会把还开着的认成被关掉的。
- 竞态：单窗口模式必须先 `ClaimForeground` 再判定，否则新窗口会被当成「场景外窗口」，把整个场景收回胶囊。
- 边界：收起中的场景不在 SHOW 时接入（避免把用户刚打开的程序藏掉），只在展开时认领。

### 问题：浏览器切过标签页，场景重开时两个窗口互相换位（1.11.0）

**TL;DR**：窗口身份用的是「exe + 标题」，而标题是窗口上最易变的属性。换掉标题这一层，改成「应用 + 窗口类名 + 簇内位次」，标题降为弱信号。

- 问题：同一个浏览器开两个窗口，切一下标签页（标题变了）→ 收起 → 关闭场景 → 重新打开，两个窗口各回**对方**的位置；有时还会重复启动一份。
- 根因：`SceneEdit.Key = exe + 标题` 既被用来去重，也被用来配对。标题一变，旧键认不回原窗口，`PickRestoreCandidate` 只能退回「矩形最近」——两个窗口位置接近时就互相调换。
- 解决：两层身份。L1 `AppId`（`aumid:` / `exe:`）决定归组，L2 `(L1, 类名, 位次)` 决定「是不是同一个窗口」。位次 = 同 (L1, 类名) 的窗口按保存位置排序的序号，跨重启可复现；`EnumWindows` 顺序不行，它不稳定。
- **实现期踩到的坑**：排序必须先按**显示器**分组再比坐标。保存侧读 `WINDOWPLACEMENT.rcNormalPosition`（工作区坐标），还原侧读 `GetWindowRect`（屏幕坐标）；跨屏直接比 Top / Left 会把同一个应用在两块屏上的窗口排出相反次序，位次全错。已补单测。
- 兼容：旧快照没有 `AppId` / 类名 / 位次 → 自动跳过「同位次」这一级，退回标题 + 矩形，行为与改造前一致。**不做一次性补算**：旧数据里没有这些信息，猜错就是把窗口摆到别处。
- 去重与「移除过的窗口不再写回」也换成身份键，但保留旧键做双键匹配：旧快照的 `Suppressed` 里存的是旧键，不能因为换了键就失效。
- `AppId()` 支持 `aumid:` 前缀，但读取端暂时传 null：绝大多数桌面应用没有显式 AUMID，且候选本来就按 exe 过滤，填了不改变结果——等真有消费场景再接。

### 问题：窗口关掉了，胶囊上的数字要等 2.5 s；「已关闭 N 个」也是假的（1.10.0）

**TL;DR**：状态变化只有一个 2.5 s 轮询在驱动，而「关闭窗口」报的是**投递出去**的 `WM_CLOSE` 个数——目标程序弹一个保存框，一个都没关，提示照样说「已关闭 3 个窗口」。两件事要的是同一批信号，合在一个钩子里做。

- 问题一：场景收起中，从任务管理器结束一个成员进程，胶囊计数最多要等 2.5 s 才变。
- 问题二：点「关闭窗口」时，程序弹「是否保存」，提示已经报出「已关闭 3 个窗口」——数字来自 `PostMessage` 的返回值，不是实际结果。
- 根因：唯一的信号源是 `DispatcherTimer` 轮询；关闭路径把「投递成功」当成了「已关闭」。
- 解决一：按被跟踪进程订阅 `EVENT_OBJECT_DESTROY / SHOW / HIDE`。**一个进程一个钩子**，因为 `SetWinEventHook` 的 `idProcess` 是注册期过滤——装成一个全局钩子等于把所有菜单 / 提示框的显示隐藏都收进回调。事件合并 150 ms 再对账（一次最小化会连着发好几条）。
- 解决二：`Close` 返回投出去的句柄，`CloseReport.Measure` 在 250 ms 后复核；还有活着的再等 800 ms，按实际数字报「已关闭 2 个，1 个仍在等待响应」。
- **不能一刀切全事件化**：图标重贴没有对应事件——应用换 favicon 走 `WM_SETICON`，它不发 WinEvent，SHELL 层的 `EVENT_OBJECT_NAMECHANGE` 也不是每个程序都发。所以 `Prune()` 只留状态对账，`ReapplyIcons()` 交给 5 s 定时器。把没有事件的活儿硬塞进事件钩子，结果是「钩子装了、图标还是不刷新」。
- 两个实测出来的细节：① 事件是投递到**安装线程的消息队列**的，必须在有消息循环的线程上装（UI 线程），测试进程里装不了，所以过滤规则抽成了纯函数 `Classify`；② 一个进程会有一堆自己的辅助窗口（WPF `HwndWrapper`、浏览器标签页），关掉一个进程能收到十几条 `DESTROY`，必须按「这个句柄在不在记录里」先筛一遍，否则每关一个程序都在白白触发对账。
- 实测延迟：`SW_HIDE` → Hide 5 ms、`SW_SHOWNA` → Show 5 ms、`WM_CLOSE` → 销毁 16–24 ms；**窗口已被收起（`SW_HIDE`）再从任务管理器强杀 → 5 ms 收到 `DESTROY`**（这才是真实场景：收起中的成员没有可见性变化，只测可见窗口等于没测）。加 150 ms 合并窗口后仍远小于旧的 2.5 s。

### 问题：补 AutomationId 补了个寂寞——Border 在 UIA 里是透明的（1.9.1）

**TL;DR**：计划里写的是「给关键控件补 `AutomationProperties.AutomationId`」，但本项目的按钮是带 `MouseLeftButtonUp` 的 `Border`。`Border` 没有 automation peer，UIA 树里根本不存在，挂上去的 ID 谁也读不到。先把控件换成真正的 `Button`，ID 才有意义。

- 问题：按计划给面板 / 详情窗补 ID，用 UIA 检查器看——一个都找不到。
- 定位方法（可复用，夹具本身不入库，按这段描述十几行就能重建）：写一个离屏夹具，同进程内用 UIA 客户端查自己（`AutomationElement.FromHandle` + `TreeWalker.ControlViewWalker` / `RawViewWalker` 遍历，`FindFirst(PropertyCondition(AutomationIdProperty, id))` 查找）。实测结果：`Border` + `AutomationId` + `Name` 在 **ControlView 和 RawView 里都找不到**（只有一个 `ControlType.Text` 子元素冒出文字）；`Button` 完整暴露 `ControlType.Button` + id + name，`TryGetCurrentPattern(InvokePattern.Pattern)` 为真，`Focusable=false` 也能 `Invoke` 且 Click 真的到达。
- 解决：`Ui.ActionButton` 改成返回 `Button` + 自定义 `ControlTemplate`（圆角底板 + `ContentPresenter`，`Padding` 走 `TemplateBinding` 到内容 `Margin`）。外观逐项对齐原 `Border`：底色 / `CornerRadius 8` / `Padding 11×5` / 悬停变色（触发放在 `Style` 里，局部 `Background` 仍是更高优先级，所以说明窗那个透明 `✕` 的自定义悬停照旧生效）/ 禁用 `Opacity 0.45` + 箭头光标。卡片行与危险按钮各自成一套样式（`CardButton` / `DangerButton`）。
- 嵌套按钮是安全的：卡片行现在是 `Button`，里面还套着「详情」。内层 `ButtonBase` 在 `MouseLeftButtonDown` 就把事件标记 handled 并捕获鼠标，外层收不到、`IsPressed` 不成立，点「详情」不会顺带触发整行开合。
- 视觉怎么验：`RenderTargetBitmap` 把三个窗导出 PNG，和「改造前的 `Border` 版」并排看。这比"应该差不多"可靠——按钮底色 / 圆角 / 内边距这种差异肉眼在小图上很难发现。
- 预防：UI 里凡是「可点的东西」，一律走 `Ui.ActionButton` / `Ui.CardButton` / `Ui.DangerButton`，不要手搓 `Border` + `MouseLeftButtonUp`。手搓的代价不是样式不一致，而是**这个控件对自动化和读屏永久隐身**。

### 问题：浏览器插件弹窗 / 软件对话框被当成窗口收进场景（1.9.0）

**TL;DR**：候选窗口枚举缺了「被拥有的窗口」这一条判据，弹窗成了场景成员，占条目、污染配对、关场景时连坐主窗口。修法是「窗口簇」：只收无 owner 的簇主，owned 弹窗跟着隐藏但不进场景。

- 问题：浏览器里点开扩展弹窗后收纳，弹窗变成场景里的一个成员；之后关闭场景，主窗口被一起关掉。
- 根因：`WindowEnumerator` 只挡了工具窗、cloaked、无标题、shell 类，漏了 owner。对话框 / 弹出层的标志就是有 owner——Chromium 的扩展弹窗源码里就是用 `GetWindow(child, GW_OWNER) == owner` 判断归属的。
- 为什么不能照抄 Alt+Tab 判据：微软给的 Alt+Tab 算法会在有可见对话框时把**对话框**当成这个簇的代表、把主窗口排除。对切换器是对的，对收纳正好相反。
- 解决：准入只看 `GetWindow(hwnd, GW_OWNER) == 0`（`WS_EX_APPWINDOW` 豁免，**不是** `GA_ROOTOWNER`——见 1.13.4）；配对候选、追加列表同样排除 owned；快照落盘前再判一次。**必须连带处理隐藏**：`SW_HIDE` 主窗口不会联动 owned 窗口（系统只在最小化 / 销毁时联动），所以收纳时把同簇可见弹窗一起藏（`StashedWindow.OwnedHidden`），展开 / 删除场景 / 退出时放回，崩溃恢复时也补放一次。关闭只对簇主发 `WM_CLOSE`，并跳过「根 owner 也在本组」的句柄。
- 预防：凡是「这个窗口算不算用户的窗口」的判断，只有一个出处（`WindowEnumerator.ExclusionReason`），枚举和 `--probe` 共用；新增一条规则时同步给 `--probe` 加一类原因，误杀才能一眼定位。
- 一个反直觉的点：`WS_EX_APPWINDOW` 的语义是「当作没有 owner」。WinForms 默认窗体会带上这个样式，所以「有 owner 但要上任务栏」的窗口本来就该算窗口——豁免不是补丁，是系统语义。

### 问题：单窗口模式打开场景后，其它窗口不最小化、场景还在 z 序最下面（1.8.1）

**TL;DR**：恢复、置前、单窗口过滤三件事各写了一半，缺一个统一收尾点；而置前依赖的 `ForegroundBefore` 对「从快照拉起的组」恒为 0。

- 问题：单窗口模式下点胶囊打开一个已关闭 / 已收起的场景，场景窗口出来了，但屏幕上其它窗口纹丝不动，而且场景窗口停在最下面（看着像没打开）。
- 根因一：`ToggleScene` / `RestoreScene` / `ExpandLatest` 开头都会 `SingleWindowMode.SuspendTemporarily()`（800 ms），批量还原期间的前台事件全被压掉；而单窗口模式的过滤**只**由前台事件驱动，抑制期过后系统不会再给事件 → 「其它窗口最小化」永远不发生。
- 根因二：`ExpandGroup` 只在 `IsEligibleActivationTarget(group.ForegroundBefore)` 成立时激活窗口。`ForegroundBefore` 是 `init`，只有 `StashAll` 建组时才写；`Adopt`（从快照拉起窗口时建组）不写，恒为 `IntPtr.Zero` → 这类场景**永远**不置前，再收起再展开也一样。
- 根因三：最小化用的是 `SW_MINIMIZE`，它「会激活 Z 序里的下一个顶层窗口」，一轮下来把焦点从场景窗口抢走。
- 解决：新增 `AppShell.FinishSceneOpen(groupId)`（置前 + `SingleWindowMode.ApplyNow`）作为唯一收尾点，三处调用 + 启动出来的窗口在接管时补一次；置前目标改由纯规则 `SceneFocusRules.Pick` 决定（优先 `ForegroundBefore`，否则第一个刚还原的窗口）；最小化改 `SW_SHOWMINNOACTIVE`。
- 预防：**「状态恢复」和「恢复之后该发生什么」要放在同一个收尾函数里**，否则抑制窗口（`SuspendTemporarily`）会把两者一起吃掉。凡是「只在事件里做的事」，被抑制之后必须有显式的补跑入口——`ApplyNow` 就是那个入口。
- 附带修掉的状态机问题：往「收起中」的场景追加窗口会让整组翻成「已展开」（新增 `StashSingle` 保持收起）；快照写盘失败仍建场景（违反坑 4，改为 `SaveFrom` 返回 null 并放回窗口）。

### 问题：胶囊「被任务栏压住」长达一个轮询周期（1.7.1）

**TL;DR**：任务栏和胶囊在同一个 topmost 波段里抢 z 序，旧实现只在每 2 s 的一轮体检里发现被压住，期间胶囊看得见但点不到；改成事件驱动后最坏 ≤250 ms。

- 问题：用户点任务栏 / 切窗口之后，胶囊有时会「灰一下」甚至点不动，点击被任务栏吃掉，可能误触相邻的任务栏按钮。
- 根因：不是判定错，是节奏错。`WindowFromPoint` 命中测试能正确识别「被任务栏或其子窗口压住」，但入口只有 `DispatcherTimer` 的 2 s 心跳，而任务栏抬自己发生在点击 / 激活 / 改几何这些瞬间。
- 为什么不换更硬的方案：DeskBand 在 Win11 已被移除；`SetWindowBand` 对普通进程永远返回 0x5，`CreateWindowInBand` 只允许 `ZBID_DEFAULT` / `ZBID_DESKTOP`，`ZBID_UIACCESS` 需要 UIAccess token（要签名证书 + 受信安装路径）；把胶囊 `SetParent` 到 `Shell_TrayWnd` 不受支持，Explorer 重建任务栏就丢。浮窗抢 z 序是唯一可行路径，只能把竞速赢下来。
- 解决：`EVENT_SYSTEM_FOREGROUND` 事件立即体检；按 Explorer 进程订阅 `EVENT_OBJECT_LOCATIONCHANGE` 覆盖几何变化；事件后 120 ms 自适应守卫 16 轮；复位用 `SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE` 只抬 z 序，把「抬起来」和「重新布局」彻底分开。
- 预防：任何「与系统 UI 抢层级」的窗口，判断标准应是「输入命中」（`WindowFromPoint`）而不是「矩形坐标」；而驱动必须是**事件**，轮询只能当兜底——轮询周期就是用户能感知到的故障时长。
- 刻意不做：被开始菜单 / 通知中心盖住时不抢，那属于正常遮挡；硬抢会让胶囊浮在浮层上面。

### 问题：胶囊「看得见但点不到」，持续 0–30 s

**TL;DR**：胶囊与任务栏共用 topmost 波段，且几何上落在任务栏矩形内；点一次任务栏，Explorer 就被抬到胶囊之上，输入全落到 `MSTaskListWClass`。

- 问题：失效期间截图逐像素一致（任务栏半透明，胶囊从 DWM 合成里透出来），用户会以为程序卡死，甚至误点到别的任务栏按钮。
- 根因：`SetWindowPos(HWND_TOPMOST)` 只保证在 topmost 波段内，任务栏被点击后也在同一波段里更靠前；原实现只在矩形变化时才重贴。
- 解决：每个 2 s tick 先做 z 序体检——取胶囊中心点 `WindowFromPoint`，命中不是自己且根窗口是 `Shell_TrayWnd` → 判定被盖，强制完整重贴；未被盖不重绘，避免闪烁。
- 预防：任何「贴任务栏 / 贴屏幕边缘」的自绘窗口，都要按「输入命中」而不是「矩形坐标」判断自己是否可点。
- 效果：自愈耗时从 10.1 / 20.0 / 27.2 s 降到 700 / 1500 / 1600 ms。

> 1.7.1 起驱动方式改为事件 + 自适应守卫（见上一节），这段「2 s tick 体检」只作为兜底保留；上表里的 700–1600 ms 就是轮询周期的代价。

### 问题：保存场景后，原编组胶囊消失，切组变成「关闭再打开」（历史记录，1.1.1 已修）

**TL;DR**：曾把场景作为第二套模型并行展示，出现了两枚语义不同的胶囊；同一个组的操作路径被拆成「运行时切换」和「场景关闭 / 启动」。1.5.0 起进一步收敛：收纳即建场景，运行时组只作为场景的运行态存在。

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

### 问题：编组胶囊展开后，再点没反应（历史记录，1.2.1 已修；GroupChip 已在 1.5.0 删除）

**TL;DR**：规则化时把「再点收起」丢了，默认流写成「未展开才展开」，展开状态落到空分支。

- 问题：README 承诺「再点 → 收起」，实际点击没有任何反馈，看起来像程序卡住。
- 根因：GroupChip 默认树用 `cond.chipOnScreen` 包住 `act.expandGroup`，`Then` 留空；改造前的手感没有被完整翻译成规则。
- 解决：默认流改成单张 `act.toggleGroup`；同时给 `cond.hasUntracked` / `cond.hasExpandedGroup` 补 `OnlyTriggers = Hotkey`，避免加进胶囊流后永远为 false。
- 预防：默认树是产品手感的一部分，改规则要对着 README 逐条核对；新增条件卡先确认「这个事实在哪几个入口算过」。

## 四、数据文件与调试

- `%LOCALAPPDATA%\TaskbarDrawer\settings.json`、`saved-groups.json`、`recovery.json`、`logs\`
- `--probe`：确认候选窗口集合（判断「为什么这个窗口没被收进去」）
- `dotnet test tests/TaskbarDrawer.Tests`：规则引擎回归测试（默认手感 / 步数与深度护栏 / 目录约束）
- 面板「场景」区块：点整行收起 / 展开；「关闭窗口」真关；「详情」在面板旁打开侧窗，移除 / 追加立即同步运行时组
- 发布：`dotnet publish ... -p:PublishSingleFile=true`，刻意保持框架依赖以把 exe 压到约 500 KB（含 SVG 重绘的矢量说明图）；完整步骤见 [交接](docs/handoff.md)

## 五、文档维护

| 场景 | 动作 |
| --- | --- |
| 改功能 | 更新本文件对应小节 |
| 踩坑并解决 | 追加「问题：…」一节（TL;DR / 问题 / 根因 / 解决 / 预防） |
| 发版 | `CHANGELOG.md` 加版本节 + 版本号 bump + 更新 `AGENTS.md` 基线行 |
| 约定变化 | 更新 `AGENTS.md` 或按需拆到 `rules/` |
