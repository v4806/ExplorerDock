using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media;
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
        ApplyMenuTheme();
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

        // 启动时还没有任何快照，先按"当前有没有文件夹窗口"判定一次显隐，
        // 否则开机自启会留下一条空白的悬浮栏
        Dock.RefreshVisibility();

        _watcher = new ExplorerWatcher
        {
            TakeoverEnabled = Settings.TakeoverEnabled,
        };
        _watcher.SnapshotUpdated += OnSnapshotUpdated;
        _watcher.Start();

        _tray = new TrayIconManager(this);

        _quitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName);
        StartQuitListener();

        // 调试用：ExplorerDock.exe --theme-editor 直接打开自定义主题面板
        if (e.Args.Any(a => a.Equals("--theme-editor", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(new Action(OpenThemeEditor));
        }

        // 调试用：--appmenu 在鼠标位置弹出托盘那套菜单（用来验证外观与关闭行为）
        if (e.Args.Any(a => a.Equals("--appmenu", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(new Action(() => ShowTrayMenu()));
        }
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

            // Show() 之后阴影层可能盖到悬浮栏上面（表现为整条变成一块纯色），
            // 这里立刻重排一次层级
            Dock.ReorderLayers();
            Dock.RefreshVisibility();
        }
        else
        {
            // 走 RefreshVisibility：它会 Hide() 悬浮栏**并把阴影层一起隐藏**。
            // 之前只调 Hide()，画阴影那层留在原地，就成了一块同形状的纯色块。
            Dock.RefreshVisibility();
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

    /// <summary>应急：把所有文件夹窗口的按钮还给任务栏（清掉统一分组 + 还原可能被摘除的按钮）。</summary>
    public static void RestoreEverythingToTaskbar()
    {
        using var taskbar = new TaskbarTweaker();

        foreach (var hwnd in ExplorerWatcher.EnumerateWindows())
        {
            try
            {
                AppUserModelId.TrySet(hwnd, null);
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

    // ---------- 主题 ----------

    /// <summary>切换配色方案（跟随系统 / 深色 / 浅色 / 自定义）。</summary>
    public void SetTheme(DockTheme theme)
    {
        Settings.Theme = theme;
        Settings.Save();
        PreviewTheme();
    }

    /// <summary>把当前设置立刻应用到悬浮栏、菜单与托盘（不保存）。</summary>
    public void PreviewTheme()
    {
        ApplyMenuTheme();
        Dock?.ApplyThemeAndRebuild();
        _tray?.ApplyTheme();
    }

    /// <summary>托盘右键：弹出与悬浮栏相同的现代深色菜单。</summary>
    public void ShowTrayMenu() => Dock?.ShowTrayMenu();

    private ThemeEditorWindow? _themeEditor;

    /// <summary>打开自定义主题设置面板。</summary>
    public void OpenThemeEditor()
    {
        try
        {
            if (_themeEditor is { IsVisible: true })
            {
                _themeEditor.Activate();
                return;
            }

            _themeEditor = new ThemeEditorWindow(this);
            _themeEditor.Closed += (_, _) => _themeEditor = null;
            _themeEditor.Show();
        }
        catch (Exception ex)
        {
            // 宁可弹个提示，也不要整个程序挂掉
            MessageBox.Show(
                $"自定义主题面板打开失败：\n\n{ex.GetType().Name}: {ex.Message}",
                "ExplorerDock",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>菜单与提示气泡的配色走 DynamicResource，这里整体换一套刷子。</summary>
    public static void ApplyMenuTheme()
    {
        var resources = Current.Resources;

        // 自定义主题下，菜单与提示气泡也跟着悬浮栏的配色走
        if (Settings.Theme == DockTheme.Custom)
        {
            var background = ParseColor(Settings.CustomBackground, Color.FromArgb(0xF2, 0x1B, 0x1B, 0x1F));
            var border = ParseColor(Settings.CustomBorder, Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            var text = ParseColor(Settings.CustomText, Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2));
            var hover = ParseColor(Settings.CustomHover, Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

            // 菜单底色比悬浮栏实一点，否则叠在窗口上会看不清内容
            resources["DockMenuBackground"] = new SolidColorBrush(Color.FromArgb(
                Math.Max(background.A, (byte)0xE0), background.R, background.G, background.B));
            resources["DockMenuBorder"] = new SolidColorBrush(border);
            resources["DockMenuForeground"] = new SolidColorBrush(text);
            resources["DockMenuHighlight"] = new SolidColorBrush(hover);
            resources["DockMenuDisabled"] = new SolidColorBrush(Color.FromArgb(0x66, text.R, text.G, text.B));
            resources["DockMenuSeparator"] = new SolidColorBrush(Color.FromArgb(0x26, text.R, text.G, text.B));
            return;
        }

        bool light = Settings.ResolveLightTheme();

        if (light)
        {
            resources["DockMenuBackground"] = new SolidColorBrush(Color.FromArgb(0xFA, 0xFA, 0xFA, 0xFA));
            resources["DockMenuBorder"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x24, 0x24, 0x24));
            resources["DockMenuForeground"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));
            resources["DockMenuHighlight"] = new SolidColorBrush(Color.FromArgb(0x24, 0x00, 0x00, 0x00));
            resources["DockMenuDisabled"] = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
            resources["DockMenuSeparator"] = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));
        }
        else
        {
            resources["DockMenuBackground"] = new SolidColorBrush(Color.FromArgb(0xF2, 0x1B, 0x1B, 0x1F));
            resources["DockMenuBorder"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x3A, 0x3A, 0x42));
            resources["DockMenuForeground"] = new SolidColorBrush(Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2));
            resources["DockMenuHighlight"] = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            resources["DockMenuDisabled"] = new SolidColorBrush(Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF));
            resources["DockMenuSeparator"] = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        }
    }

    /// <summary>把 #AARRGGBB 字符串解析成颜色，失败用兜底色。</summary>
    private static Color ParseColor(string? text, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text) && ColorConverter.ConvertFromString(text) is Color color)
            {
                return color;
            }
        }
        catch
        {
            // 非法输入用兜底
        }

        return fallback;
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
