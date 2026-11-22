using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows.Media;
using ExplorerDock.Interop;
using ExplorerDock.Models;

namespace ExplorerDock.Services;

/// <summary>
/// <see cref="IWindowHost"/> 的跨进程实现：界面进程这边只负责把命令写给
/// <c>--host</c> 进程、等回复，并把 Host 推来的窗口快照转成普通快照对象。
///
/// 界面进程是普通权限（这样才收得到资源管理器发来的拖放等消息），
/// 需要管理员权限的事全在 Host 那边做。
/// </summary>
internal sealed class RemoteWindowHost : IWindowHost
{
    private sealed class PendingCall
    {
        public HostMessage Request { get; init; } = new();
        public ManualResetEventSlim Done { get; } = new(false);
        public Exception? Error { get; set; }
    }

    private sealed class OutgoingCall
    {
        public HostMessage Message { get; init; } = new();
        public long Deadline { get; init; }
    }

    private readonly object _pendingGate = new();
    private readonly Dictionary<int, PendingCall> _pending = new();
    private readonly BlockingCollection<OutgoingCall> _outgoing = new(128);
    private readonly object _snapshotGate = new();
    private readonly AutoResetEvent _snapshotSignal = new(false);
    private readonly object _keyStateGate = new();
    private readonly AutoResetEvent _keyStateSignal = new(false);

    private NamedPipeServerStream? _server;
    private Stream? _stream;
    private Thread? _reader;
    private Thread? _writer;
    private Thread? _snapshotThread;
    private Thread? _keyStateThread;
    private SnapshotDto? _pendingSnapshot;
    private KeyStateSnapshot? _pendingKeyState;
    private volatile bool _snapshotStop;
    private volatile bool _keyStateStop;
    private Process? _process;
    private int _nextId;
    private int _lastWindows = -1;
    private bool _keyboardInstalled;
    private volatile bool _connected;

    public event Action<ExplorerSnapshot>? SnapshotUpdated;

    public event Action<string>? KeyAction;

    public bool Connected => _connected;

    public bool KeyboardInstalled => _keyboardInstalled;

    /// <summary>拉起 Host 进程并连上它。返回 false = 没连上（调用方该退回同进程实现）。</summary>
    public bool Connect(bool elevated, int timeoutMs = 20000)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return false;

        try
        {
            // 管道由**界面进程**（普通权限）当服务端：提权进程创建的命名管道，
            // 默认 ACL 不允许普通权限去连（实测 Access denied），反过来就没这个问题。
            // 两边一律用同步读写：异步打开的管道上再同步读会出岔子。
            var server = new NamedPipeServerStream(
                HostProtocol.PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            _server = server;

            var accepted = new ManualResetEventSlim(false);

            var waiter = new Thread(() =>
            {
                try
                {
                    server.WaitForConnection();
                    accepted.Set();
                }
                catch
                {
                    // 管道被关掉之类，当作没连上
                }
            })
            {
                IsBackground = true,
                Name = "ExplorerDock.HostWait",
            };

            waiter.Start();

            var taskResult = elevated
                ? StartupRegistration.TryStartElevatedTask(exe)
                : StartupRegistration.ScheduledHostStartResult.NotReady;

            bool connected = false;

            if (taskResult == StartupRegistration.ScheduledHostStartResult.LaunchAccepted)
            {
                AltTabController.Log("host: scheduled task launch accepted, waiting for pipe");
            }
            else if (taskResult == StartupRegistration.ScheduledHostStartResult.LaunchFailed)
            {
                AltTabController.Log("host: scheduled task launch failed, checking for an existing host");
                connected = accepted.Wait(3000);
            }

            if (taskResult != StartupRegistration.ScheduledHostStartResult.LaunchAccepted && !connected)
            {
                AltTabController.Log("host: falling back to direct host launch");

                var info = new ProcessStartInfo(exe)
                {
                    Arguments = "--host",
                    UseShellExecute = true,
                };

                // 需要接管管理员程序时，功能进程才请求提权（界面进程始终普通权限）
                if (elevated) info.Verb = "runas";

                _process = Process.Start(info);
            }

            if (!connected && !accepted.Wait(timeoutMs))
            {
                AltTabController.Log("host: connect timed out");
                ClosePendingServer();
                return false;
            }

            _stream = server;
            _connected = true;

            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ExplorerDock.HostClient" };
            _reader.Start();
            StartWriter();
            StartSnapshotWorker();
            StartKeyStateWorker();

            AltTabController.Log("host: connected");
            return true;
        }
        catch (Exception ex)
        {
            // 用户在 UAC 弹窗上点了"否"，或者进程/管道起不来
            AltTabController.Log($"host: connect failed {ex.GetType().Name}: {ex.Message}");
            ClosePendingServer();
            return false;
        }
    }

