using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ExplorerDock.Interop;

/// <summary>
/// 抓一张窗口快照当缩略图。
///
/// 用 PrintWindow + PW_RENDERFULLCONTENT：对 DWM 合成的窗口（资源管理器、浏览器、UWP）
/// 也能拿到真实内容，而 BitBlt 抓不到被遮挡的窗口。
/// 实测单个窗口 1–47ms，所以只在后台线程串行调用，抓到一张交一张。
/// </summary>
internal static class WindowThumbnail
{
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    /// <summary>单张位图的尺寸上限（像素）。极端超宽窗口下宁可截一块，也不要一次分配几百 MB。</summary>
    private const int MaxPixelsWide = 3840;
    private const int MaxPixelsHigh = 2160;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeMethods.RECT lpRect);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// 抓缩略图。失败、或者抓到的是全黑（最小化/受保护窗口）时返回 null，
    /// 由调用方退化成"图标 + 标题"卡片 —— 系统对这类窗口也是这么处理的。
    /// </summary>
    public static BitmapSource? Capture(IntPtr hwnd, int targetWidth)
    {
        if (targetWidth <= 0) return null;

        try
        {
            if (!GetWindowRect(hwnd, out var rect)) return null;

            int width = Math.Min(rect.Right - rect.Left, MaxPixelsWide);
            int height = Math.Min(rect.Bottom - rect.Top, MaxPixelsHigh);
            if (width <= 0 || height <= 0) return null;

            Bitmap full;
            try
            {
                full = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            }
            catch
            {
                // 位图都分配不出来（内存吃紧），这次就算了
                return null;
            }

            using (full)
            {
                using (var graphics = Graphics.FromImage(full))
                {
                    if (!TryPrint(graphics, hwnd, PW_RENDERFULLCONTENT))
                    {
                        // 有些窗口偏偏只认老式的 PrintWindow（flags = 0），再试一次
                        TryPrint(graphics, hwnd, 0);
                    }
                }

                if (IsAllBlack(full)) return null;

                int thumbWidth = Math.Min(targetWidth, full.Width);
                int thumbHeight = Math.Max(1, (int)Math.Round(full.Height * (thumbWidth / (double)full.Width)));

                using var scaled = new Bitmap(thumbWidth, thumbHeight, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(scaled))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(full, new Rectangle(0, 0, thumbWidth, thumbHeight));
                }

                return ToBitmapSource(scaled);
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool TryPrint(Graphics graphics, IntPtr hwnd, uint flags)
    {
        var hdc = graphics.GetHdc();
        try
        {
            return PrintWindow(hwnd, hdc, flags);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }
    }

    private static BitmapSource? ToBitmapSource(Bitmap bitmap)
    {
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                handle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();   // 冻结后才能跨线程交给 UI
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    /// <summary>抽 8×8 个点采样：整张全黑（含全透明）就认为这张图没有参考价值。</summary>
    private static bool IsAllBlack(Bitmap bitmap)
    {
        int stepX = Math.Max(1, bitmap.Width / 8);
        int stepY = Math.Max(1, bitmap.Height / 8);

        for (int y = stepY / 2; y < bitmap.Height; y += stepY)
        {
            for (int x = stepX / 2; x < bitmap.Width; x += stepX)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.A > 8 && (color.R > 8 || color.G > 8 || color.B > 8)) return false;
            }
        }

        return true;
    }
}
