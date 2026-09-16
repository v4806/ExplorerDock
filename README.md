# ExplorerDock

把 Windows 11 任务栏上的「文件资源管理器」窗口按钮，搬到一个常驻屏幕顶部的悬浮栏里。

开着十个文件夹干活时，任务栏会被一排一模一样的文件夹按钮塞满。ExplorerDock 把这些按钮从任务栏上摘下来，
集中放进一条轻量的悬浮栏，点一下就切过去——任务栏腾出来给真正需要常驻的应用。

```
                 ⠿  📁 Windows   📁 downloads   📁 项目资料   📁 此电脑          ← 悬浮栏（顶部居中）
   ┌────┬────┬────┬────┬────┬────┬────┐
   │开始│搜索│应用A│应用B│应用C│应用D│ …  │                                        ← 任务栏，没有文件夹按钮
   └────┴────┴────┴────┴────┴────┴────┘
```

## 特性

- **任务栏减负**：所有文件夹窗口的按钮从任务栏移除，ALT+TAB 不受影响（窗口仍在切换列表里）。
- **自动跟随**：新开的文件夹自动出现，关掉的自动消失，无需手动维护。
- **像任务栏一样操作**：左键切到该窗口，已经在最前就最小化；当前活动的文件夹会高亮。
- **真实文件夹图标**：读取每个窗口当前所在路径，用对应的 shell 图标和完整路径提示。
- **位置随你**：默认贴在屏幕顶部居中，可拖到任意位置并被记住。
- **退出即还原**：退出程序会把所有按钮还给任务栏；即使异常终止，也有 `--restore` 兜底。

## 安装

### 方式一：安装包（推荐）

到 [Releases](../../releases) 下载 `ExplorerDock-Setup-x.y.z.exe`，双击安装。
安装程序会附带 .NET 运行时（自包含），不需要额外装任何东西。

### 方式二：免安装单文件

下载 `ExplorerDock-x.y.z-portable.zip` 解压运行。这是框架依赖版本，需要机器上装有
**.NET 10 Desktop Runtime**。

### 方式三：从源码构建

```powershell
git clone https://github.com/v4806/ExplorerDock.git
cd ExplorerDock

# 开发构建
dotnet build ExplorerDock.csproj -c Release

# 单文件发布（框架依赖，体积最小）
dotnet publish ExplorerDock.csproj -c Release -r win-x64 --no-self-contained `
  -p:PublishSingleFile=true -p:DebugType=none -o publish

# 自包含发布（免运行时，体积约 150MB）
dotnet publish ExplorerDock.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:DebugType=none -o publish
```

需要 .NET 10 SDK。

## 使用

### 悬浮栏

| 操作 | 行为 |
| --- | --- |
| 左键点按钮 | 切到该文件夹；若它已经在最前，则最小化 |
| 中键点按钮 | 关闭这个文件夹窗口 |
| 右键点按钮 | 切换到该文件夹 / 最小化窗口 / 复制文件夹路径 / 临时放回任务栏 / 关闭这个窗口 / 关闭所有文件夹窗口 |
| 拖动最左 `⠿` 把手 | 移动悬浮栏位置（会被记住） |
| 双击 `⠿` 把手 | 隐藏悬浮栏 |
| 右键 `⠿` 把手 | 打开设置菜单 |
| 滚轮 | 文件夹太多时横向滚动 |
| 悬停按钮 | 显示文件夹名、完整路径与操作提示 |

当前正在看的那个文件夹窗口，按钮会**高亮**；前台是浏览器等其他应用时，全部不高亮。

### 托盘图标

双击显示 / 隐藏悬浮栏，右键是全部设置：

- 显示悬浮栏
- 接管任务栏按钮（核心开关，关掉就把按钮还给任务栏）
- 没有文件夹时自动隐藏
- 开机自动启动
- 回到屏幕顶部居中
- 把窗口还原到任务栏
- 关闭所有文件夹窗口
- 重启资源管理器
- 退出 ExplorerDock

### 默认设置

首次运行时的默认值：接管任务栏 **开**、没有文件夹时自动隐藏 **开**、显示完整标题 **开**、开机自动启动 **开**。
可在托盘或悬浮栏右键菜单里随时改。

## 命令行

| 参数 | 作用 |
| --- | --- |
| （无） | 正常启动；已有实例在运行时会提示并退出 |
| `--quit` | 让正在运行的实例正常退出（退出前会把按钮还给任务栏） |
| `--restore` | 把所有文件夹窗口的按钮还给任务栏后立即退出，用于应急 |

被任务管理器强杀后，任务栏按钮不会自动回来，运行一次 `ExplorerDock.exe --restore` 即可全部还原，
或者重启一次资源管理器。

## 工作原理

- 用 shell 官方的 `ITaskbarList::DeleteTab` 把窗口按钮从任务栏摘掉，`AddTab` 还回去。
  这是系统提供的接口，只影响任务栏按钮，**ALT+TAB 里依旧能看到这些窗口**。
- 后台一个 STA 线程每 350ms 枚举一次 `CabinetWClass` 顶层窗口；同时通过 `Shell.Application`
  读取每个窗口当前的路径，用来取对应的文件夹图标和完整路径。
- 判断「该切换还是该最小化」用的是 Z 序：悬浮栏始终置顶，沿 Z 序往下遇到的第一个真正的
  应用窗口，就是用户点击之前正在看的那个窗口。
- 唤醒窗口时用 `AttachThreadInput` 绕过 Windows 的前台窗口锁定，保证切换一定生效。

## 已知行为与边界

- 只处理资源管理器**文件夹窗口**，不碰任何其他应用的窗口，也不动任务栏上固定的资源管理器图标。
- 关闭文件夹窗口时发的是 `WM_CLOSE`，等价于手动点窗口的 ×；如果开了多标签页，系统仍会按你自己的
  设置弹出「要关闭所有选项卡吗」的确认框。
- 摘除动作对系统无害：即使程序异常终止，重启资源管理器或重启系统也会恢复原状。
- 因为悬浮栏窗口不使用透明外边距和投影，它没有阴影效果——这是刻意的取舍：窗口命中区域必须与
  可见 UI 完全一致，否则透明边距会吃掉旁边窗口的点击。

## 配置文件

`%APPDATA%\ExplorerDock\settings.json`，记录悬浮栏位置、接管开关等。删掉即可恢复默认。

## 项目结构

| 文件 | 职责 |
| --- | --- |
| `DockWindow.xaml(.cs)` | 悬浮栏窗口：按钮生成、点击切换、右键菜单、位置记忆、提示气泡 |
| `Services/ExplorerWatcher.cs` | 后台轮询：发现 / 移除文件夹窗口，摘除与还原任务栏按钮 |
| `Services/ShellUrlProbe.cs` | 通过 Shell.Application 取每个窗口的当前路径 |
| `Services/TrayIconManager.cs` | 托盘图标与菜单 |
| `Services/Settings.cs` | 配置读写 |
| `Interop/TaskbarTweaker.cs` | `ITaskbarList` 封装（DeleteTab / AddTab） |
| `Interop/NativeMethods.cs` | 窗口枚举、激活、最小化、关闭等 Win32 封装 |
| `Interop/ShellInterop.cs` | 文件夹图标获取（SHGetFileInfo） |
| `installer/ExplorerDock.iss` | Inno Setup 安装包脚本 |

## 许可

[MIT](LICENSE)
