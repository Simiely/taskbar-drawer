---
id: docs.design.window-identity
title: 窗口识别与弹窗归属（终版方案）
summary: 调研修正了 AUMID 的可用性假设：exe 仍是主键，AUMID 只作打包应用增强。最终方案是「窗口簇」模型 + 三层过滤 + 位次配对。三步已在 1.9.0 / 1.10.0 全部落地。
type: explanation
kind: decision
status: active
owner: "@simiely"
tier: normal
topics: [development]
source_type: doc
source: src/TaskbarDrawer/Core/WindowEnumerator.cs
last_reviewed: 2026-11-18
related: []
---
# 窗口识别与弹窗归属（终版方案）

> 本文替代同一天的初稿。初稿把 AppUserModelID 当成主键，调研后发现这个假设不成立，第二节说明修正依据。

## 实施状态

- ✅ **P1 窗口簇**（1.9.0）：准入 owner 过滤、同簇弹窗一起隐藏 / 恢复、关闭只针对簇主、配对候选排除 owned、`--probe` 输出排除原因
- ✅ **P2 详情窗状态**（1.9.0）：`SavedWindowItem.IsOpen` + 每行「已打开 / 未打开」+ 汇总
- ✅ **P3 两层身份 + 位次配对**（1.11.0）：`SavedWindow` 增加 `AppId` / `ClassName` / `Ordinal`；`WindowIdentity` 承担 L1 / 位次 / 配对三段纯规则；`SceneEdit.Key` 旁边新增 `IdentityKey`；旧快照不迁移，自动退回旧路径

实现期发现的一处修正（已写进代码注释与单测）：位次排序必须**先按显示器分组再比坐标**。保存侧读的是 `WINDOWPLACEMENT.rcNormalPosition`（工作区坐标），还原侧读的是 `GetWindowRect`（屏幕坐标），跨屏直接比 Top / Left 会把同一应用在两块屏上的窗口排出相反次序。

## 一、要解决的三个现象

1. 浏览器插件弹窗、软件对话框被当成独立窗口收进场景；关闭场景时主窗口被连坐关掉。
2. 标题会变的窗口（浏览器切标签、编辑器换文件、终端跑命令）重开后认不回来。
3. 场景详情里「已在场景但当前没打开」的成员没有标识，无法判断要不要移除。

## 二、调研修正：AUMID 不能当主键

初稿是「L1 = AUMID，回退 exe」。查证后这条要改：

> 「These functions only work for explicit AppUserModeIDs. They do not work for implicit AppUserModeIDs that are assigned by Windows… Applications cannot retrieve a system-assigned AppUserModelID.」

即 `SHGetPropertyStoreForWindow` + `PKEY_AppUserModel_ID` **只返回应用自己显式设置的 AUMID**。没显式设置时，Windows 用一组启发式在内部生成，而这个值**取不到**（设计如此）。实测旁证：一个真实 issue 同时打印 transgui 与 Slack 的窗口属性，两者都是 `AUMID: (none)`——Slack 是 MSIX，其 AUMID 由 OS 在包层管理，也不从这个窗口属性暴露。

结论：

- **exe 路径是桌面应用的可靠主键**（任务栏的隐式分组本质上也是按 exe 推导的）。
- **AUMID 只在打包应用（UWP/MSIX）上有价值**，且应走 `GetApplicationUserModelId(hProcess)`（kernel32 / appmodel.h，Win8+）而不是窗口属性；返回 `APPMODEL_ERROR_NO_APPLICATION` 表示没有应用标识，回退 exe。
- 所以主键定义成一个**带类型前缀的字符串**，而不是「AUMID 优先」：

```
L1 = "aumid:<package-appid>"   // 仅打包应用，来自 GetApplicationUserModelId(pid)
   | "exe:<规范化路径>"          // 其余全部，含绝大多数桌面应用
```

## 三、弹窗：用「窗口簇」而不是「逐窗过滤」

### 3.1 依据

微软给出的 Alt+Tab 判据（Raymond Chen《Which windows appear in the Alt+Tab list?》）：

```c
HWND hwndWalk = GetAncestor(hwnd, GA_ROOTOWNER);
while ((hwndTry = GetLastActivePopup(hwndWalk)) != hwndTry) {
    if (IsWindowVisible(hwndTry)) break;
    hwndWalk = hwndTry;
}
return hwndWalk == hwnd;
```

同文明确写了两个样式的语义覆盖：`WS_EX_TOOLWINDOW` 当作不可见；`WS_EX_APPWINDOW` 当作没有 owner。

