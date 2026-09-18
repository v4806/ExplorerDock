using System.Collections.Concurrent;
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

        AltTabController.Log($"host: keyboard installed={_host.KeyboardInstalled}");

        while (!_stop.IsSet)
        {
            try
            {
                AcceptAndServeAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // 界面进程断开、管道出错：退回去等下一次连接
            }

            Close();

            if (_stop.IsSet) break;

            Thread.Sleep(300);
        }

        _host.Dispose();
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

        T result = fallback;
        using var done = new ManualResetEventSlim(false);

        try
        {
            _guiQueue.Add(() =>
            {
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
                    done.Set();
                }
            });
        }
        catch
        {
            return fallback;
        }

        return done.Wait(8000) ? result : fallback;
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
        AltTabController.Log("host: ui connected");

        while (!_stop.IsSet)
        {
            var line = await HostProtocol.ReadLineAsync(client, CancellationToken.None);
            if (line is null) break;

            var message = HostProtocol.Decode(line);
            if (message is null) continue;

            // 窗口操作统一丢到 GUI 线程上做（见 _guiQueue 的注释）：
            // 线程池线程没有消息队列，SetFocus 那种操作在那里是不可靠的
            if (!RunOnGui(() => Handle(message), false)) break;
        }

        AltTabController.Log("host: ui disconnected");
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
                    _host.RestoreNow();
                    _stop.Set();
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
