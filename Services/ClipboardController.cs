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
    private readonly App _app;
    private readonly ClipboardStore _store;

    private IntPtr _originForeground;

    public ClipboardController(App app, ClipboardStore store)
    {
        _app = app;
        _store = store;

        Panel = new ClipboardPanel(store);
        Panel.ItemActivated += OnItemActivated;
        Panel.DroppedOnWindow += OnItemDroppedOnWindow;
        Panel.DroppedOnOwnSurface += OnItemDroppedOnOwnSurface;
        Panel.FavoriteRequested += OnFavoriteRequested;
        Panel.DeleteRequested += item => _store.Remove(item);
        Panel.ClearRequested += _app.ClearClipboardHistory;

        // Win+V 由提权进程里的钩子接管（管理员程序占前台时，普通权限的钩子收不到按键），
        // 这边只响应它推过来的动作
        _app.WindowHost.KeyAction += OnKeyAction;
    }

    public ClipboardPanel Panel { get; }

    /// <summary>打开面板之前谁在前台 —— 收面板时要把它还回去。</summary>
    public IntPtr OriginForeground => _originForeground;

    /// <summary>面板是不是正开着（悬浮栏用它避让置顶）。</summary>
    public bool IsActive => Panel.IsVisible;

    public void Start()
    {
        ClipboardMonitor.Log($"clipboard takeover={App.Settings.ClipboardTakeover} installed={_app.WindowHost.KeyboardInstalled}");

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

    // ---------- 按键动作（由提权进程里的钩子推过来） ----------

    /// <summary>
    /// Win+V 的判定（纯 Win+V、补见证键、吞 Win 抬起）全在提权进程那边做完了，
    /// 这里只要把面板切一下。
    /// </summary>
    private void OnKeyAction(string action)
    {
        if (action != HostProtocol.KeyToggleClipboard) return;
        if (!App.Settings.ClipboardTakeover) return;

        Post(Toggle);
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
        _app.WindowHost.Activate(origin, -1);

        // 切前台是异步的：立刻发按键会落到我们自己或别的窗口上，等一小会儿再补
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _app.WindowHost.SendPaste();
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
        _app.WindowHost.Activate(target, -1);

        // 目标窗口虽然到了前台，但它的输入框不一定拿到了焦点（QQ 这类尤其明显），
        // 所以先在落点补一次真实点击，再把 Ctrl+V 递进去
        var click = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        click.Tick += (_, _) =>
        {
            click.Stop();

            if (NativeMethods.GetCursorPos(out var point)) _app.WindowHost.ClickAtCursor();

            var paste = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            paste.Tick += (_, _) =>
            {
                paste.Stop();
                _app.WindowHost.SendPaste();
                Services.ClipboardMonitor.Log($"dropped -> clicked at cursor and sent ctrl+v to {target}");
            };
            paste.Start();
        };
        click.Start();
    }

    /// <summary>
    /// 拖着条目落在我们自己的界面上松手：悬浮栏按钮就粘到那个窗口（多标签窗口会先切到对应标签页），
    /// Alt+Tab 卡片则是"粘到这张卡最后活动的那个窗口"。没落在按钮/卡片上就什么都不做。
    /// </summary>
    private void OnItemDroppedOnOwnSurface(ClipItem item, int screenX, int screenY)
    {
        _app.PasteClipItemOnOwnSurface(item, screenX, screenY);
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
            // 钩子在提权进程那边，这里只要退订
            _app.WindowHost.KeyAction -= OnKeyAction;
        }
        catch
        {
            // 忽略
        }
    }
}


