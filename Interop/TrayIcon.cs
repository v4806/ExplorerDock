using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace ExplorerDock.Interop;

/// <summary>
/// 托盘图标（Shell_NotifyIcon 的直接封装）。
///
/// 为什么不用 WinForms 的 NotifyIcon：就为了一个托盘图标，要把整个 System.Windows.Forms
/// （十几 MB）塞进安装包，不值。这里只做我们用得到的那点事：
/// 加图标、把鼠标消息转成回调、资源管理器重启后自动把图标加回来。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_TRAYICON = 0x8000 + 1;   // WM_APP + 1

    private const uint NIM_ADD = 0;
    private const uint NIM_DELETE = 2;

    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;

    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    private readonly string _tooltip;
    private readonly Action _onRightClick;
    private readonly Action _onDoubleClick;

    private readonly HwndSource? _source;

    /// <summary>图标对象由它持有（HICON 的生命周期跟着它走，不要自己 DestroyIcon）。</summary>
    private System.Drawing.Icon? _iconSource;

    /// <summary>盯资源管理器进程用的定时器（见 CheckShell）。</summary>
    private readonly Timer _watchdog;

    private readonly object _gate = new();

    /// <summary>上一次看到的、且图标已经加成功的资源管理器进程。</summary>
    private uint _shellPid;

    /// <summary>正在重新添加图标（防止定时器/广播一次接一次地起线程）。</summary>
    private int _reAdding;

    /// <summary>上次因为注册消息去补图标的时间（限流用）。</summary>
    private DateTime _lastReAdd = DateTime.MinValue;

    /// <summary>
    /// 图标 ID，固定不变。
    ///
    /// 以前重试时每轮换一个 ID（1、2、3…），想绕过"同 hWnd + 同 uID 的旧记录"。代价是：
    /// 某轮 ADD 成功的那条记录再也不会被删掉，下次补加又从 1 开始，同一个窗口下就攒出
    /// 第二条记录 —— 托盘里显示成两个一模一样的图标。现在永远只用这一个 ID。
    /// </summary>
    private const uint Uid = 1;

    /// <summary>旧版本重试时 ID 最多换到 10；清理残留记录时扫到 16 留点余量。</summary>
    private const uint StaleUidScanTo = 16;

    /// <summary>TaskbarCreated 的注册消息号（每次登录会话固定），只认它，别的注册消息一概不理。</summary>
    private readonly int _taskbarCreated;

    /// <summary>正在退出：别让后台补图标的线程在退出路上又加一个。</summary>
    private volatile bool _disposed;

    public TrayIcon(Window owner, string tooltip, Action onRightClick, Action onDoubleClick)
    {
        _tooltip = tooltip;
        _onRightClick = onRightClick;
        _onDoubleClick = onDoubleClick;

        // 托盘消息得有个窗口来收：直接挂在悬浮栏窗口上，不另建隐藏窗口。
        // 悬浮栏可能从没显示过（设置里 ShowDock=false，启动时就不会 Show()），
        // 这时窗口还没有 HWND，Handle 是 0，FromHwnd(0) 会抛 "Hwnd of zero is not valid."。
        // EnsureHandle() 只在没有时按需创建，不会把窗口显示出来。
        var interop = new WindowInteropHelper(owner);
        var handle = interop.Handle;
        if (handle == IntPtr.Zero) handle = interop.EnsureHandle();

        _source = HwndSource.FromHwnd(handle);

        // 先拿到 TaskbarCreated 的消息号（只认它，原因见 WndProc）。
        _taskbarCreated = unchecked((int)RegisterWindowMessage("TaskbarCreated"));

        _iconSource = LoadAppIcon();

        // 加成功才认这个 shell；失败就先记着，交给 watchdog 或 TaskbarCreated 去补
        if (TryAdd(sweepStaleUids: true)) _shellPid = CurrentShellPid();

        // 钩子要等图标加完再挂：挂早了，构造期间窗口就会收到广播、后台线程和上面这次
        // TryAdd 抢着加图标 —— 以前"启动后几十毫秒又来一次 add"就是这么来的。
        _source?.AddHook(WndProc);

        _watchdog = new Timer(_ => CheckShell(), null, 3000, 3000);
    }

    /// <summary>把图标加进托盘；返回是否成功。</summary>
    /// <param name="sweepStaleUids">
    /// 是否顺手把旧版本可能用过的每个 ID 都删一遍。第一轮/启动时清一次就够，
    /// 后续重试只需要删自己固定那一条。
    /// </param>
    private bool TryAdd(bool sweepStaleUids)
    {
        lock (_gate)
        {
            if (_disposed) return false;

            if (sweepStaleUids)
            {
                // 旧版本重试时会把 ID 往上换（最多到 10），每换一次就是一条独立记录，
                // 同一个窗口下留着两条记录，托盘里就是两个图标。先把这一片 ID 全删掉，
                // 再只加下面这一条 —— 不管之前残留的是哪个 ID，都清得干净。
                DeleteStaleIds();
            }
            else
            {
                // 资源管理器重启后 shell 里可能还留着上一次的记录（同一个 hWnd + uID），
                // 这时候直接 ADD 会被当成"已经存在"而返回失败，图标却不会显示出来
                DeleteData(Uid);
            }

            var data = CreateData(Uid);
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            data.uCallbackMessage = WM_TRAYICON;

            var ok = Shell_NotifyIcon(NIM_ADD, ref data);
            Log($"add uid={Uid} ok={ok} hIcon=0x{data.hIcon.ToInt64():X} hwnd=0x{data.hWnd.ToInt64():X}");

            return ok;
        }
    }

    /// <summary>把旧版本可能用过的每个 ID 都删一遍（删不存在的记录没有副作用）。</summary>
    private void DeleteStaleIds()
    {
        for (uint id = 1; id <= StaleUidScanTo; id++) DeleteData(id);
    }

    private void DeleteData(uint uid)
    {
        var data = CreateData(uid);
        if (Shell_NotifyIcon(NIM_DELETE, ref data)) Log($"del uid={uid} ok");
    }

    private static uint CurrentShellPid()
    {
        try
        {
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return 0;

            GetWindowThreadProcessId(tray, out var pid);
            return pid;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 定期看一眼资源管理器还是不是原来那个进程。
    ///
    /// 它一重启（崩溃，或菜单里点了「重启资源管理器」），托盘就被清空，必须把图标重新加一遍。
    /// </summary>
    private void CheckShell()
    {
        try
        {
            var pid = CurrentShellPid();
            if (pid == 0 || pid == _shellPid) return;

            Log($"shell pid {_shellPid} -> {pid}");
            StartReAdd();
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 收到 TaskbarCreated 时去补一次图标（Watchdog 那套仍然兜底）。
    /// </summary>
    private void ScheduleReAdd()
    {
        if (_disposed) return;
        if ((DateTime.UtcNow - _lastReAdd).TotalSeconds < 3) return;

        _lastReAdd = DateTime.UtcNow;
        StartReAdd();
    }

    /// <summary>在后台线程里反复尝试把图标加回托盘（资源管理器刚起来时托盘可能还没建好）。</summary>
    private void StartReAdd()
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _reAdding, 1, 0) != 0) return;

        var thread = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < 10; i++)
                {
                    if (_disposed) return;

                    // 第一轮顺手清掉旧版本换 ID 时留下的记录；后面几轮只删自己这一条。
                    // ID 必须固定：以前每轮换一个，ADD 成功后那条记录就再也删不掉了，
                    // 下次补加又从 1 开始 → 同一个 hWnd 下两条记录 → 托盘两个图标。
                    if (TryAdd(sweepStaleUids: i == 0))
                    {
                        _shellPid = CurrentShellPid();
                        return;
                    }

                    Thread.Sleep(700);
                }

                Log("re-add gave up after 10 tries");
            }
            catch
            {
                // 忽略
            }
            finally
            {
                Interlocked.Exchange(ref _reAdding, 0);
            }
        })
        {
            IsBackground = true,
            Name = "ExplorerDock.TrayReAdd",
        };

        thread.Start();
    }

    private NOTIFYICONDATA CreateData(uint uid) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _source?.Handle ?? IntPtr.Zero,
        uID = uid,
        hIcon = _iconSource?.Handle ?? IntPtr.Zero,
        szTip = _tooltip,
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            // 老式托盘消息：lParam 的低 16 位是鼠标消息
            var mouse = lParam.ToInt32() & 0xFFFF;

            if (mouse == WM_RBUTTONUP)
            {
                handled = true;
                _onRightClick();
            }
            else if (mouse == WM_LBUTTONDBLCLK)
            {
                handled = true;
                _onDoubleClick();
            }

            return IntPtr.Zero;
        }

        if (_taskbarCreated != 0 && msg == _taskbarCreated)
        {
            // 资源管理器（重新）启动后，会把 TaskbarCreated 广播给所有顶层窗口。
            // 以前是"0xC000 以上的消息都当 TaskbarCreated"（注释里说注册号对不上），
            // 结果窗口被各种程序的注册消息广播刷屏（日志里一片 0xC252/0xC1E6），
            // 每次都白补一遍图标；补的过程中还可能与别的路径抢着加，偶发两个托盘图标。
            // 现在只认准 TaskbarCreated 这一条，其它注册消息一概不理。
            Log("TaskbarCreated");
            ScheduleReAdd();
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 图标取自程序自身（csproj 里的 ApplicationIcon）；取不到就用系统默认图标。
    ///
    /// 用 System.Drawing 的 ExtractAssociatedIcon 而不是 user32 的 LoadImage(LR_LOADFROMFILE)：
    /// 后者对 exe 取回来的 HICON 是空的，托盘上就是一块透明。
    /// </summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;

            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch
        {
            // 取不到就退回系统图标
        }

        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _disposed = true;

        try
        {
            _watchdog.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            // 和 TryAdd 共用一把锁：后台补图标线程若正好在 ADD，等它走完再删，
            // 免得删完又被它加回去。
            lock (_gate)
            {
                DeleteData(Uid);
            }
        }
        catch
        {
            // 忽略
        }

        try
        {
            _source?.RemoveHook(WndProc);
        }
        catch
        {
            // 忽略
        }

        try
        {
            _iconSource?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _iconSource = null;
    }

    private static int _logCount;

    /// <summary>托盘消息的小日志（%TEMP%\ExplorerDock.tray.log）：托盘不听话时唯一能看的东西。</summary>
    private static void Log(string message)
    {
        if (Interlocked.Increment(ref _logCount) > 200) return;

        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "ExplorerDock.tray.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 忽略
        }
    }
}
