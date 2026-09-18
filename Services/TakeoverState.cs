namespace ExplorerDock.Services;

/// <summary>
/// 「哪些窗口被悬浮栏接管」的单一事实来源。
///
/// 为什么单独抽出来：接管范围现在由三处共用 —— 悬浮栏按钮列表（<see cref="ExplorerWatcher"/>）、
/// Alt+Tab 的「同名进程打包」判定（<c>AltTabWindowList</c>）、以及 Alt+~ 能不能用
/// （<c>HostKeyboard.CanSwitchWithinProcess</c>，那里明确要求"绝不枚举窗口"）。
/// 监视线程每轮发布一份快照，另外两处只做 O(1) 查表。
///
/// 它跑在功能进程（<c>--host</c>）里；未拆进程时跑在界面进程里。
/// 读取方可能来自别的线程，所以整份快照用 volatile 引用发布、内部集合建好之后不再修改。
/// </summary>
internal static class TakeoverState
{
    /// <summary>当前处于接管范围内的窗口句柄。</summary>
    private static volatile HashSet<IntPtr> _handles = new();

    private static volatile bool _autoMultiWindow;
    private static volatile string[] _manual = Array.Empty<string>();
    private static volatile AltTabGroupMode _groupMode = AltTabGroupMode.TakeoverOnly;

    /// <summary>自动接管：同名程序窗口达到这个数就接管。</summary>
    public const int AutoTakeoverThreshold = 2;

    /// <summary>当前设置里「自动接管多窗口程序」开着没。</summary>
    public static bool AutoMultiWindow => _autoMultiWindow;

    /// <summary>
    /// Alt+Tab 的同名进程窗口合并范围（不合并 / 只合并悬浮栏接管的 / 合并全部）。
    ///
    /// 这份状态必须由界面进程推过来：卡片列表、Alt+~ 的放行判断都发生在功能进程里，
    /// 它自己读 settings.json 只会读到启动那一刻的值 —— 在界面上改合并范围就"没反应"。
    /// </summary>
    public static AltTabGroupMode GroupMode => _groupMode;

    /// <summary>合并范围变化时更新。</summary>
    public static void SetGroupMode(AltTabGroupMode mode) => _groupMode = mode;

    /// <summary>手动指定的程序名（小写、不带扩展名）。</summary>
    public static IReadOnlyList<string> ManualProcesses => _manual;

    /// <summary>这些窗口现在被悬浮栏接管。</summary>
    public static IReadOnlyCollection<IntPtr> Handles => _handles;

    /// <summary>监视线程发布本轮结果。</summary>
    public static void Publish(IEnumerable<IntPtr> handles) => _handles = new HashSet<IntPtr>(handles);

    /// <summary>设置里两个开关/名单变化时更新（由界面进程推过来，或启动时从 settings.json 读）。</summary>
    public static void SetScope(bool autoMultiWindow, IEnumerable<string>? processes)
    {
        _autoMultiWindow = autoMultiWindow;

        var list = new List<string>();

        if (processes is not null)
        {
            foreach (var process in processes)
            {
                var name = Normalize(process);
                if (name.Length > 0 && !list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
            }
        }

        _manual = list.ToArray();
    }

    /// <summary>这个程序是不是被用户手动指定接管的。</summary>
    public static bool IsManual(string processName)
    {
        if (processName.Length == 0) return false;

        foreach (var process in _manual)
        {
            if (string.Equals(process, processName, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// 这个窗口是不是"被接管的"（Alt+Tab 的同名进程打包、Alt+~ 的放行判断都问它）。
    ///
    /// 资源管理器窗口类直接算接管（它本来就是默认接管的对象，不受两个新开关影响）；
    /// 其余按监视线程发布的窗口集合判定。
    /// </summary>
    public static bool IsTakeoverWindow(IntPtr hwnd, string className)
        => ExplorerWatcher.IsTakeoverWindowClass(className) || _handles.Contains(hwnd);

    /// <summary>把 "C:\…\chrome.exe" / "Chrome.EXE" / "chrome" 一律归一成小写的 "chrome"。</summary>
    public static string Normalize(string? process)
    {
        if (string.IsNullOrWhiteSpace(process)) return string.Empty;

        var name = process.Trim();

        try
        {
            var file = System.IO.Path.GetFileName(name);
            if (file.Length > 0) name = file;
        }
        catch
        {
            // 名字里有非法字符就当它本来就是程序名
        }

        var dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];

        return name.ToLowerInvariant();
    }
}
