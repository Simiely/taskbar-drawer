# AGENTS.md · 项目规则

> 📌 **文档基线**：2026-11-25 · `2b4ece8`（1.15.5：辅助窗口宿主保留）　← 前序：abaa907
> **更新文档 / 代码后，请更新此行**（日期 + 新 commit hash），并在 CHANGELOG 追加版本。

> 生成文件，勿手改。改规则请改 `schema/agents.template.md`，再运行 `python tools/repo-kit/generate.py --root . --write`。

只写代码里看不出来的信息。

## 技术栈

- .NET 8（`net8.0-windows`）/ C# 12 / x64 / WPF 纯代码 UI（无 XAML）
- WinForms 仅用于托盘 `NotifyIcon`；csproj 移除了 `System.Windows.Forms`、`System.Drawing` 的隐式 using，避免与 WPF 类型撞名（文件里用 `using Forms = System.Windows.Forms;`）
- 单文件、框架依赖发布（`--self-contained false`），依赖 .NET 8 Desktop Runtime
- `app.manifest`：`asInvoker` + PerMonitorV2

## 关键坑

1. **Win11 塞不进任务栏**：胶囊是贴 `Shell_TrayWnd` 的 topmost 小窗，与任务栏同属一个 topmost 波段——谁最后被抬起谁在上面。任务栏在「点它 / 切窗口 / 改几何」时会抬自己，所以胶囊会短暂被压住（看得见但点不到）。判定靠 `WindowFromPoint` 命中测试；**复位要走两条路**：只是被压住时用 `SetWindowPos(HWND_TOPMOST, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE)` 纯抬 z 序，几何真变了才做全量重排（1.7.1）。**驱动必须是事件**（`EVENT_SYSTEM_FOREGROUND` + 按 Explorer 进程订阅的 `EVENT_OBJECT_LOCATIONCHANGE`）+ 120 ms 自适应守卫（16 轮后停），只留 2 s 全量体检兜底；纯轮询会让遮挡窗口长达一个周期，且点击事件被任务栏吃掉。
2. **关闭窗口只按句柄**：`PostMessageW(WM_CLOSE)` 只发给该组自己记录的 hwnd。曾按 exe 路径关同类窗口，把同一程序里不在组内的下载窗口一起关了（弹「这将取消所有传输」）。
3. **「收起」是 `SW_HIDE` 不是最小化**：要同时记录 `WINDOWPLACEMENT`（换算成屏幕坐标），还原后尺寸偏差 > 2 px 用 `SetWindowPos` 校正。
4. **运行时组只是场景的运行态**：`StashAllService` 持句柄，`saved-groups.json` 存快照，二者靠 `SavedId` 一一对应。收纳 = `StashAll` + `SaveFrom` + `LinkSaved`，正常路径下不允许出现没有 `SavedId` 的组；保存失败要 `Release` 回桌面，不能留孤儿组。
5. **外围状态必须兜底**：热键注册失败（1409 被占用）要回退默认组合并提示；`recovery.json` 记录当前隐藏的窗口，退出走 `ReleaseAll`、未处理异常走 `RegisterRescue`，否则被隐藏的窗口永远留在桌面外。**提权重启**必须给新实例传 `--elevated-restart`，由 `Program.AcquireSingleInstance` 等旧实例释放单实例互斥体（最多 10 s）后再接管；否则新实例会把自己当成第二个实例，唤起旧实例后直接退出，表现为「重启失败、一个都不剩」（1.6.4 修复）。
6. **多显示器按屏隔离（屏幕未知时 fail-closed）**：`StashAll` / `ExpandNewest` / `ExpandAll` / `CollapseExpandedScenes` / `HasUntrackedVisibleWindows` 的显示器参数**必填**；屏幕判断失败时入口直接提示并返回，绝不能把「不知道哪块屏」当成「所有屏」（旧写法 `monitorDevice == null || …` 是 bug）。归屏比较统一走 `DisplayRules.BelongsTo`；场景归属屏用收纳时冻结的 `StashGroup.Screen`（见本条末尾 1.15.1），**不能**按当前可见成员的位置实测——那会随拖动漂移。前台是本程序 / shell 时用鼠标位置（`MonitorDeviceOfCursor`）判断当前屏。`SavedWindow.MonitorDevice` 用来把窗口摆回保存的那块屏；还原**不能**因为「同程序窗口在另一块屏」就跳过（程序重启后系统常把窗口开在主屏，跳过会表现为位置没恢复），匹配顺序见规则 8。**收起场景（`StashGroup`）也要带屏**：场景可能有成员被还原到 / 拖到另一块屏，必须按屏收紧，跨屏成员留在原屏——否则单窗口模式或「收起其它场景」会让另一块屏的窗口整组消失（1.6.2 修复）。**单窗口模式的开关入口按鼠标 / 面板所在屏选目标**（不能优先前台窗口：托盘菜单不改变前台，拿到的常是另一块屏的窗口），开关生效后再 `SuspendTemporarily` 压掉菜单收起时系统还前台的那次事件，否则点一块屏会把两块各清一遍（1.6.3 修复）。**单窗口模式按「每块屏幕完全独立」处理（1.15.1）**：只枚举前台所在屏幕的窗口，另一块屏在做什么都不受影响；不能因为副屏上的切换去收主屏的场景（15:37:30 日志：在 `DISPLAY1` 点资源管理器，把 `DISPLAY2` 的整组场景收走了 5 个）。**场景有固定归属屏**（`StashGroup.Screen` / `SavedGroup.MonitorDevice`，收纳 / 创建时确定）：成员被拖到别的屏幕也不改归属，只有解散后在新屏重新收纳才换屏；`MonitorOfGroup` / `MonitorOfSaved` 必须优先返回它，不能按可见成员实测（拖动成员会让归属漂移）。前台属于某个场景、但这块屏不是它的归属屏时，它在本屏只是一个普通窗口，不展开 / 收起它的同伴；别的屏的场景成员（哪怕被拖过来）也不参与本屏判定。**场景是整体**：收起时 `StashGroup` 不传屏幕，连拖到别的屏的成员一起收；胶囊 `Expanded` 按「任意屏幕有可见成员」判断。**跨屏拖动不会发 FOREGROUND（1.15.3）**：拖动前后前台窗口是同一个，系统只在「前台换成别的窗口」时发事件；必须额外监听 `EVENT_SYSTEM_MOVESIZEEND`，拖动结束后按 `GetForegroundWindow()` 补一次判定，否则窗口拖到另一块屏后那块屏的场景不收起，用户要最小化再最大化才触发。
7. **窗口落点必须兜底**：还原 / 摆位统一过 `DisplayRules.EnsureVisible`（`MonitorFromRect(MONITOR_DEFAULTTONEAREST)` 的工作区，交集 < 30% 就夹回），拔掉副屏或换分辨率后窗口不会停在桌面外。`WINDOWPLACEMENT.rcNormalPosition` 的「工作区坐标」只扣本屏任务栏偏移（`rcWork - rcMonitor`），**不是**绝对工作区原点——按绝对原点换算会给副屏矩形多加一整块屏宽，展开 / 恢复时窗口贴边或被夹到错误位置（1.6.5 修复）。旧场景由 `DisplayRules.TryRepairLegacyRect` 在启动时自动换算回来（主屏原点是 0，旧数据本来就对，不能误改）。纯几何规则放在 `Core/DisplayRules.cs`，不碰句柄，由 `DisplayRulesTests` 覆盖。
8. **恢复按「同程序里最像的那个窗口」配对（1.6.5）**：`DisplayRules.PickRestoreCandidate` 的顺序固定为「同屏 + 同标题 → 同标题（跨屏也要挪回保存屏）→ 同屏最近矩形 → 任意屏最近矩形」。旧实现按枚举顺序取第一个，资源管理器 / 浏览器同程序多窗口会互相换位。只有被其它场景接管的窗口（`excluded`）不能挪、也不能重复启动；已运行但开错屏的窗口会被摆回保存位置。`PositionLater` 等新窗口时同样按标题优先配对，系统把它开在主屏也能接管。
9. **诊断日志**：`Diagnostics` 默认写程序目录 `logs\app-yyyyMMdd.log`（按天分文件，启动与跨天时删除超过 1 天的旧日志），程序目录不可写时退回 `%LOCALAPPDATA%\TaskbarDrawer\logs`。启动记录版本 / 显示器布局 / 提权状态；场景保存与还原（每个窗口的标题 / 屏 / 矩形 / 匹配结果 / 摆位前后实际矩形）、收纳、展开、单窗口决策、异常都要留一行。写日志失败必须静默，不能反过来影响主流程；改跨屏 / 摆位逻辑时先想「只看日志能不能判断是谁把窗口放错了」。
10. **场景「回到屏幕」是一个必须收尾的动作（1.8.1）**：恢复 / 展开期间 `SingleWindowMode.SuspendTemporarily()` 会压掉所有前台事件，而系统之后**不会**再补一次——「避免误最小化同伴」和「该最小化其它窗口」被同一段抑制一起取消。所以任何新增的批量恢复入口，收尾都必须调 `AppShell.FinishSceneOpen(groupId)`（置前 + `SingleWindowMode.ApplyNow`）。置前目标不能只看 `StashGroup.ForegroundBefore`：`Adopt` 现场建的组该值为 0，会表现为「打开的场景永远在最下面」，必须走 `SceneFocusRules.Pick` 退回第一个还原窗口。最小化别的窗口要用 `SW_SHOWMINNOACTIVE`，`SW_MINIMIZE` 会激活 Z 序下一个窗口、把焦点抢走。
11. **弹窗不是窗口：按「窗口簇」处理（1.9.0）**：对话框、弹出层、浏览器扩展弹窗都有 owner（`GetWindow(hwnd, GW_OWNER) != 0`，Chromium 扩展弹窗就是用 owner 挂到浏览器窗口上的），它们不能成为场景成员，否则会占条目、污染配对、关场景时连坐主窗口。判定只在 `WindowEnumerator.ExclusionReason()` 一处，注意两条容易搞反的语义：`WS_EX_APPWINDOW` 表示「当作没有 owner」（要有豁免），`WS_EX_TOOLWINDOW` 表示「当作不可见」。**准入只看 `GA_ROOTOWNER`，绝不能用 Alt+Tab 那套 `GetLastActivePopup` 循环**——有对话框时它会把对话框当簇代表、把主窗口排除，与收纳语义相反。另外 `SW_HIDE` 主窗口**不会**连带隐藏 owned 弹窗（系统只在最小化 / 销毁时联动），所以收纳必须显式把同簇弹窗一起藏（记在 `StashedWindow.OwnedHidden`，不进快照），否则会留下孤儿对话框。
12. **UI 里可点的东西必须是真 `Button`（1.9.1）**：`Border` 没有 automation peer，在 UIA 树里**不存在**——挂 `AutomationId` / `Name` 都读不到（ControlView、RawView 实测都查不到），这个控件对自动化与读屏永久隐身。所以只用 `Ui.ActionButton` / `Ui.CardButton` / `Ui.DangerButton`，不要手搓 `Border` + `MouseLeftButtonUp`。补 ID 用 `Ui.Id(element, "panel.newScene")`，命名按「窗口.控件」用点分小写（`panel.scene.{id}.close`、`editor.remove.{index}`、`editor.status.{index}`）；内容不是纯文本的卡片行要额外给 `Name`，否则 UIA 里是一串空白。验证方式：离屏夹具里同进程查自己（`AutomationElement.FromHandle` + `TreeWalker`），别靠肉眼看检查器截图。
**嵌套按钮必须按「点击源」判定，不能靠 `e.Handled`**（1.13.6）：`ButtonBase` 的类处理器是 `handledEventsToo=true`，内层按钮标 Handled 后外层 Button 照样触发 Click——点场景卡片的「详情」会把整行 toggle 也触发，场景收起/展开交替。用 `PreviewMouseLeftButtonDown` 记录是否按在某个 `ButtonBase` 内（`Ui.PressedInsideButton`），外层 `Click` 据此跳过。这是 1.9.1 把 Border 换成 Button 时踩出来的。**同一场景再次点详情要收起详情窗**（1.13.8）：判定走 `SceneEdit.ShouldCloseEditor(openSavedId, clickedSavedId)`，只有同一个 `savedId` 才关；点另一个场景仍是切换内容 + 置前。别把「详情窗开关」和「外层卡片 toggle」混成一套逻辑。
13. **状态要事件驱动，但「本来就没有事件的活儿」不能硬塞进去（1.10.0）**：窗口销毁 / 显示 / 隐藏有 `EVENT_OBJECT_DESTROY / SHOW / HIDE`，装钩子要按**被跟踪进程**逐个装（`SetWinEventHook` 的 `idProcess` 是注册期过滤，装全局钩子等于把所有菜单 / 提示框的显示隐藏都收进回调）。事件必须在**有消息循环的线程**（UI 线程）上装，回调里只做「句柄在不在记录里 → 重启 150 ms 合并定时器」，真正的对账放到定时器里做。**但图标重贴没有事件**——应用换 favicon 走 `WM_SETICON`，不发任何 WinEvent，所以 `Prune()`（状态对账）与 `ReapplyIcons()`（图标重贴）是分开的，后者 5 s 定时跑，别为了「全事件化」把没事件的活儿塞进钩子。另外「关闭窗口」的 `WM_CLOSE` 是投递，返回成功不等于关了：用 `CloseReport.Measure` 过 250 ms 复核（还有活着的再等 800 ms），按实际数字报，别报投递数。
14. **窗口身份不能用标题（1.11.0）**：`exe + 标题` 曾同时充当去重键与配对键，而标题是窗口上最易变的属性——浏览器切个标签页就变，配对随即退回「矩形最近」，两个窗口互换位置。现在用两层身份：L1 `WindowIdentity.AppId`（`aumid:` / `exe:`），L2 `(L1, 类名, 位次)`，位次 = 同 (L1, 类名) 的窗口按保存位置排序。**排序必须先按显示器分组再比坐标**：保存侧是 `rcNormalPosition`（工作区坐标）、还原侧是 `GetWindowRect`（屏幕坐标），跨屏直接比会排出相反次序。位次是相对量，只能基于「固定的候选集合」一次算好，不能一边配对一边去掉已用的。旧快照没有身份字段 → 跳过同位次这一级、退回标题 + 矩形，**不要写补算迁移**。**跨屏位次命中要跳过（1.14.2）**：位次按显示器名排序，保存项在副屏、主屏也有同程序窗口时，位次 0 会落在主屏；保存屏上有同应用同类名窗口时必须放弃位次级，交给标题 / 最近矩形（它们优先同屏），否则会把另一块屏的窗口挪过来。
15. **状态机不要直接调 Win32（1.12.0）**：`StashAllService` 的收起 / 展开 / 接入 / 摘除是纯状态迁移，但每一步都要碰窗口，导致「有没有把记录搞乱」只能靠实机点。现在统一走 `IWindowGateway`（`StashAllService` 与 `WindowStash` 共用同一个实例），单测用 `FakeWindowGateway` 在内存里跑迁移矩阵。**关键约束**：端口只收状态机真会调的动作（约 12 个），不要把 `NativeMethods` 每个函数都建接口；`SW_HIDE` / `SW_SHOWNA` / `SW_RESTORE` / `SW_SHOWMINNOACTIVE` 行为不同，别用一个 bool 表达。时间规则（`Prune` 的宽限期）要能注入时钟，否则测不了。抽完端口必须用**真实网关 + 真实窗口**再验一遍——假实现永远无法证明你没把隐藏 / 还原改坏。
16. **版本号只能有一个源（1.13.2）**：`csproj` 的 `<InformationalVersion>` 曾是手写常量，与 `<Version>` 各走一套，结果界面上的版本从 1.9.0 起就没更新过（二进制是新的，显示是旧的）。现在它由 `$(Version)` 派生，时间只到「月-日 时:分」，且关掉了 SDK 自动追加的 commit sha（`IncludeSourceRevisionInInformationalVersion=false`）——托盘一行要放得下。**改版本时只改 `<Version>`**，界面显示由 `VersionTests` 锁住（含长度上限），别再加第二个字面量。
17. **弹窗成为前台时，单窗口模式不能把它的场景收掉（1.13.4）**：点浏览器扩展弹窗、工具窗、对话框，系统是让**弹窗**成为前台（MS「Window Features」：用户点击即设置前台窗口；catch22 的停靠工具栏教程写明「工具窗显示为活动、owner 变非活动」），而弹窗不是场景成员，`groupOf(popup)=0`，于是「别的场景整组收回胶囊」会把弹窗所属的场景整个收走。两道兜底：① `SingleWindowMode.ResolveGroup` 按**簇主**归组（沿 owner 链）；② 簇主也对不上（Chromium 气泡由中间一层无 owner 的 border 窗口拥有）时，按 `WindowEnumerator.ExclusionReason()` 判断它连窗口都不算，直接忽略这次前台变化。**另一个坑**：`GetAncestor(hwnd, GA_ROOTOWNER)` 对 owned 的 `WS_POPUP` 实测返回窗口**自身**（`GetParent` 对非 `WS_CHILD` 返回 0，链是空的），所以簇主必须走 `NativeMethods.RootOwner()` 里手写的 `GW_OWNER` 循环；`GA_ROOTOWNER` 只能当资料引用，不能当实现依据。
18. **本程序自己弹窗时别让单窗口模式动作（1.13.5）**：钩子用了 `WINEVENT_SKIPOWNPROCESS`，所以收不到本程序窗口自己的 `EVENT_SYSTEM_FOREGROUND`；但打开面板 / 详情 / 说明窗时 Windows 可能把前台给**另一个进程**的窗口，那个事件会被收到，于是「切到场景外 → 整组收回胶囊」把用户的场景全收了。打开 / 关闭 / 切换本程序 UI 时统一调 `SingleWindowMode.SuspendTemporarily(ms, 原因)` 暂停判定。日志位置是**程序目录** `logs\app-yyyyMMdd.log`（不是 `%LOCALAPPDATA%`），`应用 前台=` 行带 pid/class/owner，排查时先看它。
19. **移除窗口要从运行时和快照两侧一起摘（1.13.7）**：`exe + 标题` 不能当移除键——浏览器切标签页 / 资源管理器改路径后标题就变了，同 exe 多窗口更难分。`StashAllService.Detach` 必须复用 `WindowIdentity.Candidate` → `AssignOrdinals` → `Pair`（与还原、详情窗「已打开」同一套规则），摘掉的是**配对结果对应的 hwnd**，不是标题相同的那个。更关键的是 `WorkspaceService.UpdateSnapshot` 关场景时**只刷新位置，不碰 `group.Windows` 成员集合**：曾因它整份重抓快照，把刚从详情窗移除、但因旧 `Suppressed` 键失配仍在运行时组里的窗口又写回磁盘，下次开场景就复活。旧快照没有身份字段时保持双键 / 矩形回退，不要写迁移猜身份。
20. **程序关掉重开是新的句柄，运行时组不会自己认回来（1.14.0）**：运行时组只记 hwnd，窗口销毁后 `Prune` 把记录移出，快照那一项还在。用户重新打开程序时新窗口不在组里，表现是「场景展开着，但它不跟着收 / 展，计数也不对」；**退出本程序重启后反而正常**（场景回到「已关闭」再点开，会重新走还原配对）——这个对比是判断方向的关键，别去改快照匹配规则。认领规则在 `Core/SceneReattach`：`Missing` 用「同标题 → 同程序最近位置」一对一配对，**不要用位次**（少一个成员后位次整体前移，会把还开着的认成被关掉的）；`Match` 同 exe + 类名、标题优先，只有唯一缺失项才允许标题变化。驱动是全局 `EVENT_OBJECT_SHOW`（150 ms 合并，钩子装不上退回 5 s 巡检），另外 `Restore` / `ExpandLatest` / `ExpandAll` 展开时还要兜底扫一次。**单窗口模式必须先用 `ClaimForeground` 认领再判定**，否则刚打开的程序会被当成场景外窗口，把整个场景收走。
21. **同应用的辅助窗口不是场景成员，也不能触发单窗口动作（1.14.1 / 1.14.4 / 1.15.4）**：微信图片查看器是 `Weixin.exe` 自己的 Qt 顶层窗（与成员同 pid），小程序宿主 `WeChatAppEx.exe` 是它拉起的**子进程**；两者 `owner=0`、不是工具窗，按现有准入规则进不了场景，却又会被单窗口模式当成「切到了别的应用」，于是打开一张图就把整个场景收回胶囊（日志 `收回场景=5`）。`Core/ProcessSceneLink` 用「同 pid → 父 pid → 祖父 pid，最多两层」判断它和哪个场景是同一个应用。**飞书的图片 / 视频查看器走的是兄弟进程（1.15.4）**：主进程 `Feishu.exe` 拉起多个同 exe 子进程，媒体中心（`--category=mediacenter`）与聊天窗口互为兄弟，共同父进程不在场景里，父链走不到成员；所以父链之后再按「同一个 exe」兜底，同一 exe 属于多个场景时仍不猜。`python.exe` / `node.exe` / `java.exe` / `dotnet.exe` / `electron.exe` 等运行环境宿主不参与同 exe 兜底（同名进程往往互不相干），shell / 启动器也不参与。**父进程链必须在 shell / 启动器处截断**（`explorer.exe`、`svchost`、`RuntimeBroker`、`cmd` / `powershell` 等）：从桌面 / 任务栏启动的程序父进程都是 `explorer.exe`，场景里又有资源管理器窗口时，记事本、写字板、浏览器都会被误判成同应用（1.14.5 日志 `跳过前台 … class=Notepad`），场景于是不收起、其它窗口也不最小化。**前台遇到这类窗口时直接跳过、什么都不做，绝不能再把返回值当 group 去跑最小化循环**——否则一个资源管理器窗口（所有文件夹窗口共用一个 `explorer.exe`）会被当成场景被激活，出现 `关联组=1 最小化=1`（1.14.4）。**辅助窗口打开时，宿主进程的窗口也不能被最小化（1.15.5）**：飞书图片 / 视频由媒体中心进程打开，父进程就是飞书主进程；候选最小化前用 `ProcessSceneLink.AncestorPids` 取前台进程的父 / 祖父 pid（遇到 shell 截断），祖先进程的窗口保留（日志 `宿主=N`，命中时打印 `保留宿主`）。**前台自己的进程就是 shell / 启动器时 `ProcessSceneLink` 必须返回 0（1.15.0）**：资源管理器窗口在归属 / 认领上仍是普通应用，不能整条「同应用 → 跳过」。它是否参与单窗口模式的计数由面板开关「资源管理器不计入」决定（1.15.4，**默认开启**）：开启时 `SingleWindowMode` 对不属于场景的资源管理器窗口既不最小化、也不因它收起场景 / 最小化别的窗口；属于场景的资源管理器仍按场景整体处理。判定在 `SingleWindowMode.ShouldSkipExplorerWindow`，不要把开关塞进 `ProcessSceneLink`。**不要 Adopt 进场景**（会污染快照 / 计数 / 收起展开）。同一进程属于多个场景时不猜；只有 `ExclusionReason == null` 的顶层应用窗口才做这条判断（1.14.2）。候选枚举里最小化窗口的位置要读 `WINDOWPLACEMENT.rcNormalPosition`，`GetWindowRect` 的 -32000 哨兵会让位次 / 最近矩形全部失真。

