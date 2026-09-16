using System.Diagnostics;
using System.Threading;
using System.Windows;
using ExplorerDock.Interop;
using ExplorerDock.Models;
using ExplorerDock.Services;

namespace ExplorerDock;

public partial class App : Application
{
    private const string StartupValueName = "ExplorerDock";
    private const string QuitEventName = @"Local\ExplorerDock.QuitEvent.v1";

    private Mutex? _mutex;
    private EventWaitHandle? _quitEvent;
    private ExplorerWatcher? _watcher;
    private TrayIconManager? _tray;
    private TaskbarTweaker? _taskbar;

    public static Settings Settings { get; private set; } = new();

    public DockWindow? Dock { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _mutex = new Mutex(true, @"Local\ExplorerDock.SingleInstance.v1", out bool createdNew);

        // ExplorerDock.exe --quit：让正在运行的实例走正常退出流程（会先把窗口按钮还给任务栏）
        if (e.Args.Any(a => a.Equals("--quit", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var quit = EventWaitHandle.OpenExisting(QuitEventName);
                quit.Set();
            }
            catch
            {
                // 没有实例在跑
            }

            Shutdown();
            return;
        }

        if (!createdNew)
        {
            MessageBox.Show(
                "ExplorerDock 已经在运行了，请查看右下角托盘图标。",
                "ExplorerDock",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Settings = Settings.Load();
        _taskbar = new TaskbarTweaker();
        ApplyStartupRegistration();

        // 应急出口：ExplorerDock.exe --restore 把所有文件夹按钮还给任务栏后退出
        if (e.Args.Any(a => a.Equals("--restore", StringComparison.OrdinalIgnoreCase)))
        {
            RestoreEverythingToTaskbar();
            Shutdown();
            return;
        }

        Dock = new DockWindow();
        if (Settings.ShowDock) Dock.Show();

        _watcher = new ExplorerWatcher
        {
            TakeoverEnabled = Settings.TakeoverEnabled,
        };
        _watcher.SnapshotUpdated += OnSnapshotUpdated;
        _watcher.Start();

        _tray = new TrayIconManager(this);

        _quitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName);
        StartQuitListener();
    }

    private void StartQuitListener()
    {
        var thread = new Thread(() =>
        {
            try
            {
                if (_quitEvent is null) return;
                _quitEvent.WaitOne();
                Dispatcher.BeginInvoke(new Action(ExitApp));
            }
            catch
            {
                // 忽略
            }
        })
        {
            IsBackground = true,
            Name = "ExplorerDock.QuitListener",
        };

        thread.Start();
    }

    private void OnSnapshotUpdated(ExplorerSnapshot snapshot)
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() => Dock?.ApplySnapshot(snapshot)));
        }
        catch
        {
            // 程序正在退出
        }
    }

    // ---------- 设置项 ----------

    public void SetTakeover(bool enabled)
    {
        Settings.TakeoverEnabled = enabled;
        Settings.Save();

        if (_watcher is null) return;
        _watcher.TakeoverEnabled = enabled;

        if (enabled) _watcher.ReapplyNow();
        else _watcher.RestoreNow();
    }

    public void SetHideWhenEmpty(bool enabled)
    {
        Settings.HideWhenEmpty = enabled;
        Settings.Save();
    }

    public void SetShowDock(bool show)
    {
        Settings.ShowDock = show;
        Settings.Save();

        if (Dock is null) return;

        if (show)
        {
            Dock.Show();
            Dock.RefreshVisibility();
        }
        else
        {
            Dock.Hide();
        }
    }

    /// <summary>切换"显示完整标题"后让按钮重新排版。</summary>
    public void RebuildDockItems()
    {
        Dock?.RefreshVisibility();
    }

    /// <summary>把悬浮栏放回屏幕底部居中。</summary>
    public void ResetDockPosition()
    {
        Dock?.ResetPosition();
    }

    /// <summary>把某个窗口临时还回任务栏（下次轮询若接管开启会再次摘除）。</summary>
    public void RestoreToTaskbar(IntPtr hwnd)
    {
        _taskbar?.Restore(hwnd);
    }

    /// <summary>应急：把所有文件夹窗口的按钮还给任务栏。</summary>
    public static void RestoreEverythingToTaskbar()
    {
        using var taskbar = new TaskbarTweaker();

        foreach (var hwnd in ExplorerWatcher.EnumerateWindows())
        {
            try
            {
                taskbar.Restore(hwnd);
            }
            catch
            {
                // 窗口可能已经关了
            }
        }
    }

    /// <summary>把所有窗口还回任务栏，并暂停接管，方便用户对照。</summary>
    public void RestoreAllToTaskbar()
    {
        SetTakeover(false);
    }

    public void SetRunAtStartup(bool enabled)
    {
        Settings.RunAtStartup = enabled;
        Settings.Save();
        ApplyStartupRegistration();
    }

    /// <summary>让注册表里的自启项与设置保持一致（默认就是开启，首次运行也要落盘）。</summary>
    private void ApplyStartupRegistration()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return;

            if (Settings.RunAtStartup)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe)) key.SetValue(StartupValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(StartupValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 写注册表失败（极少见）时忽略
        }
    }

    // ---------- 维护 ----------

    /// <summary>关闭所有资源管理器文件夹窗口。</summary>
    public static void CloseAllExplorerWindows()
    {
        foreach (var hwnd in ExplorerWatcher.EnumerateWindows())
        {
            try
            {
                if (NativeMethods.IsWindow(hwnd))
                {
                    NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch
            {
                // 忽略单个失败
            }
        }
    }

    public static void RestartExplorer()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // 忽略单个失败
                }
            }

            Thread.Sleep(900);
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch
        {
            // 忽略
        }
    }

    public void ExitApp()
    {
        try
        {
            _watcher?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _tray?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _taskbar?.Dispose();
        }
        catch
        {
            // 忽略
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _watcher?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _mutex?.Dispose();
        base.OnExit(e);
    }
}
