# ExplorerDock #7-#11 修复 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 补齐 #7/#9/#10/#11 的相关边界缺口，验证 #8 的现有实现，测试通过后同步仓库并关闭五条 issue。

**Architecture:** 对纯状态和高风险边界做最小可测试抽取：剪贴板热键状态机、虚拟桌面即时探测器、强制快照请求、Explorer 接管优先级和监听注册失败传播。WPF/Shell 行为保留现有结构，通过真实 Windows 交互验证。

**Tech Stack:** C# 13 / .NET 10 / WPF / Win32 P-Invoke / UI Automation / PowerShell / Inno Setup（仅在已有安装脚本存在时做构建核验）

**Spec:** `docs/superpowers/specs/2026-11-07-explorerdock-issues-7-11-design.md`

## Global Constraints

- 修改前完整源码备份：`D:\DEV\ExplorerDock-backups\ExplorerDock-source-20261107-184427.zip`，SHA-256 `62C033EE25C6E051AFD0B74D0F2DF6DD6B4D9CE3E5FC1E810E38D9BBCCB1C368`。
- 基线提交 `d4a5b48`，基线 `Release/x64` 构建为 0 errors、4 warnings；不得新增警告。
- 不处理现有 `ClipboardPanel.FavoriteRequested` 与 WFO0003 警告，除非本次修项直接需要。
- 不新增自定义热键、不重做菜单、不发布 Release、不创建只有 tag 没有安装包的版本。
- 同一次修改只解决一个根因；每项先观察失败断言，再写最小实现。
- 真实测试必须使用临时设置与剪贴板数据；原 `%APPDATA%\ExplorerDock` 先备份，测试后恢复。
- 鼠标与键盘操作使用 `computer-use`；原生窗口截图按 HWND 使用 `PrintWindow(..., PW_RENDERFULLCONTENT)`。

## Review Focus

- 热键只依据异步 `GetAsyncKeyState` 就启动：普通 V 或 IME 输入被误当成 Win+V。
- 左右 Win 共用一个布尔状态：释放另一侧 Win 清掉正在进行的接管序列，留下卡键。
- 接管禁用发生在 V/Win 已吞下之后：释放事件未收完，后续输入被吞或系统开始菜单误弹。
- 虚拟桌面切换刚完成、watcher 尚未发布：Alt+Tab 仍显示上一桌面窗口。
- Explorer 已从上一轮 `TakeoverState` 集合移除不及时：关闭独立开关后仍被判为参与“只合并接管程序”的分组。
- 剪贴板监听注册失败被当成成功：界面显示开启但实际不采集，后续无法正常重试。

---

### Task 1: 建立最小 Issue 回归测试工程与 Explorer 优先级失败用例

**Files:**
- Create: `tests/ExplorerDock.IssueTests/ExplorerDock.IssueTests.csproj`
- Create: `tests/ExplorerDock.IssueTests/Program.cs`
- Modify: `ExplorerDock.csproj`
- Modify: `AssemblyInfo.cs`

**Interfaces:**
- Consumes: `TakeoverState.SetExplorerTakeover(bool)`, `TakeoverState.Publish(IEnumerable<IntPtr>)`, `TakeoverState.IsTakeoverWindow(IntPtr, string)`.
- Produces: console test command `dotnet run --project tests/ExplorerDock.IssueTests/ExplorerDock.IssueTests.csproj -c Release`; main assembly exposes internals to `ExplorerDock.IssueTests`.

- [ ] **Step 1: 创建无外部测试包的测试工程和断言工具**

测试工程目标为 `net10.0-windows`，引用主项目。`Program.cs` 使用表驱动测试和明确的 `FAIL <name>: expected ... actual ...` 输出，任一失败返回退出码 1。

- [ ] **Step 2: 写 Explorer 排除优先级测试**

```csharp
var explorer = new IntPtr(0x101);
var app = new IntPtr(0x202);

TakeoverState.Publish(new[] { explorer, app });
TakeoverState.SetExplorerTakeover(false);

AssertFalse("explorer override wins over stale handle",
    TakeoverState.IsTakeoverWindow(explorer, "CabinetWClass"));
AssertTrue("other takeover handle remains",
    TakeoverState.IsTakeoverWindow(app, "Notepad"));
```

