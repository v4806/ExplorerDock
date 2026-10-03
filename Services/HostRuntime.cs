using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using ExplorerDock.Interop;

namespace ExplorerDock.Services;

/// <summary>
/// 功能进程（<c>ExplorerDock.exe --host</c>）：以管理员权限跑的"脏活"进程。
///
/// 它负责所有需要碰目标窗口的事 —— 枚举窗口、读标题、摘任务栏按钮、激活/最小化、
/// 读写标签页。界面进程是普通权限，这些事在它那里遇到管理员程序就会失效（UIPI），
/// 所以统统搬到这里；两边只通过命名管道说话。
///
/// 它自己没有窗口、不进任务栏、不碰设置文件。
/// </summary>
internal sealed class HostRuntime : IDisposable
{
    private readonly LocalWindowHost _host = new();
    private readonly object _writeGate = new();
    private readonly ManualResetEventSlim _stop = new(false);

    private Stream? _stream;
    private int _lastWindows = -1;
    private bool _writeFailed;

    /// <summary>
    /// 专门跑窗口操作的 GUI 线程。
    ///
    /// 为什么非要有这么个线程：AttachThreadInput、SetFocus、SetForegroundWindow 这些操作
    /// 只有从**有消息队列的线程**发起才可靠。命令是在管道读循环里处理的，而那个循环用
    /// ConfigureAwait(false) 跑在线程池线程上 —— 在那里 SetFocus 会失败，于是出现
    /// "窗口切到前台了、键盘焦点却没进去"（实测：切过去直接 Ctrl+V 没反应，点一下窗口内部才行）。
    /// 系统 Alt+Tab 之所以从来不出这问题，正是因为它就在 GUI 线程上下文里做这件事。
    /// </summary>
    private readonly BlockingCollection<Action> _guiQueue = new();
    private Thread? _guiThread;

    public void Run()
    {
        AltTabController.Log($"host: start pid={Environment.ProcessId} elevated={IsElevated()} injected={KeyboardHook.AllowInjected}");

        EnsureGuiThread();

        _host.SnapshotUpdated += OnSnapshot;
        _host.KeyAction += NotifyKeyAction;

        // 接管范围先按主进程的设置初始化一次（之后界面侧改了会再推过来）
        TakeoverState.SetScope(App.Settings.AutoTakeoverMultiWindow, App.Settings.TakeoverProcesses);
        TakeoverState.SetGroupMode(App.Settings.AltTabGroupScope);

        if (App.Settings.TakeoverEnabled) _host.SetTakeover(true);
        _host.Start();

        // 计划任务（「以管理员身份运行」时的自启用它）**只有管理员权限才建得出来**，
        // 界面进程永远是普通权限，所以由我们（提权进程）在这儿建/纠偏：
        //   ① 补建任务；
        //   ② 把旧版本用 schtasks 命令行建出来的任务改成正确的设置（电池可启动、无 72 小时时限）；
        //   ③ 任务确认合规后，顺手清掉界面进程留下的兜底 Run 键。
        // 任务已经正确时只跑一次 schtasks /Query，开销可以忽略。
        StartupRegistration.Apply(App.Settings.RunAtStartup, App.Settings.RunElevated, Environment.ProcessPath);
        StartupRegistration.LogSnapshot("host");

        AltTabController.Log($"host: keyboard installed={_host.KeyboardInstalled}");

        StartWatchdog();

        var attempts = 0;

        while (!_stop.IsSet)
        {
            bool served = false;

            try
            {
                AcceptAndServeAsync().GetAwaiter().GetResult();

                // 服务过又断开 = 界面走了
                served = true;
            }
            catch
            {
                // 还没连上（界面可能正在启动），记一笔交给看门狗
                if (!_uiConnected && _uiDisconnectedAt == DateTime.MinValue) _uiDisconnectedAt = DateTime.UtcNow;
            }

            Close();

            if (_stop.IsSet) break;

            // 界面连上过又断了：立刻把任务栏按钮还回去并退出，不再等它回来
            // （界面重新启动时会拉起新的功能进程）
            if (served)
            {
                _host.RestoreNow();
                break;
            }

            // 从没连上过：给几次机会，别界面还在启动就自己先走了
            if (++attempts >= 5) break;

            Thread.Sleep(400);
        }

        _host.Dispose();
    }

