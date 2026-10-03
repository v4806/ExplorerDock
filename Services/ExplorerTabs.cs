using System.Windows.Automation;
using ExplorerDock.Interop;

namespace ExplorerDock.Services;

/// <summary>资源管理器窗口里的一个标签页。</summary>
public sealed class ExplorerTabInfo
{
    /// <summary>标签页序号（0 起，从左到右）。</summary>
    public int Index { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>是不是这个窗口里当前显示的那个标签页。</summary>
    public bool Selected { get; init; }
}

/// <summary>
/// 读资源管理器窗口的标签页、以及切到指定标签页。
///
/// 只能走 UI Automation：标签页不是独立窗口，纯 Win32 最多只能从窗口标题里看到
/// "和 N 个其他选项卡"，拿不到每个标签叫什么、排在哪儿。
///
/// 两个关键设计：
/// 1) 所有 UIA 调用都排到一个专用 STA 线程上执行 —— UIA 客户端在 STA 上最稳，
///    调用方（轮询线程、Alt+Tab 的后台线程、界面线程）也不用操心 COM 初始化；
/// 2) 读结果按窗口缓存一小段时间 —— 单次 UIA 调用约 100ms，轮询里绝不能每 350ms 挨个问一遍。
/// </summary>
internal static class ExplorerTabs
{
    /// <summary>拿 UIA 结果的超时。超时也放手：UIA 卡住时不能让轮询和界面跟着僵住。</summary>
    private const int CallTimeoutMs = 1500;

    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, Entry> Cache = new();

    private sealed class Entry
    {
        public DateTime At { get; set; }

        public List<ExplorerTabInfo> Tabs { get; set; } = new();
    }

    /// <summary>
    /// 窗口标题里带"还有 N 个其他选项卡"时，这个窗口才可能有多标签页。
    /// 只有这种窗口才值得去问 UIA —— 单标签窗口（绝大多数）保持零开销。
    /// </summary>
    public static bool MaybeMultiTab(string title)
    {
        if (string.IsNullOrEmpty(title)) return false;

        return title.Contains("个其他选项卡", StringComparison.Ordinal)
            || title.Contains("个其他标签页", StringComparison.Ordinal)
            || title.Contains("other tab", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 读窗口的标签页（带缓存，默认 1.5 秒内复用）。
    /// 不是多标签窗口、或 UIA 读不到时返回空列表 —— 调用方就按"整窗一项"处理。
    ///
    /// 最小化的窗口要特别注意：标签栏根本没画在屏幕上，UIA 这时读不到标签项。
    /// 所以最小化期间一律用上次的结果，否则悬浮栏会从"每个标签页一个按钮"
    /// 悄悄退回"整窗一个按钮"，Alt+Tab 里也一样。
    /// </summary>
    public static List<ExplorerTabInfo> Read(IntPtr hwnd, int maxAgeMs = 1500)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return new List<ExplorerTabInfo>();

        // 未响应的窗口根本不发 UIA 调用：UIA 对 hung 窗口能卡十几秒，白白占掉一条通道。
        // 返回空列表后，下面的缓存逻辑会保住上一次认出来的标签页（最小化走的也是这条兜底）。
        if (NativeMethods.IsHungAppWindow(hwnd)) return new List<ExplorerTabInfo>();

        bool minimized = NativeMethods.IsIconic(hwnd);

        lock (Gate)
        {
            if (Cache.TryGetValue(hwnd, out var cached))
            {
                if (minimized && cached.Tabs.Count > 0) return cached.Tabs;

                if ((DateTime.UtcNow - cached.At).TotalMilliseconds < maxAgeMs) return cached.Tabs;
            }
        }

        var tabs = Run(Channel("read", hwnd), () => ReadCore(hwnd), new List<ExplorerTabInfo>(), "uia: read tabs");

        lock (Gate)
        {
            if (!Cache.TryGetValue(hwnd, out var entry))
            {
                if (Cache.Count > 64) Cache.Clear();

                entry = new Entry();
                Cache[hwnd] = entry;
            }

            // 读空了但之前有结果（最小化、标签栏刚开始重建的瞬间）：
            // 保住旧结果，别让已经认出来的标签页丢掉
            if (tabs.Count == 0 && entry.Tabs.Count > 0)
            {
                entry.At = DateTime.UtcNow;
                return entry.Tabs;
            }

            entry.At = DateTime.UtcNow;
            entry.Tabs = tabs;
        }

        return tabs;
    }

    /// <summary>
    /// 切到某个标签页（0 起）。成功后顺手把缓存里的选中状态改过来，
    /// 悬浮栏和切换面板立刻就能反映新状态，不用等下一次 UIA 刷新。
    ///
    /// 已经在目标标签上时直接返回：省掉一次没意义的 UI Automation 调用。
    /// </summary>
    public static bool Activate(IntPtr hwnd, int index)
    {
        if (hwnd == IntPtr.Zero || index < 0 || !NativeMethods.IsWindow(hwnd)) return false;

        // 未响应的窗口发了也白发（还会占住一条通道十几秒）
        if (NativeMethods.IsHungAppWindow(hwnd)) return false;

        // 已经在目标标签上：什么都不用做（省掉一次没意义的 UI Automation 调用）
        if (SelectedIndex(hwnd) == index) return true;

        if (!Run(Channel("activate", hwnd), () => ActivateCore(hwnd, index), false, "uia: activate tab")) return false;

        return UpdateSelectedCache(hwnd, index);
    }

    /// <summary>切换成功后把缓存里的选中状态改过来（悬浮栏/卡片立刻反映，不用等下一轮 UIA）。</summary>
    private static bool UpdateSelectedCache(IntPtr hwnd, int index)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(hwnd, out var entry) && entry.Tabs.Count > 0)
            {
                var updated = new List<ExplorerTabInfo>(entry.Tabs.Count);

                foreach (var tab in entry.Tabs)
                {
                    updated.Add(new ExplorerTabInfo
                    {
                        Index = tab.Index,
                        Title = tab.Title,
                        Selected = tab.Index == index,
                    });
                }

                entry.Tabs = updated;
                entry.At = DateTime.UtcNow;
            }
        }

