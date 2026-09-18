using System.Windows.Media;
using ExplorerDock.Interop;

namespace ExplorerDock.Services;

/// <summary>
/// 合并卡里的一个预览格：源窗口，以及这一格要不要显示实时画面。
///
/// 同一个窗口的多个标签页共用一张画面（DWM 的缩略图是按窗口给的），
/// 所以只有"窗口里当前显示的那个标签页"这一格才挂画面，其余格子显示图标。
/// </summary>
public readonly record struct AltTabPreviewCell(IntPtr Handle, bool ShowThumbnail);

/// <summary>Alt+Tab 面板里的一张卡片。</summary>
public sealed class AltTabWindowInfo
{
    /// <summary>
    /// 代表哪个窗口：合并卡里是"组内最后活动过的那个窗口"（Z 序最前），
    /// 切到这张卡时激活的就是它。
    /// </summary>
    public IntPtr Handle { get; init; }

    /// <summary>要切到那个窗口的第几个标签页（0 起）；-1 表示整个窗口。</summary>
    public int TabIndex { get; init; } = -1;

    /// <summary>这一项代表的是不是"窗口里当前显示的那个标签页"。</summary>
    public bool TabSelected { get; init; } = true;

    public string Title { get; init; } = string.Empty;

    /// <summary>窗口图标（卡片标题栏用）。取不到就是 null。</summary>
    public ImageSource? Icon { get; set; }

    /// <summary>所属进程名（小写 exe 名）；取不到就是空串。</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>
    /// 这张卡要预览的内容，按 Z 序 / 标签顺序（最近用过的在前）。
    /// 合并卡是这个进程的全部标签页，普通卡只有一个。
    /// </summary>
    public IReadOnlyList<AltTabPreviewCell> Cells { get; init; } = Array.Empty<AltTabPreviewCell>();

    /// <summary>卡上显示的窗口/标签页数（角标用）。</summary>
    public int GroupCount => Cells.Count;

    /// <summary>是不是合并出来的卡（组里不止一项）。</summary>
    public bool Grouped => Cells.Count > 1;
}

/// <summary>
/// 按 Z 序枚举"该出现在 Alt+Tab 里"的窗口，并把同名进程的窗口合并成一张卡。
///
/// 多标签资源管理器窗口在这里被拆开：**每个标签页算一项**（和悬浮栏一个道理），
/// 所以"同名进程打包"打包的是标签页，卡片里的网格也是一格一个标签页。
///
/// 这是本功能真正的意义所在：这份列表是我们自己数的，不依赖系统那套 ——
/// 任务栏按钮被摘掉的文件夹窗口，照样在里面。
/// </summary>
internal static class AltTabWindowList
{
    /// <summary>
    /// 按 Z 序（最近用的在最前）取一批卡片。
    ///
    /// 参与打包的项目按 exe 名归到一张卡里（整张卡的位置＝组内第一项在 Z 序里的位置）；
    /// 不参与的各自独占一张卡，行为与打包功能之前完全一样。
    /// </summary>
    public static List<AltTabWindowInfo> Snapshot(int max = 40)
    {
        var mode = TakeoverState.GroupMode;

        var order = new List<string>();
        var groups = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);

        foreach (var candidate in EnumerateCandidates(max))
        {
            // 合并条件：全部合并，或者"只合并被接管的程序"且这一项确实被接管；
            // 选项是"不合并"时一律走下面的 win: 分支 —— 每项独占一张卡
            bool merge = mode == AltTabGroupMode.AllProcesses
                      || (mode == AltTabGroupMode.TakeoverOnly && candidate.Takeover);

            string key = merge && candidate.ProcessName.Length > 0
                ? "proc:" + candidate.ProcessName
                : $"win:{candidate.Handle.ToInt64():X}:{candidate.TabIndex}";

            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<Candidate>();
                groups[key] = list;
                order.Add(key);
            }

