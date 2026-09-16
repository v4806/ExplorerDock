using System.Windows.Media;

namespace ExplorerDock.Models;

/// <summary>一个资源管理器窗口（文件夹窗口）的只读快照。</summary>
public sealed class ExplorerWindowInfo
{
    public IntPtr Handle { get; init; }
    public string Title { get; set; } = "文件资源管理器";
    public string? LocationPath { get; set; }
    public bool IsMinimized { get; set; }
    public ImageSource? Icon { get; set; }

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
