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
    private readonly KeyboardHook _hook = new();

    private AltTabOverlay? _overlay;
    private List<AltTabWindowInfo> _items = new();
    private int _index;
    private volatile bool _active;
    private volatile bool _opening;
    private volatile bool _altDown;
    private volatile bool _shiftDown;
    private volatile bool _cancelPending;
    private bool _commitPending;
    private int _pendingTabs;
    private IntPtr _originForeground;

    public AltTabController(App app)
    {
        _app = app;
        _hook.AddHandler(OnKey);
    }

    /// <summary>面板是不是正开着（悬浮栏要用它避让置顶）。</summary>
    public bool IsActive => _active;

    public bool IsInstalled => _hook.IsInstalled;

    public void Start()
    {
        _hook.Install();

        Log($"start installed={_hook.IsInstalled} injected={KeyboardHook.AllowInjected} takeover={App.Settings.AltTabTakeover}");

        // 预热面板窗口句柄，让第一次按 Alt+Tab 不用等窗口初始化
        _app.Dispatcher.BeginInvoke(new Action(() => EnsureOverlay()));
    }

    /// <summary>诊断日志：写到 %TEMP%\ExplorerDock.alttab.log，超过 200KB 就不再写。</summary>
    internal static void Log(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "ExplorerDock.alttab.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 200_000) return;

            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败绝不影响功能
        }
    }

    /// <summary>立刻收起面板（关掉接管开关、程序退出时用）。</summary>
    public void CancelNow() => Post(Cancel);

    // ---------- 钩子线程 ----------

    private bool OnKey(KeyStroke stroke)
    {
        try
        {
            if (!App.Settings.AltTabTakeover) return false;

            switch (stroke.Vk)
            {
                case KeyboardHook.VK_LSHIFT:
                case KeyboardHook.VK_RSHIFT:
                    _shiftDown = stroke.Down;
                    return false;

                case KeyboardHook.VK_LMENU:
                case KeyboardHook.VK_RMENU:
                    _altDown = stroke.Down;

                    // Alt 单独按下必须放行，否则 Alt 菜单、Alt+Space 全废
                    if (stroke.Down) return false;
                    if (!_active && !_opening) return false;

                    // 我们要吞掉这个"Alt 抬起"，可物理键盘状态得让它闭合，
                    // 否则 Alt 会一直卡在按下状态 —— 表现就是之后打字全变快捷键、按键错乱。
                    // 补发必须在这里做：UI 那条路上有好几个提前返回的分支（切回原窗口、列表为空、
                    // 正在数窗口……），从那儿溜走就再没人补发了。补发事件带 INJECTED 标志，不会再进本钩子。
                    NativeMethods.RestoreAltKeyState();

                    // 只吞键、不动 _active：真正的收尾交给 UI 线程的 Commit 做。
                    // （在这里置 false 的话，Commit 一看"面板没开"就直接早退了，面板永远关不掉）
                    Post(Commit);
                    return true;

                case KeyboardHook.VK_TAB:
                    // Alt 的状态优先信自己跟踪的：注入或远程使用场景下 flags 未必带 ALTDOWN
                    if (!(stroke.AltDown || _altDown) && !_active) return false;   // 单独按 Tab 与我们无关

                    // 全屏应用（游戏等）在前台时可以选择让系统自己处理，免得用户切不出来
                    if (stroke.Down && !_active
                        && App.Settings.AltTabFullscreenPassthrough
                        && IsForegroundFullscreen())
                    {
                        return false;
                    }

                    if (stroke.Down) Post(Advance);
                    return true;   // 抬起也吞：别让前台窗口收到半个组合键

                case KeyboardHook.VK_ESCAPE:
                    if (!stroke.Down || !_active) return false;

                    _cancelPending = true;
                    Post(Cancel);
                    return true;

                default:
                    return false;
            }
        }
        catch
        {
            // 钩子里出任何岔子都当没接管，绝不能影响键盘
            return false;
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

    // ---------- UI 线程 ----------

    private void Advance()
    {
        if (_opening)
        {
            // 窗口列表还没数完，先把这次按键记下来
            _pendingTabs += _shiftDown ? -1 : 1;
            return;
        }

        if (!_active)
        {
            Open();
            return;
        }

        Move(_shiftDown ? -1 : 1);
    }

    private void Open()
    {
        _opening = true;
        _pendingTabs = 0;
        _cancelPending = false;
        _commitPending = false;
        _originForeground = NativeMethods.GetForegroundWindow();

        var foreground = _originForeground;

        var thread = new Thread(() =>
        {
            List<AltTabWindowInfo> items;

            try
            {
                items = AltTabWindowList.Snapshot();
                foreach (var item in items) item.Icon = ShellInterop.GetWindowIcon(item.Handle);
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

        bool cancelled = _cancelPending;
        _cancelPending = false;

        if (cancelled || items.Count == 0)
        {
            _commitPending = false;
            _pendingTabs = 0;
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
            Activate(items[target].Handle);
            return;
        }

        _items = items;
        _index = Wrap(InitialIndex(items, foreground) + _pendingTabs, items.Count);
        _pendingTabs = 0;

        // 先置位再弹：否则紧接着的 Tab 会以为面板还没开，又走一遍 Open
        _active = true;

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

        var target = _index >= 0 && _index < _items.Count ? _items[_index].Handle : IntPtr.Zero;

        StopThumbnails();
        _overlay?.Dismiss();

        if (target == IntPtr.Zero) return;

        // 面板显示期间用户用鼠标点了别的窗口：前台已经换了，我们别去抢
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != _originForeground && !IsSelf(foreground)) return;

        if (target == foreground) return;   // Tab 转了一圈又回到原窗口

        Activate(target);
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

        StopThumbnails();
        _overlay?.Dismiss();
    }

    private void OnHovered(int index) => MoveTo(index);

    private void OnClicked(int index)
    {
        MoveTo(index);
        Commit();
    }

    private void Activate(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) return;

        NativeMethods.ForceForeground(hwnd);
        NativeMethods.RestoreAltKeyState();
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

            _thumbnailHost ??= new AltTabThumbnailHost();

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

    /// <summary>
    /// 缩略图留边处的底色：把半透明的"缩略图底色"合成到卡片底色上，
    /// 这样等比缩放留下的边条跟卡片本身一个色，不会出现突兀的方块。
    /// </summary>
    private static System.Windows.Media.Color ThumbnailBackground()
    {
        var palette = ThemePalette.Resolve();

        System.Windows.Media.Color top = palette.ThumbBack;
        System.Windows.Media.Color bottom = palette.Hover;

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
        _overlay.Preload();
        return _overlay;
    }

    private static int InitialIndex(IReadOnlyList<AltTabWindowInfo> items, IntPtr foreground)
    {
        // 列表是 Z 序，当前窗口通常是第 0 个；系统默认选中"上一个用过的窗口"，
        // 也就是当前窗口的下一个
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Handle == foreground) return (i + 1) % items.Count;
        }

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

    private static bool IsForegroundFullscreen()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            if (!NativeMethods.TryGetMonitorRect(hwnd, out var monitor)) return false;
            if (!NativeMethods.GetWindowRect(hwnd, out var bounds)) return false;

            return bounds.Left <= monitor.Left + 1
                && bounds.Top <= monitor.Top + 1
                && bounds.Right >= monitor.Right - 1
                && bounds.Bottom >= monitor.Bottom - 1;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            _active = false;
            _opening = false;
            _cancelPending = true;
            _commitPending = false;

            StopThumbnails();
            _overlay?.Dismiss();
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

        try
        {
            _hook.Dispose();
        }
        catch
        {
            // 忽略
        }
    }
}
