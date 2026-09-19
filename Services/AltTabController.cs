using System.IO;
using System.Threading;
using ExplorerDock.Interop;
using ExplorerDock.Views;

namespace ExplorerDock.Services;

/// <summary>
/// Alt+Tab 接管的大脑。
///
/// 线程分工：
/// - 钩子线程只调 <see cref="OnKey"/>：看一眼按键、改几个 volatile 标志、把动作丢进 UI 队列就返回，
///   绝不做枚举/抓图这类慢活（钩子卡住 = 整个系统的键盘卡住）；
/// - UI 线程负责真正的弹面板、算选中项、切窗口；
/// - 另外两个后台线程分别数窗口列表（含取图标）和抓缩略图。
/// </summary>
internal sealed class AltTabController : IDisposable
{
    private readonly App _app;
    private AltTabOverlay? _overlay;
    private List<AltTabWindowInfo> _items = new();
    private int _index;
    private volatile bool _active;
    private volatile bool _opening;
    private volatile bool _cancelPending;
    private bool _commitPending;

    /// <summary>这一轮列表是不是"只含当前进程"（Alt+~ 打开的）。</summary>
    private volatile bool _processScoped;
    private int _pendingTabs;
    private IntPtr _originForeground;

    /// <summary>拖放悬停已经激活过哪张卡（DragOver 触发很密，同一张卡只抢一次前台）。</summary>
    private int _dragHoverIndex = -1;

    public AltTabController(App app)
    {
        _app = app;

        // 按键由提权进程里的钩子接管（管理员程序占前台时，普通权限的钩子收不到按键），
        // 这边只负责响应它推过来的"动作"
        _app.WindowHost.KeyAction += OnKeyAction;
    }

    /// <summary>面板是不是正开着（悬浮栏要用它避让置顶）。</summary>
    public bool IsActive => _active;

    /// <summary>界面是否正在接管中（面板开着，或窗口列表还在数）。</summary>
    public bool PanelBusy => _active || _opening;

    public bool IsInstalled => _app.WindowHost.KeyboardInstalled;

    public void Start()
    {
        _app.PushKeyState();

        Log($"start installed={IsInstalled} injected={KeyboardHook.AllowInjected} takeover={App.Settings.AltTabTakeover}");

        // 预热面板窗口句柄，让第一次按 Alt+Tab 不用等窗口初始化
        _app.Dispatcher.BeginInvoke(new Action(() => EnsureOverlay()));
    }

    /// <summary>
    /// 诊断日志：写到 %TEMP%\ExplorerDock.alttab.log（功能进程写 ExplorerDock.host.log）。
    /// 超过 200KB 就不再写。
    ///
    /// 两个进程各写各的文件：同一个文件被两个进程同时追加会互相抢，日志会丢行。
    /// </summary>
    internal static void Log(string message)
    {
        try
        {
            lock (LogGate)
            {
                var path = Path.Combine(Path.GetTempPath(), LogFileName);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 200_000) return;

                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败绝不影响功能
        }
    }

    private static readonly object LogGate = new();

    private static readonly string LogFileName =
        Environment.GetCommandLineArgs().Any(a => a.Equals("--host", StringComparison.OrdinalIgnoreCase))
            ? "ExplorerDock.host.log"
            : "ExplorerDock.alttab.log";

    /// <summary>立刻收起面板（关掉接管开关、程序退出时用）。</summary>
    public void CancelNow() => Post(Cancel);

    // ---------- 按键动作（由提权进程里的钩子推过来） ----------

    /// <summary>
    /// 键盘接管发生在提权进程那边，这里只做界面该做的事：
    /// 弹面板 / 移动选中 / 提交切换 / 取消。
    /// </summary>
    private void OnKeyAction(string action)
    {
        switch (action)
        {
            case HostProtocol.KeyAdvance:
                Post(() => Advance(1));
                break;

            case HostProtocol.KeyAdvanceBack:
                Post(() => Advance(-1));
                break;

            case HostProtocol.KeyAdvanceProcess:
                Post(() => AdvanceInProcess(1));
                break;

            case HostProtocol.KeyAdvanceProcessBack:
                Post(() => AdvanceInProcess(-1));
                break;

            case HostProtocol.KeyCommit:
                Post(Commit);
                break;

            case HostProtocol.KeyCancel:
                // 先置位再排队：窗口列表还在后台数的时候也要能取消掉
                _cancelPending = true;
                Post(Cancel);
                break;
        }
    }

    private void Post(Action action)
    {
        try
        {
            _app.Dispatcher.BeginInvoke(action);
        }
        catch
        {
            // 程序正在退出
        }
    }

    /// <summary>把"界面是否正在接管中"告诉提权进程 —— 钩子要靠它判断 Alt 抬起怎么处理。</summary>
    private void SyncKeyState() => _app.PushKeyState();