    /// <summary>
    /// 界面断开多久之后，才去检查"界面进程是不是真的没了"。给足余量：
    /// 界面那边一次操作超时（未响应窗口）就会短暂断开，但那不代表它走了。
    /// </summary>
    private const double HostIdleExitSeconds = 10;

    /// <summary>
    /// 除了自己，还有没有别的 ExplorerDock 进程（也就是界面进程）在跑。
    ///
    /// 看门狗用它做最终判据：界面进程还在就继续等它重连，**不要因为管道断开就自杀** ——
    /// 以前只看"断开 2 秒"，界面那边点一下未响应窗口就足够把宿主带走，
    /// 之后界面所有操作都失败，用户看到的就是"整个软件卡死"。
    /// </summary>
    private static bool UiProcessAlive()
    {
        try
        {
            int self = Environment.ProcessId;

            foreach (var process in Process.GetProcessesByName("ExplorerDock"))
            {
                using (process)
                {
                    if (process.Id != self) return true;
                }
            }
        }
        catch
        {
            // 查不到就当界面不在了：宁可退出，也不要留一个摘了按钮却没人管的僵尸进程
        }

        return false;
    }

    /// <summary>界面进程当前是不是连着。</summary>
    private volatile bool _uiConnected;

    /// <summary>最后一次与界面断开的时间。</summary>
    private DateTime _uiDisconnectedAt = DateTime.MinValue;

    /// <summary>
    /// "界面没了就收摊"的看门狗：界面进程被强杀 / 崩溃时，它没法再发退出命令，
    /// 这里负责把被摘掉的任务栏按钮还回去，然后结束自己。
    ///
    /// 为什么另开线程：断开之后主循环会阻塞在"等下一次连接"上，计时根本推不动。
    /// 不这么干的话，这个进程会一直赖着不走，还继续把任务栏按钮往掉摘 ——
    /// 界面早没了、按钮也回不来，就是"僵尸进程"那个毛病。
    /// </summary>
    private void StartWatchdog()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(500);

                if (_uiConnected) continue;

                var since = _uiDisconnectedAt;
                if (since == DateTime.MinValue) continue;
                if ((DateTime.UtcNow - since).TotalSeconds < HostIdleExitSeconds) continue;

                // 关键判据是"界面进程还在不在"，不是"断开了多久"：界面还活着就继续等它重连
                if (UiProcessAlive()) continue;

                AltTabController.Log("host: ui gone, restoring taskbar buttons and exiting");

                try
                {
                    _host.RestoreNow();
                }
                catch
                {
                    // 忽略
                }

