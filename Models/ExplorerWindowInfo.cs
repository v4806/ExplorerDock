using System.Windows.Media;

namespace ExplorerDock.Models;

/// <summary>悬浮栏上的一个按钮：单标签窗口是整个窗口，多标签窗口是一个标签页。</summary>
public sealed class ExplorerWindowInfo
{
    /// <summary>所属窗口。</summary>
    public IntPtr Handle { get; init; }

    public string Title { get; set; } = "文件资源管理器";

    public string? LocationPath { get; set; }

    /// <summary>
    /// 所属程序（小写 exe 名，不带扩展名）。悬浮栏按它分组：每个程序独占一行。
    /// 资源管理器固定为 "explorer"，排在所有程序的最前面。
    /// </summary>
    public string ProcessName { get; init; } = "explorer";

    /// <summary>程序 exe 的完整路径（非文件夹窗口取图标用）；取不到就是 null。</summary>
    public string? IconPath { get; init; }

    /// <summary>是不是资源管理器文件夹窗口：决定图标来源与右键菜单的文案。</summary>
    public bool IsExplorer { get; init; } = true;

    public bool IsMinimized { get; set; }

    public ImageSource? Icon { get; set; }

    /// <summary>标签页序号（0 起）；-1 表示这一项代表整个窗口（没有标签页信息）。</summary>
    public int TabIndex { get; init; } = -1;

    /// <summary>是不是窗口里当前显示的那个标签页（非多标签窗口恒为 true）。</summary>
    public bool TabSelected { get; init; } = true;

    public string Tooltip
    {
        get
        {
            var text = Title;
            if (!string.IsNullOrWhiteSpace(LocationPath)) text += "\n" + LocationPath;
            return text + "\n\n左键：切换窗口　中键：关闭　右键：更多";
        }
    }
}

/// <summary>一次轮询得到的全局快照。</summary>
public sealed class ExplorerSnapshot
{
    public static readonly ExplorerSnapshot Empty = new();

    /// <summary>按打开顺序排列。</summary>
    public IReadOnlyList<ExplorerWindowInfo> Windows { get; init; } = Array.Empty<ExplorerWindowInfo>();

    public IntPtr Foreground { get; init; }
}
