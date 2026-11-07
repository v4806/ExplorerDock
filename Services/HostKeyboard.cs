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
    private readonly KeyboardHook _hook = new();
    private readonly Action<string> _notify;
    private readonly ClipboardHotkeyStateMachine _clipboardKeys = new();

    // ---------- 界面推过来的状态镜像 ----------
    private volatile bool _altTabTakeover = true;
    private volatile bool _clipboardTakeover;

    /// <summary>界面是否正在接管中（面板开着、或者窗口列表还在数）。</summary>
    private volatile bool _panelOpen;

    private volatile bool _fullscreenPassthrough = true;

    // ---------- 钩子自己跟踪的按键状态 ----------
    private volatile bool _altDown;
    private volatile bool _shiftDown;

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
        // 记住"面板是什么时候开始接管的"：ESC 的吞噬要有个时间兜底，见 OnKey 里那一支
        if (panelOpen && !_panelOpen) _panelOpenedAt = DateTime.UtcNow;

        _altTabTakeover = altTabTakeover;
        _clipboardTakeover = clipboardTakeover;
        _panelOpen = panelOpen;
        _fullscreenPassthrough = fullscreenPassthrough;
    }

    /// <summary>面板开始接管的时刻（ESC 兜底用）。</summary>
    private DateTime _panelOpenedAt = DateTime.MinValue;

    /// <summary>面板最多接管多久；超过这个时间还认为"开着"，就当成状态脏了、放行按键。</summary>
    private const double PanelTrustSeconds = 20;

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

                // 兜底：面板状态万一没同步回来（界面侧漏发一次 / 管道丢包 / 界面进程异常），
                // ESC 会被永远吞掉 —— 表现为"任何程序里 ESC 都没反应，退出本软件才恢复"。
                // 真实的面板只开几秒，超过 PanelTrustSeconds 就当状态是脏的，直接放行。
                if ((DateTime.UtcNow - _panelOpenedAt).TotalSeconds > PanelTrustSeconds) return false;

                _notify(HostProtocol.KeyCancel);
                return true;

            default:
                return false;
        }
    }

    private bool HandleClipboard(KeyStroke stroke)
    {
        var decision = _clipboardKeys.Process(stroke, _clipboardTakeover, NativeMethods.IsKeyDown);

        switch (decision)
        {
            case ClipboardHotkeyDecision.ToggleClipboard:
                NativeMethods.SendWitnessKey();
                _notify(HostProtocol.KeyToggleClipboard);
                return true;

            case ClipboardHotkeyDecision.SwallowAndRestoreWin:
                return NativeMethods.TrySendKeyUp((ushort)stroke.Vk);

            case ClipboardHotkeyDecision.Swallow:
                return true;

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