- [ ] **Step 3: 运行测试并确认 RED**

Run: `dotnet run --project tests/ExplorerDock.IssueTests/ExplorerDock.IssueTests.csproj -c Release`

Expected: Explorer 断言失败，因为 `IsTakeoverWindow` 仍命中上一轮 `_handles`；其它断言保持正确。

- [ ] **Step 4: 写 #10 热键状态机失败测试**

测试左右 Win 分开、未观察 Win down 不授权、缓存陈旧、Ctrl/Alt/Shift 否决、禁用后收尾、长按不重复触发等表驱动序列。引用尚不存在的 `ClipboardHotkeyStateMachine`。

- [ ] **Step 5: 运行测试并确认 RED**

Run: `dotnet run --project tests/ExplorerDock.IssueTests/ExplorerDock.IssueTests.csproj -c Release`

Expected: 编译失败，明确缺少 `ClipboardHotkeyStateMachine` 及其决策枚举。

### Task 2: 修复 Explorer 优先级与剪贴板热键状态机

**Files:**
- Modify: `Services/TakeoverState.cs`
- Create: `Services/ClipboardHotkeyStateMachine.cs`
- Modify: `Services/HostKeyboard.cs`
- Modify: `Interop/NativeMethods.cs`
- Test: `tests/ExplorerDock.IssueTests/Program.cs`

**Interfaces:**
- Consumes: `KeyStroke`, `NativeMethods.IsKeyDown`, `NativeMethods.TrySendKeyUp`, `HostProtocol.KeyToggleClipboard`.
- Produces: `ClipboardHotkeyDecision Process(KeyStroke stroke, bool enabled, bool winPhysicallyDown, bool ctrlDown, bool altDown, bool shiftDown)`；决策为 `Pass`、`Swallow`、`ToggleClipboard`、`SwallowAndRestoreWin`。

- [ ] **Step 1: 修复 Explorer 判定顺序**

`TakeoverState.IsTakeoverWindow` 对 `CabinetWClass` / `ExploreWClass` 直接返回 `_explorerTakeover`；非 Explorer 窗口才查询 `_handles`。

- [ ] **Step 2: 将热键规则抽成纯状态机**

状态机分别保存左右 Win、当前 V 序列、Win up 待收尾标记。新序列必须同时满足：本状态机观察到对应 Win down、物理 Win 仍按下、Ctrl/Alt/Shift 均未按下、V 是新按下且接管已启用。

- [ ] **Step 3: 收到释放时先收尾再检查启用状态**

已吞下的 V up、Win up 无论接管是否刚被禁用都必须消费；禁用只阻止新序列启动。另一侧 Win 的释放不得清掉当前侧序列。

- [ ] **Step 4: 把 SendKeyUp 改为可报告成功**

新增 `NativeMethods.TrySendKeyUp(ushort vk) -> bool`，现有 `SendKeyUp` 保留为兼容包装。Win up 只有在注入成功时才吞真实事件；失败则放行真实 Win up。

- [ ] **Step 5: 接入 `HostKeyboard`**

`HandleClipboard` 先调用状态机，再把 `ToggleClipboard` 映射到见证键与通知，把 `SwallowAndRestoreWin` 映射到 `TrySendKeyUp`。生产模式仍过滤注入事件，钩子内不做日志 I/O。

- [ ] **Step 6: 运行测试确认 GREEN**

Run: `dotnet run --project tests/ExplorerDock.IssueTests/ExplorerDock.IssueTests.csproj -c Release`

Expected: Explorer 优先级与全部热键序列断言通过，退出码 0。

### Task 3: 收紧剪贴板宿主就绪与监听注册边界

**Files:**
- Modify: `Services/HostKeyboard.cs`
- Modify: `App.xaml.cs`
- Modify: `Interop/ClipboardInterop.cs`
- Modify: `Services/ClipboardController.cs`
- Test: `tests/ExplorerDock.IssueTests/Program.cs`

