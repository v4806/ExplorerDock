using System.Runtime.InteropServices;
using System.Threading;

namespace ExplorerDock.Interop;

/// <summary>一次键盘事件（钩子线程里解析出来的最小信息）。</summary>
internal readonly struct KeyStroke
{
    public KeyStroke(int vk, bool down, bool altDown, bool injected)
    {
        Vk = vk;
        Down = down;
        AltDown = altDown;
        Injected = injected;
    }

    /// <summary>虚拟键码。</summary>
    public int Vk { get; }

    /// <summary>true = 按下，false = 抬起。</summary>
    public bool Down { get; }

    /// <summary>事件发生时 Alt 是否按着（系统在 flags 里直接给了）。</summary>
    public bool AltDown { get; }

    /// <summary>是不是模拟出来的按键。</summary>
    public bool Injected { get; }
}

/// <summary>
/// 全局低级键盘钩子（WH_KEYBOARD_LL）。唯一目的：把 Alt+Tab 序列截下来交给切换器。
///
/// 两条铁律：
/// 1) 回调里只做状态判断 —— 超过系统的 LowLevelHooksTimeout（默认 300ms）会被摘钩，
///    而这个钩子是全局的，卡住它等于卡住整个系统的键盘输入；
/// 2) 任何异常都必须落到 CallNextHookEx —— 宁可这次不接管，也不能让键盘失灵。
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const uint PM_NOREMOVE = 0x0000;
    private const uint WM_QUIT = 0x0012;
    private const uint LLKHF_ALTDOWN = 0x20;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    public const int VK_TAB = 0x09;
    public const int VK_ESCAPE = 0x1B;

    /// <summary>` / ~ 键（Alt+~ 用来在同一个进程的窗口之间切换）。</summary>
    public const int VK_OEM_3 = 0xC0;

    public const int VK_LMENU = 0xA4;
    public const int VK_RMENU = 0xA5;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int GetMessage(out NativeMethods.MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessage(out NativeMethods.MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>
    /// 测试用开关：允许处理"模拟出来"的按键（默认关）。
    /// 正常运行时注入事件必须忽略，否则我们补发的按键抬起会被自己再吃一遍；
    /// 但自动化验证 Alt+Tab / Win+V 只能靠 SendInput 造按键，所以留这个门。
    /// 环境变量会在提权重启时丢掉，所以同时认命令行开关 —— 参数是会被转发的。
    /// </summary>
    internal static readonly bool AllowInjected =
        Environment.GetEnvironmentVariable("EXPLORERDOCK_TEST_INJECTED") == "1"
        || Environment.GetCommandLineArgs().Any(a => a.Equals("--allow-injected", StringComparison.OrdinalIgnoreCase));

    private readonly LowLevelKeyboardProc _proc;   // 必须保活：被 GC 回收后系统回调就是随机崩溃
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly object _gate = new();
    private Func<KeyStroke, bool>[] _handlers = Array.Empty<Func<KeyStroke, bool>>();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook = IntPtr.Zero;
    private volatile bool _running;

    public KeyboardHook() => _proc = Proc;

    /// <summary>
    /// 注册一个按键处理器，返回 true = 这个按键被我们吃掉，不再传给系统。
    /// 多个处理器按注册顺序依次询问（Alt+Tab 与 Win+V 各占一个，互不干扰）。
    /// 在钩子线程同步调用，必须极快。
    /// </summary>
    public void AddHandler(Func<KeyStroke, bool> handler)
    {
        lock (_gate)
        {
            var list = _handlers.ToList();
            list.Add(handler);
            _handlers = list.ToArray();   // 数组整体替换，钩子线程读到的永远是一份完整快照
        }
    }

    public bool IsInstalled { get; private set; }

    public void Install()
    {
        if (_running) return;

        _running = true;
        _thread = new Thread(ThreadProc) { IsBackground = true, Name = "ExplorerDock.KeyboardHook" };
        _thread.Start();

        // 等钩子挂好（失败也没什么可做的，切换器直接不可用而已）
        _ready.Wait(1500);
    }

    private void ThreadProc()
    {
        try
        {
            // 先摸一下消息队列：低级钩子的回调是靠消息泵派发的，线程没有队列就挂不上
            PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            IsInstalled = _hook != IntPtr.Zero;
        }
        catch
        {
            IsInstalled = false;
        }
        finally
        {
            _ready.Set();
        }

        if (!IsInstalled) return;

        // 每 20 秒把钩子重新挂一遍：系统会因为回调超时之类的理由把低级钩子悄悄摘掉
        // （不会通知我们，IsInstalled 也还停在 true），重挂的代价是微秒级，
        // 换来的是"就算被摘了也能自己回来"。
        SetTimer(IntPtr.Zero, new IntPtr(HookTimerId), HookHealthIntervalMs, IntPtr.Zero);

        // 纯粹挂着钩子等消息，判断全在 Proc 里做，这里不 Translate 也不 Dispatch
        while (_running && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.message == WM_TIMER) Reinstall();
        }

        try
        {
            KillTimer(IntPtr.Zero, new IntPtr(HookTimerId));
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        }
        catch
        {
            // 进程要走了，卸不掉也无所谓
        }

        _hook = IntPtr.Zero;
        IsInstalled = false;
    }

    /// <summary>重挂一次钩子（自愈用）。</summary>
    private void Reinstall()
    {
        try
        {
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            IsInstalled = _hook != IntPtr.Zero;
        }
        catch
        {
            IsInstalled = false;
        }
    }

    private const int HookTimerId = 1;
    private const uint HookHealthIntervalMs = 20000;
    private const uint WM_TIMER = 0x0113;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

    private IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

        try
        {
            var handlers = _handlers;
            if (handlers.Length > 0)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var message = (int)wParam;
                bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
                bool altDown = (data.flags & LLKHF_ALTDOWN) != 0;
                bool injected = (data.flags & LLKHF_INJECTED) != 0;

                // 模拟出来的按键一律不碰（AllowInjected 只在自动化验证时打开）：
                // 里面既有我们自己补发的按键抬起，也有别的自动化工具，接管它们只会打架
                if (!injected || AllowInjected)
                {
                    var stroke = new KeyStroke((int)data.vkCode, down, altDown, injected);

                    foreach (var handler in handlers)
                    {
                        if (handler(stroke)) return new IntPtr(1);
                    }
                }
            }
        }
        catch
        {
            // 我们的 bug 绝不能变成键盘失灵，放行了事
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (!_running) return;

        _running = false;

        try
        {
            if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // 忽略
        }

        try
        {
            _thread?.Join(800);
        }
        catch
        {
            // 忽略
        }

        _thread = null;
        _ready.Dispose();
    }
}
