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
    private const uint SHGFI_SYSICONINDEX = 0x00004000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const int SHIL_JUMBO = 0x4;
    private const int SHIL_EXTRALARGE = 0x2;
    private const int ILD_TRANSPARENT = 0x1;

    private static readonly Guid IIDIImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
        int ReplaceIcon(int i, IntPtr hicon, ref int pi);
        int SetOverlayImage(int iImage, int iOverlay);
        int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
        int Draw(IntPtr pimldp);
        int Remove(int i);
        int GetIcon(int i, int flags, out IntPtr picon);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

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
        // 首选 shell 的 jumbo(256px) 图像列表：按显示尺寸缩下去最清晰，高 DPI 下也不糊
        try
        {
            bool real = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
            var info = new SHFILEINFO();
            var flags = SHGFI_SYSICONINDEX | (real ? 0u : SHGFI_USEFILEATTRIBUTES);
            SHGetFileInfo(real ? path! : "folder", FILE_ATTRIBUTE_DIRECTORY, ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(), flags);

            var iid = IIDIImageList;
            if (SHGetImageList(SHIL_JUMBO, ref iid, out var list) == 0 && list is not null)
            {
                if (list.GetIcon(info.iIcon, ILD_TRANSPARENT, out var hIcon) == 0 && hIcon != IntPtr.Zero)
                {
                    var source = FromHIcon(hIcon);
                    DestroyIcon(hIcon);
                    if (source is not null) return source;
                }
            }
        }
        catch
        {
            // 落到下面的兜底路径
        }

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