**Interfaces:**
- Consumes: `App.PushKeyState()`, `ClipboardMessageWindow`, `AddClipboardFormatListener`.
- Produces: Host clipboard takeover 初始值为 `false`；`ClipboardMessageWindow` 在注册失败时抛 `Win32Exception` 并释放 `HwndSource`。

- [ ] **Step 1: 写失败断言**

测试宿主默认状态不放行 Win+V；只有显式 `SetState(..., clipboardTakeover: true, ...)` 后才允许。人工故障注入记录注册失败时监听窗口已释放。

- [ ] **Step 2: 默认关闭宿主剪贴板接管**

`_clipboardTakeover` 初始化为 `false`；`App.PushKeyState` 继续只在 `ClipboardEnabled && ClipboardTakeover && _clipboard is not null` 时开启。

- [ ] **Step 3: 关闭总开关时先推禁用**

`App.SetClipboardEnabled(false)` 先保存设置并调用 `PushKeyState()`，再释放控制器、监听器和库。

- [ ] **Step 4: 让监听注册失败成为初始化失败**

`ClipboardMessageWindow` 检查 `AddClipboardFormatListener`；失败时读取 `Marshal.GetLastWin32Error()`，移除 hook、释放 `HwndSource`、抛 `Win32Exception`。`App.StartClipboard` 的现有回滚继续负责设置回关闭与提示。

- [ ] **Step 5: 重新核对排队热键动作**

`ClipboardController.OnKeyAction` 与最终 `Toggle` 都在当前设置/实例仍有效时才执行；关闭后排队动作不得弹回面板。菜单主动打开入口保持不变。

- [ ] **Step 6: 运行回归测试与构建**

Run: `dotnet run --project tests/ExplorerDock.IssueTests/ExplorerDock.IssueTests.csproj -c Release`

Run: `dotnet build ExplorerDock.csproj -c Release -p:Platform=x64 --no-restore`

Expected: 测试退出码 0；构建 0 errors，警告数不超过基线 4。

### Task 4: 修复虚拟桌面即时查询与首份快照

**Files:**
- Modify: `Interop/VirtualDesktop.cs`
- Modify: `Services/AltTabWindowList.cs`
- Modify: `Services/AltTabController.cs`
- Modify: `Services/ExplorerWatcher.cs`
- Modify: `Services/HostRuntime.cs`
- Test: `tests/ExplorerDock.IssueTests/Program.cs`

**Interfaces:**
- Consumes: `IVirtualDesktopManager`, `TakeoverState.PublishCurrentDesktop`, `ExplorerWatcher.ReapplyNow`.
- Produces: `VirtualDesktop.CreateProbe() -> VirtualDesktopProbe?`；`ExplorerWatcher.ReapplyNow()` 同时请求一次无条件快照；本地 Alt+Tab 列表线程为 STA。

- [ ] **Step 1: 写 COM 失败降级与快照请求的可测试断言**

测试无桌面管理器时探测结果为“不过滤”；`ReapplyNow` 后快照标志被消费一次。若无法稳定构造 COM 失败，保留可空探测器的纯逻辑断言，并在人工测试记录真实 COM 路径。

- [ ] **Step 2: 增加当前 STA 的桌面探测器**

`VirtualDesktopProbe` 在创建线程上实例化并缓存 `IVirtualDesktopManager`，独立于 watcher 的静态缓存；任何创建/调用失败都按“不过滤”返回，并在 Dispose 时释放 RCW。

- [ ] **Step 3: Alt+Tab 本次枚举使用即时探测器**

`AltTabWindowList.EnumerateCandidates` 每次调用创建一个 probe，用本次枚举的 HWND 查询当前桌面；不在 `Move` 时重复查询。`AltTabController.Open` 的列表线程显式设置 STA。

- [ ] **Step 4: 首份快照无条件发布**

`ExplorerWatcher` 增加一次性 `_forceSnapshot`。`ReapplyNow()` 设置它；Loop 在 `changed || forceSnapshot` 时调用 `SnapshotUpdated`。`HostRuntime.CmdStart` 调用 `_host.ReapplyNow()`，确保连接后即使窗口集合与前台未变也补发。

- [ ] **Step 5: 运行测试与构建**

