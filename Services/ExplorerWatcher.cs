using System.Threading;
using ExplorerDock.Interop;
using ExplorerDock.Models;

namespace ExplorerDock.Services;

/// <summary>一个"可能被接管"的顶层窗口（各类过滤之后的候选）。</summary>
internal readonly record struct WindowCandidate(
    IntPtr Handle,
    string ClassName,
    uint Pid,
    string ProcessName,
    string ExePath,
    string Title);

/// <summary>
/// 后台盯着桌面上的窗口：把"接管范围"里的窗口从任务栏摘掉，并把窗口列表推给悬浮栏；
/// 窗口关闭、程序退出或接管范围变化时把任务栏还原。
///
/// 接管范围（<see cref="ShouldTakeOver"/>）有三条，按优先级是：
/// ① 资源管理器文件夹窗口 —— 默认接管，不受两个新开关影响；
/// ② 设置里手动指定的程序 —— 只要有 1 个窗口就接管；
/// ③ 多窗口程序自动接管 —— 开关开着、且同名程序（按 exe 名）的可见窗口 ≥2 个。
///
/// 类名保留 ExplorerWatcher 是有意的：它一开始只管文件夹窗口，改名会牵动一堆调用点；
/// 现在它管的是"所有该被接管的窗口"，文件夹窗口只是其中一类。
/// </summary>
public sealed class ExplorerWatcher : IDisposable
{
    private const int PollIntervalMs = 350;
    private const int ShellRefreshTicks = 4;      // 约 1.4s 刷新一次路径信息
    private const int TakeoverReapplyTicks = 8;   // 约 2.8s 重新确认一次任务栏状态

    /// <summary>自动接管的取消滞后：窗口数掉到 1 之后连续这么多轮才真正还回去，避免开关窗口时按钮闪烁。</summary>
    private const int AutoDropGraceTicks = 4;

    private static readonly string[] TitleSuffixes =
    {
        " - 文件资源管理器",
        " - File Explorer",
        " - Windows 资源管理器",
        " - Windows Explorer",
    };