    private void ClosePendingServer()
    {
        try
        {
            _server?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _server = null;
    }

    // ---------- IWindowHost ----------

    public void Start()
    {
        Request(new HostMessage
        {
            Cmd = HostProtocol.CmdStart,
            Enabled = App.Settings.TakeoverEnabled,
            TakeoverExplorer = App.Settings.TakeoverExplorer,
        });
    }

    public void SetTakeover(bool enabled)
        => Request(new HostMessage { Cmd = HostProtocol.CmdTakeover, Enabled = enabled });

    public void SetExplorerTakeover(bool enabled)
        => Request(new HostMessage { Cmd = HostProtocol.CmdExplorerTakeover, Enabled = enabled });

    public void SetTakeoverScope(bool autoMultiWindow, IReadOnlyList<string> processes)
    {
        var message = new HostMessage
        {
            Cmd = HostProtocol.CmdTakeoverScope,
            AutoTakeover = autoMultiWindow,
            Processes = processes.ToList(),
        };

        Request(message);
    }

    public void SetAltTabGroupMode(AltTabGroupMode mode)
        => Request(new HostMessage { Cmd = HostProtocol.CmdAltTabGroup, Value = (int)mode });

    public List<RunningProcessInfo> RunningProcesses()
    {
        var result = new List<RunningProcessInfo>();

        var message = new HostMessage { Cmd = HostProtocol.CmdRunningProcesses };

        // 要枚举一遍全部窗口，超时给宽一点
        if (!Request(message, 8000) || message.ProcessList is null) return result;

        foreach (var dto in message.ProcessList)
        {
            result.Add(new RunningProcessInfo
            {
                Name = dto.Name,
                ExePath = dto.ExePath,
                WindowCount = dto.WindowCount,
                SampleTitle = dto.SampleTitle,
            });
        }

        return result;
    }

    public void SetKeyState(bool altTabTakeover, bool clipboardTakeover, bool panelOpen)
    {
        QueueKeyState(new KeyStateSnapshot(
            altTabTakeover,
            clipboardTakeover,
            panelOpen,
            App.Settings.AltTabFullscreenPassthrough));
    }

    public void ReapplyNow()
        => Request(new HostMessage { Cmd = HostProtocol.CmdReapply });

    public void RestoreNow()
        => Request(new HostMessage { Cmd = HostProtocol.CmdRestoreAll });

    public bool Activate(IntPtr hwnd, int tabIndex)
    {
        var message = new HostMessage { Cmd = HostProtocol.CmdActivate, Hwnd = hwnd.ToInt64(), Tab = tabIndex };
        return Request(message) && message.Ok;
    }

    public Task<bool> ActivateAsync(IntPtr hwnd, int tabIndex)
        => Task.Run(() => Activate(hwnd, tabIndex));

    public void Minimize(IntPtr hwnd)
        => Request(new HostMessage { Cmd = HostProtocol.CmdMinimize, Hwnd = hwnd.ToInt64() });

    public int SelectedTab(IntPtr hwnd)
    {
        var message = new HostMessage { Cmd = HostProtocol.CmdSelectedTab, Hwnd = hwnd.ToInt64() };
        return Request(message) ? message.Value : -1;
    }

    public List<AltTabWindowInfo> SnapshotCards()
        => RequestCards(HostProtocol.CmdCards, IntPtr.Zero);

    public List<AltTabWindowInfo> ProcessCards(IntPtr hwnd)
        => RequestCards(HostProtocol.CmdProcessCards, hwnd);

    /// <summary>图标在界面进程这边按句柄取（跨权限取不到时自然降级成通用图标）。</summary>
    public ImageSource? WindowIcon(IntPtr hwnd) => ShellInterop.GetWindowIcon(hwnd);

    public void SendPaste()
        => Request(new HostMessage { Cmd = HostProtocol.CmdSendPaste });

    public bool PasteIntoFolder(IntPtr hwnd)
    {
        var message = new HostMessage { Cmd = HostProtocol.CmdPasteInto, Hwnd = hwnd.ToInt64() };

        // UI Automation 调用可能慢一点，超时给宽些
        return Request(message, 3000) && message.Ok;
    }

    public bool CloseWindow(IntPtr hwnd)
    {
        var message = new HostMessage { Cmd = HostProtocol.CmdCloseWindow, Hwnd = hwnd.ToInt64() };
        return Request(message, 3000) && message.Ok;
    }

    public bool CloseProcessWindows(IntPtr hwnd)
    {
        var message = new HostMessage { Cmd = HostProtocol.CmdCloseProcessWindows, Hwnd = hwnd.ToInt64() };
        return Request(message, 5000) && message.Ok;
    }

    public bool KillProcesses(IntPtr hwnd)
    {
        var message = new HostMessage { Cmd = HostProtocol.CmdKillProcesses, Hwnd = hwnd.ToInt64() };
        return Request(message, 8000) && message.Ok;
    }

    public void RestoreAltKey()
        => Request(new HostMessage { Cmd = HostProtocol.CmdSendAltUp });

    public void ClickAtCursor()
        => Request(new HostMessage { Cmd = HostProtocol.CmdClickCursor });

    public bool IsForegroundWindow(IntPtr hwnd)
    {
        var message = new HostMessage { Cmd = HostProtocol.CmdIsForeground, Hwnd = hwnd.ToInt64() };

        // 面板显示期间每 300ms 问一次，超时给短一点
        return Request(message, 800) && message.Ok;
    }

    public Task<bool> IsForegroundWindowAsync(IntPtr hwnd)
        => Task.Run(() => IsForegroundWindow(hwnd));

    public bool IsLeftButtonDown()
    {
        var message = new HostMessage { Cmd = HostProtocol.CmdIsLeftDown };

        // 只在"本地以为已经松手"时才问，超时短一点
        return Request(message, 500) && message.Ok;
    }

    public void Dispose()
    {
        _snapshotStop = true;
        _snapshotSignal.Set();
        _keyStateStop = true;
        _keyStateSignal.Set();

        try
        {
            Request(new HostMessage { Cmd = HostProtocol.CmdExit }, 1500);
        }
        catch
        {
            // 忽略
        }

        _outgoing.CompleteAdding();
        _connected = false;

        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _server?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _stream = null;
        _server = null;

        try
        {
            if (_process is { HasExited: false }) _process.WaitForExit(1500);
        }
        catch
        {
            // 忽略
        }

        try
        {
            if (_process is { HasExited: false }) _process.Kill();
        }
        catch
        {
            // 忽略
        }

        _process = null;
    }

    // ---------- 管道 ----------

    private List<AltTabWindowInfo> RequestCards(string command, IntPtr hwnd)
    {
        var result = new List<AltTabWindowInfo>();

        var message = new HostMessage { Cmd = command, Hwnd = hwnd.ToInt64() };
        if (!Request(message, 8000) || message.Cards is null) return result;

        foreach (var card in message.Cards) result.Add(HostProtocol.FromDto(card, loadIcon: false));

        return result;
    }

    /// <summary>
    /// 默认等待上限：1.5 秒。
    ///
    /// 这个等待是**在调用线程上同步做的** —— 给太长就等于"点一下冻住界面这么久"。
    /// 宿主那边现在对卡住的操作会快速失败，正常操作都是毫秒级，1.5 秒足够；
    /// 真卡住时宁快速失败，也别把悬浮栏冻住。
    /// </summary>
    private bool Request(HostMessage message, int timeoutMs = 1500)
    {
        var stream = _stream;
        if (stream is null || !_connected) return false;

        long started = Environment.TickCount64;
        var call = new PendingCall { Request = message };
        message.Id = Interlocked.Increment(ref _nextId);

        lock (_pendingGate)
        {
            _pending[message.Id] = call;
        }

        if (!_outgoing.TryAdd(new OutgoingCall
        {
            Message = message,
            Deadline = started + timeoutMs,
        }))
        {
            lock (_pendingGate)
            {
                _pending.Remove(message.Id);
            }

            AltTabController.Log($"ui: write queue stopped {message.Cmd}");
            return false;
        }

        if (call.Done.Wait(timeoutMs))
        {
            if (call.Error is null) return true;

            lock (_pendingGate)
            {
                _pending.Remove(message.Id);
            }

            AltTabController.Log($"ui: write failed {call.Error.GetType().Name}: {call.Error.Message}");
            return false;
        }

        lock (_pendingGate)
        {
            _pending.Remove(message.Id);
        }

        AltTabController.Log($"ui: request timeout {message.Cmd}");
        return false;
    }

    private void StartWriter()
    {
        if (_writer is not null) return;

        _writer = new Thread(() =>
        {
            foreach (var outgoing in _outgoing.GetConsumingEnumerable())
            {
                if (Environment.TickCount64 > outgoing.Deadline)
                {
                    lock (_pendingGate)
                    {
                        _pending.Remove(outgoing.Message.Id);
                    }

                    continue;
                }

                try
                {
                    var stream = _stream;
                    if (stream is null) throw new IOException("host pipe is closed");

                    HostProtocol.WriteLine(stream, HostProtocol.Encode(outgoing.Message));
                }
                catch (Exception ex)
                {
                    _connected = false;
                    FailPending(ex);
                    continue;
                }
            }
        })
        {
            IsBackground = true,
            Name = "ExplorerDock.HostWriter",
        };

        _writer.Start();
    }

    private async void ReadLoop()
    {
        var stream = _stream;
        if (stream is null) return;

        while (true)
        {
            var line = await HostProtocol.ReadLineAsync(stream, CancellationToken.None);
            if (line is null)
            {
                AltTabController.Log("ui: host pipe closed");
                break;
            }

            var message = HostProtocol.Decode(line);
            if (message is null) continue;

            if (message.Id > 0)
            {
                PendingCall? call;

                lock (_pendingGate)
                {
                    _pending.Remove(message.Id, out call);
                }

                if (call is not null)
                {
                    CopyReply(message, call.Request);
                    call.Done.Set();
                }

                continue;
            }

            if (message.Cmd == HostProtocol.CmdKeyAction && message.Action is { Length: > 0 } keyAction)
            {
                try
                {
                    KeyAction?.Invoke(keyAction);
                }
                catch
                {
                    // 界面正在退出之类，丢掉这个动作
                }

                continue;
            }

            if (message.Cmd == "snapshot" && message.Snapshot is not null)
            {
                QueueSnapshot(message.Snapshot);
            }
        }

        AltTabController.Log("ui: read loop end");
        FailPending(new IOException("host pipe closed"));
        _connected = false;
    }

    private void FailPending(Exception error)
    {
        List<PendingCall> calls;

        lock (_pendingGate)
        {
            calls = _pending.Values.ToList();
            _pending.Clear();
        }

        foreach (var call in calls)
        {
            call.Error = error;
            call.Done.Set();
        }
    }

    private void StartSnapshotWorker()
    {
        if (_snapshotThread is not null) return;

        _snapshotThread = new Thread(() =>
        {
            while (true)
            {
                _snapshotSignal.WaitOne();

                SnapshotDto? dto;

                lock (_snapshotGate)
                {
                    dto = _pendingSnapshot;
                    _pendingSnapshot = null;
                }

                if (dto is null)
                {
                    if (_snapshotStop) return;
                    continue;
                }

                if (dto.Windows.Count != _lastWindows)
                {
                    _lastWindows = dto.Windows.Count;
                    AltTabController.Log($"ui: snapshot windows={_lastWindows}");
                }

                try
                {
                    SnapshotUpdated?.Invoke(HostProtocol.FromDto(dto));
                }
                catch (Exception ex)
                {
                    AltTabController.Log($"ui: snapshot apply failed {ex.GetType().Name}: {ex.Message}");
                }
            }
        })
        {
            IsBackground = true,
            Name = "ExplorerDock.SnapshotApply",
        };

        _snapshotThread.Start();
    }

    private void QueueSnapshot(SnapshotDto snapshot)
    {
        lock (_snapshotGate)
        {
            _pendingSnapshot = snapshot;
        }

        _snapshotSignal.Set();
    }

    private void StartKeyStateWorker()
    {
        if (_keyStateThread is not null) return;

        _keyStateThread = new Thread(() =>
        {
            while (true)
            {
                _keyStateSignal.WaitOne();

                KeyStateSnapshot? state;

                lock (_keyStateGate)
                {
                    state = _pendingKeyState;
                    _pendingKeyState = null;
                }

                if (state is null)
                {
                    if (_keyStateStop) return;
                    continue;
                }

                var message = new HostMessage
                {
                    Cmd = HostProtocol.CmdKeyState,
                    TakeoverAltTab = state.AltTabTakeover,
                    TakeoverClipboard = state.ClipboardTakeover,
                    PanelOpen = state.PanelOpen,
                    FullscreenPassthrough = state.FullscreenPassthrough,
                };

                if (Request(message, 800)) _keyboardInstalled = message.Ok;
            }
        })
        {
            IsBackground = true,
            Name = "ExplorerDock.KeyState",
        };

        _keyStateThread.Start();
    }

    private void QueueKeyState(KeyStateSnapshot state)
    {
        lock (_keyStateGate)
        {
            _pendingKeyState = state;
        }

        _keyStateSignal.Set();
    }

    private sealed record KeyStateSnapshot(
        bool AltTabTakeover,
        bool ClipboardTakeover,
        bool PanelOpen,
        bool FullscreenPassthrough);

    /// <summary>把回复内容拷回请求对象（调用方拿的就是它）。</summary>
    private static void CopyReply(HostMessage reply, HostMessage request)
    {
        request.Ok = reply.Ok;
        request.Error = reply.Error;
        request.Value = reply.Value;
        request.Cards = reply.Cards;
        request.ProcessList = reply.ProcessList;
    }
}