        return true;
    }

    /// <summary>当前选中的标签页序号；读不到就是 -1。</summary>
    public static int SelectedIndex(IntPtr hwnd)
    {
        foreach (var tab in Read(hwnd))
        {
            if (tab.Selected) return tab.Index;
        }

        return -1;
    }

    /// <summary>
    /// 直接触发资源管理器命令栏上的"粘贴"命令（UI Automation Invoke）。
    ///
    /// **这是让"拖放到按钮上"真正粘进文件夹窗口的关键一步**（实测确认）：
    /// Ctrl+V 只会落在"当前有键盘焦点的控件"上，而窗口刚被激活/刚切过标签时，
    /// 焦点常常不在文件列表里 —— 那时补的 Ctrl+V 什么都粘不上，用户还得先点一下文件区。
    /// 让窗口执行它自己的粘贴命令就绕开了焦点：命令是窗口自己跑的，谁触发它不在乎。
    /// </summary>
    public static bool InvokePasteCommand(IntPtr hwnd)
    {
        // 未响应的窗口：粘贴命令发了也落不到实处，先挡掉（顺便不占通道）
        if (hwnd == IntPtr.Zero || NativeMethods.IsHungAppWindow(hwnd)) return false;

        return Run(Channel("paste", hwnd), () => InvokePasteCore(hwnd), false, "uia: paste");
    }

    private static bool InvokePasteCore(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;

        var root = AutomationElement.FromHandle(hwnd);
        if (root is null) return false;

        // 命令栏上的按钮类型不止 Button：新建/粘贴那排是 Button，也有 SplitButton/MenuItem 的场合
        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));

        AutomationElementCollection all;

        try
        {
            all = root.FindAll(TreeScope.Descendants, condition);
        }
        catch
        {
            return false;
        }

        foreach (AutomationElement element in all)
        {
            string name;

            try
            {
                name = NormalizeCommandName(element.Current.Name);
            }
            catch
            {
                continue;
            }

            // 只认"粘贴"本身；"粘贴快捷方式"之类不在命令栏里，这里不用担心误点
            if (name != "粘贴" && !name.Equals("Paste", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
                    && pattern is InvokePattern invoke)
                {
                    invoke.Invoke();
                    return true;
                }
            }
            catch
            {
                // 换下一个同名元素
            }
        }

        AltTabController.Log($"uia paste 0x{hwnd.ToInt64():X}: 没找到可用的“粘贴”命令");
        return false;
    }

    /// <summary>
    /// 规范化命令按钮的名字。
    ///
    /// 资源管理器命令栏的按钮名字里塞了**零宽字符**（用来对齐快捷键助记符）：
    /// UIA 读出来是 "粘贴\u200B\u200B"，直接跟 "粘贴" 比较永远不相等 ——
    /// 不处理这一点，就会一直报"找不到粘贴命令"。
    /// </summary>
    private static string NormalizeCommandName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;

        var builder = new System.Text.StringBuilder(name.Length);

        foreach (var ch in name)
        {
            if (ch is '\u200B' or '\u200C' or '\u200D' or '\uFEFF') continue;
            if (char.IsWhiteSpace(ch)) continue;

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>UIA 就绪时用它自己的树找；这里只做一次缓存清理（窗口关掉后不再留着）。</summary>
    public static void Forget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        lock (Gate)
        {
            Cache.Remove(hwnd);
        }
    }

    // ---------- UIA 调用隔离 ----------

    /// <summary>
    /// 所有 UIA 调用都从这儿走：独立线程 + 硬超时，通道按"用途 + 窗口"分开。
    ///
    /// 旧实现是**一个**专用 worker 线程 + 队列：调用方超时就不等了，可 worker 仍卡在那个
    /// UIA 调用里（对未响应的窗口能卡十几秒），于是后面所有 UIA 请求全部排队、一个个等超时 ——
    /// 表现就是"一个未响应窗口让标签页、粘贴、拖放全哑掉"，严重时看起来就是悬浮栏卡死。
    /// 现在每个窗口各自一条通道：卡住的那条被跳过，其他窗口照常工作。
    /// </summary>
    private static T Run<T>(string channel, Func<T> work, T fallback, string tag)
    {
        // UIA 客户端在 STA 线程上最稳（原来那个 worker 就是 STA），隔离线程沿用同一策略
        return BoundedCall.Run(channel, CallTimeoutMs, work, fallback, tag, sta: true);
    }

    /// <summary>通道名：按用途 + 窗口句柄分，保证一个窗口卡住不牵连别的窗口。</summary>
    private static string Channel(string kind, IntPtr hwnd) => $"uia:{kind}:0x{hwnd.ToInt64():X}";

    private static List<ExplorerTabInfo> ReadCore(IntPtr hwnd)
    {
        var list = new List<ExplorerTabInfo>();

        var root = AutomationElement.FromHandle(hwnd);
        if (root is null) return list;

        var found = FindTabItems(root);

        for (int i = 0; i < found.Count; i++)
        {
            var tab = found[i];
            bool selected = false;

            try
            {
                if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                    && pattern is SelectionItemPattern select)
                {
                    selected = select.Current.IsSelected;
                }
            }
            catch
            {
                // 拿不到选中状态就当没选中
            }

            var name = string.Empty;

            try
            {
                name = tab.Current.Name ?? string.Empty;
            }
            catch
            {
                // 元素可能已经消失了
            }

            list.Add(new ExplorerTabInfo
            {
                Index = i,
                Title = name,
                Selected = selected,
            });
        }

        return list;
    }

    private static bool ActivateCore(IntPtr hwnd, int index)
    {
        var root = AutomationElement.FromHandle(hwnd);
        if (root is null) return false;

        var found = FindTabItems(root);
        if (index >= found.Count) return false;

        var tab = found[index];

        if (!tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
            || pattern is not SelectionItemPattern select)
        {
            return false;
        }

        select.Select();
        return true;
    }

    private static AutomationElementCollection FindTabItems(AutomationElement root)
    {
        var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
        return root.FindAll(TreeScope.Descendants, condition);
    }
}
