using System.Diagnostics;
using System.IO;
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
    private AltTabController? _altTab;
    private ClipboardStore? _clipStore;
    private ClipboardMonitor? _clipMonitor;
    private ClipboardController? _clipboard;

    public static Settings Settings { get; private set; } = new();

    /// <summary>主题变了。各常驻窗口（剪贴板面板这类）订阅它刷新自己的配色。</summary>
    public static event Action? ThemeChanged;

    public DockWindow? Dock { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 调试用：--clip-dump 只读导出剪贴板库。放在最前面：它不受单实例锁限制，
        // 也不会因为"当前是管理员权限启动"被提权重启顶掉。
        if (e.Args.Any(a => a.Equals("--clip-dump", StringComparison.OrdinalIgnoreCase)))
        {
            Settings = Settings.Load();

            using (var store = new ClipboardStore())
            {
                store.Load();
                DumpClipboardStore(store);
            }

            Shutdown();
            return;
        }

        // 调试用：--clip-set "文本" 把一段文本放进剪贴板（也用来验证采集链路）
        int setIndex = Array.FindIndex(e.Args, a => a.Equals("--clip-set", StringComparison.OrdinalIgnoreCase));
        if (setIndex >= 0 && setIndex + 1 < e.Args.Length)
        {
            ClipboardWriter.WriteText(e.Args[setIndex + 1]);
            Shutdown();
            return;
        }

        // 调试用：--clip-paste <id> 把历史里某一条按原格式写回剪贴板
        // （用来验证"点击条目粘贴"实际写进去的是哪些格式，不经过面板）
        int pasteIndex = Array.FindIndex(e.Args, a => a.Equals("--clip-paste", StringComparison.OrdinalIgnoreCase));
        if (pasteIndex >= 0 && pasteIndex + 1 < e.Args.Length)
        {
            using (var store = new ClipboardStore())
            {
                store.Load();

                var target = store.Items.FirstOrDefault(i => i.Id == e.Args[pasteIndex + 1]);

                if (target is not null)
                {
                    ClipboardWriter.WriteAll(
                        target.Text,
                        target.ImageFormat,
                        store.GetImageBytes(target),
                        target.Formats);
                }
            }

            Shutdown();
            return;
        }

        // 接力重启（提权、降权都走这条）：新进程先把旧实例请走，再接手单实例锁
        if (e.Args.Any(a => a.Equals("--relaunch", StringComparison.OrdinalIgnoreCase)))
        {
            WaitForPreviousInstance();
        }

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

        // 接力场景：旧实例可能正在退场（比如刚被要求换成普通权限重新启动），
        // 先给它一点时间让出单实例锁，别抢着报"已经在运行"。
        for (int i = 0; i < 15 && !createdNew; i++)
        {
            Thread.Sleep(200);

            try
            {
                _mutex?.Dispose();
            }
            catch
            {
                // 忽略
            }

            _mutex = new Mutex(true, @"Local\ExplorerDock.SingleInstance.v1", out createdNew);
        }

        if (!createdNew)
        {
            Views.ConfirmDialog.Notify(
                "ExplorerDock 已经在运行",
                "右下角的托盘图标就是它，双击可以显示或隐藏悬浮栏。");
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

        // 记住的选择：上次设成"以管理员身份运行"，这次却是普通权限起来的（双击 exe、开机自启……），
        // 就自己再提权重启一次。提权实例进来时 IsElevated 为 true，不会递归。
        // （放在 --restore 之后：应急开关不该被 UAC 拦一道）
        if (Settings.RunElevated && !IsElevated)
        {
            RestartElevated();
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

        // Alt+Tab 接管：钩子挂上之后就由我们自己画切换面板
        _altTab = new AltTabController(this);
        _altTab.Start();

        // 剪贴板库：先解密载入历史，再开始监听剪贴板变化；随后接上 Win+V 接管
        _clipStore = new ClipboardStore();
        _clipStore.Load();
        _clipMonitor = new ClipboardMonitor(_clipStore);
        _clipboard = new ClipboardController(this, _clipStore);
        _clipboard.Start();

        _quitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName);
        StartQuitListener();

        // 调试用：ExplorerDock.exe --theme-editor 直接打开自定义主题面板
        if (e.Args.Any(a => a.Equals("--theme-editor", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(new Action(OpenThemeEditor));
        }

        // 调试用：ExplorerDock.exe --clipboard 直接打开剪贴板面板
        if (e.Args.Any(a => a.Equals("--clipboard", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(new Action(OpenClipboardPanel));
        }

        // 调试用：ExplorerDock.exe --clip-settings 直接打开剪贴板设置
        if (e.Args.Any(a => a.Equals("--clip-settings", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(new Action(OpenClipboardSettings));
        }

        // 调试用：--appmenu 在鼠标位置弹出托盘那套菜单（用来验证外观与关闭行为）
        if (e.Args.Any(a => a.Equals("--appmenu", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(new Action(() => ShowTrayMenu()));
        }

        // 首次启动：先让用户把偏好定下来（之后随时能从菜单再打开）
        if (!Settings.SetupCompleted)
        {
            Dispatcher.BeginInvoke(new Action(OpenSetupWizard));
        }
    }

    /// <summary>
    /// 提权重启专用：通知现有实例退出，并等它让出单实例锁。
    /// 新进程没法直接顶替旧进程，只能这样接力。
    /// </summary>
    private static void WaitForPreviousInstance()
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

        for (int i = 0; i < 30; i++)
        {
            Thread.Sleep(200);

            try
            {
                using var probe = Mutex.OpenExisting(@"Local\ExplorerDock.SingleInstance.v1");
            }
            catch
            {
                return;   // 锁没了 = 旧实例已经退干净
            }
        }
    }

    /// <summary>当前进程是不是以管理员身份运行。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(identity)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 以管理员身份重启自己。
    ///
    /// 提权程序（不少游戏、某些工具）占着前台时，普通权限的键盘钩子收不到任何按键，
    /// Alt+Tab 只能由系统接管 —— 想让这类窗口也归我们管，就得和它们同级。
    /// 代价是每次启动要过 UAC。
    /// </summary>
    public void RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;

            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = BuildRelaunchArguments(),
            });
        }
        catch
        {
            // 用户在 UAC 弹窗上点了"否"：那就什么都不做，继续用普通权限跑着
            return;
        }

        ExitApp();
    }

    /// <summary>重启时把原始命令行参数原样带上（调试开关要能穿过提权重启）。</summary>
    private static string BuildRelaunchArguments()
    {
        var parts = new List<string> { "--relaunch" };

        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
        {
            if (arg.Equals("--relaunch", StringComparison.OrdinalIgnoreCase)) continue;
            parts.Add(arg.Contains(' ') ? $"\"{arg}\"" : arg);
        }

        return string.Join(' ', parts);
    }

    /// <summary>降回普通权限重启（提权状态下取消勾选时走这条）。</summary>
    public void RestartNormal()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;

            // 从提权进程直接 CreateProcess 出来的还是提权进程；让已经跑着的 explorer 代劳，才会是普通用户权限。
            // 注意 explorer **不会把命令行参数转给被启动的程序**，所以这里一个参数都不带 ——
            // 新实例自己会等一会儿，等我们退场、让出单实例锁之后再接手。
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            return;
        }

        ExitApp();
    }

    /// <summary>记住"以管理员身份运行"这个选择，并按新状态重启一次。</summary>
    public void SetRunElevated(bool enabled)
    {
        Settings.RunElevated = enabled;
        Settings.Save();
        RefreshStartupRegistration();

        if (enabled && !IsElevated) RestartElevated();
        else if (!enabled && IsElevated) RestartNormal();
    }

    /// <summary>取消"以管理员身份运行"之前的提醒。返回 false 表示用户反悔了。</summary>
    public static bool ConfirmElevatedOff()
    {
        return Views.ConfirmDialog.Confirm(
            "取消以管理员身份运行？",
            "取消后，用管理员身份打开的程序在应用切换列表里看不到窗口画面，也无法用 Alt+Tab 切换。",
            "确定取消",
            "保持勾选");
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

    /// <summary>Alt+Tab 面板是不是正开着（悬浮栏用它避让置顶）。</summary>
    public bool AltTabActive => _altTab?.IsActive == true;

    // ---------- 剪贴板 ----------

    /// <summary>剪贴板面板是不是正开着（悬浮栏用它避让置顶）。</summary>
    public bool ClipboardActive => _clipboard?.IsActive == true;

    /// <summary>剪贴板库当前占用的磁盘字节数（设置窗口显示用）。</summary>
    public static long CurrentClipboardBytes => (Current as App)?._clipStore?.TotalBytes ?? 0;

    /// <summary>打开（或收起）剪贴板面板。</summary>
    public void ToggleClipboardPanel() => _clipboard?.Toggle();

    /// <summary>收起剪贴板面板（弹悬浮栏菜单前先收，免得菜单点别处关不掉）。</summary>
    public void HideClipboardPanel()
    {
        if (_clipboard is not { IsActive: true }) return;

        var origin = _clipboard.OriginForeground;

        _clipboard.Panel.HidePanel();

        // 面板是 ForceForeground 抢来的前台；收起来后前台会卡住，
        // 菜单的'点别处关闭'就永远等不到失活。这里显式把它还给用户原来的窗口。
        if (origin != IntPtr.Zero && NativeMethods.IsWindow(origin))
        {
            NativeMethods.ForceForeground(origin);
        }
    }

    /// <summary>拖动悬停：把光标位置交给悬浮栏，命中文件夹按钮就把对应窗口提到前台。</summary>
    public bool TryActivateDockItemAt(int screenX, int screenY)
        => Dock?.TryActivateItemAtScreenPoint(screenX, screenY) ?? false;

    /// <summary>拖动结束：让悬浮栏忘掉这次悬停激活过的窗口。</summary>
    public void ResetDockDragHover() => Dock?.ResetDragHover();

    /// <summary>
    /// 剪贴板面板开始/结束拖动条目时调用：拖动期间锁住悬浮栏自己的鼠标交互，
    /// 免得它把鼠标当成"按住拖动悬浮栏位置"（见 DockWindow.SetInteractionEnabled）。
    /// </summary>
    public void SetDockInteraction(bool enabled) => Dock?.SetInteractionEnabled(enabled);

    /// <summary>打开剪贴板面板。</summary>
    public void OpenClipboardPanel()
    {
        if (_clipboard is null) return;

        try
        {
            if (_clipboard.IsActive) return;
            _clipboard.Toggle();
        }
        catch (Exception ex)
        {
            Views.ConfirmDialog.Notify("剪贴板面板打开失败", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 把一条记录按它原来的格式写回系统剪贴板。
    ///
    /// targetWindow 是即将接收这次粘贴的窗口，用来决定要不要带上富文本格式：
    /// Word/Excel/PPT 要靠 HTML、RTF 才能保住图文排版；
    /// 而聊天软件（QQ 就是）拿到 HTML 会去解析里面那张图片的路径 ——
    /// Word 给的路径是它自己的临时文件，早没了，于是解析失败、什么都不粘。
    /// 这就是"从 Word 复制的图文粘不进 QQ、别的条目却可以"的原因。
    /// </summary>
    public bool WriteClipItemToClipboard(ClipItem item, IntPtr targetWindow = default)
    {
        if (_clipStore is null) return false;

        try
        {
            _clipMonitor?.Suppress(TimeSpan.FromSeconds(1.5));

            switch (item.Kind)
            {
                case ClipKind.Text:
                case ClipKind.Image:
                    byte[]? raw = null;

                    if (item.Kind == ClipKind.Image)
                    {
                        raw = _clipStore.GetImageBytes(item);
                        if (raw is null) return false;
                    }

                    // 一次性把这条记录里存下来的格式放回剪贴板：
                    // 图文混排是"图片 + HTML + RTF + 文字"并存，粘到 Word 保留排版、粘到记事本只是文字。
                    // 但目标不是 Office 那套时，把富文本剔掉 —— 见方法注释。
                    var formats = PrefersRichText(targetWindow) ? item.Formats : WithoutRichFormats(item.Formats);

                    return ClipboardWriter.WriteAll(item.Text, item.ImageFormat, raw, formats);

                case ClipKind.Files:
                    var files = new System.Collections.Specialized.StringCollection();
                    if (item.Files is not null) files.AddRange(item.Files);
                    Clipboard.SetFileDropList(files);
                    return true;
            }
        }
        catch (Exception ex)
        {
            Views.ConfirmDialog.Notify("写回剪贴板失败", $"{ex.GetType().Name}: {ex.Message}");
        }

        return false;
    }

    /// <summary>目标窗口是不是"只认文件"的那种（资源管理器、桌面）。</summary>
    public static bool IsFileManagerWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        var className = NativeMethods.GetClassNameSafe(hwnd);

        switch (className)
        {
            case "CabinetWClass":   // 资源管理器
            case "ExploreWClass":   // 老版资源管理器
            case "Progman":         // 桌面
            case "WorkerW":         // 桌面（壁纸层）
            case "desktop":
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// 把图片条目落成临时文件，并把该文件放进剪贴板（FileDrop）。
    ///
    /// 资源管理器/桌面只认文件：直接给它图片数据它不会粘，给它一个文件路径，
    /// Ctrl+V 就会把这个文件复制过去 —— 字节还是库里存的那份原样数据。
    /// </summary>
    public bool WriteClipItemAsFileDrop(ClipItem item)
    {
        if (_clipStore is null) return false;

        try
        {
            var path = ExportClipItemToFile(item);
            if (path is null) return false;

            _clipMonitor?.Suppress(TimeSpan.FromSeconds(1.5));

            var files = new System.Collections.Specialized.StringCollection { path };
            Clipboard.SetFileDropList(files);
            return true;
        }
        catch (Exception ex)
        {
            Views.ConfirmDialog.Notify("导出文件失败", $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>把一条图片记录的原图字节写成临时文件，返回文件路径。</summary>
    public string? ExportClipItemToFile(ClipItem item)
    {
        if (_clipStore is null || item.Kind != ClipKind.Image) return null;

        try
        {
            var bytes = _clipStore.GetImageBytes(item);
            if (bytes is null || bytes.Length == 0) return null;

            // PNG 本身就是完整文件，直接落盘
            if (string.Equals(item.ImageFormat, "PNG", StringComparison.OrdinalIgnoreCase))
            {
                return WriteDropFile(bytes, item, ".png");
            }

            // DIB / Format17 / Bitmap：没有文件头，补成 BMP
            var wrapped = DibTools.WrapAsBmp(bytes);
            if (wrapped is not null) return WriteDropFile(wrapped, item, ".bmp");

            // EMF 这类：渲成 PNG 落盘
            var rendered = DibTools.RenderEmfThumbnail(bytes, 4096);
            if (rendered is null) return null;

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rendered));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            return WriteDropFile(stream.ToArray(), item, ".png");
        }
        catch (Exception ex)
        {
            Views.ConfirmDialog.Notify("导出文件失败", $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string WriteDropFile(byte[] bytes, ClipItem item, string extension)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ExplorerDock", "drop");
        Directory.CreateDirectory(directory);

        // 顺手清掉一天前的残留，拖过的文件不会越攒越多
        foreach (var stale in Directory.GetFiles(directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddDays(-1)) File.Delete(stale);
            }
            catch
            {
                // 删不掉就算了
            }
        }

        var id = item.Id.Length >= 6 ? item.Id[..6] : item.Id;
        var path = Path.Combine(directory, $"clip-{item.CreatedUtc.ToLocalTime():yyyyMMdd-HHmmss}-{id}{extension}");

        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>目标窗口是不是 Office 那三件套（它们才需要 HTML/RTF 来保排版）。</summary>
    private static bool PrefersRichText(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        return NativeMethods.GetClassNameSafe(hwnd) switch
        {
            "OpusApp" => true,          // Word
            "XLMAIN" => true,           // Excel
            "PP12FrameClass" => true,   // PowerPoint
            "PPTFrameClass" => true,
            _ => false,
        };
    }

    private static readonly string[] RichTextFormats =
    {
        "HTML Format", "HTML", "Rich Text Format", "RTF",
        "EnhancedMetafile", "EMF", "WordDocument", "Object Descriptor", "Link Source",
    };

    /// <summary>
    /// 剔掉富文本格式。剩下的（位图 / PNG / 纯文本）够聊天软件直接用，
    /// 而且不会让它去解析那些已经失效的图片路径。
    /// </summary>
    private static IReadOnlyList<ClipFormat>? WithoutRichFormats(IReadOnlyList<ClipFormat>? formats)
    {
        if (formats is null) return null;

        var kept = formats
            .Where(f => f.Name is not null
                && !RichTextFormats.Any(r => string.Equals(f.Name, r, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return kept.Count == 0 ? null : kept;
    }

    /// <summary>剪贴板历史上限设置。</summary>
    public void OpenClipboardSettings()
    {
        try
        {
            Views.ClipboardSettingsWindow.Show(_clipboard?.Panel);
        }
        catch (Exception ex)
        {
            Views.ConfirmDialog.Notify("剪贴板设置打开失败", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>清空剪贴板历史（保留收藏）。</summary>
    public void ClearClipboardHistory()
    {
        if (_clipStore is null) return;

        if (!Views.ConfirmDialog.Confirm(
                "清空剪贴板历史？",
                "收藏的记录会保留，其余全部删除，删除后无法恢复。",
                "清空",
                "取消"))
        {
            return;
        }

        _clipStore.ClearUnfavorited();
    }

    /// <summary>打开剪贴板数据所在的文件夹。</summary>
    public void OpenClipboardFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(ClipboardCrypto.DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ClipboardCrypto.DataDirectory}\"") { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了
        }
    }

    public void SetAltTabTakeover(bool enabled)
    {
        Settings.AltTabTakeover = enabled;
        Settings.Save();

        // 关掉的时候立刻把可能开着的面板收掉，下一次 Alt+Tab 就交还系统
        if (!enabled) _altTab?.CancelNow();
    }

    public void SetAltTabFullscreenPassthrough(bool enabled)
    {
        Settings.AltTabFullscreenPassthrough = enabled;
        Settings.Save();
    }

    /// <summary>是否接管 Win+V（用本软件的面板替换系统剪贴板历史）。</summary>
    public void SetClipboardTakeover(bool enabled)
    {
        Settings.ClipboardTakeover = enabled;
        Settings.Save();
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

    /// <summary>设置变化后重新注册自启（向导收尾时会调用）。</summary>
    public void RefreshStartupRegistration() => ApplyStartupRegistration();

    /// <summary>
    /// 让自启与设置保持一致。
    ///
    /// 分成两条路：普通权限写注册表 Run 键就够了；
    /// 选了「以管理员身份运行」就得改用计划任务 —— Run 键拉起来的进程提不了权，
    /// 而计划任务能标 /RL HIGHEST，登录时静默地以管理员身份跑起来，不弹 UAC。
    /// </summary>
    private void ApplyStartupRegistration()
    {
        try
        {
            var exe = Environment.ProcessPath;
            bool valid = !string.IsNullOrWhiteSpace(exe);

            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            {
                if (key is not null)
                {
                    if (Settings.RunAtStartup && !Settings.RunElevated && valid)
                    {
                        key.SetValue(StartupValueName, $"\"{exe}\"");
                    }
                    else
                    {
                        key.DeleteValue(StartupValueName, throwOnMissingValue: false);
                    }
                }
            }

            ApplyStartupTask(Settings.RunAtStartup && Settings.RunElevated && valid, exe);
        }
        catch
        {
            // 注册自启失败不影响主功能
        }
    }

    /// <summary>创建或删除"登录时以最高权限运行"的计划任务（需要管理员权限，失败就忽略）。</summary>
    private static void ApplyStartupTask(bool create, string? exe)
    {
        try
        {
            var start = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (create && !string.IsNullOrWhiteSpace(exe))
            {
                start.ArgumentList.Add("/Create");
                start.ArgumentList.Add("/TN");
                start.ArgumentList.Add(StartupValueName);
                start.ArgumentList.Add("/TR");
                start.ArgumentList.Add(exe);   // 引号交给运行时按需补
                start.ArgumentList.Add("/SC");
                start.ArgumentList.Add("ONLOGON");
                start.ArgumentList.Add("/RL");
                start.ArgumentList.Add("HIGHEST");
                start.ArgumentList.Add("/F");
            }
            else
            {
                start.ArgumentList.Add("/Delete");
                start.ArgumentList.Add("/TN");
                start.ArgumentList.Add(StartupValueName);
                start.ArgumentList.Add("/F");
            }

            using var process = Process.Start(start);
            process?.WaitForExit(5000);
        }
        catch
        {
            // 没有权限、或者 schtasks 不可用：忽略
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

        // 广播给其它界面：改主题/缩放时立刻生效，不用等下次打开
        ThemeChanged?.Invoke();
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
            Views.ConfirmDialog.Notify("自定义主题面板打开失败", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private Views.SetupWizardWindow? _wizard;

    /// <summary>首次启动（或从菜单点进来）的设置向导。</summary>
    public void OpenSetupWizard()
    {
        try
        {
            if (_wizard is { IsVisible: true })
            {
                _wizard.Activate();
                return;
            }

            _wizard = new Views.SetupWizardWindow(this);
            _wizard.Closed += (_, _) => _wizard = null;

            // 向导本身以普通权限跑完；要不要提权、由它在收尾时按用户的勾选决定
            _wizard.ShowDialog();
        }
        catch (Exception ex)
        {
            Views.ConfirmDialog.Notify("设置向导打开失败", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 菜单与提示气泡的配色走 DynamicResource，这里整体换一套刷子。
    /// 色值全部来自 ThemePalette —— 和悬浮栏、剪贴板面板是同一份定义。
    /// </summary>
    public static void ApplyMenuTheme()
    {
        var palette = ThemePalette.Resolve();
        var resources = Current.Resources;

        resources["DockMenuBackground"] = new SolidColorBrush(Color.FromArgb(palette.SurfaceAlpha, palette.Background.R, palette.Background.G, palette.Background.B));
        resources["DockMenuBorder"] = new SolidColorBrush(palette.Border);
        resources["DockMenuForeground"] = new SolidColorBrush(palette.Text);
        resources["DockMenuHighlight"] = new SolidColorBrush(palette.Hover);
        resources["DockMenuHighlightText"] = new SolidColorBrush(palette.HoverText);
        resources["DockMenuDisabled"] = new SolidColorBrush(palette.Muted);
        resources["DockMenuSeparator"] = new SolidColorBrush(palette.Separator);

        // 主题编辑器自身的输入框/按钮/滑块也吃同一套主题（App.xaml 里那批 Editor* 样式）
        resources["EditorSurface"] = new SolidColorBrush(palette.Hover);
        resources["EditorBorder"] = new SolidColorBrush(palette.Border);
        resources["EditorText"] = new SolidColorBrush(palette.Text);
        resources["EditorMuted"] = new SolidColorBrush(palette.Muted);
        resources["EditorAccent"] = new SolidColorBrush(palette.Accent);
        resources["EditorAccentHover"] = new SolidColorBrush(Lighten(palette.Accent));
        resources["EditorAccentPressed"] = new SolidColorBrush(Darken(palette.Accent));
        resources["EditorHover"] = new SolidColorBrush(palette.Active);
        resources["EditorPressed"] = new SolidColorBrush(palette.Background);
        resources["EditorFontSize"] = palette.FontSizeBody;
    }

    /// <summary>提亮（强调色的悬停态）。</summary>
    private static Color Lighten(Color color)
        => Color.FromRgb((byte)Math.Min(255, color.R + 0x1A), (byte)Math.Min(255, color.G + 0x1A), (byte)Math.Min(255, color.B + 0x1A));

    /// <summary>压暗（强调色的按下态）。</summary>
    private static Color Darken(Color color)
        => Color.FromRgb((byte)(color.R * 0.8), (byte)(color.G * 0.8), (byte)(color.B * 0.8));

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

    /// <summary>
    /// 调试用：把剪贴板库导出到 %TEMP%\ed-clipdump（索引摘要 + 每条图片的原始字节），
    /// 用来核对"图片原样无损"这条硬指标。
    /// </summary>
    private void DumpClipboardStore(ClipboardStore store)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "ed-clipdump");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

            var lines = new List<string>
            {
                $"available={store.IsAvailable}",
                $"count={store.Count}",
                $"totalBytes={store.TotalBytes}",
                $"error={store.LastError ?? "-"}",
            };

            foreach (var item in store.Items)
            {
                lines.Add($"- id={item.Id} kind={item.Kind} fav={item.Favorited} size={item.SizeText} format={item.ImageFormat} extras={item.Formats?.Count ?? 0} text={item.Text?.Length ?? 0} preview={item.Preview}");

                if (item.Kind != ClipKind.Image && item.PreviewBlobId is null && item.Formats is null) continue;

                // 诊断用：把"其他格式"的原始字节也导出来（HTML / RTF / QQ 私有格式…）
                if (item.Formats is not null)
                {
                    foreach (var format in item.Formats)
                    {
                        if (format.Bytes is not { Length: > 0 }) continue;

                        var safe = format.Name.Replace(' ', '_').Replace('/', '_').Replace('\\', '_');
                        File.WriteAllBytes(Path.Combine(dir, $"{item.Id}.{safe}.bin"), format.Bytes);
                    }
                }

                var raw = store.GetImageBytes(item) ?? store.GetPreviewBytes(item);
                if (raw is null)
                {
                    lines.Add("  blob=缺失");
                    continue;
                }

                var output = raw;
                var extension = ".bin";

                if (string.Equals(item.ImageFormat, "PNG", StringComparison.OrdinalIgnoreCase))
                {
                    extension = ".png";
                }
                else
                {
                    if (DibTools.TryReadHeader(raw, out int width, out int height, out int bits, out uint compression, out _))
                    {
                        lines.Add($"  dib={width}x{height} bpp={bits} compression={compression} bytes={raw.Length}");
                    }

                    var bmp = DibTools.WrapAsBmp(raw);
                    if (bmp is not null)
                    {
                        output = bmp;
                        extension = ".bmp";
                    }
                }

                File.WriteAllBytes(Path.Combine(dir, item.Id + extension), output);
            }

            File.WriteAllLines(Path.Combine(dir, "dump.txt"), lines);
        }
        catch
        {
            // 调试功能失败不影响任何东西
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

        // 一定要卸掉键盘钩子：不然程序走了，Alt+Tab 还被我们压着
        try
        {
            _altTab?.Dispose();
        }
        catch
        {
            // 忽略
        }

        // 剪贴板：先停止采集，再把没写完的最后一条同步落盘
        try
        {
            _clipboard?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _clipMonitor?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _clipStore?.SaveNow();
            _clipStore?.Dispose();
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

        try
        {
            _altTab?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _clipStore?.SaveNow();
            _clipStore?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _mutex?.Dispose();
        base.OnExit(e);
    }
}