Run: `dotnet run --project tests/ExplorerDock.IssueTests/ExplorerDock.IssueTests.csproj -c Release`

Run: `dotnet build ExplorerDock.csproj -c Release -p:Platform=x64 --no-restore`

Expected: 测试通过；构建 0 errors、无新增警告。

### Task 5: 修复搜索框 Delete 归属并验收 #8

**Files:**
- Modify: `Views/ClipboardPanel.cs`
- Test: `tests/ExplorerDock.IssueTests/Program.cs`
- Manual evidence: `artifacts/test/2026-11-07/`

**Interfaces:**
- Consumes: `ClipboardPanel.OnPreviewKeyDown`, `_searchBox.IsKeyboardFocusWithin`.
- Produces: 搜索框聚焦时 Delete 永不触发历史删除；列表/面板聚焦时保留 Delete 删选中项。#8 不改产品代码。

- [ ] **Step 1: 写键盘归属测试**

用可调用的小型判定函数覆盖“搜索框聚焦 + 空文本”“搜索框聚焦 + 有文本”“列表聚焦”。先观察空文本场景失败。

- [ ] **Step 2: 最小修改 Delete 分支**

搜索框聚焦时直接 `break`，不设置 `Handled`；只有搜索框未聚焦时才执行 `DeleteRequested`。

- [ ] **Step 3: 运行测试确认 GREEN**

Run: `dotnet run --project tests/ExplorerDock.IssueTests/ExplorerDock.IssueTests.csproj -c Release`

Expected: 三种键盘归属断言通过。

- [ ] **Step 4: #8 静态与交互验收**

验证旧 `settings.json` 缺少字段时 `AltTabMouseHoverSelect` 为 false；面板打开后光标停在卡片上不改变键盘选中，点击仍切换。开启设置后鼠标真实移动才改变选中，重启后保持。

### Task 6: 真实 Windows 交互测试

**Files:**
- Create: `artifacts/test/2026-11-07/acceptance.md`
- Create: `artifacts/test/2026-11-07/*.png`（需要留图时）

**Interfaces:**
- Consumes: Release 构建产物、临时 `%APPDATA%\ExplorerDock` 数据、computer-use 键鼠操作、HWND 截图。
- Produces: 实际结果记录，包含 Windows build、进程模式、测试数据和通过/失败。

- [ ] **Step 1: 备份并隔离用户数据**

将 `%APPDATA%\ExplorerDock` 复制/归档到 `D:\DEV\ExplorerDock-backups`，确认归档可读后，用临时数据目录运行测试。结束后先移除临时数据，再恢复原目录；不读取或记录原有剪贴板内容。

- [ ] **Step 2: 启动候选构建并确认宿主**

停止当前安装版进程，启动 `bin\x64\Release\net10.0-windows\win-x64\ExplorerDock.exe`。记录 UI/host PID、是否提权、宿主日志中的 connected/keyboard installed。

- [ ] **Step 3: 执行 #7**

用唯一文本、文件路径、图片和收藏组填充临时库，检查大小写、路径、类型词、无结果、收藏交集；空搜索框按 Delete 不删除；微软拼音组字期间 Enter/Esc/方向键不误执行面板命令。

- [ ] **Step 4: 执行 #8**

光标停在屏幕中部和卡片上打开 Alt+Tab，按 Tab 改变选中并记录所选卡片；点击卡片仍有效。开启悬停后，只有移动鼠标才改变选中，重启偏好保留。

- [ ] **Step 5: 执行 #9**

桌面 A/B 各放同数量的可辨认 Explorer 和普通窗口。A→B→A，每次切换后立即打开 Alt+Tab，核对悬浮栏和切换器只显示当前桌面窗口；再测 Alt+~。保留现场证据，不强求截图含敏感内容。

- [ ] **Step 6: 执行 #10**

在普通编辑器和管理员编辑器分别测试：中文组字含 V、退格、空格、候选提交/取消；裸 V；左右 Win+V；Ctrl/Alt/Shift 混合；长按 V；释放后裸 V；跨过 20 秒钩子重挂。记录是否误弹、系统面板、开始菜单或输入丢失。

- [ ] **Step 7: 执行 #11**

