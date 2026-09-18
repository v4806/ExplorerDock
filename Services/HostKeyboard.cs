using ExplorerDock.Interop;

namespace ExplorerDock.Services;

/// <summary>
/// 键盘接管（Alt+Tab / Alt+~ / Win+V），**跑在提权进程里**。
///
/// 为什么必须在这里：管理员程序占前台时，普通权限进程的 `WH_KEYBOARD_LL`
/// 完全收不到按键（实测），Alt+Tab 只能落回系统那套。而钩子回调有 300ms 超时、
/// 超了就被系统摘钩，所以回调里**绝不等界面** —— 自己判断吞不吞，
/// 判定完把"动作"推给界面去执行（弹面板、移动选中、提交）。
///
/// 界面状态（接管开关、面板是否开着）由界面推过来当镜像；钩子自己的按键状态（Alt/Shift/Win）
/// 只有这里知道。
/// </summary>
internal sealed class HostKeyboard : IDisposable
{
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_V = 0x56;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    private readonly KeyboardHook _hook = new();
    private readonly Action<string> _notify;

    // ---------- 界面推过来的状态镜像 ----------
    private volatile bool _altTabTakeover = true;
    private volatile bool _clipboardTakeover = true;

    /// <summary>界面是否正在接管中（面板开着、或者窗口列表还在数）。</summary>
    private volatile bool _panelOpen;

    private volatile bool _fullscreenPassthrough = true;

    // ---------- 钩子自己跟踪的按键状态 ----------
    private volatile bool _altDown;
    private volatile bool _shiftDown;
    private volatile bool _winDown;
    private bool _swallowWinUp;
    private bool _vSwallowed;

    /// <param name="notify">把按键动作推出去（同进程就是直接回调，跨进程就是写管道）。</param>
    public HostKeyboard(Action<string> notify)
    {
        _notify = notify;
        _hook.AddHandler(OnKey);
    }

    public bool Installed => _hook.IsInstalled;

    public void Install() => _hook.Install();

    public void SetState(bool altTabTakeover, bool clipboardTakeover, bool panelOpen, bool fullscreenPassthrough)
    {
        _altTabTakeover = altTabTakeover;
        _clipboardTakeover = clipboardTakeover;
        _panelOpen = panelOpen;
        _fullscreenPassthrough = fullscreenPassthrough;
    }

    public void Dispose() => _hook.Dispose();

    // ---------- 钩子线程 ----------

    private bool OnKey(KeyStroke stroke)
    {
        try
        {
            if (HandleAltTab(stroke)) return true;
            if (HandleClipboard(stroke)) return true;

            return false;
        }
        catch
        {
            // 钩子里出任何岔子都当没接管，绝不能影响键盘
            return false;
        }
    }

    private bool HandleAltTab(KeyStroke stroke)
    {
        if (!_altTabTakeover) return false;

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
                if (!_panelOpen) return false;

                // 这个"Alt 抬起"我们要吞掉，但物理键盘状态得闭合 ——
                // 补发必须当场做：界面那条路上有好几个提前返回的分支，从那儿溜走
                // 就再没人补发，Alt 会卡在按下状态（键盘错乱，退出程序也不会自愈）。
                NativeMethods.RestoreAltKeyState();

                _notify(HostProtocol.KeyCommit);
                return true;

            case KeyboardHook.VK_TAB:
                // Alt 的状态优先信自己跟踪的：注入或远程使用场景下 flags 未必带 ALTDOWN
                if (!(stroke.AltDown || _altDown) && !_panelOpen) return false;

                // 全屏应用（游戏等）在前台时可以把 Alt+Tab 让给系统，免得起不来
                if (stroke.Down && !_panelOpen && _fullscreenPassthrough && IsForegroundFullscreen())
                {
                    return false;
                }

                if (stroke.Down)
                {
                    _notify(_shiftDown ? HostProtocol.KeyAdvanceBack : HostProtocol.KeyAdvance);
                }

                return true;   // 抬起也吞：别让前台窗口收到半个组合键

            case KeyboardHook.VK_OEM_3:
                // Alt+~（`/~ 键）：在"当前窗口所属进程"的窗口之间切换
                if (!(stroke.AltDown || _altDown) && !_panelOpen) return false;
                if (stroke.Down && !_panelOpen && !CanSwitchWithinProcess()) return false;

                if (stroke.Down)
                {
                    _notify(_shiftDown ? HostProtocol.KeyAdvanceProcessBack : HostProtocol.KeyAdvanceProcess);
                }

                return true;

            case KeyboardHook.VK_ESCAPE:
                if (!stroke.Down || !_panelOpen) return false;

                _notify(HostProtocol.KeyCancel);
                return true;

            default:
                return false;
        }
    }

    private bool HandleClipboard(KeyStroke stroke)
    {
        if (!_clipboardTakeover) return false;

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
                NativeMethods.SendKeyUp((ushort)stroke.Vk);
                return true;

            case VK_V:
                if (stroke.Down)
                {
                    // 长按时 V 会不停重复发 down。这些重复事件必须继续吞掉：
                    // 一旦放行，系统就看到了 V，会把它自己那个剪贴板面板也弹出来。
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

                    // 先补一个"见证按键"：不然系统会把随后的 Win 抬起当成单击 Win，弹开始菜单
                    NativeMethods.SendWitnessKey();

                    _notify(HostProtocol.KeyToggleClipboard);
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

    /// <summary>
    /// 当前前台窗口是不是"有打包卡可切"的进程（Alt+~ 放不放行的判断）。
    ///
    /// 这里只做微秒级的查询，绝不枚举窗口、也绝不查进程名。
    /// </summary>
    private static bool CanSwitchWithinProcess()
    {
        try
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;

            NativeMethods.GetWindowThreadProcessId(foreground, out uint pid);
            if ((int)pid == Environment.ProcessId) return false;   // 功能进程自己的窗口

            // 合并范围决定了 Alt+~ 有没有意义：选了"不合并"就没有打包卡可切，
            // 整个模式下也不打包，直接放行
            if (TakeoverState.GroupMode == AltTabGroupMode.None) return false;

            if (TakeoverState.GroupMode == AltTabGroupMode.AllProcesses) return true;

            // 只合并"被悬浮栏接管"的程序：资源管理器窗口，加上设置里手动指定 /
            // 多窗口自动接管进来的程序（判定结果由监视线程发布的快照给出，这里只查表）
            return TakeoverState.IsTakeoverWindow(foreground, NativeMethods.GetClassNameSafe(foreground));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsForegroundFullscreen()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            // 桌面 / 任务栏本来就铺满整个屏幕，不能算"全屏应用"。
            // 不排除它们的话，在桌面上按 Alt+Tab 会被让给系统 —— 用户报的"桌面上按出来的是系统面板"。
            var className = NativeMethods.GetClassNameSafe(hwnd);
            if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                return false;
            }

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
}
