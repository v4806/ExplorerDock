using System.Windows.Threading;
using ExplorerDock.Interop;
using ExplorerDock.Views;

namespace ExplorerDock.Services;

/// <summary>
/// Win+V 接管的大脑。
///
/// 线程分工和 Alt+Tab 那套完全一样：
/// - 钩子线程只做状态判断（吞键、改几个标志、把动作丢进 UI 队列），绝不做慢活；
/// - UI 线程负责弹面板、写回剪贴板、把焦点还给原窗口并补 Ctrl+V。
///
/// 为什么要吞"Win 抬起"：我们吃掉了 V，系统就以为用户只是单独按了一下 Win，
/// 于是抬起 Win 时开始菜单会弹出来。把这次抬起也吃掉，系统就当这个组合没发生过。
/// 代价是物理按键状态要自己补一次注入的抬起，否则 Win 会卡在按下状态。
/// </summary>
internal sealed class ClipboardController : IDisposable
{
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_V = 0x56;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    private readonly App _app;
    private readonly ClipboardStore _store;
    private readonly KeyboardHook _hook = new();

    private volatile bool _winDown;
    private volatile bool _vSwallowed;
    private volatile bool _swallowWinUp;
    private IntPtr _originForeground;

    public ClipboardController(App app, ClipboardStore store)
    {
        _app = app;
        _store = store;

        Panel = new ClipboardPanel(store);
        Panel.ItemActivated += OnItemActivated;
        Panel.DroppedOnWindow += OnItemDroppedOnWindow;
        Panel.FavoriteRequested += OnFavoriteRequested;
        Panel.DeleteRequested += item => _store.Remove(item);
        Panel.ClearRequested += _app.ClearClipboardHistory;

        _hook.AddHandler(OnKey);
    }

    public ClipboardPanel Panel { get; }

    /// <summary>打开面板之前谁在前台 —— 收面板时要把它还回去。</summary>
    public IntPtr OriginForeground => _originForeground;

    /// <summary>面板是不是正开着（悬浮栏用它避让置顶）。</summary>
    public bool IsActive => Panel.IsVisible;

    public void Start()
    {
        _hook.Install();
        ClipboardMonitor.Log($"clipboard hook installed={_hook.IsInstalled} takeover={App.Settings.ClipboardTakeover}");

        // 预热面板窗口句柄，第一次按 Win+V 就不用等窗口初始化
        _app.Dispatcher.BeginInvoke(new Action(() => Panel.Preload()));
    }

    /// <summary>打开或收起面板（托盘菜单、调试参数也走这里）。</summary>
    public void Toggle()
    {
        try
        {
            if (Panel.IsVisible)
            {
                ClipboardMonitor.Log("toggle -> hiding panel");
                Panel.HidePanel();
                return;
            }

            _originForeground = NativeMethods.GetForegroundWindow();
            Panel.Present();
            ClipboardMonitor.Log($"toggle -> presented visible={Panel.IsVisible} left={Panel.Left} top={Panel.Top} foreground={NativeMethods.GetForegroundWindow()}");
        }
        catch (Exception ex)
        {
            ClipboardMonitor.Log($"toggle failed: {ex.GetType().Name} {ex.Message}");
        }
    }

    // ---------- 钩子线程 ----------