    // ---------- UI 线程 ----------

    /// <summary>方向由钩子那边定（Shift 的状态只有它知道）：+1 往后，-1 往前。</summary>
    private void Advance(int delta)
    {
        if (_opening)
        {
            // 窗口列表还没数完，先把这次按键记下来
            _pendingTabs += delta;
            return;
        }

        if (!_active)
        {
            Open(false);
            return;
        }

        Move(delta);
    }

    /// <summary>Alt+~：在同一个进程名的窗口之间切换。</summary>
    private void AdvanceInProcess(int delta)
    {
        if (_opening)
        {
            // 窗口列表还没数完，先把这次按键记下来
            _pendingTabs += delta;
            return;
        }

        if (!_active)
        {
            Open(true);
            return;
        }

        // 面板已经开着：跟 Tab 一样只在当前这份列表里移动，不中途换列表
        Move(delta);
    }

    /// <summary>
    /// 打开切换面板。
    /// currentProcessOnly = true 时列表只含"当前前台窗口所属进程"的窗口（Alt+~）。
    /// </summary>
    private void Open(bool currentProcessOnly)
    {
        _opening = true;
        SyncKeyState();
        _pendingTabs = 0;
        _cancelPending = false;
        _commitPending = false;
        _processScoped = currentProcessOnly;
        _originForeground = NativeMethods.GetForegroundWindow();

        var foreground = _originForeground;

        var thread = new Thread(() =>
        {
            List<AltTabWindowInfo> items;

            try
            {
                items = currentProcessOnly
                    ? _app.WindowHost.ProcessCards(foreground)
                    : _app.WindowHost.SnapshotCards();
            }
            catch
            {
                items = new List<AltTabWindowInfo>();
            }

            Post(() => FinishOpen(items, foreground));
        })
        {
            IsBackground = true,
            Name = "ExplorerDock.AltTabList",
            Priority = ThreadPriority.AboveNormal,
        };

        thread.Start();
    }

    private void FinishOpen(List<AltTabWindowInfo> items, IntPtr foreground)
    {
        _opening = false;

        // 不管后面走哪条分支，先把"已经不在 opening 了"同步给宿主。
        //
        // 少了这一句，"打开面板的过程中就松手"那条路（下面直接切换、不弹面板）会漏掉同步：
        // 宿主还记着 panelOpen=true，于是 ESC 被它一直吞掉 —— 用户报的"ESC 有时候卡住按了没反应"。
        SyncKeyState();

        bool processScoped = _processScoped;
        _processScoped = false;

        bool cancelled = _cancelPending;
        _cancelPending = false;

        // Alt+~ 而当前进程只有一个窗口：没有可切换的目标，连面板都不用弹
        if (cancelled || items.Count == 0 || (processScoped && items.Count < 2))
        {
            _commitPending = false;
            _pendingTabs = 0;
            SyncKeyState();
            return;
        }

        // 用户在这几十毫秒里已经松手了：直接完成这次切换，不弹面板（照系统的行为，松手就切）
        if (_commitPending)
        {
            _commitPending = false;

            int pending = _pendingTabs;
            _pendingTabs = 0;
            _items = items;

            int target = Wrap(InitialIndex(items, foreground) + pending, items.Count);
            Activate(items[target].Handle, items[target].TabIndex);
            return;
        }

        _items = items;
        _index = Wrap(InitialIndex(items, foreground) + _pendingTabs, items.Count);
        _pendingTabs = 0;

        // 先置位再弹：否则紧接着的 Tab 会以为面板还没开，又走一遍 Open
        _active = true;
        SyncKeyState();

        EnsureOverlay().Present(items, _index);
        StartThumbnails();
    }

    private void Move(int delta)
    {
        if (_items.Count == 0) return;

        _index = Wrap(_index + delta, _items.Count);
        _overlay?.SetSelection(_index);
    }

    private void MoveTo(int index)
    {
        if (!_active || index < 0 || index >= _items.Count) return;

        _index = index;
        _overlay?.SetSelection(index);
    }

    private void Commit()
    {
        if (_opening)
        {
            // 列表还在后台数，等 FinishOpen 收尾
            _commitPending = true;
            return;
        }

        if (!_active) return;

        _active = false;
        _commitPending = false;
        _pendingTabs = 0;
        _dragHoverIndex = -1;
        SyncKeyState();

        var item = _index >= 0 && _index < _items.Count ? _items[_index] : null;
        var target = item?.Handle ?? IntPtr.Zero;
        int tabIndex = item?.TabIndex ?? -1;

        StopThumbnails();
        _overlay?.Dismiss();

        if (target == IntPtr.Zero) return;

        // 面板显示期间用户用鼠标点了别的窗口：前台已经换了，我们别去抢
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != _originForeground && !IsSelf(foreground)) return;

