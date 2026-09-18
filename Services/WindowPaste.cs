using System.Windows;
using System.Windows.Threading;
using ExplorerDock.Interop;

namespace ExplorerDock.Services;

/// <summary>
/// 「把内容粘到某个窗口」的收尾动作：先把内容放进系统剪贴板（调用方负责），
/// 再激活目标窗口（必要时切到指定标签页）、确认它真的到了前台，然后补一次 Ctrl+V。
///
/// 之所以不直接把拖放数据交给目标窗口：OLE 拖放时图片会退化成 CF_BITMAP，
/// 原图字节到不了对面（详见 ClipboardController 的说明），而"写剪贴板 + 补 Ctrl+V"
/// 能让目标拿到的格式和点一下条目粘贴完全一致。
///
/// 不补点击：目标窗口被切到前台活动之后，Ctrl+V 就会落到它身上 —— 悬浮栏的悬停呼出、
/// Alt+Tab 卡片的悬停激活都在松手之前把前台准备好了。
/// </summary>
internal static class WindowPaste
{
    /// <summary>让一拍再动手：拖放/松手刚结束时 OLE 还在收尾，当场硬抢前台常被前台锁顶回来。</summary>
    private const int SettleMs = 60;

    /// <summary>最多催几次前台；到点还没到位就放弃，免得把 Ctrl+V 打到别的窗口上。</summary>
    private const int MaxForegroundAttempts = 5;

    /// <summary>确认前台之后等多久发 Ctrl+V（让窗口把焦点安顿好）。</summary>
    private const int AfterActivateMs = 160;

    /// <summary>
    /// 拖过来的数据能不能走"写剪贴板 + 补 Ctrl+V"这条路粘出去。
    /// 不能粘的类型就别回"可放下"—— 系统的拖放光标会变成带叉的禁止样子，等于告诉用户"这儿不能放"。
    /// </summary>
    public static bool CanPaste(IDataObject? data)
    {
        if (data is null) return false;

        return data.GetDataPresent(DataFormats.FileDrop)
            || data.GetDataPresent(DataFormats.UnicodeText)
            || data.GetDataPresent(DataFormats.Text)
            || data.GetDataPresent(DataFormats.Bitmap)
            || data.GetDataPresent(DataFormats.Html)
            || data.GetDataPresent(DataFormats.Rtf);
    }

    /// <summary>激活目标窗口（tabIndex ≥ 0 时切到那个标签页），随后确认前台并把 Ctrl+V 补上去。</summary>
    public static void Deliver(App app, IntPtr hwnd, int tabIndex = -1)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;

        var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SettleMs) };

        settle.Tick += (_, _) =>
        {
            settle.Stop();

            app.WindowHost.Activate(hwnd, tabIndex);
            Confirm(app, hwnd, tabIndex, 0);
        };

        settle.Start();
    }

    /// <summary>
    /// 反复确认目标窗口真的到了前台，再补 Ctrl+V。
    ///
    /// 切标签（多标签资源管理器窗口）走的是 UI Automation，比单纯切前台慢，
    /// 前台没站稳就发键，这次 Ctrl+V 会打到别的窗口上。
    /// </summary>
    private static void Confirm(App app, IntPtr hwnd, int tabIndex, int attempt)
    {
        int delay = attempt == 0 ? (tabIndex >= 0 ? 420 : 180) : 250;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            if (!app.WindowHost.IsForegroundWindow(hwnd))
            {
                if (attempt >= MaxForegroundAttempts)
                {
                    ClipboardMonitor.Log($"paste: 0x{hwnd.ToInt64():X} 始终没到前台，放弃这次粘贴");
                    return;
                }

                app.WindowHost.Activate(hwnd, attempt == 0 ? tabIndex : -1);
                Confirm(app, hwnd, tabIndex, attempt + 1);
                return;
            }

            // 文件夹窗口：直接让它执行自己的"粘贴"命令（UI Automation Invoke 命令栏的粘贴按钮）。
            //
            // 这是**真正让拖放粘进文件夹窗口的那条路**（实测确认）：Ctrl+V 只落在"有键盘焦点的控件"上，
            // 而窗口刚被激活 / 刚切过标签时焦点常常不在文件列表里，补的 Ctrl+V 会粘不上
            // （表现为"切过去直接 Ctrl+V 没反应，得先点一下文件区"）。
            if (App.IsFileManagerWindow(hwnd) && app.WindowHost.PasteIntoFolder(hwnd))
            {
                ClipboardMonitor.Log($"paste -> hwnd=0x{hwnd.ToInt64():X} tab={tabIndex} via=uia");
                return;
            }

            // 其他程序（QQ、Blender 这类）以及命令没找到时的兜底：补一次 Ctrl+V
            var paste = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AfterActivateMs) };

            paste.Tick += (_, _) =>
            {
                paste.Stop();

                app.WindowHost.SendPaste();
                ClipboardMonitor.Log($"paste -> hwnd=0x{hwnd.ToInt64():X} tab={tabIndex}");
            };

            paste.Start();
        };

        timer.Start();
    }
}