浏览器插件弹窗确实有 owner：Chromium 源码 `extension_popup.cc` 的 `IsOwnerOf()` 就是 `GetWindow(child, GW_OWNER) == owner`；2024 年的 `windows_utils_views.cc` 注释也写明 bubble 类弹窗「不物理父子于浏览器窗口，但它的 widget 是浏览器窗口 widget 的子级」。

### 3.2 但不能照抄 Alt+Tab 算法

Alt+Tab 的 `GetLastActivePopup` 循环会在**有可见对话框时把对话框当成这个簇的代表**、把主窗口排除掉。对 Alt+Tab 这是对的（用户想回到那个弹窗），对我们**正好相反**：我们要主窗口，弹窗不能进场景。

所以采用「**窗口簇**」模型，比 Alt+Tab 更贴合收纳语义：

| 概念 | 定义 |
| --- | --- |
| 窗口簇 | 一个**无 owner** 的顶层窗口（簇主）+ 它当前可见的 owned 窗口（`GetWindow(hwnd, GW_OWNER) != 0`，含 `GetLastActivePopup` 链上的） |
| 场景成员 | **只有簇主**。owned 弹窗永远不成为成员、不写快照、不参与配对 |
| 收纳 | 簇主 + 它当前可见的弹窗**一起隐藏**；弹窗句柄只记在运行时记录（`StashedWindow.OwnedHidden`），不落盘 |
| 展开 | 先还原簇主，再把 `OwnedHidden` 里仍存在的弹窗用 `SW_SHOWNA` 显示回来 |
| 关闭场景 | 只对**簇主**句柄发 `WM_CLOSE`（弹窗随主窗口自然关闭） |

为什么必须「一起隐藏」：`SW_HIDE` 主窗口不会连带隐藏 owned 弹窗（系统只在**最小化**或**销毁**时才联动）。只过滤不隐藏，会留下一个孤零零的对话框浮在桌面上——比现在更糟。

### 3.3 准入规则（三道闸）

| 闸 | 位置 | 规则 |
| --- | --- | --- |
| 归属 | `WindowEnumerator` | 用 `GetWindow(hwnd, GW_OWNER) == 0` 判「无 owner 即簇主」（`WS_EX_APPWINDOW` 豁免）；排除 `WS_EX_NOACTIVATE`；排除无标题 `WS_POPUP`；保留现有 tool window / cloaked / shell 类过滤。**不要用 `GA_ROOTOWNER`**：实机上它对 owned 的 `WS_POPUP` 直接返回窗口自身（见 1.13.4） |
| 快照 | `WorkspaceService.CaptureWindows` | 落盘前再判一次 owner，弹窗不写快照（防旧数据 / 竞态） |
| 关闭 | `WorkspaceService.Close` | 组里若意外混进有 owner 的窗口，先关它、簇主保留（兜底旧场景数据） |

**误杀风险**：少数 Electron / 老 MFC 应用会把主窗口错误地设成 owned。兜底两条——`WS_EX_APPWINDOW` 豁免；详情窗手动追加能把它加回来。同时给 `--probe` 增加「排除原因」列，误杀时一眼看出是哪条规则命中。

> `GetLastActivePopup` 只用于**隐藏**阶段（把同簇弹窗一起收走），不用于**准入**判定（准入只看 `GW_OWNER`）。
>
> **1.13.4 修正**：`GetAncestor(hwnd, GA_ROOTOWNER)` 在实机上对 owned 的 `WS_POPUP` 返回的是弹窗**自身**——`GetParent` 对非 `WS_CHILD` 返回 0，owner 链是空的。需要簇主时改用 `NativeMethods.RootOwner()`，它自己沿 `GW_OWNER` 逐层往上走。

## 四、两层身份：标题降级为弱信号

问题 2 的根子是 `SceneEdit.Key = exe + 标题`——标题是窗口上最易变的属性。

```
L1 应用身份 = aumid:… | exe:…            // 决定「启动谁」与「同程序归组」
L2 窗口身份 = (L1, 窗口类名, 簇内位次)     // 决定「是不是同一个窗口」
标题：只用于显示 + 配对时的弱加权
```

**位次怎么算才稳**：不按 `EnumWindows` 顺序（不稳定），按**保存时的矩形排序**——同 (L1, 类名) 的窗口按 `(Top, Left)` 升序排名。只要布局不变位次就不变；重开场景后按同样规则再算一遍就能对上。这是唯一能跨重启稳定复现的序，且不需要窗口内 ID。

