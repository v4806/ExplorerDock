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
    /// 图标 ID。正常就是 1；重试时会往上换 —— 万一卡着的是"同 hWnd + 同 uID 的旧记录"，
    /// 换个 ID 就能加进去。
    /// </summary>
    private uint _uid = 1;

    public TrayIcon(Window owner, string tooltip, Action onRightClick, Action onDoubleClick)
    {
        _tooltip = tooltip;
        _onRightClick = onRightClick;
        _onDoubleClick = onDoubleClick;

        // 托盘消息得有个窗口来收：直接挂在悬浮栏窗口上，不另建隐藏窗口
        var handle = new WindowInteropHelper(owner).Handle;

        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);

        RegisterWindowMessage("TaskbarCreated");

        _iconSource = LoadAppIcon();

        // 加成功才认这个 shell；失败就先记着，交给 watchdog 或注册消息去补
        if (TryAdd()) _shellPid = CurrentShellPid();

        _watchdog = new Timer(_ => CheckShell(), null, 3000, 3000);
    }

    /// <summary>把图标加进托盘；返回是否成功。</summary>
    private bool TryAdd()
    {
        lock (_gate)
        {
            var data = CreateData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            data.uCallbackMessage = WM_TRAYICON;

            // 先删一遍：资源管理器重启后 shell 里可能还留着上一次的记录（同一个 hWnd + uID），
            // 这时候直接 ADD 会被当成"已经存在"而返回失败，图标却不会显示出来
            Shell_NotifyIcon(NIM_DELETE, ref data);

            var ok = Shell_NotifyIcon(NIM_ADD, ref data);
            Log($"add ok={ok} hIcon=0x{data.hIcon.ToInt64():X} hwnd=0x{data.hWnd.ToInt64():X}");

            return ok;
        }
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
    /// 收到注册消息（TaskbarCreated 之类）时去补一次图标。
    ///
    /// 为什么不认准 TaskbarCreated 那个消息号：实测我们自己注册到的是 0xC0BD，
    /// 而资源管理器起来时广播过来的是 0xC110，对不上（原因不明）。
    /// 干脆凡是从这个区间来的消息都当"shell 可能刚起来"，顺手补一次 —— 多补几次没有副作用。
    /// </summary>
    private void ScheduleReAdd()
    {
        if ((DateTime.UtcNow - _lastReAdd).TotalSeconds < 3) return;

        _lastReAdd = DateTime.UtcNow;
        StartReAdd();
    }

    /// <summary>在后台线程里反复尝试把图标加回托盘（资源管理器刚起来时托盘可能还没建好）。</summary>
    private void StartReAdd()
    {
        if (Interlocked.CompareExchange(ref _reAdding, 1, 0) != 0) return;

        var thread = new Thread(() =>
        {
            try
            {
                var pid = CurrentShellPid();

                for (var i = 0; i < 10; i++)
                {
                    // 每轮换个图标 ID：万一是"同 hWnd + 同 uID 的旧记录"卡着，换一个就能加进去
                    _uid = (uint)(i + 1);

                    if (TryAdd())
                    {
                        if (pid != 0) _shellPid = pid;
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

    private NOTIFYICONDATA CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _source?.Handle ?? IntPtr.Zero,
        uID = _uid,
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

        if (msg >= 0xC000)
        {
            // 注册消息：可能是"任务栏重建了"，趁机补一次图标
            Log($"regmsg=0x{msg:X}");
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
            var data = CreateData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
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