关闭总开关后复制标记 B，确认不采集且 Win+V 交系统；重启仍关闭；重开后复制 D，确认历史保留 A 和 D、没有 B/C。关闭 Win+V 接管但保留采集。关闭 Explorer 接管后确认任务栏按钮归还、悬浮栏移除、默认分组不再视为托管，其他程序不受影响；重启后偏好保留。

- [ ] **Step 8: 恢复用户数据并核对**

退出候选构建，恢复原 `%APPDATA%\ExplorerDock`，确认归档和恢复后的文件计数/哈希一致，再按需要重启原安装版。验收记录写明实际结果，不用“应该”或推测。

### Task 7: 最终验证与代码审查

**Files:**
- Review: 全部分支差异
- Modify: 仅修复审查发现的 Critical/Important 项

**Interfaces:**
- Consumes: 计划、Spec、IssueTests、人工验收记录。
- Produces: 可推送的干净提交。

- [ ] **Step 1: 运行静态与构建验证**

Run: `git diff --check`

Run: `dotnet run --project tests/ExplorerDock.IssueTests/ExplorerDock.IssueTests.csproj -c Release`

Run: `dotnet build ExplorerDock.csproj -c Release -p:Platform=x64 --no-restore`

Expected: diff 无空白错误；测试退出码 0；构建 0 errors，无新增警告。

- [ ] **Step 2: 按 Review Focus 自审差异**

逐项检查命中测试、释放收尾、STA、快照、Explorer 开关、监听回滚和 Delete 归属；不扩大范围。

- [ ] **Step 3: 检查需要提交的文件**

Run: `git status --short`

Expected: 只包含设计/计划、测试工程、issue 修复代码和验收记录；构建产物保持 ignored。

- [ ] **Step 4: 提交**

```bash
git add AssemblyInfo.cs ExplorerDock.csproj App.xaml.cs Interop/ClipboardInterop.cs Interop/NativeMethods.cs Interop/VirtualDesktop.cs Services/AltTabController.cs Services/AltTabWindowList.cs Services/ClipboardController.cs Services/ClipboardHotkeyStateMachine.cs Services/ExplorerWatcher.cs Services/HostKeyboard.cs Services/HostRuntime.cs Services/TakeoverState.cs Views/ClipboardPanel.cs tests/ExplorerDock.IssueTests docs/superpowers
git commit -m "修复 issue #7/#9/#10/#11 边界问题"
```

### Task 8: 同步仓库、回复并关闭 issues

**Files:**
- No source files.

**Interfaces:**
- Consumes: 已验证提交、GitHub Git credential、GitHub REST API。
- Produces: 远端 `main` 更新、五条 issue 各一条实际验证回复、五条 issue closed。

- [ ] **Step 1: 获取远端最新状态**

Run: `git fetch origin`

确认 `origin/main` 仍为 `b41702b` 或是可安全整合的新提交；不强制推送。

- [ ] **Step 2: 本地合入 main 并验证**

在临时切换前记录当前分支与 SHA；`main` fast-forward 到已验证提交，重新运行 IssueTests 与 Release 构建。

- [ ] **Step 3: 推送 main**

Run: `git push origin main`

Expected: 远端 `main` 指向验证提交。

- [ ] **Step 4: 逐条回复 issue**

#7：说明搜索入口、匹配范围和中文输入/Delete 结果。

#8：说明默认关闭悬停选中、点击仍有效、如何开启。

#9：说明悬浮栏和切换器按当前桌面过滤及 A/B 往返结果。

#10：说明普通 V、中文组字、裸 V、左右 Win+V、长按、普通/管理员前台结果，不声称旧日志展示了根因。

#11：说明两个开关的位置、总开关与只让出 Win+V 的区别、历史保留和 Explorer 按钮归还结果。

- [ ] **Step 5: 关闭 issue**

仅在回复发布且主分支已验证后，以 `completed` 状态逐条关闭 #7/#8/#9/#10/#11。任何未通过项保持开放并在回复中写清剩余问题。

- [ ] **Step 6: 最终核对**

核对远端 `main` SHA、五条 issue 状态和回复链接。完成后不再运行无关测试。