                LeaveNow();
            }
        })
        {
            IsBackground = true,
            Name = "ExplorerDock.HostWatchdog",
        };

        thread.Start();
    }

    /// <summary>启动 GUI 线程（幂等）。</summary>
    private void EnsureGuiThread()
    {
        if (_guiThread is not null) return;

        _guiThread = new Thread(() =>
        {
            // 第一时间建出消息队列：AttachThreadInput 要求调用线程有输入队列
            NativeMethods.PumpMessages();

            foreach (var action in _guiQueue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch
                {
                    // 单个操作失败不影响后面的
                }

                NativeMethods.PumpMessages();
            }
        })
        {
            IsBackground = true,
            Name = "ExplorerDock.HostGui",
        };

        _guiThread.SetApartmentState(ApartmentState.STA);
        _guiThread.Start();
    }

    /// <summary>把窗口操作放到 GUI 线程上执行并等结果（已经在该线程上时直接执行，避免自等死锁）。</summary>
    /// <summary>GUI 线程忙过这个时长，就不再往队列里塞新命令（塞进去只会让每个调用方各等 8 秒）。</summary>
    private const int GuiBusySkipMs = 1200;

    /// <summary>GUI 线程当前有没有在执行命令；有的话从什么时候开始。</summary>
    private int _guiBusy;
    private long _guiBusySince;

    private T RunOnGui<T>(Func<T> work, T fallback)
    {
        if (_guiThread is not null && Thread.CurrentThread == _guiThread)
        {
            try
            {
                return work();
            }
            catch
            {
                return fallback;
            }
        }

        EnsureGuiThread();

        // GUI 线程正被某个卡住的操作占着（超过阈值）：**不要再排队**。
        // 排队让每个请求各等 8 秒，用户感觉"整个软件卡死"—— 实测一个未响应窗口
        // 让 4 次点击各等 8 秒，直到它恢复才一起完成。对手已经卡住的窗口，排队也没有意义：
        // 直接快速失败，等它恢复后下一次点击自然正常。
        if (Volatile.Read(ref _guiBusy) != 0 &&
            Environment.TickCount64 - Volatile.Read(ref _guiBusySince) > GuiBusySkipMs)
        {
            AltTabController.Log($"host: gui 忙超过 {GuiBusySkipMs}ms，跳过请求（多半是未响应窗口占着）");
            return fallback;
        }

        T result = fallback;
        using var done = new ManualResetEventSlim(false);

        try
        {
            _guiQueue.Add(() =>
            {
                Volatile.Write(ref _guiBusySince, Environment.TickCount64);
                Volatile.Write(ref _guiBusy, 1);

                try
                {
                    result = work();
                }
                catch
                {
                    // 忽略，返回 fallback
                }
                finally
                {
                    Volatile.Write(ref _guiBusy, 0);
                    done.Set();
                }
            });
        }
        catch
        {
            return fallback;
        }

        if (done.Wait(8000)) return result;

        // 卡住的操作用户看不见，日志里必须留一笔（最常见的原因是目标窗口未响应）
        AltTabController.Log("host: gui 调用超时 8000ms（队列可能被未响应窗口的操作占着）");
        return fallback;
    }

    private async Task AcceptAndServeAsync()
    {
        // 管道是界面进程（普通权限）当服务端，我们提权这边当客户端 ——
        // 反过来（提权建管道、普通权限去连）会被 ACL 挡掉。
        // 异步模式：同步读会占着管道内部的锁，把另一个线程的写卡死。
        using var client = new NamedPipeClientStream(
            ".",
            HostProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        client.Connect(3000);

        _stream = client;
        _uiConnected = true;
        _uiDisconnectedAt = DateTime.MinValue;
        AltTabController.Log("host: ui connected");

        while (!_stop.IsSet)
        {
            var line = await HostProtocol.ReadLineAsync(client, CancellationToken.None);
            if (line is null) break;

            var message = HostProtocol.Decode(line);
            if (message is null) continue;

            // 窗口操作统一丢到 GUI 线程上做（见 _guiQueue 的注释）：
            // 线程池线程没有消息队列，SetFocus 那种操作在那里是不可靠的。
            //
            // fallback 必须是 true：RunOnGui 在"GUI 线程 8 秒还没做完"时返回 fallback，
            // 而 false 在这个循环里等于"界面要求收摊"。两者一混，就变成
            // "只要有一个未响应窗口让某次操作卡住 → 宿主自杀 → 界面从此全部功能失效"。
            // 超时 ≠ 收摊：只有 Handle 明确返回 false（收到退出命令）才结束。
            if (!RunOnGui(() => Handle(message), true)) break;
        }

        _uiConnected = false;
        _uiDisconnectedAt = DateTime.UtcNow;
        AltTabController.Log("host: ui disconnected");

        // 断开 ≠ "界面走了"：可能是界面自己重启，也可能只是一次抖动。
        // 这里**不要结束进程** —— 回到主循环重新等连接；"界面真的没了"由看门狗按
        // "进程是否还在"来判定。（以前这里直接 LeaveNow()，配合上面那处超时误判，
        // 就成了"一次卡顿 = 宿主自杀、界面从此全废"。）
    }

    /// <summary>
    /// 界面没了（或者界面让它退）就立刻结束自己。
    ///
    /// 不在这里做"把任务栏按钮还回去"：任务栏相关的收尾由界面那边重启文件资源管理器解决，
    /// 这里只管消失，不留僵尸。
    /// </summary>
    private void LeaveNow()
    {
        AltTabController.Log("host: ui gone, exiting");

        Environment.Exit(0);
    }

    /// <summary>处理一条命令；返回 false 表示该收摊了。</summary>
    private bool Handle(HostMessage message)
    {
        try
        {
            switch (message.Cmd)
            {
                case HostProtocol.CmdStart:
                    _host.SetTakeover(message.Enabled);
                    break;

                case HostProtocol.CmdTakeover:
                    _host.SetTakeover(message.Enabled);
                    break;

                case HostProtocol.CmdTakeoverScope:
                    // 接管范围：自动接管开关 + 手动指定的程序名单（都是宿主侧判定用的）
                    TakeoverState.SetScope(message.AutoTakeover, message.Processes);
                    _host.ReapplyNow();
                    message.Ok = true;
                    break;

                case HostProtocol.CmdRunningProcesses:
                    message.ProcessList = ToProcessList(ExplorerWatcher.EnumerateRunningProcesses());
                    message.Ok = true;
                    break;

                case HostProtocol.CmdAltTabGroup:
                    // Alt+Tab 的合并范围（不合并 / 只合并接管的 / 合并全部）
                    TakeoverState.SetGroupMode((AltTabGroupMode)message.Value);
                    message.Ok = true;
                    break;

                case HostProtocol.CmdReapply:
                    _host.ReapplyNow();
                    break;

                case HostProtocol.CmdRestoreAll:
                    _host.RestoreNow();
                    break;

                case HostProtocol.CmdActivate:
                    // 未响应的窗口：激活注定失败（SetForegroundWindow / AttachThreadInput 会一直等它），
                    // 而且会把**串行**的 GUI 队列占住十几秒 —— 后面所有点击都在排队，
                    // 用户感觉就是"点哪儿都没反应"。直接快速失败，别占队列。
                    if (NativeMethods.IsHungAppWindow(new IntPtr(message.Hwnd)))
                    {
                        message.Ok = false;
                        AltTabController.Log($"host: activate 跳过（窗口未响应）0x{message.Hwnd:X}");
                        break;
                    }

                    message.Ok = _host.Activate(new IntPtr(message.Hwnd), message.Tab);
                    AltTabController.Log(
                        $"host: activate 0x{message.Hwnd:X} tab={message.Tab} ok={message.Ok} " +
                        $"fg=0x{NativeMethods.GetForegroundWindow().ToInt64():X} {NativeMethods.LastActivateDiagnostics}");
                    break;

                case HostProtocol.CmdMinimize:
                    _host.Minimize(new IntPtr(message.Hwnd));
                    message.Ok = true;
                    break;

                case HostProtocol.CmdSelectedTab:
                    message.Value = _host.SelectedTab(new IntPtr(message.Hwnd));
                    message.Ok = true;
                    break;

                case HostProtocol.CmdCards:
                    message.Cards = ToCards(_host.SnapshotCards());
                    message.Ok = true;
                    break;

                case HostProtocol.CmdProcessCards:
                    message.Cards = ToCards(_host.ProcessCards(new IntPtr(message.Hwnd)));
                    message.Ok = true;
                    break;

                case HostProtocol.CmdKeyState:
                    _host.SetKeyState(
                        message.TakeoverAltTab,
                        message.TakeoverClipboard,
                        message.PanelOpen);

                    message.Ok = _host.KeyboardInstalled;
                    break;

                case HostProtocol.CmdIsForeground:
                    message.Ok = NativeMethods.GetForegroundWindow() == new IntPtr(message.Hwnd);
                    break;

                case HostProtocol.CmdIsLeftDown:
                    message.Ok = NativeMethods.IsKeyDown(0x01);   // VK_LBUTTON
                    break;

                case HostProtocol.CmdSendPaste:
                    AltTabController.Log($"host: send ctrl+v (fg=0x{NativeMethods.GetForegroundWindow().ToInt64():X})");
                    NativeMethods.SendCtrlV();
                    message.Ok = true;
                    break;

                case HostProtocol.CmdPasteInto:
                    // 让窗口执行它自己的"粘贴"命令（走 UI Automation，不依赖键盘焦点）
                    var pasteTarget = new IntPtr(message.Hwnd);
                    message.Ok = ExplorerTabs.InvokePasteCommand(pasteTarget);

                    AltTabController.Log($"host: paste into 0x{message.Hwnd:X} via=uia ok={message.Ok}");
                    break;

                case HostProtocol.CmdCloseWindow:
                    message.Ok = ExplorerWatcher.CloseWindow(new IntPtr(message.Hwnd));
                    break;

                case HostProtocol.CmdCloseProcessWindows:
                    int closed = ExplorerWatcher.CloseProcessWindows(new IntPtr(message.Hwnd));
                    AltTabController.Log($"host: close process windows of 0x{message.Hwnd:X} -> {closed}");
                    message.Ok = closed > 0;
                    break;

                case HostProtocol.CmdKillProcesses:
                    int killed = ExplorerWatcher.KillProcesses(new IntPtr(message.Hwnd));
                    AltTabController.Log($"host: kill processes of 0x{message.Hwnd:X} -> {killed}");
                    message.Ok = killed > 0;
                    break;

                case HostProtocol.CmdSendAltUp:
                    NativeMethods.RestoreAltKeyState();
                    message.Ok = true;
                    break;

                case HostProtocol.CmdClickCursor:
                    if (NativeMethods.GetCursorPos(out var point))
                    {
                        AltTabController.Log($"host: click at {point.X},{point.Y}");
                        NativeMethods.ClickAt(point.X, point.Y);
                    }
                    message.Ok = true;
                    break;

                case HostProtocol.CmdExit:
                    // 先回一句再走：界面那边是同步等回复的（等 1.5 秒），
                    // 不回复的话它会白等，退出流程白白多花一秒多
                    message.Ok = true;
                    Reply(message);

                    // 界面要退了：不做什么收尾，立刻结束自己
                    LeaveNow();
                    return false;

                default:
                    message.Error = $"unknown command: {message.Cmd}";
                    break;
            }
        }
        catch (Exception ex)
        {
            message.Ok = false;
            message.Error = $"{ex.GetType().Name}: {ex.Message}";
        }

        Reply(message);
        return true;
    }

    private static List<CardDto> ToCards(List<AltTabWindowInfo> cards)
    {
        var list = new List<CardDto>(cards.Count);
        foreach (var card in cards) list.Add(HostProtocol.ToDto(card));

        return list;
    }

    private static List<ProcessDto> ToProcessList(List<RunningProcessInfo> processes)
    {
        var list = new List<ProcessDto>(processes.Count);

        foreach (var process in processes)
        {
            list.Add(new ProcessDto
            {
                Name = process.Name,
                ExePath = process.ExePath,
                WindowCount = process.WindowCount,
                SampleTitle = process.SampleTitle,
            });
        }

        return list;
    }

    private void Reply(HostMessage message)
    {
        if (message.Id <= 0) return;

        Send(message);
    }

    private void Send(HostMessage message)
    {
        var stream = _stream;
        if (stream is null) return;

        try
        {
            lock (_writeGate)
            {
                HostProtocol.WriteLine(stream, HostProtocol.Encode(message));
            }
        }
        catch (Exception ex)
        {
            // 界面进程没了：下一次读会返回 null，退出服务循环
            if (!_writeFailed)
            {
                _writeFailed = true;
                AltTabController.Log($"host: write failed {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>把接管到的按键动作推给界面（钩子线程调用，写管道有锁保护）。</summary>
    private void NotifyKeyAction(string action)
    {
        if (_stream is null) return;

        Send(new HostMessage { Cmd = HostProtocol.CmdKeyAction, Action = action });
    }

    private void OnSnapshot(ExplorerDock.Models.ExplorerSnapshot snapshot)
    {
        if (_stream is null) return;

        // 只在窗口数变化时记一笔，免得日志被刷爆
        if (snapshot.Windows.Count != _lastWindows)
        {
            _lastWindows = snapshot.Windows.Count;
            AltTabController.Log($"host: snapshot windows={_lastWindows}");
        }

        Send(new HostMessage
        {
            Cmd = "snapshot",
            Snapshot = HostProtocol.ToDto(snapshot),
        });
    }

    private void Close()
    {
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _stream = null;
    }

    public void Dispose()
    {
        _stop.Set();
        Close();
    }

    /// <summary>功能进程是不是以管理员身份跑着（拖放/注入能不能对管理员窗口生效全看它）。</summary>
    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);

            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
