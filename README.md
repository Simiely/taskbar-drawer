# 任务栏收纳 · TaskbarDrawer

把一组正在用的窗口「收」进任务栏上的一枚小胶囊，点一下原样展开。每次收纳都会自动存成一个**场景**，关掉之后还能按保存的位置一键拉起。

> 程序里只有两个概念：💾 **场景** = 一组窗口 + 保存的位置；**单窗口模式** = 每个场景当成一个窗口来切换。

> Windows 11 不允许第三方程序往任务栏里塞控件，所以胶囊是一枚紧贴任务栏的无边框置顶小窗：视觉上贴着任务栏，行为上可点击。

## 功能

- **新建场景**：`Ctrl+Alt+Shift+D` 把当前显示器上可见的窗口收成一个新场景并自动保存；这块屏幕上没有可收的窗口时再按一次，展开这块屏幕上最近的场景
- **场景胶囊**：每个场景一枚胶囊贴在任务栏（横排 / 竖排都跟得住），左键收起 / 展开，右键：重命名 / 关闭窗口 / 删除场景；窗口关掉后胶囊作为「已关闭」留下，点一下按保存的位置重新打开
- **编辑场景**：主面板场景行点「编辑」，可以单独移除不需要的窗口，或把其它正在运行的场景追加进来；只改保存的快照，当前窗口不受影响
- **单窗口模式**：每个场景算一个窗口；切到场景里的任意窗口时同场景窗口一起保留，切到场景外时整个场景收回胶囊
- **多显示器**：收纳、展开、收起都只作用于当前操作的显示器，另一块屏的窗口和场景不受影响；单窗口模式也按屏独立生效
- **主面板**：双击托盘图标打开（左键单击切换单窗口模式），查看场景列表、切换开关、改热键；拖动后记住位置
- **细节**：开机自启、深色托盘菜单、单文件发布、崩溃日志

## 系统要求

- Windows 10 / 11 x64（开发与验证环境：Windows 11 24H2，横排与竖排任务栏）
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（框架依赖的单文件发布，不内置运行时）

## 快速开始

1. 下载 `TaskbarDrawer.exe`，放到固定目录（建议 `%LOCALAPPDATA%\Programs\TaskbarDrawer`）
2. 双击运行：托盘出现图标（没有收纳内容时任务栏上还没有胶囊）
3. 打开几个窗口，按 `Ctrl+Alt+Shift+D` → 窗口收进胶囊，自动生成「场景 1」
4. 左键点胶囊 → 原位展开；再点 → 收起
5. 右键胶囊 → 重命名，给场景起个好记的名字；之后关掉窗口也能点胶囊按位置重新打开

> 首次运行会提示：Win11 默认把新托盘图标收进折叠区（`^`）。

## 从源码构建

```powershell
dotnet build src/TaskbarDrawer/TaskbarDrawer.csproj -c Release

dotnet test tests/TaskbarDrawer.Tests/TaskbarDrawer.Tests.csproj -c Release

dotnet publish src/TaskbarDrawer/TaskbarDrawer.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true -o publish
```

产物是单个 `publish\TaskbarDrawer.exe`（约 320 KB，需要 .NET 8 运行时）。

## 命令行

| 命令 | 作用 |
| --- | --- |
| `TaskbarDrawer.exe` | 启动并常驻托盘；重复启动会唤起已运行实例的主面板 |
| `TaskbarDrawer.exe --probe` | 只读打印候选窗口（句柄 / PID / 类名 / 矩形），排查「哪些窗口会被收纳」 |
| `TaskbarDrawer.exe --help` | 打印用法 |

## 数据与日志

全部在 `%LOCALAPPDATA%\TaskbarDrawer\`：

| 文件 | 内容 |
| --- | --- |
| `settings.json` | 热键、面板位置、单窗口模式、开机自启 |
| `saved-groups.json` | 场景：软件路径 + 窗口位置 |
| `recovery.json` | 当前被隐藏的窗口，异常退出后下次启动自动救回 |
| `logs\` | 崩溃与局部异常日志，滚动保留 10 份 |

删掉整个目录即可恢复出厂设置。

## 已知限制

- 收纳的是普通桌面窗口；UWP、管理员权限更高的窗口可能收不进去
- 胶囊与 Win11 任务栏共用 topmost 波段，极少数时刻会被任务栏短暂盖住，2 秒内自动重贴
- 面板位置以 DIP 记忆，双屏不同缩放比例时可能偏移（恢复时会拉回工作区）
- 场景按 exe 路径重新启动；同路径软件已卸载时该项会被跳过
- 不需要管理员权限，也不修改任何系统设置

## 文档

- [AGENTS.md](AGENTS.md) — 给 AI / 未来的自己：技术栈、关键坑、约定、常用命令
- [DEVELOPMENT.md](DEVELOPMENT.md) — 架构说明与关键问题记录
- [CHANGELOG.md](CHANGELOG.md) — 版本历史

维护规范见 [knowledge-base · 单项目规范](https://github.com/Simiely/knowledge-base/tree/main/模板库/单项目规范)。
