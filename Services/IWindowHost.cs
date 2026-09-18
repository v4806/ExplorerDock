using System.Windows.Media;
using ExplorerDock.Models;

namespace ExplorerDock.Services;

/// <summary>「接管指定程序」列表里的一个运行中程序。</summary>
public sealed class RunningProcessInfo
{
    /// <summary>程序名（小写 exe 名，不带扩展名）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>exe 完整路径（列表里取图标用）。</summary>
    public string ExePath { get; init; } = string.Empty;

    /// <summary>当前有可见窗口数。</summary>
    public int WindowCount { get; init; }

    /// <summary>随便一个窗口标题，帮用户认人。</summary>
    public string SampleTitle { get; init; } = string.Empty;
}

/// <summary>
/// "需要碰目标窗口"的那部分能力，也就是拆分后要跑在提权进程里的事。
///
/// 为什么单独抽出来：活动窗口是管理员程序时，不提权的话读窗口标题、摘任务栏按钮、
/// 激活窗口、读写标签页都会失效（UIPI）。这些事集中到一个接口后面，
/// 界面代码就不必知道它们到底在本地还是另一个进程里跑。
///
/// 目前只有"窗口监视 + 窗口操作 + 列表"这三条链走接口；
/// 键盘接管与按键注入要等钩子整体搬进提权进程时再接进来（那时它们不再跨进程）。
/// </summary>
public interface IWindowHost : IDisposable
{
    /// <summary>窗口/标签页快照更新（悬浮栏按钮靠它刷新）。</summary>
    event Action<ExplorerSnapshot>? SnapshotUpdated;

    /// <summary>
    /// 接管到的按键动作（`advance` / `advance_process` / `commit` / `cancel` / `toggle_clipboard`）。
    ///
    /// 键盘钩子装在提权进程里（管理员程序占前台时，普通权限的钩子收不到按键），
    /// 界面只负责响应这些动作 —— 弹面板、移动选中、提交切换。
    /// </summary>
    event Action<string>? KeyAction;

    /// <summary>把接管开关与面板状态镜像给宿主（钩子要靠它判断吞不吞）。</summary>
    void SetKeyState(bool altTabTakeover, bool clipboardTakeover, bool panelOpen);

    /// <summary>键盘钩子装上没有（诊断用）。</summary>
    bool KeyboardInstalled { get; }

    void Start();

    /// <summary>接管开关：关掉时把摘掉的任务栏按钮还回去。</summary>
    void SetTakeover(bool enabled);

    /// <summary>
    /// 接管范围：多窗口程序是否自动接管，以及手动指定要接管的程序名单。
    ///
    /// 这两条判定都在宿主侧做（枚举窗口是宿主的活），所以设置变了要推过去。
    /// </summary>
    void SetTakeoverScope(bool autoMultiWindow, IReadOnlyList<string> processes);

    /// <summary>
    /// Alt+Tab 的同名进程窗口合并范围（不合并 / 只合并悬浮栏接管的 / 合并全部）。
    ///
    /// 和接管范围一样，判定发生在宿主侧（卡片列表、Alt+~ 放行都在那边算），设置改了要推过去。
    /// </summary>
    void SetAltTabGroupMode(AltTabGroupMode mode);

    /// <summary>当前有可见窗口的运行中程序（「选择要接管的程序」列表的数据来源）。</summary>
    List<RunningProcessInfo> RunningProcesses();

    /// <summary>设置变化后立刻对现有窗口重新生效一次。</summary>
    void ReapplyNow();

    /// <summary>把所有窗口还给任务栏。</summary>
    void RestoreNow();

    /// <summary>激活窗口并切到指定标签页（tabIndex &lt; 0 表示整窗）。</summary>
    bool Activate(IntPtr hwnd, int tabIndex);

    /// <summary>最小化窗口。</summary>
    void Minimize(IntPtr hwnd);

    /// <summary>窗口里当前选中的是第几个标签页（-1 = 不知道 / 不是多标签窗口）。</summary>
    int SelectedTab(IntPtr hwnd);

    /// <summary>Alt+Tab 面板要的卡片列表（含合并信息与图标）。</summary>
    List<AltTabWindowInfo> SnapshotCards();

    /// <summary>Alt+~ 用的"同一个进程名的全部项目"。</summary>
    List<AltTabWindowInfo> ProcessCards(IntPtr hwnd);

    /// <summary>取窗口图标；跨进程时可能降级为 exe 图标。</summary>
    ImageSource? WindowIcon(IntPtr hwnd);

    /// <summary>
    /// 关掉一个窗口（发 WM_CLOSE）。
    ///
    /// 必须走宿主：普通权限进程往管理员程序窗口发消息会被 UIPI 挡掉 ——
    /// 这就是"关闭对管理员程序不生效"的原因。
    /// </summary>
    bool CloseWindow(IntPtr hwnd);

    /// <summary>关掉这个窗口所属程序的所有顶层窗口（文件夹窗口用的是这一条）。</summary>
    bool CloseProcessWindows(IntPtr hwnd);

    /// <summary>
    /// 结束这个窗口所属程序的所有进程（同一个 exe 启动出来的多个独立进程一起结束）。
    ///
    /// 破坏性操作，只该在用户明确选择"结束进程"时调用。
    /// </summary>
    bool KillProcesses(IntPtr hwnd);

    /// <summary>
    /// 让文件夹窗口执行它自己的"粘贴"命令（UI Automation Invoke 命令栏的粘贴按钮）。
    ///
    /// 拖放粘贴到文件夹窗口靠的就是这条：Ctrl+V 只落在"有键盘焦点的控件"上，
    /// 而窗口刚被激活/切过标签时焦点常常不在文件列表里，补的 Ctrl+V 会粘不上。
    /// 返回 false 表示没找到可用命令，调用方退回 Ctrl+V。
    /// </summary>
    bool PasteIntoFolder(IntPtr hwnd);

    /// <summary>
    /// 给当前前台窗口补一次 Ctrl+V。
    ///
    /// 必须在提权进程里发：从普通权限进程注入的按键，管理员程序窗口收不到（UIPI）。
    /// </summary>
    void SendPaste();

    /// <summary>补发一次 Alt 抬起（我们吞掉了这个抬起，得让键盘状态闭合）。</summary>
    void RestoreAltKey();

    /// <summary>在光标当前位置点一下左键（拖放粘贴时把焦点落到落点上）。</summary>
    void ClickAtCursor();

    /// <summary>
    /// 某个窗口现在是不是前台。
    ///
    /// 必须由提权进程回答：普通权限的界面进程调 `GetForegroundWindow()` 会返回 **0**
    /// （自己线程的输入队列里没有前台窗口），拿它做判断必然误判。
    /// </summary>
    bool IsForegroundWindow(IntPtr hwnd);

    /// <summary>
    /// 鼠标左键现在是不是还按着。
    ///
    /// 同样必须由提权进程回答：前台是高权限程序时，普通权限进程的
    /// `GetAsyncKeyState` 会返回 0，看起来就像"已经松手"。
    /// </summary>
    bool IsLeftButtonDown();
}
