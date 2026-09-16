using System.Runtime.InteropServices;

namespace ExplorerDock.Services;

public readonly record struct ShellWindowLocation(string Url, string DisplayName)
{
    public string? ToPath()
    {
        if (string.IsNullOrWhiteSpace(Url)) return null;
        if (!Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var uri = new Uri(Url);
            var path = Uri.UnescapeDataString(uri.LocalPath);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 借 Shell.Application 拿到每个资源管理器窗口当前所在的路径。
/// 拿不到不算错——只是按钮上没有专属图标、提示里没有完整路径。
/// </summary>
internal static class ShellUrlProbe
{
    public static Dictionary<IntPtr, ShellWindowLocation> Query()
    {
        var map = new Dictionary<IntPtr, ShellWindowLocation>();

        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return map;

            dynamic shell = Activator.CreateInstance(type)!;
            dynamic windows = shell.Windows();
            int count = (int)windows.Count;

            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic window = windows.Item(i);
                    long raw = Convert.ToInt64(window.HWND);
                    if (raw == 0) continue;

                    var url = Convert.ToString(window.LocationURL) ?? string.Empty;
                    var name = Convert.ToString(window.LocationName) ?? string.Empty;
                    map[new IntPtr(raw)] = new ShellWindowLocation(url, name);
                }
                catch
                {
                    // 某个窗口取不到就跳过
                }
            }
        }
        catch
        {
            // Shell.Application 不可用时静默降级
        }

        return map;
    }
}