    /// <summary>
    /// shell 自己的顶层窗口，不该出现在接管范围里，也不该出现在 Alt+Tab 里。
    /// （Alt+Tab 的候选枚举现在也共用这一份。）
    /// </summary>
    public static readonly string[] ShellWindowClasses =
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",
        "ApplicationManager_DesktopShellWindow",
        "XamlExplorerHostIslandWindow",
        "ForegroundStaging",
        "MultitaskingViewFrame",
        "TaskListThumbnailWnd",
    };

    private readonly Thread _thread;
    private readonly TaskbarTweaker _taskbar = new();

    /// <summary>候选窗口首次出现的顺序 —— 悬浮栏按钮顺序就是它。</summary>
    private readonly List<IntPtr> _windows = new();

    private readonly HashSet<IntPtr> _windowSet = new();

    /// <summary>已经摘掉任务栏按钮的窗口。</summary>
    private readonly HashSet<IntPtr> _stripped = new();

    /// <summary>因"自动接管"而处于接管状态的程序名。</summary>
    private readonly HashSet<string> _autoHeld = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>自动接管中、但本轮窗口数不够的程序名 → 连续未达标轮数（取消接管的滞后计数）。</summary>
    private readonly Dictionary<string, int> _autoMiss = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>上一轮发布出去的接管窗口集合，用来判断要不要重新发布。</summary>
    private readonly HashSet<IntPtr> _published = new();

    /// <summary>
    /// 悬浮栏上的按钮列表。多标签资源管理器窗口是**一个标签页一项**，
    /// 其他程序是**一个窗口一项**。
    /// </summary>
    private List<ExplorerWindowInfo> _tabs = new();

    private volatile bool _running = true;

    /// <summary>设置变化后让下一轮立刻跑，不等这 350ms。</summary>
    private volatile bool _forceReapply;

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

    public void Start()
    {
        _thread.Start();
    }

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

                // 1) 枚举候选窗口（过滤条件集中在 TryDescribeWindow 里）
                var candidates = EnumerateCandidateWindows();

                var byHandle = new Dictionary<IntPtr, WindowCandidate>(candidates.Count);
                foreach (var candidate in candidates) byHandle[candidate.Handle] = candidate;

                // 2) 自动接管的程序集合（按程序名判定，带取消滞后）
                UpdateAutoHeld(candidates);

                // 3) 窗口清单：关掉的删掉，新出现的追加（顺序即悬浮栏的顺序）
                bool changed = false;

                for (int i = _windows.Count - 1; i >= 0; i--)
                {
                    if (byHandle.ContainsKey(_windows[i])) continue;

                    ExplorerTabs.Forget(_windows[i]);
                    _windowSet.Remove(_windows[i]);
                    _windows.RemoveAt(i);
                    changed = true;
                }

                foreach (var candidate in candidates)
                {
                    if (!_windowSet.Add(candidate.Handle)) continue;

                    _windows.Add(candidate.Handle);
                    changed = true;
                }

                // 4) 本轮接管范围
                var scope = new List<IntPtr>(_windows.Count);

                foreach (var handle in _windows)
                {
                    if (byHandle.TryGetValue(handle, out var candidate) && ShouldTakeOver(candidate)) scope.Add(handle);
                }

                SyncTaskbar(scope);
                PublishScope(scope);

                // 5) 重建按钮列表（多标签窗口在这里展开成一个标签页一项）
                if (RebuildItems(scope, byHandle, locations)) changed = true;

                var foreground = NativeMethods.GetForegroundWindow();

                // 前台窗口变了也算变化：悬浮栏要靠它更新"最后在用的窗口"
                if (foreground != _lastForeground)
                {
                    _lastForeground = foreground;
                    changed = true;
                }

                if (changed)
                {
                    SnapshotUpdated?.Invoke(new ExplorerSnapshot
                    {
                        Windows = _tabs,
                        Foreground = foreground,
                    });
                }
            }
            catch
            {
                // 单次轮询失败不影响下一轮
            }

            NativeMethods.PumpMessages();

            // 设置刚变过就别等了：让用户点完开关立刻看到效果
            bool immediate = _forceReapply;
            _forceReapply = false;

            Thread.Sleep(immediate ? 30 : PollIntervalMs);
            _tick++;
        }
    }

    /// <summary>
    /// 这个窗口该不该被接管。三条来源见类注释。
    /// </summary>
    private bool ShouldTakeOver(WindowCandidate candidate)
    {
        if (IsTakeoverWindowClass(candidate.ClassName)) return true;
        if (TakeoverState.IsManual(candidate.ProcessName)) return true;

        return TakeoverState.AutoMultiWindow && _autoHeld.Contains(candidate.ProcessName);
    }

    /// <summary>
    /// 更新"自动接管"的程序集合。
    ///
    /// 达标（同名程序窗口 ≥2）立刻接管；掉到达标线以下时先记着，
    /// 连续 <see cref="AutoDropGraceTicks"/> 轮都没回来才取消 ——
    /// 关掉一个窗口的瞬间就把另一个窗口的按钮甩回任务栏会很难看。
    /// </summary>
    private void UpdateAutoHeld(List<WindowCandidate> candidates)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            counts.TryGetValue(candidate.ProcessName, out int count);
            counts[candidate.ProcessName] = count + 1;
        }

        if (!TakeoverState.AutoMultiWindow)
        {
            // 关掉自动接管：所有靠它接管的程序立刻交还（走正常还原流程）
            foreach (var name in _autoHeld) _autoMiss.Remove(name);
            _autoHeld.Clear();
            return;
        }

        foreach (var (name, count) in counts)
        {
            if (count >= TakeoverState.AutoTakeoverThreshold)
            {
                _autoHeld.Add(name);
                _autoMiss.Remove(name);
                continue;
            }

            if (!_autoHeld.Contains(name)) continue;

            _autoMiss.TryGetValue(name, out int miss);
            miss++;

            if (miss >= AutoDropGraceTicks)
            {
                _autoHeld.Remove(name);
                _autoMiss.Remove(name);
            }
            else
            {
                _autoMiss[name] = miss;
            }
        }

        // 窗口已经全关掉的程序名不用再留着
        foreach (var name in _autoHeld.ToList())
        {
            if (!counts.ContainsKey(name)) _autoHeld.Remove(name);
        }

        foreach (var name in _autoMiss.Keys.ToList())
        {
            if (!_autoHeld.Contains(name)) _autoMiss.Remove(name);
        }
    }

    /// <summary>把任务栏状态同步到"本轮接管范围"：新增的摘掉，离开范围的还回去。</summary>
    private void SyncTaskbar(List<IntPtr> scope)
    {
        if (!TakeoverEnabled)
        {
            if (_stripped.Count == 0) return;

            foreach (var handle in _stripped.ToList()) RestoreTaskbar(handle);
            _stripped.Clear();
            return;
        }

        var keep = new HashSet<IntPtr>(scope);

        foreach (var handle in scope)
        {
            // 只做这一件事：摘掉任务栏按钮。
            // 不要再跟着调 SetWindowPos(SWP_FRAMECHANGED) —— 那会让 shell 重新评估这个窗口、
            // 把刚摘掉的按钮又加回来（v1.0.4 的遗留探索代码，实测对 Alt+Tab 也没帮助）。
            if (_stripped.Add(handle)) _taskbar.Remove(handle);
        }

        foreach (var handle in _stripped.ToList())
        {
            if (keep.Contains(handle)) continue;

            _stripped.Remove(handle);
            RestoreTaskbar(handle);
        }

        // 任务栏偶尔会自己把按钮加回来（切标签、最小化还原、程序重设 AUMID），定期补一刀
        if (_tick % TakeoverReapplyTicks != 0) return;

        foreach (var handle in _stripped) _taskbar.Remove(handle);
    }

    private void RestoreTaskbar(IntPtr handle)
    {
        try
        {
            AppUserModelId.TrySet(handle, null);
            _taskbar.Restore(handle);
        }
        catch
        {
            // 窗口可能已经没了
        }
    }

    /// <summary>把接管窗口集合发布给 Alt+Tab 打包、Alt+~ 判断那些读取方（集合没变就不发）。</summary>
    private void PublishScope(List<IntPtr> scope)
    {
        if (_published.Count == scope.Count)
        {
            bool same = true;

            foreach (var handle in scope)
            {
                if (_published.Contains(handle)) continue;
                same = false;
                break;
            }

            if (same) return;
        }

        TakeoverState.Publish(scope);

        _published.Clear();
        foreach (var handle in scope) _published.Add(handle);
    }

    /// <summary>
    /// 重建悬浮栏按钮列表。顺序：资源管理器窗口在前（按打开顺序、多标签展开），
    /// 其他程序按"该程序第一个窗口出现的顺序"分组，组内按窗口出现顺序 ——
    /// 悬浮栏按这个顺序分行，每个程序独占一行。
    /// </summary>
    private bool RebuildItems(List<IntPtr> scope, Dictionary<IntPtr, WindowCandidate> byHandle, Dictionary<IntPtr, ShellWindowLocation> locations)
    {
        var list = new List<ExplorerWindowInfo>(_tabs.Count);

        // 1) 资源管理器：整窗一项 / 多标签展开成一标签页一项
        foreach (var handle in scope)
        {
            if (byHandle.TryGetValue(handle, out var candidate) && IsTakeoverWindowClass(candidate.ClassName))
            {
                AppendExplorerItems(list, handle, locations);
            }
        }

        // 2) 其他程序：**按进程名升序**排（原来按"首次出现顺序"，先开的程序会一直压在上面，
        //    而且窗口开关一轮顺序就可能变，悬浮栏的行位置看着就飘）；组内仍按窗口首见顺序
        var processNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var handle in scope)
        {
            if (!byHandle.TryGetValue(handle, out var candidate)) continue;
            if (IsTakeoverWindowClass(candidate.ClassName)) continue;

            processNames.Add(candidate.ProcessName);
        }

        var processOrder = processNames.ToList();
        processOrder.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (var process in processOrder)
        {
            foreach (var handle in scope)
            {
                if (!byHandle.TryGetValue(handle, out var candidate)) continue;
                if (IsTakeoverWindowClass(candidate.ClassName)) continue;
                if (!string.Equals(candidate.ProcessName, process, StringComparison.OrdinalIgnoreCase)) continue;

                list.Add(new ExplorerWindowInfo
                {
                    Handle = candidate.Handle,
                    Title = candidate.Title,
                    ProcessName = candidate.ProcessName,
                    IconPath = candidate.ExePath.Length > 0 ? candidate.ExePath : null,
                    IsExplorer = false,
                    IsMinimized = NativeMethods.IsIconic(candidate.Handle),
                    Icon = ShellInterop.GetExeIcon(candidate.ExePath),
                    TabIndex = -1,
                    TabSelected = true,
                });
            }
        }

        bool changed = !SameTabs(list, _tabs);
        _tabs = list;

        return changed;
    }

    /// <summary>把一个资源管理器窗口（或它的每个标签页）加进按钮列表。</summary>
    private static void AppendExplorerItems(List<ExplorerWindowInfo> list, IntPtr handle, Dictionary<IntPtr, ShellWindowLocation> locations)
    {
        locations.TryGetValue(handle, out var location);

        var windowTitle = CleanTitle(NativeMethods.GetWindowTextSafe(handle));
        if (windowTitle.Length == 0 && !string.IsNullOrWhiteSpace(location.DisplayName))
        {
            windowTitle = location.DisplayName;
        }

        var path = location.ToPath();
        var minimized = NativeMethods.IsIconic(handle);

        // 只有标题提示"还有别的选项卡"的窗口才去问 UI Automation，单标签窗口零开销
        var tabs = ExplorerTabs.MaybeMultiTab(windowTitle)
            ? ExplorerTabs.Read(handle)
            : new List<ExplorerTabInfo>();

        if (tabs.Count <= 1)
        {
            var single = tabs.Count == 1 && tabs[0].Title.Length > 0 ? tabs[0].Title : windowTitle;

            list.Add(new ExplorerWindowInfo
            {
                Handle = handle,
                Title = single,
                LocationPath = path,
                IsMinimized = minimized,
                Icon = ShellInterop.GetFolderIcon(path),
                TabIndex = -1,
                TabSelected = true,
                ProcessName = "explorer",
                IsExplorer = true,
            });

            return;
        }

        foreach (var tab in tabs)
        {
            list.Add(new ExplorerWindowInfo
            {
                Handle = handle,
                Title = tab.Title.Length > 0 ? tab.Title : windowTitle,
                // 路径只有当前显示的那个标签页才准（其余标签页的地址栏内容拿不到）
                LocationPath = tab.Selected ? path : null,
                IsMinimized = minimized,
                Icon = ShellInterop.GetFolderIcon(tab.Selected ? path : null),
                TabIndex = tab.Index,
                TabSelected = tab.Selected,
                ProcessName = "explorer",
                IsExplorer = true,
            });
        }
    }

    private static bool SameTabs(List<ExplorerWindowInfo> a, List<ExplorerWindowInfo> b)
    {
        if (a.Count != b.Count) return false;

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Handle != b[i].Handle) return false;
            if (a[i].TabIndex != b[i].TabIndex) return false;
            if (a[i].TabSelected != b[i].TabSelected) return false;
            if (a[i].IsMinimized != b[i].IsMinimized) return false;
            if (a[i].IsExplorer != b[i].IsExplorer) return false;
            if (!string.Equals(a[i].ProcessName, b[i].ProcessName, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(a[i].IconPath, b[i].IconPath, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(a[i].Title, b[i].Title, StringComparison.Ordinal)) return false;
            if (!string.Equals(a[i].LocationPath, b[i].LocationPath, StringComparison.Ordinal)) return false;
        }

        return true;
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

        // 多标签页时标题形如「当前标签页名 和 1 个其他选项卡」，原样保留：
        // 该后缀正好提示"这个窗口里还有别的标签页"，是有用信息，不去掉。
        return raw.Trim();
    }

    // ---------- 候选窗口 ----------

    /// <summary>自己的 exe 名：界面进程的窗口（悬浮栏、各种面板）不能被自己接管。</summary>
    private static readonly string SelfProcessName = ResolveSelfProcessName();

    /// <summary>pid → (程序名, exe 路径) 的短缓存：一次枚举里同一个进程会被问很多次。</summary>
    private static readonly Dictionary<uint, (string Name, string Path, DateTime At)> ProcessCache = new();
    private static readonly object ProcessCacheGate = new();
    private const int ProcessCacheMs = 20000;

    private static string ResolveSelfProcessName()
    {
        try
        {
            return System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
        }
        catch
        {
            return "ExplorerDock";
        }
    }

    private static (string Name, string Path) DescribeProcess(uint pid)
    {
        lock (ProcessCacheGate)
        {
            if (ProcessCache.TryGetValue(pid, out var cached) &&
                (DateTime.UtcNow - cached.At).TotalMilliseconds < ProcessCacheMs)
            {
                return (cached.Name, cached.Path);
            }
        }

        var path = NativeMethods.GetProcessImagePath(pid);
        var name = path.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(path) : string.Empty;

        if (name.Length == 0) return (name, path);

        lock (ProcessCacheGate)
        {
            if (ProcessCache.Count > 512) ProcessCache.Clear();
            ProcessCache[pid] = (name, path, DateTime.UtcNow);
        }

        return (name, path);
    }

    /// <summary>
    /// 这个顶层窗口算不算"候选"：可见、无 owner、非 toolwindow、没被 DWM 藏起来、
    /// 有标题、不是 shell 自己的窗口、不是本程序自己的窗口。
    ///
    /// 悬浮栏接管与 Alt+Tab 候选共用这一份判据（以前两处各写一遍）。
    /// </summary>
    internal static bool TryDescribeWindow(IntPtr hwnd, out WindowCandidate candidate)
    {
        candidate = default;

        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;
        if (!NativeMethods.IsWindowVisible(hwnd)) return false;

        long extended = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((extended & NativeMethods.WS_EX_TOOLWINDOW) != 0) return false;

        // 有 owner 的通常是对话框/浮动面板，不是独立的应用窗口 —— 但带 WS_EX_APPWINDOW 的例外：
        // 那是任务栏上**真有自己按钮**的窗口（Blender 的「偏好设置」就是这样：owner 是主窗口，
        // 却因为带了这个样式单独占一个任务栏按钮）。既然任务栏看得见它，就该能接管它。
        // 没带这个样式的 owned 窗口（普通对话框）任务栏上本来就没按钮，仍然排除。
        if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero
            && (extended & NativeMethods.WS_EX_APPWINDOW) == 0)
        {
            return false;
        }

        // UWP 的"隐身窗口"：IsWindowVisible 说可见，其实被 DWM 藏着
        if (NativeMethods.IsCloaked(hwnd)) return false;

        var className = NativeMethods.GetClassNameSafe(hwnd);

        foreach (var shell in ShellWindowClasses)
        {
            if (string.Equals(className, shell, StringComparison.Ordinal)) return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if ((int)pid == Environment.ProcessId) return false;   // 功能进程自己的窗口

        var title = NativeMethods.GetWindowTextSafe(hwnd);
        if (string.IsNullOrWhiteSpace(title)) return false;

        var (name, path) = DescribeProcess(pid);
        if (name.Length == 0) return false;

        // 界面进程和功能进程是同一个 exe：设置窗口、剪贴板面板这些不能被自己接管
        if (string.Equals(name, SelfProcessName, StringComparison.OrdinalIgnoreCase)) return false;

        candidate = new WindowCandidate(hwnd, className, pid, name.ToLowerInvariant(), path, title.Trim());
        return true;
    }

    private static List<WindowCandidate> EnumerateCandidateWindows()
    {
        var result = new List<WindowCandidate>(32);

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (TryDescribeWindow(hwnd, out var candidate)) result.Add(candidate);
            }
            catch
            {
                // 单个窗口查不动就跳过，别把整次枚举带崩
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// 按给定设置算一遍"应当被接管的窗口"（不带自动接管的取消滞后）。
    ///
    /// 只给应急还原用：<c>ExplorerDock.exe --restore</c> 跑在新进程里，
    /// 拿不到监视线程已经算好的集合，只能现场重算一份。
    /// </summary>
    public static List<IntPtr> ComputeScope(bool autoMultiWindow, IEnumerable<string>? manualProcesses)
    {
        var manual = new List<string>();

        if (manualProcesses is not null)
        {
            foreach (var process in manualProcesses)
            {
                var name = TakeoverState.Normalize(process);
                if (name.Length > 0) manual.Add(name);
            }
        }

        var candidates = EnumerateCandidateWindows();

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            counts.TryGetValue(candidate.ProcessName, out int count);
            counts[candidate.ProcessName] = count + 1;
        }

        var result = new List<IntPtr>();

        foreach (var candidate in candidates)
        {
            bool takeover = IsTakeoverWindowClass(candidate.ClassName);

            if (!takeover && autoMultiWindow && counts[candidate.ProcessName] >= TakeoverState.AutoTakeoverThreshold)
            {
                takeover = true;
            }

            if (!takeover)
            {
                foreach (var name in manual)
                {
                    if (!string.Equals(name, candidate.ProcessName, StringComparison.OrdinalIgnoreCase)) continue;
                    takeover = true;
                    break;
                }
            }

            if (takeover) result.Add(candidate.Handle);
        }

        return result;
    }

    /// <summary>
    /// 当前有可见窗口的运行中程序（「选择要接管的程序」列表的数据来源）。
    /// 顺序 = 各程序第一个窗口出现的顺序。
    /// </summary>
    public static List<RunningProcessInfo> EnumerateRunningProcesses()
    {
        var candidates = EnumerateCandidateWindows();

        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var first = new Dictionary<string, WindowCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (!counts.TryGetValue(candidate.ProcessName, out int count))
            {
                order.Add(candidate.ProcessName);
                first[candidate.ProcessName] = candidate;
                counts[candidate.ProcessName] = 1;
                continue;
            }

            counts[candidate.ProcessName] = count + 1;
        }

        var result = new List<RunningProcessInfo>(order.Count);

        foreach (var name in order)
        {
            var sample = first[name];

            result.Add(new RunningProcessInfo
            {
                Name = name,
                ExePath = sample.ExePath,
                WindowCount = counts[name],
                SampleTitle = sample.Title,
            });
        }

        return result;
    }

    /// <summary>
    /// 这个窗口类是不是"被悬浮栏接管"的那一类。
    ///
    /// 目前只有资源管理器文件夹窗口走这条恒接管的路；其他程序靠设置里的名单与自动接管，
    /// 判定结果由 <see cref="TakeoverState"/> 提供。
    /// </summary>
    public static bool IsTakeoverWindowClass(string className)
        => className is "CabinetWClass" or "ExploreWClass";

    /// <summary>枚举当前所有打开着的资源管理器文件夹窗口（"关闭所有文件夹窗口"这类维护动作用它）。</summary>
    public static List<IntPtr> EnumerateWindows()
    {
        var result = new List<IntPtr>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            if (!IsTakeoverWindowClass(NativeMethods.GetClassNameSafe(hwnd))) return true;

            // 带 owner 的通常是弹出的小窗口，不算文件夹主窗口
            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return true;

            result.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// 关掉一个窗口（给它发 WM_CLOSE）。
    ///
    /// 必须由功能进程做：普通权限进程往管理员程序窗口发 WM_CLOSE 会被 UIPI 挡掉，
    /// 表现就是"右键/中键关不掉管理员程序"。
    /// </summary>
    public static bool CloseWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;

        NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    /// <summary>
    /// 关掉某个窗口所属程序的**所有**顶层窗口。
    /// 先把目标收集起来再逐个关：枚举过程中窗口关闭会让遍历乱掉。
    /// </summary>
    public static int CloseProcessWindows(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return 0;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return 0;

        var targets = new List<IntPtr>();

        NativeMethods.EnumWindows((candidate, _) =>
        {
            try
            {
                NativeMethods.GetWindowThreadProcessId(candidate, out uint owner);
                if (owner != pid) return true;

                if (!NativeMethods.IsWindowVisible(candidate)) return true;

                // 带 owner 的通常是对话框/浮动面板，不算独立窗口；
                // 但带 WS_EX_APPWINDOW 的要另算 —— 它在任务栏上占着自己的按钮，
                // 跟接管范围保持同一套规则，关的时候也一起关。
                long extended = NativeMethods.GetWindowLongPtr(candidate, NativeMethods.GWL_EXSTYLE);
                if (NativeMethods.GetWindow(candidate, NativeMethods.GW_OWNER) != IntPtr.Zero
                    && (extended & NativeMethods.WS_EX_APPWINDOW) == 0)
                {
                    return true;
                }

                targets.Add(candidate);
            }
            catch
            {
                // 单个窗口查不动就跳过
            }

            return true;
        }, IntPtr.Zero);

        int closed = 0;

        foreach (var target in targets)
        {
            NativeMethods.PostMessage(target, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            closed++;
        }

        return closed;
    }

    /// <summary>
    /// 结束某个窗口所属程序的所有进程。
    ///
    /// 按 **exe 全路径**匹配：同一个程序文件可能被启动成多个独立进程（用户要的就是一起结束），
    /// 而按进程名匹配又会误伤"同名但不同路径"的程序。
    /// 只应该由用户明确点"结束进程"时调用 —— 未保存的内容会丢。
    /// </summary>
    public static int KillProcesses(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return 0;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return 0;

        var imagePath = NativeMethods.GetProcessImagePath(pid);
        if (imagePath.Length == 0) return 0;

        int self = Environment.ProcessId;
        int killed = 0;

        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (process.Id == self) continue;

                var path = NativeMethods.GetProcessImagePath((uint)process.Id);
                if (path.Length == 0) continue;
                if (!string.Equals(path, imagePath, StringComparison.OrdinalIgnoreCase)) continue;

                process.Kill();
                killed++;
            }
            catch
            {
                // 单个进程杀不掉（权限/已退出）就跳过
            }
            finally
            {
                process.Dispose();
            }
        }

        return killed;
    }

    /// <summary>把所有被接管的窗口还给任务栏。</summary>
    private void RestoreAll()
    {
        foreach (var handle in _stripped.ToList()) RestoreTaskbar(handle);
        _stripped.Clear();
    }

    /// <summary>设置变化后立刻对所有窗口生效（下一次轮询也会自动生效，这里只是不等那 350ms）。</summary>
    public void ReapplyNow() => _forceReapply = true;

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