    private bool OnKey(KeyStroke stroke)
    {
        try
        {
            if (!App.Settings.ClipboardTakeover) return false;

            switch (stroke.Vk)
            {
                case VK_LWIN:
                case VK_RWIN:
                    if (stroke.Down)
                    {
                        _winDown = true;
                        return false;   // Win 键本身放行：开始菜单、其它 Win 组合键都不该受影响
                    }

                    _winDown = false;

                    if (!_swallowWinUp) return false;

                    _swallowWinUp = false;

                    // 吞掉这次抬起（系统不该看到它，否则开始菜单会跟着弹），
                    // 再补一次注入的抬起让 Win 键状态闭合 —— 不补的话 Win 会"卡住"，
                    // 之后所有按键都会被当成 Win 组合，面板根本收不到键盘。
                    ClipboardMonitor.Log("win-up swallowed + reinjected");
                    NativeMethods.SendKeyUp((ushort)stroke.Vk);
                    return true;

                case VK_V:
                    if (stroke.Down)
                    {
                        // 长按时 V 会不停重复发 down。这些重复事件必须**继续吞掉**：
                        // 一旦放行，系统就看到了 V，会把它自己那个剪贴板面板也弹出来。
                        // （Alt+Tab 那边就是每次都吞，所以从来没这个毛病）
                        if (_vSwallowed) return true;

                        if (!_winDown) return false;

                        // 只有"纯 Win+V"归我们，Ctrl/Alt/Shift 掺进来的组合照旧给系统
                        if (NativeMethods.IsKeyDown(VK_CONTROL)
                            || NativeMethods.IsKeyDown(VK_MENU)
                            || NativeMethods.IsKeyDown(VK_SHIFT))
                        {
                            return false;
                        }

                        _vSwallowed = true;
                        _swallowWinUp = true;
                        ClipboardMonitor.Log("win+v swallowed -> toggle panel");

                        // 先补一个"见证按键"：不然系统会把随后的 Win 抬起当成单击 Win，弹开始菜单
                        NativeMethods.SendWitnessKey();

                        Post(() => Toggle());
                        return true;
                    }

                    if (_vSwallowed)
                    {
                        _vSwallowed = false;
                        return true;   // V 的抬起也吞掉，别让前台窗口收到半个组合键
                    }

                    return false;

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

    /// <summary>点了某条记录：写回剪贴板 → 收起面板 → 焦点还给原窗口 → 补 Ctrl+V。</summary>
    private void OnItemActivated(ClipItem item)
    {
        var origin = _originForeground;

        // 把"要粘到哪个窗口"传下去：Office 才需要 HTML/RTF 保排版，
        // 聊天软件（QQ）拿到 HTML 反而会去解析里面失效的图片路径，结果什么都不粘
        if (!_app.WriteClipItemToClipboard(item, origin)) return;

        Panel.HidePanel();

        if (origin == IntPtr.Zero || !NativeMethods.IsWindow(origin)) return;

        // 面板抢过焦点，这里把前台还给用户原来的窗口
        NativeMethods.ForceForeground(origin);

        // 切前台是异步的：立刻发按键会落到我们自己或别的窗口上，等一小会儿再补
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            NativeMethods.SendCtrlV();
        };
        timer.Start();
    }

    /// <summary>
    /// 把条目拖到别的软件窗口上松手。
    ///
    /// 这里**不走 OLE 拖放**，而是"写进系统剪贴板 + 给落点窗口补一次 Ctrl+V"：
    /// OLE 拖放时 WPF 只能给目标一个自己转换过的 CF_BITMAP，图片条目的原图字节
    /// （PNG / CF_DIB / EMF）到不了对面，目标看着像收到个文件又不可用，结果什么都没粘上。
    /// 换成粘贴这条路，目标拿到的格式和我们"点一下条目粘贴"完全一致。
    /// </summary>
    private void OnItemDroppedOnWindow(ClipItem item, IntPtr target)
    {
        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target)) return;

        // 资源管理器 / 桌面只认文件：图片先落成临时文件再粘，不然它什么都不收
        if (item.Kind == ClipKind.Image && App.IsFileManagerWindow(target))
        {
            if (!_app.WriteClipItemAsFileDrop(item)) return;
        }
        else if (!_app.WriteClipItemToClipboard(item, target))
        {
            return;
        }

        Panel.HidePanel();

        // 面板刚才是前台，把前台还给落点窗口，否则后续的点击和 Ctrl+V 会打到我们自己身上
        NativeMethods.ForceForeground(target);

        // 目标窗口虽然到了前台，但它的输入框不一定拿到了焦点（QQ 这类尤其明显），
        // 所以先在落点补一次真实点击，再把 Ctrl+V 递进去
        var click = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        click.Tick += (_, _) =>
        {
            click.Stop();

            if (NativeMethods.GetCursorPos(out var point)) NativeMethods.ClickAt(point.X, point.Y);

            var paste = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            paste.Tick += (_, _) =>
            {
                paste.Stop();
                NativeMethods.SendCtrlV();
                Services.ClipboardMonitor.Log($"dropped -> clicked at cursor and sent ctrl+v to {target}");
            };
            paste.Start();
        };
        click.Start();
    }

    private void OnFavoriteRequested(ClipItem item)
    {
        // 第 4 轮换成"弹收藏设置浮窗、选分组"；现在先直接切换收藏状态
        _store.SetFavorited(item, !item.Favorited, item.GroupId);
    }

    public void Dispose()
    {
        try
        {
            Panel.HidePanel();
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


