using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ExplorerDock.Interop;

/// <summary>采集到的剪贴板内容类别。</summary>
internal enum ClipboardPayloadKind
{
    Text,
    Image,
    Files,
}

/// <summary>剪贴板上一种格式的原始内容。</summary>
public sealed class ClipFormat
{
    /// <summary>剪贴板格式名（HTML Format / Rich Text Format / UnicodeText …）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>纯文本内容（文本类格式用）。</summary>
    public string? Text { get; set; }

    /// <summary>原始字节（HTML / RTF 这类二进制格式用）。</summary>
    public byte[]? Bytes { get; set; }
}

/// <summary>
/// 一次剪贴板采集的结果。
///
/// 图片一律是**原样字节**：剪贴板给什么就存什么，不转码、不缩放、不做有损压缩。
/// ImageFormat 记录这串字节原本属于哪个剪贴板格式，粘贴时按同一个格式放回去；
/// Formats 记录同一次复制里并存的其他格式，粘贴时一并放回。
/// </summary>
internal sealed class ClipboardPayload
{
    public ClipboardPayloadKind Kind { get; init; }

    public string? Text { get; init; }

    public byte[]? ImageBytes { get; init; }

    /// <summary>PNG / DIB / DIBV5 / EMF。</summary>
    public string? ImageFormat { get; init; }

    public string[]? Files { get; init; }

    /// <summary>同一次复制里并存的其他格式（HTML / RTF 的原始字节）。</summary>
    public List<ClipFormat>? Formats { get; init; }

    /// <summary>
    /// 从 HTML 里抠出来的预览图（只用来在列表里显示缩略图，**不写回剪贴板**）。
    /// QQ、微信这类程序复制"图 + 字"时只给私有格式和 HTML，一个位图格式都不给，
    /// 想看到缩略图只能从 HTML 里捞。
    /// </summary>
    public byte[]? PreviewImageBytes { get; init; }

    /// <summary>内容指纹（SHA-256），用于"连复制两次同一内容不重复入库"。</summary>
    public string Hash { get; init; } = string.Empty;
}

/// <summary>
/// 剪贴板变化监听窗口。
///
/// AddClipboardFormatListener 需要一个窗口句柄来收 WM_CLIPBOARDUPDATE。
/// 这里建一个 0 尺寸、放到屏幕外的 HwndSource 专用消息窗口 ——
/// 不占用悬浮栏的窗口，也不会因为悬浮栏被隐藏而丢事件。
/// 必须在 UI 线程创建。
/// </summary>
internal sealed class ClipboardMessageWindow : IDisposable
{
    private readonly HwndSource _source;

    public ClipboardMessageWindow()
    {
        var parameters = new HwndSourceParameters("ExplorerDock.ClipboardListener")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        ClipboardInterop.AddClipboardFormatListener(_source.Handle);
    }

    /// <summary>剪贴板内容变了（已经在 UI 线程）。</summary>
    public event Action? Updated;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)ClipboardInterop.WM_CLIPBOARDUPDATE)
        {
            try
            {
                Updated?.Invoke();
            }
            catch
            {
                // 采集出问题绝不影响剪贴板本身
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        try
        {
            ClipboardInterop.RemoveClipboardFormatListener(_source.Handle);
        }
        catch
        {
            // 忽略
        }

        try
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
        catch
        {
            // 忽略
        }
    }
}

/// <summary>剪贴板读取与剪贴板相关的 Win32 调用。</summary>
internal static class ClipboardInterop
{
    public const uint WM_CLIPBOARDUPDATE = 0x031D;

    public const uint CF_BITMAP = 2;
    public const uint CF_DIB = 8;
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_ENHMETAFILE = 14;
    public const uint CF_DIBV5 = 17;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern uint GetEnhMetaFileBits(IntPtr hemf, uint cbBuffer, byte[]? lpbBuffer);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    private const string PngFormatName = "PNG";

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    // ---------- 采集 ----------

    private const int ReadAttempts = 3;
    private const int RetryDelayMs = 120;

    /// <summary>文本最多存这么多字符（超出截断）。</summary>
    public const int MaxTextChars = 4_000_000;

