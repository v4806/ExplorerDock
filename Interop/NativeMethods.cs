using System.Runtime.InteropServices;
using System.Text;

namespace ExplorerDock.Interop;

internal static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_TOPMOST = 0x00000008L;
    public const long WS_EX_APPWINDOW = 0x00040000L;
    public const long WS_EX_TRANSPARENT = 0x00000020L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>递归找第一个类名匹配的子窗口。</summary>
    public static IntPtr FindChildWindow(IntPtr parent, string className)
    {
        IntPtr found = IntPtr.Zero;

        if (parent == IntPtr.Zero) return found;

        try
        {
            EnumChildWindows(parent, (hwnd, _) =>
            {
                if (string.Equals(GetClassNameSafe(hwnd), className, StringComparison.OrdinalIgnoreCase))
                {
                    found = hwnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 找不到就算了
        }

        return found;
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    public const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    /// <summary>这个窗口是不是我们自己进程的（拖放时用来判断"拖回自己身上"）。</summary>
    public static bool IsOwnProcess(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;

        try
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    public const int SW_SHOWNORMAL = 1;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;

    public const uint GW_OWNER = 4;
    public const uint GW_HWNDNEXT = 2;

    public const uint WM_CLOSE = 0x0010;

    /// <summary>激活请求：到达时系统还没把前台交出去，这是问"点击前谁在前台"的唯一时机。</summary>
    public const uint WM_MOUSEACTIVATE = 0x0021;

    public const uint WM_GETICON = 0x007F;
    public const int ICON_SMALL = 0;
    public const int ICON_BIG = 1;
    public const int ICON_SMALL2 = 2;

    public const int GCLP_HICON = -14;
    public const int GCLP_HICONSM = -34;

    public const uint PM_REMOVE = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetWindowText(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// 当前前台窗口是不是我们自己进程的。
    /// 用来区分"面板被自己的子窗口（确认框/设置窗/浮窗）顶掉焦点"和"用户真的点到别的程序去了"。
    /// </summary>
    public static bool IsOwnProcessForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        GetWindowThreadProcessId(foreground, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>把键盘焦点设到窗口上（只能设本线程的窗口）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    /// <summary>本线程当前的焦点窗口。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    /// <summary>
    /// 把窗口强制带到前台，并把键盘焦点真的交给它。
    ///
    /// Windows 会拦住"非前台进程"抢焦点，这里先走系统 Alt+Tab 用的那条路
    /// （SwitchToThisWindow：它除切前台之外也会把键盘焦点正确交给目标窗口），
    /// 不行再用 AttachThreadInput 挂输入队列的兜底办法。
    ///
    /// **光切前台是不够的**：窗口到了前台、焦点却还留在原处（或我们自己的窗口）时，
    /// 切过去直接 Ctrl+V 毫无反应，必须先用鼠标点一下目标窗口内部才行 ——
    /// 用户实测的正是这个现象，而系统原生 Alt+Tab 切过去就没这问题。
    /// 焦点是"线程内的概念"，所以 SetFocus 之前必须把当前线程挂到**目标窗口的线程**上。
    /// </summary>
    public static bool ForceForeground(IntPtr hWnd)
    {
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);

        // 系统 Alt+Tab 的实现：切前台 + 交焦点一次做完（未文档化，但一直可用）
        try
        {
            SwitchToThisWindow(hWnd, true);
        }
        catch
        {
            // 拿不到就算了，下面还有兜底
        }

        var previousFocus = HandOverFocus(hWnd);

        if (GetForegroundWindow() != hWnd)
        {
            var foreground = GetForegroundWindow();
            uint foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
            uint currentThread = GetCurrentThreadId();
            bool attached = false;

            try
            {
                if (foregroundThread != 0 && foregroundThread != currentThread)
                {
                    attached = AttachThreadInput(foregroundThread, currentThread, true);
                }

                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
                HandOverFocus(hWnd);
            }
            finally
            {
                if (attached) AttachThreadInput(foregroundThread, currentThread, false);
            }
        }

        // 留一份诊断：焦点到底进没进目标窗口、这一步是在什么样的线程上做的
        try
        {
            uint targetThread = GetWindowThreadProcessId(hWnd, out _);
            var (guiActive, guiFocus) = GetThreadGuiState(targetThread);

            LastActivateDiagnostics =
                $"setFocusPrev=0x{previousFocus.ToInt64():X} active=0x{guiActive.ToInt64():X} " +
                $"focus=0x{guiFocus.ToInt64():X} fg=0x{GetForegroundWindow().ToInt64():X} " +
                $"threadHasQueue={IsGuiThread()}";
        }
        catch
        {
            LastActivateDiagnostics = "诊断失败";
        }

        return GetForegroundWindow() == hWnd;
    }

    /// <summary>最近一次激活的关键结果（谁在前台、焦点进了哪个窗口、调用线程有没有消息队列）。</summary>
    public static string LastActivateDiagnostics { get; private set; } = string.Empty;

    /// <summary>当前线程有没有消息队列 —— AttachThreadInput / SetFocus 只有在这种线程上才可靠。</summary>
    private static bool IsGuiThread()
    {
        try
        {
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            return GetGUIThreadInfo(GetCurrentThreadId(), ref info);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    /// <summary>把键盘焦点交给目标窗口（必要时先挂到它的输入队列上）。返回切换前的焦点窗口。</summary>
    private static IntPtr HandOverFocus(IntPtr hWnd)
    {
        try
        {
            uint targetThread = GetWindowThreadProcessId(hWnd, IntPtr.Zero);
            uint currentThread = GetCurrentThreadId();

            bool attached = false;
            IntPtr previous = IntPtr.Zero;

            try
            {
                if (targetThread != 0 && targetThread != currentThread)
                {
                    attached = AttachThreadInput(targetThread, currentThread, true);
                }

                previous = SetFocus(hWnd);
            }
            finally
            {
                if (attached) AttachThreadInput(targetThread, currentThread, false);
            }

            return previous;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    public static long GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex).ToInt64()
            : GetWindowLong32(hWnd, nIndex);

    public static bool SetWindowLongPtr(IntPtr hWnd, int nIndex, long value)
    {
        if (IntPtr.Size == 8)
        {
            var prev = SetWindowLongPtr64(hWnd, nIndex, new IntPtr(value));
            return prev != IntPtr.Zero || Marshal.GetLastWin32Error() == 0;
        }

        var prev32 = SetWindowLong32(hWnd, nIndex, unchecked((int)value));
        return prev32 != 0 || Marshal.GetLastWin32Error() == 0;
    }

    public static string GetWindowTextSafe(IntPtr hWnd)
        => TryGetWindowText(hWnd, out var text) ? text : string.Empty;

    /// <summary>
    /// 跨进程问窗口要标题，并告诉调用方"这次到底问出来没有"。
    ///
    /// 两种"空"必须分得清：返回 true 但字符串为空 = 窗口确实没有标题（隐形窗口这类）；
    /// 返回 false = 对方没响应（挂起或消息循环忙），WM_GETTEXT 超时、或被 SMTO_ABORTIFHUNG 直接放弃。
    /// 接管范围判据要靠这个区分「这个窗口不存在」和「这个窗口只是卡住了」。
    /// </summary>
    public static bool TryGetWindowText(IntPtr hWnd, out string text)
    {
        text = string.Empty;
        if (hWnd == IntPtr.Zero) return false;

        // 必须用 WM_GETTEXT 跨进程去问窗口要标题。
        // GetWindowText 对属于**其他进程**的窗口不会真的去问，只返回本进程缓存的副本 ——
        // 结果就是资源管理器窗口的标题变了（比如关掉多余标签页），我们却一直读到旧值。
        var sb = new StringBuilder(512);
        var ok = SendMessageTimeout(
            hWnd,
            WM_GETTEXT,
            new IntPtr(sb.Capacity),
            sb,
            SMTO_ABORTIFHUNG,
            300,
            out _);

        if (ok == IntPtr.Zero) return false;

        text = sb.ToString();
        return true;
    }

    public const uint WM_GETTEXT = 0x000D;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        StringBuilder lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    public static string GetClassNameSafe(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // ---------- 进程名（Alt+Tab 同名进程打包用） ----------

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    /// <summary>
    /// 取进程的 exe 文件名（不带扩展名，保留原样大小写），取不到返回空串。
    ///
    /// 走 QueryFullProcessImageName：只要"查询有限信息"权限就够，比 Process.MainModule
    /// 那种要读对方模块的方式稳（提权/非提权都能用），也不会为每个窗口拉一遍进程表。
    /// </summary>
    public static string GetProcessName(uint pid)
        => System.IO.Path.GetFileNameWithoutExtension(GetProcessImagePath(pid));

    /// <summary>
    /// 取进程 exe 的完整路径，取不到返回空串。
    ///
    /// 和 <see cref="GetProcessName"/> 是同一次系统调用：窗口图标要按 exe 路径取，
    /// 分成两次调用等于每轮枚举给每个窗口多做一遍 OpenProcess。
    /// </summary>
    public static string GetProcessImagePath(uint pid)
    {
        if (pid == 0) return string.Empty;

        IntPtr handle = IntPtr.Zero;

        try
        {
            handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return string.Empty;

            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;

            if (!QueryFullProcessImageName(handle, 0, buffer, ref size)) return string.Empty;

            return buffer.ToString();
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                try { CloseHandle(handle); } catch { }
            }
        }
    }

    /// <summary>在 STA 后台线程上泵消息，避免跨进程 COM 调用卡住。</summary>
    public static void PumpMessages()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    // ---------- Alt+Tab 接管用到的那几样 ----------

    /// <summary>DwmGetWindowAttribute 的 DWMWA_CLOAKED：窗口"可见"其实被 DWM 藏着（UWP 常见）。</summary>
    public const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    public static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int value, sizeof(int)) == 0 && value != 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeoutIcon(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    /// <summary>
    /// 跨进程问窗口要图标（WM_GETICON）。
    /// 必须带超时：对方进程卡住时 SendMessage 会把我们自己的线程一起拖住。
    /// 注意返回的是窗口共享的图标句柄，**不能** DestroyIcon。
    /// </summary>
    public static IntPtr QueryWindowIcon(IntPtr hwnd, int kind)
    {
        try
        {
            var ok = SendMessageTimeoutIcon(hwnd, WM_GETICON, new IntPtr(kind), IntPtr.Zero, SMTO_ABORTIFHUNG, 250, out var result);
            return ok != IntPtr.Zero ? result : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    // ---------- 屏幕范围（判断前台是不是全屏） ----------

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>取窗口所在显示器的完整范围（物理像素）。</summary>
    public static bool TryGetMonitorRect(IntPtr hwnd, out RECT rect)
    {
        rect = default;

        try
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return false;

            rect = info.rcMonitor;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------- 补发 Alt 抬起 ----------

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    // INPUT 在 x64 上是 40 字节（type 4 + 对齐 4 + union 32）。
    // union 里必须把鼠标那一支也写进来，否则 Marshal.SizeOf 只算 32，
    // SendInput 会直接返回 0 —— 而且它不报错，看起来就像"什么都没发生"。
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public HARDWAREINPUT hi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public const ushort VK_LMENU = 0xA4;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_LCONTROL = 0xA2;
    public const ushort VK_V = 0x56;

    private static INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 },
    };

    private static void SendKeys(INPUT[] inputs)
    {
        try
        {
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }
        catch
        {
            // 补发失败也不影响主流程
        }
    }

    /// <summary>
    /// 补发一次按键抬起。
    ///
    /// 我们把某些键的抬起吞掉了（不然前台程序会收到半个组合键），
    /// 代价是系统可能以为它还按着 —— 补发的事件带注入标志，不会再被我们自己的钩子吃一遍。
    /// </summary>
    public static void SendKeyUp(ushort vk) => SendKeys(new[] { KeyInput(vk, true) });

    /// <summary>补发一次按键按下。</summary>
    public static void SendKeyDown(ushort vk) => SendKeys(new[] { KeyInput(vk, false) });

    /// <summary>补发一次 Alt 的抬起（Alt+Tab 接管用）。</summary>
    public static void RestoreAltKeyState() => SendKeyUp(VK_LMENU);

    private const ushort VK_F13 = 0x7C;

    /// <summary>
    /// 注入一个"见证按键"（F13）。
    ///
    /// 起因：我们把 V 吃掉了，系统看到的就是"Win 被单独按下又抬起"，于是弹开始菜单。
    /// 在抬起 Win 之前补一个别的键，系统就知道这次 Win 不是单击。
    ///
    /// 这个键挑得很讲究，已经踩过两次坑：
    /// - F24 会被截图 / 录屏工具当成热键接走（剪贴板里凭空多出一张全屏截图）；
    /// - 左 Shift 会触发输入法的中英文切换。
    /// F13 在标准键盘上根本不存在，输入法也不认它，是这三个里最中性的。
    /// </summary>
    public static void SendWitnessKey()
        => SendKeys(new[] { KeyInput(VK_F13, false), KeyInput(VK_F13, true) });

    /// <summary>补一个完整的 Ctrl+V（点了剪贴板记录之后自动粘贴）。</summary>
    public static void SendCtrlV()
        => SendKeys(new[]
        {
            KeyInput(VK_LCONTROL, false),
            KeyInput(VK_V, false),
            KeyInput(VK_V, true),
            KeyInput(VK_LCONTROL, true),
        });

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>某个键现在是不是按着的（判断是不是"纯 Win+V"用）。</summary>
    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    /// <summary>
    /// 在指定位置点一下左键。
    ///
    /// 给"拖出去粘贴"当落点用：真正的拖放是由目标窗口自己处理"放下"的，
    /// 我们走的是粘贴这条路，所以得先在那个位置补一次真实点击，
    /// 目标窗口才会把输入光标/焦点放到落点上，随后补的 Ctrl+V 才有地方落。
    /// </summary>
    public static void ClickAt(int x, int y)
    {
        try
        {
            SetCursorPos(x, y);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }
        catch
        {
            // 点不出去也不该影响粘贴流程
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO pgui);

    /// <summary>
    /// 某个线程现在的活动窗口与**焦点窗口**。
    ///
    /// 诊断用：切换窗口之后焦点到底落在哪儿（文件列表 / 标签栏 / 地址栏）只能从这里看出来；
    /// 焦点不在那个线程时返回 0。
    /// </summary>
    public static (IntPtr Active, IntPtr Focus) GetThreadGuiState(uint threadId)
    {
        try
        {
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };

            if (!GetGUIThreadInfo(threadId, ref info)) return (IntPtr.Zero, IntPtr.Zero);

            return (info.hwndActive, info.hwndFocus);
        }
        catch
        {
            return (IntPtr.Zero, IntPtr.Zero);
        }
    }

    private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;

    /// <summary>ole32.dll 里 OLE 拖放光标资源里的"复制"（箭头 + 虚线框 + 加号）。</summary>
    private const int OLE_DRAG_COPY_CURSOR = 6;

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    /// <summary>
    /// 取 OLE 的"拖放复制"光标（拖动文件时那个带加号的箭头）。
    ///
    /// 系统光标表（IDC_*）里没有这一个，它藏在 ole32.dll 的 RT_GROUP_CURSOR 资源里，
    /// 资源 id 实测 6 就是这个样子（1=禁止、5=移动、6=复制、7=创建快捷方式）。
    /// 这里按数据文件方式加载 ole32.dll（不执行它的代码，也从不卸载，句柄长期有效）。
    /// </summary>
    public static IntPtr LoadDragCopyCursor()
    {
        try
        {
            var ole32 = LoadLibraryEx("ole32.dll", IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
            if (ole32 == IntPtr.Zero) return IntPtr.Zero;

            return LoadCursor(ole32, new IntPtr(OLE_DRAG_COPY_CURSOR));
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    // ---------- 拖动期间把光标钉住 ----------

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    /// <summary>系统默认箭头（IDC_ARROW），拖动收尾时还原用。</summary>
    private const int IDC_ARROW = 32512;

    /// <summary>
    /// 直接把当前线程的光标设成指定光标。
    ///
    /// 拖动中"悬停呼出"会把前台让给别的窗口，我们的窗口一失活，
    /// WPF 就把 Mouse.OverrideCursor 清掉，光标于是变回普通箭头；
    /// 这里绕过 WPF 直接设 —— 配合鼠标捕获（捕获在我们手上时，
    /// 鼠标哪怕停在别人的窗口上，WM_SETCURSOR 也是发给我们），
    /// 光标就能全程保持拖放样式。
    /// </summary>
    public static void ApplyCursor(IntPtr cursor)
    {
        if (cursor == IntPtr.Zero) return;

        try { SetCursor(cursor); } catch { }
    }

    /// <summary>还原成系统默认箭头（下一次 WM_SETCURSOR 会按所在窗口再修正一次）。</summary>
    public static void RestoreArrowCursor()
    {
        try { SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(IDC_ARROW))); } catch { }
    }

    /// <summary>鼠标捕获现在是不是在这个窗口上。</summary>
    public static bool HasCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        try { return GetCapture() == hwnd; } catch { return false; }
    }

    /// <summary>把鼠标捕获抢回这个窗口（失活时系统会收走，这里按回来）。</summary>
    public static void CaptureMouse(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try { SetCapture(hwnd); } catch { }
    }

    /// <summary>释放本线程的鼠标捕获。</summary>
    public static void ReleaseMouseCapture()
    {
        try { ReleaseCapture(); } catch { }
    }

    // ---------- DWM 实时缩略图（任务栏预览用的就是这套） ----------

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;

        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    public const uint DWM_TNP_RECTDESTINATION = 0x00000001;
    public const uint DWM_TNP_OPACITY = 0x00000004;
    public const uint DWM_TNP_VISIBLE = 0x00000008;
    public const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);

    /// <summary>把一个窗口的实时缩略图挂到宿主窗口上（源窗口最小化也能显示内容）。</summary>
    public static bool RegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnailId)
    {
        thumbnailId = IntPtr.Zero;

        try { return DwmRegisterThumbnail(destination, source, out thumbnailId) == 0; }
        catch { return false; }
    }

    public static void UnregisterThumbnail(IntPtr thumbnailId)
    {
        if (thumbnailId == IntPtr.Zero) return;

        try { DwmUnregisterThumbnail(thumbnailId); } catch { }
    }

    public static bool UpdateThumbnail(IntPtr thumbnailId, ref DWM_THUMBNAIL_PROPERTIES properties)
    {
        if (thumbnailId == IntPtr.Zero) return false;

        try { return DwmUpdateThumbnailProperties(thumbnailId, ref properties) == 0; }
        catch { return false; }
    }

    // ---------- 窗口区域（把宿主窗口裁成只剩缩略图那几块） ----------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>窗口客户区尺寸（拿不到返回 false）。</summary>
    public static bool GetClientSize(IntPtr hwnd, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            if (!GetClientRect(hwnd, out var rect)) return false;

            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;

            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const int RGN_OR = 2;

    /// <summary>
    /// 把窗口裁剪成"只剩这几个矩形"（坐标是窗口客户区坐标）。
    /// 传空列表就把窗口整个裁掉（等于看不见）。区域交给系统接管，不要再删。
    /// </summary>
    public static void SetWindowRegion(IntPtr hwnd, IReadOnlyList<RECT> rects)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            if (rects.Count == 0)
            {
                SetWindowRgn(hwnd, CreateRectRgn(0, 0, 0, 0), true);
                return;
            }

            var region = CreateRectRgn(
                rects[0].Left, rects[0].Top, rects[0].Right, rects[0].Bottom);

            for (int i = 1; i < rects.Count; i++)
            {
                var piece = CreateRectRgn(rects[i].Left, rects[i].Top, rects[i].Right, rects[i].Bottom);
                CombineRgn(region, region, piece, RGN_OR);
                DeleteObject(piece);
            }

            // SetWindowRgn 成功的话区域归系统所有，这里不能再删
            SetWindowRgn(hwnd, region, true);
        }
        catch
        {
            // 裁剪失败就退化成整窗可见（视觉上多盖一块，但不影响用）
        }
    }
}