配对顺序（替换 `PickRestoreCandidate` 现有四级）：

| 优先级 | 条件 | 说明 |
| --- | --- | --- |
| 1 | 同 L1 + 同类名 + 同位次 | 最稳，浏览器多窗口靠这级 |
| 2 | 同 L1 + 同标题（忽略大小写 / 两端空白） | 标题没变过的常见情况 |
| 3 | 同 L1 + 矩形距离最近 | 用户摆过的位置通常保留 |
| 4 | 同 L1 任意可见窗口 | 兜底，避免重复启动 |

现在只有 2 和 3，缺第 1 级——这正是「浏览器切过标签页就认不回来」的直接原因。

## 五、详情窗：把「未打开」显性化

- `SavedWindowItem.IsOpen`：该快照项此刻是否对应到运行时组里的某个 hwnd（按第四节的配对规则算）。
- 每行：`[已打开 | 未打开] 标题 · 程序名`；未打开的整行降饱和度，**移除按钮照常可用**（只改快照）。
- 顶部汇总：`3 个窗口 · 2 个已打开 · 1 个未打开`。
- 追加列表里「已在场景」拆成「已在场景（已打开）」与「已在场景（未打开）」，与上面口径一致。

## 六、数据字段与兼容

`SavedWindow` 增加：`AppId`（存 L1 串）、`ClassName`、`Ordinal`。

- 旧快照这三个字段为空 → 判为旧记录，配对退回「exe + 标题 + 矩形」的现行为，**不做一次性迁移**：旧数据里没有这些信息，补算都是猜测，猜错会把窗口摆到别处。
- 新快照同时保留 `ExePath`：`AppId` 只用于分组与配对，启动仍按 exe（UWP 启动需要 `IApplicationActivationManager`，本项目不做）。
- 读取时 `AppId` 为空即走旧路径，不需要版本号迁移。

## 七、分三步落地

| 步 | 内容 | 风险 | 直接消除 |
| --- | --- | --- | --- |
| **P1** | 窗口簇模型：准入 owner 过滤 + 收纳/展开时同簇弹窗一起隐藏/恢复 + 关闭只针对簇主 + `--probe` 排除原因 | 中低（改 `WindowStash` 的隐藏 / 还原路径） | 问题 1 全部 |
| **P2** | 详情窗 `IsOpen` 标识 + 汇总 + 追加列表口径 | 低（一个字段 + UI） | 问题 3 |
| **P3** | ✅ 两层身份与位次配对（`SceneEdit.IdentityKey`、`WindowIdentity.Pair`、快照字段），1.11.0 落地 | 中（动配对基线，需补单测） | 问题 2 |

顺序建议 P1 → P2 → P3：P1 最痛且不依赖数据模型；P2 很小；P3 要动已有单测基线，单独一轮验收更干净。

## 八、不做的方案（附理由）

| 方案 | 为什么不做 |
| --- | --- |
| 用 AUMID 做统一主键 | 桌面应用取不到（只有显式设过的才有），绝大多数窗口会退化成「无身份」 |
| 照抄 Alt+Tab 的 `GetLastActivePopup` 判据做准入 | 有对话框时会把主窗口排除、把对话框当代表，与收纳语义相反 |
| 用 PID 识别窗口 | PID 会复用，且同程序多窗口共用一个 PID，比 exe 更粗 |
| 用 `EnumWindows` 顺序当位次 | 枚举顺序不稳定，重启后必然错位 |
| 按窗口标题做唯一键 | 标题是最易变属性，正是问题 2 的成因 |
| 给弹窗单独建场景成员 | 弹窗是临时窗口，位置无复用价值，且关闭语义会连坐主窗口 |

## 九、验收

- **P1**：浏览器点开扩展弹窗 → 新建场景：弹窗不出现在场景里；主窗口收纳后弹窗不浮在桌面；展开后主窗口与弹窗一起回来（弹窗已被应用关掉则跳过）；关闭场景只关主窗口。
- **P2**：详情窗读出 `3 个窗口 · 2 个已打开 · 1 个未打开`，未打开项可直接移除。
- **P3**：同一浏览器开两个窗口、切标签页改变标题 → 收起 → 关闭场景 → 重新打开：两个窗口各回原位，不互换、不重复启动。
- 回归：`dotnet test` 全绿；新增与 `SceneFocusRulesTests` 同级的纯规则测试（位次计算、簇归属判定）。
