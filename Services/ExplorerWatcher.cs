using System.Threading;
using ExplorerDock.Interop;
using ExplorerDock.Models;

namespace ExplorerDock.Services;

/// <summary>
/// 后台盯着资源管理器文件夹窗口：发现新窗口就把它从任务栏摘掉，
/// 并把窗口列表推给悬浮栏；窗口关闭或程序退出时把任务栏还原。
/// </summary>
public sealed class ExplorerWatcher : IDisposable
{
    private const int PollIntervalMs = 350;
    private const int ShellRefreshTicks = 4;      // 约 1.4s 刷新一次路径信息
    private const int TakeoverReapplyTicks = 8;   // 约 2.8s 重新确认一次任务栏状态

    private static readonly string[] TitleSuffixes =
    {
        " - 文件资源管理器",
        " - File Explorer",
        " - Windows 资源管理器",
        " - Windows Explorer",
    };

    private readonly Thread _thread;
    private readonly TaskbarTweaker _taskbar = new();
    private readonly List<ExplorerWindowInfo> _order = new();
    private volatile bool _running = true;
    private IntPtr _lastForeground;
    private int _tick;

    public ExplorerWatcher()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "ExplorerDock.Watcher",
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public event Action<ExplorerSnapshot>? SnapshotUpdated;

    public bool TakeoverEnabled { get; set; } = true;

    public void Start() => _thread.Start();

    private void Loop()
    {
        Dictionary<IntPtr, ShellWindowLocation> locations = new();

        while (_running)
        {
            try
            {
                if (_tick % ShellRefreshTicks == 0)
                {
                    locations = ShellUrlProbe.Query();
                }

                var handles = EnumerateExplorerWindows();
                bool changed = false;

                // 1) 关掉的窗口从列表里拿掉
                for (int i = _order.Count - 1; i >= 0; i--)
                {
                    if (!handles.Contains(_order[i].Handle))
                    {
                        _order.RemoveAt(i);
                        changed = true;
                    }
                }

                // 2) 新窗口 / 老窗口的状态更新
                foreach (var handle in handles)
                {
                    locations.TryGetValue(handle, out var location);

                    var info = _order.Find(x => x.Handle == handle);
                    if (info is null)
                    {
                        info = new ExplorerWindowInfo { Handle = handle };
                        UpdateInfo(info, location);
                        _order.Add(info);
                        ApplyTakeover(handle);
                        changed = true;
                    }
                    else if (UpdateInfo(info, location))
                    {
                        changed = true;
                    }
                }

                // 3) 任务栏偶尔会自己把按钮加回来，定期补一刀
                if (TakeoverEnabled && _tick % TakeoverReapplyTicks == 0)
                {
                    foreach (var info in _order) ApplyTakeover(info.Handle);
                }

                var foreground = NativeMethods.GetForegroundWindow();

                // 前台窗口变了也算变化：悬浮栏要靠它更新"最后在用的文件夹窗口"
                // （ALT+TAB 里的替身显示哪个文件夹、切到哪去，都取决于这个）
                if (foreground != _lastForeground)
                {
                    _lastForeground = foreground;
                    changed = true;
                }

                if (changed)
                {
                    SnapshotUpdated?.Invoke(new ExplorerSnapshot
                    {
                        Windows = BuildSnapshot(),
                        Foreground = foreground,
                    });
                }
            }
            catch
            {
                // 单次轮询失败不影响下一轮
            }

            NativeMethods.PumpMessages();
            Thread.Sleep(PollIntervalMs);
            _tick++;
        }
    }

    private List<ExplorerWindowInfo> BuildSnapshot()
    {
        var list = new List<ExplorerWindowInfo>(_order.Count);
        foreach (var item in _order)
        {
            list.Add(new ExplorerWindowInfo
            {
                Handle = item.Handle,
                Title = item.Title,
                LocationPath = item.LocationPath,
                IsMinimized = item.IsMinimized,
                Icon = item.Icon,
            });
        }

        return list;
    }

    private static bool UpdateInfo(ExplorerWindowInfo info, ShellWindowLocation location)
    {
        bool changed = false;

        var raw = NativeMethods.GetWindowTextSafe(info.Handle);
        var title = CleanTitle(raw);
        if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(location.DisplayName))
        {
            title = location.DisplayName;
        }

        if (title.Length > 0 && title != info.Title)
        {
            info.Title = title;
            changed = true;
        }

        var path = location.ToPath();
        if (path != info.LocationPath)
        {
            info.LocationPath = path;
            var icon = ShellInterop.GetFolderIcon(path);
            if (icon is not null && !ReferenceEquals(icon, info.Icon))
            {
                info.Icon = icon;
            }

            changed = true;
        }
        else if (info.Icon is null)
        {
            info.Icon = ShellInterop.GetFolderIcon(path);
        }

        var minimized = NativeMethods.IsIconic(info.Handle);
        if (minimized != info.IsMinimized)
        {
            info.IsMinimized = minimized;
            changed = true;
        }

        return changed;
    }

    private static string CleanTitle(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        foreach (var suffix in TitleSuffixes)
        {
            if (raw.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = raw[..^suffix.Length].Trim();
                return trimmed.Length == 0 ? raw : trimmed;
            }
        }

        return raw.Trim();
    }

    private static List<IntPtr> EnumerateExplorerWindows() => EnumerateWindows();

    /// <summary>枚举当前所有打开着的资源管理器文件夹窗口。</summary>
    public static List<IntPtr> EnumerateWindows()
    {
        var result = new List<IntPtr>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            var cls = NativeMethods.GetClassNameSafe(hwnd);
            if (cls is not ("CabinetWClass" or "ExploreWClass")) return true;

            // 带 owner 的通常是弹出的小窗口，不算文件夹主窗口
            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return true;

            result.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private void ApplyTakeover(IntPtr hwnd)
    {
        if (TakeoverEnabled)
        {
            // 摘掉任务栏按钮（Win11 上这一步会连带把窗口从 ALT+TAB 移除）
            _taskbar.Remove(hwnd);

            // 紧接着强制一次框架刷新，试着让 shell 把窗口重新登记回 ALT+TAB
            NativeMethods.SetWindowPos(
                hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        }
        else
        {
            AppUserModelId.TrySet(hwnd, null);
            _taskbar.Restore(hwnd); // 兼容旧版本可能留下的"已摘除"状态
        }
    }

    /// <summary>把所有窗口还给任务栏。</summary>
    private void RestoreAll()
    {
        foreach (var info in _order.ToList())
        {
            try
            {
                AppUserModelId.TrySet(info.Handle, null);
                _taskbar.Restore(info.Handle);
            }
            catch
            {
                // 窗口可能已经没了
            }
        }
    }

    /// <summary>设置变化后立刻对所有窗口生效（新窗口在下一次轮询也会自动生效）。</summary>
    public void ReapplyNow()
    {
        try
        {
            var handles = EnumerateExplorerWindows();
            foreach (var hwnd in handles) ApplyTakeover(hwnd);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>接管关闭后，把当前已知窗口全部还原。</summary>
    public void RestoreNow() => RestoreAll();

    public void Dispose()
    {
        _running = false;

        try
        {
            _thread.Join(1500);
        }
        catch
        {
            // 忽略
        }

        RestoreAll();
        _taskbar.Dispose();
    }
}