    /// <summary>
    /// 读一次剪贴板。剪贴板是全局独占资源，别的程序正在写时会失败，所以重试几次。
    /// 全部失败返回 null（丢这一条，绝不影响剪贴板本身）。
    /// </summary>
    public static ClipboardPayload? Read()
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return ReadOnce();
            }
            catch
            {
                if (attempt >= ReadAttempts - 1) return null;
                Thread.Sleep(RetryDelayMs);
            }
        }
    }

    private static ClipboardPayload? ReadOnce()
    {
        // 文件优先：复制文件时系统会同时塞一个位图占位，先看图就会把文件当成图片
        var files = ReadFiles();
        if (files is { Length: > 0 }) return BuildFiles(files);

        var data = Clipboard.GetDataObject();
        if (data is null) return null;

        var image = ReadImage(data);
        var text = ReadText(data);
        var extras = ReadAllFormats(data);

        // 同一次复制里常常并存好几个格式（Word 就是文本 + HTML + RTF + 图元文件一起给），
        // 全都留着：列表里按图片显示，粘贴回去时原样一起放，下游程序各取所需。
        if (image is not null) return BuildImage(image.Value.Bytes, image.Value.Format, text, extras);

        if (!string.IsNullOrWhiteSpace(text))
        {
            return BuildText(text, extras, ExtractPreviewImage(extras));
        }

        return null;
    }

    private static byte[]? ExtractPreviewImage(List<ClipFormat>? formats)
    {
        if (formats is null) return null;

        foreach (var format in formats)
        {
            if (!string.Equals(format.Name, "HTML Format", StringComparison.OrdinalIgnoreCase)) continue;
            if (format.Bytes is not { Length: > 0 }) continue;

            var image = ExtractHtmlImage(format.Bytes);
            if (image is not null) return image;
        }

        return null;
    }

    /// <summary>
    /// 从 CF_HTML 里找出第一张图片。两种给图方式都要认：
    /// - data:image/...;base64,... —— 图嵌在 HTML 里；
    /// - &lt;img src="file:///E:\...\x.jpg"&gt; —— 只给本地路径（QQ、微信复制图文就是这样，
    ///   剪贴板上根本没有图片字节，读得到这个文件才有图可显示）。
    /// 抠出来的图只用于列表缩略图，**不写回剪贴板**。
    /// </summary>
    public static byte[]? ExtractHtmlImage(byte[] html)
    {
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(html);

            var embedded = ExtractBase64Image(text);
            if (embedded is not null) return embedded;

            foreach (var path in FindImagePaths(text))
            {
                var bytes = ReadLocalImage(path);
                if (bytes is not null) return bytes;
            }

            // 本地也没有（网页里的图多半是远程地址）：把第一张能下到的当预览。
            // 只在后台采集线程里做，有超时和大小上限，失败就算了。
            foreach (var url in FindRemoteImageUrls(text))
            {
                var bytes = DownloadImage(url);
                if (bytes is not null) return bytes;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static readonly System.Net.Http.HttpClient ImageClient = CreateImageClient();

    private static System.Net.Http.HttpClient CreateImageClient()
    {
        var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ExplorerDock");
        return client;
    }

    private static IEnumerable<string> FindRemoteImageUrls(string html)
    {
        foreach (System.Text.RegularExpressions.Match match in SrcPattern.Matches(html))
        {
            var raw = match.Groups[1].Value;

            if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                yield return raw;
            }
        }
    }

    private static byte[]? DownloadImage(string url)
    {
        try
        {
            using var response = ImageClient
                .GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter()
                .GetResult();

            if (!response.IsSuccessStatusCode) return null;

            var declared = response.Content.Headers.ContentLength;
            if (declared is > MaxDownloadBytes) return null;

            var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return bytes.Length is > 0 && bytes.LongLength <= MaxDownloadBytes && LooksLikeImage(bytes) ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private const long MaxDownloadBytes = 8 * 1024 * 1024;

    private static byte[]? ExtractBase64Image(string html)
    {
        int marker = html.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;

        var base64 = new System.Text.StringBuilder();
        int start = marker + "base64,".Length;

        for (int i = start; i < html.Length; i++)
        {
            char c = html[i];

            if (char.IsWhiteSpace(c)) continue;
            if (!(char.IsLetterOrDigit(c) || c is '+' or '/' or '=')) break;

            base64.Append(c);
        }

        if (base64.Length < 128) return null;

        var bytes = Convert.FromBase64String(base64.ToString());
        return LooksLikeImage(bytes) ? bytes : null;
    }

    private static readonly System.Text.RegularExpressions.Regex SrcPattern = new(
        "src\\s*=\\s*[\"']([^\"']+)[\"']",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>挑出 HTML 里所有本地图片路径（file:/// 或盘符路径）。网络地址一律不碰。</summary>
    private static IEnumerable<string> FindImagePaths(string html)
    {
        foreach (System.Text.RegularExpressions.Match match in SrcPattern.Matches(html))
        {
            var raw = match.Groups[1].Value;

            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            if (raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;

            if (raw.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            {
                yield return Uri.UnescapeDataString(raw["file:///".Length..]).Replace('/', '\\');
                continue;
            }

            if (raw.Length > 3 && char.IsLetter(raw[0]) && raw[1] == ':' && raw[2] is '\\' or '/')
            {
                yield return raw.Replace('/', '\\');
            }
        }
    }

    private static byte[]? ReadLocalImage(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (!IsImageExtension(Path.GetExtension(path))) return null;

            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > 16 * 1024 * 1024) return null;

            var bytes = File.ReadAllBytes(path);
            return LooksLikeImage(bytes) ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsImageExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" => true,
        _ => false,
    };

    internal static bool LooksLikeImage(byte[] bytes)
    {
        if (bytes.Length < 8) return false;

        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return true;   // PNG
        if (bytes[0] == 0xFF && bytes[1] == 0xD8) return true;                                            // JPEG
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return true;                        // GIF
        if (bytes[0] == 0x42 && bytes[1] == 0x4D) return true;                                            // BMP

        return false;
    }

    /// <summary>
    /// 把剪贴板上**所有**能读到的格式都原样存下来（纯文本和图片已单独处理，这里管其余全部），
    /// 粘贴时一并放回 —— 这样粘回原程序（比如 QQ）时，它认识的那些私有格式也还在。
    /// </summary>
    private static List<ClipFormat>? ReadAllFormats(IDataObject data)
    {
        string[] names;

        try
        {
            names = data.GetFormats();
        }
        catch
        {
            return null;
        }

        List<ClipFormat>? list = null;
        long total = 0;
        long deadline = Environment.TickCount64 + FormatsTimeBudgetMs;

        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (IsHandledElsewhere(name) || IsUnsafeFormat(name)) continue;
            if (!IsTextLikeFormat(name)) continue;
            if (Environment.TickCount64 > deadline) break;

            try
            {
                // 走 OLE（WPF 的 IDataObject）读，**不用**原生 OpenClipboard：
                // 原生读取期间整个剪贴板被我们独占，别人粘贴就得排队等我们。
                // Edge 复制网页图文后"粘半天才出来"，就是这么被我们拖住的。
                var bytes = ReadFormatBytes(data, name);
                if (bytes is null || bytes.Length == 0 || bytes.Length > MaxFormatBytes) continue;

                total += bytes.Length;
                if (total > MaxFormatsTotalBytes) break;

                list ??= new List<ClipFormat>();
                list.Add(new ClipFormat { Name = name, Bytes = bytes });
            }
            catch
            {
                // 单个格式读不出来不影响其它格式
            }
        }

        return list;
    }

    /// <summary>
    /// 只认"文字类"格式名（含各家私有富文本格式，比如 QQ_Unicode_RichEdit_Format）。
    /// 它们是立即数据，读起来快；剪贴板上真正拖慢系统的通常是那些巨大的二进制格式
    /// （延迟渲染，读一次要源程序现算），一律不碰。
    /// </summary>
    private static bool IsTextLikeFormat(string name)
    {
        if (name.Contains("Text", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("Rich", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("HTML", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("RTF", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static byte[]? ReadFormatBytes(IDataObject data, string name)
    {
        var value = data.GetData(name);

        return value switch
        {
            string text => System.Text.Encoding.UTF8.GetBytes(text),
            byte[] bytes => bytes,
            Stream stream => ReadAll(stream),
            _ => null,
        };
    }

    /// <summary>读"其他格式"总共最多花这么多毫秒，超了就只保留已经读到的。</summary>
    private const int FormatsTimeBudgetMs = 500;

    /// <summary>这些格式已经单独处理过（文本进 Text 字段、图片进 blob），不用重复存一遍。</summary>
    private static bool IsHandledElsewhere(string name)
        => name.Equals(DataFormats.UnicodeText, StringComparison.OrdinalIgnoreCase)
        || name.Equals(DataFormats.Text, StringComparison.OrdinalIgnoreCase)
        || name.Equals("System.String", StringComparison.OrdinalIgnoreCase)
        || name.Equals("OEMText", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Locale", StringComparison.OrdinalIgnoreCase)
        || name.Equals("PNG", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Bitmap", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Dib", StringComparison.OrdinalIgnoreCase)
        || name.Contains("MetaFile", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 这些格式不能碰：它们是 OLE 嵌入对象，读一次会把原程序的服务器拉起来，又慢又可能弹窗。
    /// 另外 Chromium 的那两个只是内部标识，存了也没用。
    /// </summary>
    private static readonly string[] UnsafeFormats =
    {
        "Embed Source",
        "Embedded Object",
        "Object Descriptor",
        "Link Source",
        "Link Source Descriptor",
        "ObjectLink",
        "Ole Private Data",
        "Chromium internal source RFH token",
        "Chromium internal source URL",
    };

    private static bool IsUnsafeFormat(string name)
    {
        foreach (var blocked in UnsafeFormats)
        {
            if (name.Equals(blocked, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>一条记录最多存多少"其他格式"的字节。</summary>
    private const int MaxFormatsTotalBytes = 4 * 1024 * 1024;

    /// <summary>单种格式最多存这么多字节。</summary>
    private const int MaxFormatBytes = 2 * 1024 * 1024;

    private static byte[]? ReadGlobalFormat(string name)
    {
        uint format = RegisterClipboardFormat(name);
        if (format == 0) return null;

        if (!OpenClipboard(IntPtr.Zero)) return null;

        try
        {
            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero) return null;

            var size = (long)GlobalSize(handle).ToUInt64();
            if (size <= 0 || size > MaxFormatBytes) return null;

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) return null;

            try
            {
                var buffer = new byte[size];
                Marshal.Copy(pointer, buffer, 0, buffer.Length);
                return buffer;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>读图片：按"最原样"的顺序挑一种格式。</summary>
    private static (byte[] Bytes, string Format)? ReadImage(IDataObject data)
    {
        // 1) PNG 原始字节：浏览器、截图工具给的就是完整 PNG，原样存取就是无损且逐字节一致
        var png = ReadFormat(data, PngFormatName);
        if (png is { Length: > 0 }) return (png, "PNG");

        // 2) CF_DIB / CF_DIBV5：DIB 裸字节（BITMAPINFOHEADER + 像素），同样原样存
        var dib = ReadFormat(data, DataFormats.Dib);
        if (dib is { Length: > 0 }) return (dib, "DIB");

        string dibV5Name = DataFormats.GetDataFormat((int)CF_DIBV5).Name;
        var dibV5 = ReadFormat(data, dibV5Name);
        if (dibV5 is { Length: > 0 }) return (dibV5, "DIBV5");

        // 3) 只剩 CF_BITMAP（GDI 位图）时，把像素无损展开成 32 位 DIB
        if (data.GetDataPresent(DataFormats.Bitmap))
        {
            var bitmapDib = ReadBitmapAsDib(data);
            if (bitmapDib is { Length: > 0 }) return (bitmapDib, "DIB");
        }

        // 4) CF_ENHMETAFILE：Word 这类程序复制图片时**只给矢量图元文件**，
        //    一个位图格式都不给。以前这里没有分支，图文混排的内容就只剩文字了。
        if (data.GetDataPresent(DataFormats.EnhancedMetafile))
        {
            var emf = ReadEnhMetaFile();
            if (emf is { Length: > 0 }) return (emf, "EMF");
        }

        return null;
    }

    /// <summary>
    /// 读 CF_ENHMETAFILE：拿到的就是 EMF 原始字节（GetEnhMetaFileBits），原样存、原样放回去。
    /// 这个格式没法通过 WPF 的 IDataObject 取字节，只能走原生剪贴板。
    /// </summary>
    private static byte[]? ReadEnhMetaFile()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;

        try
        {
            var handle = GetClipboardData(CF_ENHMETAFILE);
            if (handle == IntPtr.Zero) return null;

            uint size = GetEnhMetaFileBits(handle, 0, null);
            if (size == 0 || size > 64u * 1024 * 1024) return null;

            var buffer = new byte[size];
            return GetEnhMetaFileBits(handle, size, buffer) == 0 ? null : buffer;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static string? ReadText(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.UnicodeText)) return data.GetData(DataFormats.UnicodeText) as string;
            if (data.GetDataPresent(DataFormats.Text)) return data.GetData(DataFormats.Text) as string;
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    /// <summary>列出当前剪贴板上都有哪些格式（诊断用）。</summary>
    public static string DescribeFormats()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            return data is null ? "(null)" : string.Join(",", data.GetFormats());
        }
        catch (Exception ex)
        {
            return "err:" + ex.GetType().Name;
        }
    }

    private static string[]? ReadFiles()
    {
        try
        {
            if (!Clipboard.ContainsFileDropList()) return null;

            var list = Clipboard.GetFileDropList();
            if (list is null || list.Count == 0) return null;

            var paths = new List<string>(list.Count);
            foreach (string? path in list)
            {
                if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
            }

            return paths.Count == 0 ? null : paths.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ReadFormat(IDataObject data, string format)
    {
        try
        {
            if (string.IsNullOrEmpty(format) || !data.GetDataPresent(format)) return null;

            object? value = data.GetData(format);

            if (value is Stream stream)
            {
                using (stream)
                {
                    return ReadAll(stream);
                }
            }

            return value as byte[];
        }
        catch
        {
            return null;
        }
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// 只有 CF_BITMAP 时（GDI 位图句柄），把像素无损展开成 DIB 字节。
    ///
    /// 不走原生 OpenClipboard/GetDIBits：WPF 拿到的 IDataObject 自己也占着剪贴板，
    /// 再开一次必然失败。直接从 WPF 的位图对象里取像素更可靠。
    /// CF_BITMAP 本身只是个句柄，没有"原始字节"可言，展开成 DIB 是它能长期保存的唯一无损形式。
    /// </summary>
    private static byte[]? ReadBitmapAsDib(IDataObject data)
    {
        try
        {
            return data.GetData(DataFormats.Bitmap) is BitmapSource source ? BitmapToDib(source) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把 WPF 位图展开成 32 位无压缩 DIB（top-down）。
    /// 逐像素搬运：颜色值原样保留，只是统一成 32 位并自带尺寸信息。
    /// </summary>
    public static byte[]? BitmapToDib(BitmapSource source)
    {
        try
        {
            var converted = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            if (width <= 0 || height <= 0) return null;

            int stride = width * 4;
            int imageSize = stride * height;
            if (imageSize <= 0 || imageSize > 512 * 1024 * 1024) return null;

            var header = new BITMAPINFOHEADER
            {
                biSize = 40,
                biWidth = width,
                biHeight = -height,   // 负数 = top-down，正好与 CopyPixels 的行序一致，省一次翻转
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
                biSizeImage = (uint)imageSize,
            };

            var pixels = new byte[imageSize];
            converted.CopyPixels(pixels, stride, 0);

            var dib = new byte[40 + imageSize];
            WriteHeader(dib, header);
            pixels.CopyTo(dib, 40);

            return dib;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteHeader(byte[] target, BITMAPINFOHEADER header)
    {
        BitConverter.TryWriteBytes(target.AsSpan(0), header.biSize);
        BitConverter.TryWriteBytes(target.AsSpan(4), header.biWidth);
        BitConverter.TryWriteBytes(target.AsSpan(8), header.biHeight);
        BitConverter.TryWriteBytes(target.AsSpan(12), header.biPlanes);
        BitConverter.TryWriteBytes(target.AsSpan(14), header.biBitCount);
        BitConverter.TryWriteBytes(target.AsSpan(16), header.biCompression);
        BitConverter.TryWriteBytes(target.AsSpan(20), header.biSizeImage);
        BitConverter.TryWriteBytes(target.AsSpan(24), header.biXPelsPerMeter);
        BitConverter.TryWriteBytes(target.AsSpan(28), header.biYPelsPerMeter);
        BitConverter.TryWriteBytes(target.AsSpan(32), header.biClrUsed);
        BitConverter.TryWriteBytes(target.AsSpan(36), header.biClrImportant);
    }

    // ---------- payload 构造（含内容指纹） ----------

    private static ClipboardPayload BuildText(string text, List<ClipFormat>? formats, byte[]? previewImage)
    {
        if (text.Length > MaxTextChars) text = text[..MaxTextChars];

        return new ClipboardPayload
        {
            Kind = ClipboardPayloadKind.Text,
            Text = text,
            Formats = formats,
            PreviewImageBytes = previewImage,

            // 指纹只算"看得见的内容"：文字 + 预览图。
            // 把附加格式也算进去会出事：各程序读写剪贴板时会加上或去掉自己的私有格式
            // （Chromium 那两个就是），同一份内容因此每次指纹都不同，列表里就会一条变三条。
            Hash = HashOf($"T:{text}:{HashOfBytes(previewImage ?? Array.Empty<byte>())}"),
        };
    }

    private static ClipboardPayload BuildImage(byte[] bytes, string format, string? text, List<ClipFormat>? formats)
    {
        if (text is { Length: > MaxTextChars }) text = text[..MaxTextChars];

        return new ClipboardPayload
        {
            Kind = ClipboardPayloadKind.Image,
            ImageBytes = bytes,
            ImageFormat = format,
            Text = string.IsNullOrWhiteSpace(text) ? null : text,
            Formats = formats,
            Hash = HashOf($"I:{format}:{HashOfBytes(bytes)}"),
        };
    }

    private static ClipboardPayload BuildFiles(string[] files)
    {
        return new ClipboardPayload
        {
            Kind = ClipboardPayloadKind.Files,
            Files = files,
            Hash = HashOf("F:" + string.Join('\n', files)),
        };
    }

    private static string HashOf(string value)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private static string HashOfBytes(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    // ---------- Win32 结构 ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }
}

/// <summary>把内容写回系统剪贴板。</summary>
internal static class ClipboardWriter
{
    private const uint CF_DIB = 8;
    private const uint CF_DIBV5 = 17;
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>
    /// 把一条记录里保存的所有格式**一次性**放回剪贴板 —— 和当初复制时一样，多个格式并存。
    /// 下游程序要哪个格式就取哪个：粘到 Word 保留图文排版，粘到记事本只是文字。
    /// </summary>
    public static bool WriteAll(string? text, string? imageFormat, byte[]? imageRaw, IReadOnlyList<ClipFormat>? formats)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;

        try
        {
            EmptyClipboard();
            bool any = false;

            // 富文本格式先放：它们承载排版信息，程序一般优先看这些
            if (formats is not null)
            {
                foreach (var format in formats)
                {
                    if (string.IsNullOrEmpty(format.Name)) continue;

                    var target = RegisterClipboardFormat(format.Name);
                    if (target == 0) continue;

                    var payload = format.Bytes
                        ?? (format.Text is null ? null : System.Text.Encoding.Unicode.GetBytes(format.Text + "\0"));

                    if (payload is null || payload.Length == 0) continue;

                    any |= SetBlock(target, payload);
                }
            }

            if (imageRaw is { Length: > 0 } && !string.IsNullOrWhiteSpace(imageFormat))
            {
                any |= SetImageBlock(imageFormat, imageRaw);
            }

            if (!string.IsNullOrEmpty(text))
            {
                any |= SetBlock(CF_UNICODETEXT, System.Text.Encoding.Unicode.GetBytes(text + "\0"));
            }

            return any;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// 把图片原始字节写回剪贴板，按记录里存的原格式。
    ///
    /// 走原生 SetClipboardData，不用 WPF 的 DataObject：一来字节完全原样（中间层不会
    /// 再编码或转换一次），二来数据由系统持有，不会变成"延迟渲染"—— 那种情况下别的程序
    /// 来读一次就会再触发一次剪贴板变化，我们会被自己写回的内容反复采集成新记录。
    /// </summary>
    public static bool WriteImage(byte[] raw, string format)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;

        try
        {
            EmptyClipboard();
            return SetImageBlock(format, raw);
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static bool WriteText(string text)
        => WriteBlock(CF_UNICODETEXT, System.Text.Encoding.Unicode.GetBytes(text + "\0"));

    /// <summary>
    /// 图片和文字一起写回（同一个剪贴板会话里放两个格式）。
    /// 图文混排的内容本来就是这么并存的，粘到 Word 里是图 + 字，粘到记事本里是字。
    /// </summary>
    public static bool WriteImageAndText(byte[] raw, string format, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return WriteImage(raw, format);

        if (!OpenClipboard(IntPtr.Zero)) return false;

        try
        {
            EmptyClipboard();

            if (!SetImageBlock(format, raw)) return false;
            return SetBlock(CF_UNICODETEXT, System.Text.Encoding.Unicode.GetBytes(text + "\0"));
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool WriteBlock(uint format, byte[] data)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;

        try
        {
            EmptyClipboard();
            return SetBlock(format, data);
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>放图片：EMF 是句柄型格式，得单独处理。调用方必须已打开剪贴板并 EmptyClipboard。</summary>
    private static bool SetImageBlock(string format, byte[] raw)
    {
        if (string.Equals(format, "EMF", StringComparison.OrdinalIgnoreCase)) return SetEnhMetaFileBlock(raw);

        // 注册格式 "PNG" 只有浏览器、Office、画图这类程序认；
        // QQ/微信这类只认 CF_DIB 的程序拿到的就是"什么都没有"，表现成粘贴没反应。
        // 所以同一张图再解一份 32 位 DIB 放进去，两个格式并存，谁认哪个都能用。
        if (string.Equals(format, "PNG", StringComparison.OrdinalIgnoreCase))
        {
            bool any = SetBlock(RegisterClipboardFormat("PNG"), raw);

            var dib = PngToDib(raw);
            if (dib is not null) any |= SetBlock(CF_DIB, dib);

            return any;
        }

        // DIBV5 的头是 124 字节的 BITMAPV5HEADER。放进 CF_DIB 位置时，
        // 只认 40 字节 BITMAPINFOHEADER 的程序会按错误的头长去读像素。
        // 原样的 DIBV5 和规范化后的 DIB 都放一份。
        if (string.Equals(format, "DIBV5", StringComparison.OrdinalIgnoreCase))
        {
            bool any = SetBlock(CF_DIBV5, raw);

            var dib = NormalizeDib(raw);
            if (dib is not null) any |= SetBlock(CF_DIB, dib);

            return any;
        }

        return SetBlock(CF_DIB, raw);
    }

    /// <summary>PNG 字节解成 32 位无压缩 DIB（CF_DIB 的格式）。解不出来就返回 null。</summary>
    private static byte[]? PngToDib(byte[] raw)
    {
        try
        {
            using var stream = new MemoryStream(raw, writable: false);

            var frame = BitmapFrame.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            return ClipboardInterop.BitmapToDib(frame);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把 124 字节 BITMAPV5HEADER 的 DIB 换回 40 字节 BITMAPINFOHEADER，像素数据原样接在后面。
    /// 已经是 40 字节头的原样返回；非 32 位的（掩码一丢就没法解释）返回 null。
    /// </summary>
    private static byte[]? NormalizeDib(byte[] raw)
    {
        if (raw.Length < 40) return null;

        int headerSize = BitConverter.ToInt32(raw, 0);
        if (headerSize == 40) return raw;
        if (headerSize < 40 || headerSize > raw.Length) return null;

        if (BitConverter.ToUInt16(raw, 14) != 32) return null;

        var pixels = raw.AsSpan(headerSize);
        if (pixels.Length == 0) return null;

        var dib = new byte[40 + pixels.Length];
        raw.AsSpan(0, 40).CopyTo(dib);

        BitConverter.TryWriteBytes(dib.AsSpan(0), 40u);    // biSize
        BitConverter.TryWriteBytes(dib.AsSpan(16), 0u);    // biCompression = BI_RGB

        pixels.CopyTo(dib.AsSpan(40));

        return dib;
    }

    private static bool SetEnhMetaFileBlock(byte[] raw)
    {
        var handle = SetEnhMetaFileBits((uint)raw.Length, raw);
        if (handle == IntPtr.Zero) return false;

        if (SetClipboardData(CF_ENHMETAFILE, handle) == IntPtr.Zero)
        {
            DeleteEnhMetaFile(handle);
            return false;
        }

        return true;   // 成功后句柄归系统所有，不能再删
    }

    /// <summary>往已经打开的剪贴板里放一块数据。调用方负责 Open/Close 与 EmptyClipboard。</summary>
    private static bool SetBlock(uint format, byte[] data)
    {
        if (format == 0 || data.Length == 0) return false;

        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (handle == IntPtr.Zero) return false;

        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            return false;
        }

        Marshal.Copy(data, 0, pointer, data.Length);
        GlobalUnlock(handle);

        // 成功后这块内存归系统所有，这里绝不能再 GlobalFree
        if (SetClipboardData(format, handle) == IntPtr.Zero)
        {
            GlobalFree(handle);
            return false;
        }

        return true;
    }

    private const uint CF_ENHMETAFILE = 14;

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SetEnhMetaFileBits(uint cbBuffer, byte[] lpbBuffer);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteEnhMetaFile(IntPtr hemf);
}

/// <summary>DIB（BITMAPINFOHEADER + 像素）的头解析与 BMP 包装。</summary>
internal static class DibTools
{
    /// <summary>从 DIB 头里读出基本信息。返回 false 表示这串字节不是像样的 DIB。</summary>
    public static bool TryReadHeader(
        byte[] dib,
        out int width,
        out int height,
        out int bitCount,
        out uint compression,
        out uint headerSize)
    {
        width = height = bitCount = 0;
        compression = 0;
        headerSize = 0;

        if (dib.Length < 40) return false;

        headerSize = BitConverter.ToUInt32(dib, 0);
        if (headerSize < 12 || headerSize > dib.Length) return false;

        width = BitConverter.ToInt32(dib, 4);
        height = BitConverter.ToInt32(dib, 8);
        bitCount = BitConverter.ToUInt16(dib, 14);
        compression = BitConverter.ToUInt32(dib, 16);

        return width != 0 && height != 0;
    }

    /// <summary>读 PNG 的原始宽高（只看 IHDR 块，不解码整张图）。</summary>
    public static bool TryReadPngSize(byte[] png, out int width, out int height)
    {
        width = height = 0;

        if (png.Length < 24) return false;
        if (png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47) return false;
        if (png[12] != (byte)'I' || png[13] != (byte)'H' || png[14] != (byte)'D' || png[15] != (byte)'R') return false;

        width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];

        return width > 0 && height > 0;
    }

    public static string? ReadPngSizeText(byte[] png)
        => TryReadPngSize(png, out int width, out int height) ? $"{width}×{height}" : null;

    /// <summary>计算像素数据在 DIB 中的起始偏移（跳过调色板 / 位域掩码）。</summary>
    public static int PixelOffset(byte[] dib)
    {
        if (!TryReadHeader(dib, out _, out _, out int bitCount, out uint compression, out uint headerSize)) return -1;

        uint colorsUsed = 0;
        if (headerSize >= 40 && dib.Length >= 36) colorsUsed = BitConverter.ToUInt32(dib, 32);

        uint paletteEntries = colorsUsed != 0
            ? colorsUsed
            : bitCount <= 8 ? 1u << bitCount : 0u;

        // BITMAPINFOHEADER + BI_BITFIELDS：调色板位置放的是 3 个 DWORD 掩码
        uint masks = compression == 3 && headerSize == 40 ? 12u : 0u;

        long offset = headerSize + paletteEntries * 4L + masks;
        return offset > 0 && offset < dib.Length ? (int)offset : -1;
    }

    /// <summary>读 EMF 的边界尺寸（ENHMETAHEADER 里的 rclBounds，单位像素）。</summary>
    public static bool TryReadEmfSize(byte[] emf, out int width, out int height)
    {
        width = height = 0;
        if (emf.Length < 40) return false;

        // iType(0) nSize(4) rclBounds.left(8) top(12) right(16) bottom(20)
        width = BitConverter.ToInt32(emf, 16) - BitConverter.ToInt32(emf, 8);
        height = BitConverter.ToInt32(emf, 20) - BitConverter.ToInt32(emf, 12);

        return width > 0 && height > 0;
    }

    /// <summary>
    /// 把 EMF 渲染成缩略图。
    /// EMF 是矢量图元文件，WPF 的解码器不认，只能借 GDI+ 画一张位图出来显示；
    /// 存储始终是原始 EMF 字节，这里只影响预览。
    /// </summary>
    public static System.Windows.Media.Imaging.BitmapSource? RenderEmfThumbnail(byte[] emf, int maxWidth)
    {
        try
        {
            using var stream = new MemoryStream(emf);
            using var metafile = new System.Drawing.Imaging.Metafile(stream);

            double sourceWidth = metafile.Width;
            double sourceHeight = metafile.Height;

            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                if (!TryReadEmfSize(emf, out int rawWidth, out int rawHeight)) return null;
                sourceWidth = rawWidth;
                sourceHeight = rawHeight;
            }

            double scale = Math.Min(1.0, maxWidth / sourceWidth);
            int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));

            using var bitmap = new System.Drawing.Bitmap(width, height);

            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.Transparent);
                graphics.DrawImage(metafile, new System.Drawing.Rectangle(0, 0, width, height));
            }

            var hBitmap = bitmap.GetHbitmap(System.Drawing.Color.Transparent);

            try
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// 给裸 DIB 补一个 14 字节 BMP 文件头，方便用系统解码器直接看（只用于预览/导出，不改原字节）。
    /// </summary>
    public static byte[]? WrapAsBmp(byte[] dib)
    {
        int pixelOffset = PixelOffset(dib);
        if (pixelOffset <= 0) return null;

        int fileSize = 14 + dib.Length;
        var bmp = new byte[fileSize];

        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.TryWriteBytes(bmp.AsSpan(2), fileSize);
        BitConverter.TryWriteBytes(bmp.AsSpan(10), 14 + pixelOffset);

        dib.CopyTo(bmp, 14);
        return bmp;
    }
}