            list.Add(candidate);
        }

        var result = new List<AltTabWindowInfo>(order.Count);

        foreach (var key in order)
        {
            var list = groups[key];

            var cells = new List<AltTabPreviewCell>(list.Count);
            foreach (var item in list)
            {
                // 只有"窗口当前显示的那个标签页"有实时画面
                cells.Add(new AltTabPreviewCell(item.Handle, item.TabIndex < 0 || item.Selected));
            }

            var first = list[0];   // 组内 Z 序最前 = 最后活动过的那个窗口 / 标签页

            result.Add(new AltTabWindowInfo
            {
                Handle = first.Handle,
                TabIndex = first.TabIndex,
                TabSelected = first.TabIndex < 0 || first.Selected,
                Title = first.Title,
                ProcessName = list.Count > 1 ? first.ProcessName : string.Empty,
                Cells = cells,
            });
        }

        return result;
    }

    /// <summary>
    /// 取"和这个窗口同一个进程名"的全部项目（Z 序），给 Alt+~ 在当前进程内切换用。
    /// 多标签窗口在这里同样是每个标签页一项 —— 合并卡里装不下的标签页，照样能在里面选到。
    /// </summary>
    public static List<AltTabWindowInfo> ProcessWindows(IntPtr window, int max = 40)
    {
        var result = new List<AltTabWindowInfo>();
        if (window == IntPtr.Zero) return result;

        NativeMethods.GetWindowThreadProcessId(window, out uint pid);
        var target = NativeMethods.GetProcessName(pid).ToLowerInvariant();
        if (target.Length == 0) return result;

        foreach (var candidate in EnumerateCandidates(max))
        {
            if (!string.Equals(candidate.ProcessName, target, StringComparison.Ordinal)) continue;

            result.Add(new AltTabWindowInfo
            {
                Handle = candidate.Handle,
                TabIndex = candidate.TabIndex,
                TabSelected = candidate.TabIndex < 0 || candidate.Selected,
                Title = candidate.Title,
                ProcessName = target,
                Cells = new[] { new AltTabPreviewCell(candidate.Handle, candidate.TabIndex < 0 || candidate.Selected) },
            });
        }

        return result;
    }

    /// <summary>一个还在候选阶段的窗口 / 标签页。</summary>
    private readonly record struct Candidate(
        IntPtr Handle,
        int TabIndex,
        string Title,
        string ProcessName,
        bool Takeover,
        bool Selected);

    /// <summary>
    /// 按 Z 序列举候选窗口，多标签窗口在这里展开成一标签页一项。
    ///
    /// 窗口过滤条件与悬浮栏接管共用一份（<see cref="ExplorerWatcher.TryDescribeWindow"/>）；
    /// "算不算被悬浮栏接管"问 <see cref="TakeoverState"/> —— 所以设置里手动指定 /
    /// 自动接管进来的程序，在这里也会跟着参与同名进程打包。
    /// </summary>
    private static List<Candidate> EnumerateCandidates(int max)
    {
        var result = new List<Candidate>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (result.Count >= max) return false;

                if (!ExplorerWatcher.TryDescribeWindow(hwnd, out var window)) return true;

                bool takeover = TakeoverState.IsTakeoverWindow(hwnd, window.ClassName);

                // 多标签（只有资源管理器有这个概念）：每个标签页算一项
                var tabs = ExplorerWatcher.IsTakeoverWindowClass(window.ClassName) && ExplorerTabs.MaybeMultiTab(window.Title)
                    ? ExplorerTabs.Read(hwnd)
                    : new List<ExplorerTabInfo>();

                if (tabs.Count <= 1)
                {
                    var single = tabs.Count == 1 && tabs[0].Title.Length > 0 ? tabs[0].Title : window.Title;
                    result.Add(new Candidate(hwnd, -1, single, window.ProcessName, takeover, true));
                    return true;
                }

                foreach (var tab in tabs)
                {
                    if (result.Count >= max) break;

                    var name = tab.Title.Length > 0 ? tab.Title : window.Title;
                    result.Add(new Candidate(hwnd, tab.Index, name, window.ProcessName, takeover, tab.Selected));
                }
            }
            catch
            {
                // 单个窗口查不动就跳过，别把整次枚举带崩
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }
}
