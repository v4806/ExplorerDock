using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ExplorerDock.Interop;

internal static class ShellInterop
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>取文件夹图标的 HICON（调用方负责 DestroyIcon）。</summary>
    public static IntPtr GetFolderHIcon(bool small)
    {
        try
        {
            var info = new SHFILEINFO();
            var flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | (small ? SHGFI_SMALLICON : SHGFI_LARGEICON);
            SHGetFileInfo("folder", FILE_ATTRIBUTE_DIRECTORY, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            return info.hIcon;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static ImageSource? _defaultIcon;

    private static ImageSource? FromHIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 拿到某个文件夹在资源管理器里显示用的图标。
    /// path 为真实目录时取该目录的图标，否则退回通用文件夹图标。
    /// </summary>
    public static ImageSource? GetFolderIcon(string? path)
    {
        var key = string.IsNullOrWhiteSpace(path) ? "<default>" : path;
        lock (IconCache)
        {
            if (IconCache.TryGetValue(key, out var cached)) return cached;
        }

        var icon = BuildIcon(path);

        lock (IconCache)
        {
            if (IconCache.Count > 256) IconCache.Clear();
            IconCache[key] = icon;
        }

        return icon;
    }

    private static ImageSource? BuildIcon(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                var info = new SHFILEINFO();
                var h = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
                var src = FromHIcon(h);
                if (h != IntPtr.Zero) DestroyIcon(h);
                if (src is not null) return src;
            }
        }
        catch
        {
            // 忽略，走通用图标
        }

        if (_defaultIcon is not null) return _defaultIcon;

        var fallbackHandle = GetFolderHIcon(small: false);
        _defaultIcon = FromHIcon(fallbackHandle);
        if (fallbackHandle != IntPtr.Zero) DestroyIcon(fallbackHandle);
        return _defaultIcon;
    }
}