## 约定

- UI 文案、注释、提交说明都用中文
- 面向用户的名词只有两个：**场景**（一组窗口 + 保存的位置，运行态与快照共用一枚带彩色圆点的胶囊）、**单窗口模式**（每个场景算一个窗口）。不要再出现「编组 / 分组」
- 版本号在 `TaskbarDrawer.csproj` 手动 bump，`InformationalVersion` 自动拼构建时间；托盘菜单与启动 toast 都要显示它
- 所有用户入口（托盘 / 面板 / 胶囊 / 热键）都过 `AppShell.Safe()` 做局部异常隔离；只有退出路径不包
- 新增文件按 `Core`（不引用 WPF 控件）/ `UI`（WPF）/ `Interop`（P/Invoke）分层，命名空间与目录一致
- `AppShell` 是同一个 `partial class` 的三片，按「谁改什么」切：`AppShell.cs` 组合根与生命周期（**装配顺序即依赖**，见 DEVELOPMENT 2.1b）、`AppShell.Commands.cs` 命令实现（只走 `SceneService` / `StashAllService` / `WorkspaceService`，不碰控件）、`AppShell.Wiring.cs` 入口接线与窗口管理。新增一个入口 = Wiring 里接一条线 + Commands 里写命令，不要把命令写回组合根
- 界面颜色只从 `UI/Theme` 取，通用控件工厂用 `UI/Ui`；窗口里不再各自定义画刷或 `Frozen`
- 跨层事件订阅一律在订阅处包 `Safe(scope, ...)`：`Changed` 是同步广播，UI 异常不能冒进业务调用栈
- 主面板隐藏时不建 UI：`SetScenes` 只记数据，`ShowPanel` 里 `FlushSurface()` 一次补齐
- 胶囊左键语义固定写在默认规则里（运行中的场景 = 收起 / 展开切换，已关闭 = 还原）；右键菜单见 README

