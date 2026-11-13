# AGENTS.md · 项目规则

> 📌 **文档基线**：2026-11-13 · v1.3.0（commit e7db5b3）移除逻辑编排编辑器，入口规则固定为代码默认
> **更新文档 / 代码后，请更新此行**（日期 + 新 commit hash），并在 CHANGELOG 追加版本。

只写代码里看不出来的信息。

## 技术栈

- .NET 8（`net8.0-windows`）/ C# 12 / x64 / WPF 纯代码 UI（无 XAML）
- WinForms 仅用于托盘 `NotifyIcon`；csproj 移除了 `System.Windows.Forms`、`System.Drawing` 的隐式 using，避免与 WPF 类型撞名（文件里用 `using Forms = System.Windows.Forms;`）
- 单文件、框架依赖发布（`--self-contained false`），依赖 .NET 8 Desktop Runtime
- `app.manifest`：`asInvoker` + PerMonitorV2

## 关键坑

1. **Win11 塞不进任务栏**：胶囊是贴 `Shell_TrayWnd` 的 topmost 小窗，每 2 s 重贴一次。任务栏会把它抬到上面（看得见但点不到），靠 `WindowFromPoint` 体检判定被盖后强制复位。
2. **关闭窗口只按句柄**：`PostMessageW(WM_CLOSE)` 只发给该组自己记录的 hwnd。曾按 exe 路径关同类窗口，把同一程序里不在组内的下载窗口一起关了（弹「这将取消所有传输」）。
3. **「收起」是 `SW_HIDE` 不是最小化**：要同时记录 `WINDOWPLACEMENT`（换算成屏幕坐标），还原后尺寸偏差 > 2 px 用 `SetWindowPos` 校正。
4. **运行时组与场景是两套数据**：运行时组 = 句柄 + 分组（`StashAllService`），场景 = `saved-groups.json` 快照。两者只能靠 `SavedId` 关联；保存不得产生第二枚胶囊，否则点胶囊会退化成「关闭再重开」。
5. **外围状态必须兜底**：热键注册失败（1409 被占用）要回退默认组合并提示；`recovery.json` 记录当前隐藏的窗口，退出走 `ReleaseAll`、未处理异常走 `RegisterRescue`，否则被隐藏的窗口永远留在桌面外。

## 约定

- UI 文案、注释、提交说明都用中文
- 面向用户的名词只有两个：**编组**（运行中的一组窗口）、**场景**（保存了软件 + 位置的一组）
- 版本号在 `TaskbarDrawer.csproj` 手动 bump，`InformationalVersion` 自动拼构建时间；托盘菜单与启动 toast 都要显示它
- 所有用户入口（托盘 / 面板 / 胶囊 / 热键）都过 `AppShell.Safe()` 做局部异常隔离；只有退出路径不包
- 新增文件按 `Core`（不引用 WPF 控件）/ `UI`（WPF）/ `Interop`（P/Invoke）分层，命名空间与目录一致
- 界面颜色只从 `UI/Theme` 取，通用控件工厂用 `UI/Ui`；窗口里不再各自定义画刷或 `Frozen`
- 跨层事件订阅一律在订阅处包 `Safe(scope, ...)`：`Changed` 是同步广播，UI 异常不能冒进业务调用栈
- 主面板隐藏时不建 UI：`SetGroups` / `SetSavedGroups` 只记数据，`ShowPanel` 里 `FlushSurface()` 一次补齐
- 胶囊左键语义固定写在默认规则里（编组 = 展开 / 收起切换，场景 = 收起 / 展开）；右键菜单见 README

## 入口规则（固定，界面不可编辑）

- 入口固定三个：`Hotkey` / `GroupChip` / `SceneChip`，代码写死、界面不可增删。托盘菜单与主面板按钮仍是直接命令，不走规则——给它们套一层只有坏处
- 规则是**树**不是图：`RuleStep` 用 `Then` / `Else` 表达分支，顺序就是 `List<RuleStep>`。所以不可能成环，也不需要环检测
- 默认手感：热键三段式（收纳 → 全收起 → 展开最近一组）；编组胶囊 = 点一下展开、再点收回；场景胶囊 = 运行中切换、已关闭则还原。想改手感就改 `RuleSet.CreateDefault()`
- 条件的 `OnlyTriggers` 必须和「事实在哪里算」一致：`hasUntracked` / `hasExpandedGroup` 只在热键入口算过，放进胶囊流不会报错，但条件永远为 false
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

## 详细规则

- 暂无拆分；超过 150 词再按 knowledge-base 单项目规范拆到 `rules/`，用 @引用 加载
