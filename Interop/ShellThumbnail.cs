using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ExplorerDock.Interop;

/// <summary>
/// 取磁盘上文件的系统缩略图（Windows 资源管理器那个）。
///
/// 用途：剪贴板里复制进来的**图片/视频文件**也要以媒体的样子显示 ——
/// 图片给预览图，视频给首帧（Shell 自己会解码），不用我们写解码器。
/// 走 IShellItemImageFactory，这是资源管理器自己在用的那套。
/// </summary>
internal static class ShellThumbnail
{
    private const uint SIIGBF_RESIZETOFIT = 0x00;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, uint flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>拿不到缩略图（格式不支持、路径失效、COM 失败）时返回 null。</summary>
    public static BitmapSource? Get(string path, int size)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;

        object? item = null;
        var hBitmap = IntPtr.Zero;

        try
        {
            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);

            if (item is not IShellItemImageFactory factory) return null;

            factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_RESIZETOFIT, out hBitmap);
            if (hBitmap == IntPtr.Zero) return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);

            if (item is not null && Marshal.IsComObject(item))
            {
                try
                {
                    Marshal.ReleaseComObject(item);
                }
                catch
                {
                    // 忽略
                }
            }
        }
    }
}