## 入口规则（固定，界面不可编辑）

- 入口固定两个：`Hotkey` / `SceneChip`，代码写死、界面不可增删。托盘菜单与主面板按钮仍是直接命令，不走规则——给它们套一层只有坏处
- 规则是**树**不是图：`RuleStep` 用 `Then` / `Else` 表达分支，顺序就是 `List<RuleStep>`。所以不可能成环，也不需要环检测
- 默认手感：热键三段式（新建场景 → 全收起 → 展开最近的场景）；场景胶囊 = 运行中切换、已关闭则还原。想改手感就改 `RuleSet.CreateDefault()`
- 条件的 `OnlyTriggers` 必须和「事实在哪里算」一致：`hasUntracked` / `hasExpandedScene` 只在热键入口算过，放进胶囊流不会报错，但条件永远为 false
- 条件在执行前一次性算成 `RuleFacts`，执行过程中不再反复问系统（一次点击只枚举一次窗口）
- 新增一种卡片动两处：`RuleCatalog.All` 加定义 + `AppShell.CreateRuleRunner` 绑处理函数
- 运行期护栏：`RuleRunner.MaxSteps = 64`、`MaxDepth = 8`、逐卡 try/catch——一张卡失败只丢这一步
- 没有配置文件：规则只存在于 `RuleSet.CreateDefault()`，改手感必须改代码重新编译。不要再引入 `rules.json`、配置迁移或清洗逻辑

## 常用命令

```powershell
dotnet build src/TaskbarDrawer/TaskbarDrawer.csproj -c Release

dotnet test tests/TaskbarDrawer.Tests/TaskbarDrawer.Tests.csproj -c Release

dotnet publish src/TaskbarDrawer/TaskbarDrawer.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true -o publish

.\publish\TaskbarDrawer.exe --probe
```

```bash
python tools/repo-kit/check_repo.py --root .
python tools/repo-kit/generate.py --root . --check
```

## 当前规模

- 页面：22（critical=0）
- 按 type：explanation=17, reference=5
- 词表版本：2.9.1

## 详细规则

- 进度、待办与发布路径在 [docs/handoff.md](docs/handoff.md)，开工前先读；规则与坑仍以本文件为准
- 再扩写超过 150 词时按 knowledge-base 单项目规范拆到 `docs/`，用 @引用 加载