        // Tab 转了一圈又回到原窗口、而且就是当前那个标签页
        if (target == foreground && tabIndex < 0) return;

        Activate(target, tabIndex);
    }

    private void Cancel()
    {
        if (_opening)
        {
            _cancelPending = true;
            return;
        }

        if (!_active) return;

        _active = false;
        _cancelPending = false;
        _pendingTabs = 0;
        _dragHoverIndex = -1;
        SyncKeyState();

        StopThumbnails();
        _overlay?.Dismiss();
    }

    private void OnHovered(int index) => MoveTo(index);

    /// <summary>
    /// 屏幕坐标落在哪张卡上：拖放落点判定（悬浮栏那边问 DockWindow.TryGetItemAt）。
    ///
    /// 返回的句柄就是"这张卡会切过去的窗口"—— 合并卡取的是组内 Z 序最前的那一个，
    /// 也就是这个程序最后活动过的窗口，正是粘贴该去的地方。
    /// </summary>
    public bool TryGetCardAt(int screenX, int screenY, out IntPtr hwnd, out int tabIndex)
    {
        hwnd = IntPtr.Zero;
        tabIndex = -1;

        if (!_active || _overlay is null) return false;
        if (!_overlay.TryGetCardAt(screenX, screenY, out int index)) return false;
        if (index < 0 || index >= _items.Count) return false;

        var item = _items[index];

        hwnd = item.Handle;
        tabIndex = item.TabIndex;

        return hwnd != IntPtr.Zero;
    }

    /// <summary>
    /// 拖着内容悬停在某张卡上：把那张卡对应的窗口切到前台（与悬浮栏按钮的悬停呼出同一个行为）。
    ///
    /// 只切窗口、不切标签页：拖放悬停触发得很密，切标签走 UI Automation 会拖慢拖动；
    /// 真正落到卡片上时（<see cref="OnCardDropped"/>）才切到这张卡的那个标签页。
    /// 面板本身是 WS_EX_NOACTIVATE，不会被激活，也就不存在"面板和窗口抢前台"。
    /// </summary>
    private void OnCardDragHovered(int index)
    {
        if (!_active || index < 0 || index >= _items.Count) return;
        if (_dragHoverIndex == index) return;

        var item = _items[index];
        if (item.Handle == IntPtr.Zero) return;

        _dragHoverIndex = index;

        Log($"card drag hover -> activate hwnd=0x{item.Handle.ToInt64():X}");
        _app.WindowHost.Activate(item.Handle, -1);
    }

    /// <summary>
    /// 拖着内容落到某张卡上：写进系统剪贴板（不进本软件的剪贴板历史）→ 粘到那张卡的窗口。
    ///
    /// 故意**不收起面板**：面板由"松开 Alt"驱动关闭（用户要求拖放期间别把它关掉），
    /// 而且这时目标窗口已经被悬停激活过，前台是对的。
    /// </summary>
    private void OnCardDropped(int index, System.Windows.IDataObject data)
    {
        if (!_active || index < 0 || index >= _items.Count) return;

        var item = _items[index];
        if (item.Handle == IntPtr.Zero) return;

        _dragHoverIndex = -1;

        bool written = _app.WriteDroppedDataToClipboard(data);

        if (!written)
        {
            Log($"card drop: clipboard write failed (index={index})");
            return;
        }

        Log($"card drop -> paste hwnd=0x{item.Handle.ToInt64():X} tab={item.TabIndex}");
        WindowPaste.Deliver(_app, item.Handle, item.TabIndex);
    }

    private void OnClicked(int index)
    {
        MoveTo(index);
        Commit();
    }

    private void Activate(IntPtr hwnd, int tabIndex)
    {
        if (!NativeMethods.IsWindow(hwnd)) return;

        // 激活窗口（必要时切标签页）交给窗口宿主：活动窗口是管理员程序时，
        // 不提权这一步会直接失效
        _app.WindowHost.Activate(hwnd, tabIndex);

        // 这个补发也得由提权进程发，否则管理员窗口收不到、Alt 会卡在按下状态
        _app.WindowHost.RestoreAltKey();
    }

    // ---------- 缩略图 ----------

    private AltTabThumbnailHost? _thumbnailHost;

    /// <summary>
    /// 面板显示之后，把每张卡对应的窗口缩略图挂上去。
    ///
    /// 走 DWM 的实时缩略图（DwmRegisterThumbnail，系统任务栏预览用的同一套），不再截图：
    /// PrintWindow 对最小化的窗口只能拿到任务栏上那条 160×28 的小图、甚至全黑，
    /// DWM 的缩略图则是实时渲染的，最小化的窗口照样有真实画面。
    /// 注册一次就够，之后由 DWM 自己刷新。
    /// </summary>
    private void StartThumbnails()
    {
        // 卡片位置要等布局算完，所以压到 Loaded 优先级再摆
        _app.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_active || _overlay is null) return;

            if (_thumbnailHost is null)
            {
                _thumbnailHost = new AltTabThumbnailHost
                {
                    DragOverForward = e => _overlay?.ForwardDragOver(e),
                    DropForward = e => _overlay?.ForwardDrop(e),
                    WheelForward = e => _overlay?.ForwardMouseWheel(e),
                };
            }

            var slots = _overlay.GetThumbnailSlots();

            if (slots.Count == 0)
            {
                _thumbnailHost.Hide();
                return;
            }

            _thumbnailHost.Show(slots, ThumbnailBackground());
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void StopThumbnails() => _thumbnailHost?.Hide();

    // ---------- 面板滚动 ----------

    private readonly object _scrollGate = new();
    private Timer? _scrollTimer;

    /// <summary>
    /// 面板滚动了。DWM 缩略图贴的是屏幕坐标，不会跟着 WPF 滚动走，
    /// 所以先把它们收掉（免得错位），停手 80ms 之后再按新的位置重挂一遍。
    /// </summary>
    private void OnScrolled()
    {
        StopThumbnails();

        lock (_scrollGate)
        {
            _scrollTimer?.Dispose();
            _scrollTimer = new Timer(_ => Post(StartThumbnails), null, 80, Timeout.Infinite);
        }
    }

    private void StopScrollTimer()
    {
        lock (_scrollGate)
        {
            _scrollTimer?.Dispose();
            _scrollTimer = null;
        }
    }

    /// <summary>
    /// 缩略图留边处的底色。
    ///
    /// 宿主窗口是不透明的 Win32 窗口（DWM 缩略图不接受分层窗口），所以这里得自己算合成结果：
    /// 面板底色（不透明化）→ 叠上卡片底色 ThumbBack → 再叠上缩略图自己的底色。
    /// </summary>
    private static System.Windows.Media.Color ThumbnailBackground()
    {
        var palette = ThemePalette.Resolve();

        var surface = System.Windows.Media.Color.FromRgb(
            palette.Background.R, palette.Background.G, palette.Background.B);

        return Over(palette.ThumbBack, Over(palette.ThumbBack, surface));
    }

    /// <summary>把半透明的 top 叠在不透明底色 bottom 上。</summary>
    private static System.Windows.Media.Color Over(System.Windows.Media.Color top, System.Windows.Media.Color bottom)
    {
        double alpha = top.A / 255.0;

        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Round(top.R * alpha + bottom.R * (1 - alpha)),
            (byte)Math.Round(top.G * alpha + bottom.G * (1 - alpha)),
            (byte)Math.Round(top.B * alpha + bottom.B * (1 - alpha)));
    }

    // ---------- 工具 ----------

    private AltTabOverlay EnsureOverlay()
    {
        if (_overlay is not null) return _overlay;

        _overlay = new AltTabOverlay();
        _overlay.Hovered += OnHovered;
        _overlay.Clicked += OnClicked;
        _overlay.Scrolled += OnScrolled;
        _overlay.Dropped += OnCardDropped;
        _overlay.DragHovered += OnCardDragHovered;
        _overlay.Preload();
        return _overlay;
    }

    private static int InitialIndex(IReadOnlyList<AltTabWindowInfo> items, IntPtr foreground)
    {
        // 列表是 Z 序，当前窗口通常是第 0 项；系统默认选中"上一个用过的窗口"，
        // 也就是当前项的下一个。多标签窗口还要认到"现在显示的是哪个标签页"。
        int sameWindow = -1;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Handle != foreground) continue;

            if (sameWindow < 0) sameWindow = i;

            if (items[i].TabIndex < 0 || items[i].TabSelected) return (i + 1) % items.Count;
        }

        if (sameWindow >= 0) return (sameWindow + 1) % items.Count;

        return 0;
    }

    private static int Wrap(int value, int count)
    {
        if (count <= 0) return 0;

        value %= count;
        return value < 0 ? value + count : value;
    }

    private static bool IsSelf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid == Environment.ProcessId;
    }

    public void Dispose()
    {
        try
        {
            _active = false;
            _opening = false;
            _cancelPending = true;
            _commitPending = false;

            StopScrollTimer();
            StopThumbnails();
            _overlay?.Dismiss();
        }
        catch
        {
            // 忽略
        }

        try
        {
            // 钩子在提权进程那边，这里只要退订
            _app.WindowHost.KeyAction -= OnKeyAction;
        }
        catch
        {
            // 忽略
        }

        try
        {
            _thumbnailHost?.Dispose();
            _thumbnailHost = null;
        }
        catch
        {
            // 忽略
        }
    }
}
