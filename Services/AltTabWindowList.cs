using System.Windows.Media;
using ExplorerDock.Interop;

namespace ExplorerDock.Services;

/// <summary>Alt+Tab 面板里的一张卡片。</summary>
public sealed class AltTabWindowInfo
{
    public IntPtr Handle { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>窗口图标（卡片标题栏用）。取不到就是 null。</summary>
    public ImageSource? Icon { get; set; }
}

/// <summary>
/// 按 Z 序枚举"该出现在 Alt+Tab 里"的顶层窗口。
///
/// 这是本功能真正的意义所在：这份列表是我们自己数的，不依赖系统那套 ——
/// 任务栏按钮被摘掉的文件夹窗口，照样在里面。
/// </summary>
internal static class AltTabWindowList
{
    /// <summary>这些是 shell 自己的窗口，不能出现在切换列表里。</summary>
    private static readonly string[] ShellClasses =
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

    /// <summary>按 Z 序（最近用的在最前）取一批窗口。</summary>
    public static List<AltTabWindowInfo> Snapshot(int max = 40)
    {
        var result = new List<AltTabWindowInfo>();
        int self = Environment.ProcessId;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (result.Count >= max) return false;

                if (!NativeMethods.IsWindowVisible(hwnd)) return true;

                // 有 owner 的通常是对话框/浮动面板，不是独立的应用窗口
                if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return true;

                long extended = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
                if ((extended & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

                // UWP 的"隐身窗口"：IsWindowVisible 说可见，其实被 DWM 藏着
                if (NativeMethods.IsCloaked(hwnd)) return true;

                var className = NativeMethods.GetClassNameSafe(hwnd);
                foreach (var shell in ShellClasses)
                {
                    if (string.Equals(className, shell, StringComparison.Ordinal)) return true;
                }

                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if ((int)pid == self) return true;   // 悬浮栏、切换面板自己

                var title = NativeMethods.GetWindowTextSafe(hwnd);
                if (string.IsNullOrWhiteSpace(title)) return true;

                result.Add(new AltTabWindowInfo { Handle = hwnd, Title = title.Trim() });
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
