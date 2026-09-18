using System.Windows.Media;
using ExplorerDock.Interop;
using ExplorerDock.Models;

namespace ExplorerDock.Services;

/// <summary>
/// <see cref="IWindowHost"/> 的同进程实现：就是现在这套 ExplorerWatcher + 原生调用。
///
/// 拆进程时换成走命名管道的远程实现，界面代码一行都不用改。
/// </summary>
internal sealed class LocalWindowHost : IWindowHost
{
    private readonly ExplorerWatcher _watcher = new();
    private readonly HostKeyboard _keyboard;

    public LocalWindowHost(bool takeoverEnabled = true)
    {
        // 接管范围（自动接管开关 + 手动名单）由宿主侧判定：本地实现就是本进程
        TakeoverState.SetScope(App.Settings.AutoTakeoverMultiWindow, App.Settings.TakeoverProcesses);
        TakeoverState.SetGroupMode(App.Settings.AltTabGroupScope);

        _watcher.TakeoverEnabled = takeoverEnabled;
        _watcher.SnapshotUpdated += snapshot => SnapshotUpdated?.Invoke(snapshot);

        _keyboard = new HostKeyboard(action => KeyAction?.Invoke(action));
    }

    public event Action<ExplorerSnapshot>? SnapshotUpdated;

    public event Action<string>? KeyAction;

    public bool KeyboardInstalled => _keyboard.Installed;

    public void SetKeyState(bool altTabTakeover, bool clipboardTakeover, bool panelOpen)
        => _keyboard.SetState(altTabTakeover, clipboardTakeover, panelOpen, App.Settings.AltTabFullscreenPassthrough);

    public void Start()
    {
        _watcher.Start();
        _keyboard.Install();
    }

    public void SetTakeover(bool enabled)
    {
        _watcher.TakeoverEnabled = enabled;

        if (enabled) _watcher.ReapplyNow();
        else _watcher.RestoreNow();
    }

    public void ReapplyNow() => _watcher.ReapplyNow();

    public void SetTakeoverScope(bool autoMultiWindow, IReadOnlyList<string> processes)
    {
        TakeoverState.SetScope(autoMultiWindow, processes);
        _watcher.ReapplyNow();
    }

    public List<RunningProcessInfo> RunningProcesses() => ExplorerWatcher.EnumerateRunningProcesses();

    public void SetAltTabGroupMode(AltTabGroupMode mode) => TakeoverState.SetGroupMode(mode);

    public void RestoreNow() => _watcher.RestoreNow();

    public bool Activate(IntPtr hwnd, int tabIndex)
    {
        if (!NativeMethods.IsWindow(hwnd)) return false;

        var ok = NativeMethods.ForceForeground(hwnd);

        // 多标签窗口：窗口提到前面之后再切到目标标签页
        if (tabIndex >= 0) ExplorerTabs.Activate(hwnd, tabIndex);

        return ok;
    }

    public void Minimize(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) return;

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
    }

    public int SelectedTab(IntPtr hwnd) => ExplorerTabs.SelectedIndex(hwnd);

    public List<AltTabWindowInfo> SnapshotCards()
    {
        var items = AltTabWindowList.Snapshot();

        // 图标还是在这一侧取（跨进程时图标句柄传不过去，所以这一步留在原地最省事）
        FillIcons(items);

        return items;
    }

    public List<AltTabWindowInfo> ProcessCards(IntPtr hwnd)
    {
        var items = AltTabWindowList.ProcessWindows(hwnd);
        FillIcons(items);

        return items;
    }

    public ImageSource? WindowIcon(IntPtr hwnd) => ShellInterop.GetWindowIcon(hwnd);

    public void SendPaste() => NativeMethods.SendCtrlV();

    public bool PasteIntoFolder(IntPtr hwnd) => ExplorerTabs.InvokePasteCommand(hwnd);

    public bool CloseWindow(IntPtr hwnd) => ExplorerWatcher.CloseWindow(hwnd);

    public bool CloseProcessWindows(IntPtr hwnd) => ExplorerWatcher.CloseProcessWindows(hwnd) > 0;

    public bool KillProcesses(IntPtr hwnd) => ExplorerWatcher.KillProcesses(hwnd) > 0;

    public void RestoreAltKey() => NativeMethods.RestoreAltKeyState();

    public void ClickAtCursor()
    {
        if (NativeMethods.GetCursorPos(out var point)) NativeMethods.ClickAt(point.X, point.Y);
    }

    public bool IsForegroundWindow(IntPtr hwnd) => NativeMethods.GetForegroundWindow() == hwnd;

    public bool IsLeftButtonDown() => NativeMethods.IsKeyDown(0x01);   // VK_LBUTTON

    public void Dispose()
    {
        try
        {
            _keyboard.Dispose();
        }
        catch
        {
            // 退出流程里忽略
        }

        try
        {
            _watcher.Dispose();
        }
        catch
        {
            // 退出流程里忽略
        }
    }

    private static void FillIcons(List<AltTabWindowInfo> items)
    {
        foreach (var item in items)
        {
            try
            {
                item.Icon = ShellInterop.GetWindowIcon(item.Handle);
            }
            catch
            {
                // 单个窗口取不到图标不算事
            }
        }
    }
}
