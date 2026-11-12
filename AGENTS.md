# AGENTS.md · 项目规则

> 📌 **文档基线**：2026-11-12（commit `615673c`）完成四件套重写
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
- 新增文件按 `Core`（无 UI 依赖）/ `UI`（WPF）/ `Interop`（P/Invoke）分层，命名空间与目录一致
- 胶囊左键语义：编组 = 展开，场景 = 收起 / 展开；右键菜单见 README

## 常用命令

```powershell
dotnet build src/TaskbarDrawer/TaskbarDrawer.csproj -c Release

dotnet publish src/TaskbarDrawer/TaskbarDrawer.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true -o publish

.\publish\TaskbarDrawer.exe --probe
```

## 详细规则

- 暂无拆分；超过 150 词再按 knowledge-base 单项目规范拆到 `rules/`，用 @引用 加载
