using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ExplorerDock.Views;

/// <summary>
/// 隐藏的 HTML 渲染器：把剪贴板里的网页内容塞进一个看不见的 WebView2，渲染完截一张图当预览。
///
/// 为什么需要它：网页复制过来的内容，图片可能是 CSS 背景、远程地址、或者压根不在 &lt;img&gt; 里，
/// 光靠解析 HTML 抠不出来。能"把它渲染出来"的只有浏览器内核 —— 而系统自带一个
/// （Edge WebView2 运行时），所以不用把内核打进安装包，只引用一层托管包装。
///
/// 只在别的办法都拿不到图时才动用，而且一次只渲一张、有超时、结果会存进库里。
/// </summary>
internal sealed class HtmlPreviewRenderer : IDisposable
{
    private const int RenderWidth = 900;
    private const int RenderHeight = 640;
    private const int RenderTimeoutMs = 4000;
    private const int MaxHtmlChars = 2 * 1024 * 1024;

    private static HtmlPreviewRenderer? _instance;
    private static bool _probed;

    private readonly Queue<RenderJob> _queue = new();

    private Window? _host;
    private WebView2? _webView;
    private bool _ready;
    private bool _failed;
    private bool _busy;

    /// <summary>取实例；系统上根本没有 WebView2 运行时（老机器）时返回 null，调用方自动降级。</summary>
    public static HtmlPreviewRenderer? Instance
    {
        get
        {
            if (_instance is not null) return _instance;
            if (_probed) return null;

            _probed = true;

            if (!RuntimeAvailable()) return null;

            _instance = new HtmlPreviewRenderer();
            return _instance;
        }
    }

    /// <summary>系统里有没有 WebView2 运行时。</summary>
    private static bool RuntimeAvailable()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>排一次渲染；回调拿到 PNG 字节，失败给 null。</summary>
    public void Request(string key, string html, Action<byte[]?> onDone)
    {
        if (_failed || string.IsNullOrWhiteSpace(html))
        {
            onDone(null);
            return;
        }

        if (html.Length > MaxHtmlChars) html = html[..MaxHtmlChars];

        _queue.Enqueue(new RenderJob(key, html, onDone));
        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        if (_busy) return;
        _busy = true;

        try
        {
            while (_queue.Count > 0)
            {
                var job = _queue.Dequeue();
                await RenderAsync(job);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RenderAsync(RenderJob job)
    {
        try
        {
            EnsureHost();

            if (_webView is null || !await EnsureReadyAsync())
            {
                job.Done(null);
                return;
            }

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) => loaded.TrySetResult(true);

            _webView.NavigationCompleted += OnNavigationCompleted;

            try
            {
                _webView.NavigateToString(job.Html);

                await Task.WhenAny(loaded.Task, Task.Delay(RenderTimeoutMs));

                // 远程图片可能还没下载完，等一下（最多再 1.5 秒）
                await WaitForImagesAsync();

                using var stream = new MemoryStream();
                await _webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);

                var png = stream.Length > 0 ? stream.ToArray() : null;
                Services.ClipboardMonitor.Log($"html preview rendered key={job.Key} bytes={png?.Length ?? 0}");

                job.Done(png);
            }
            finally
            {
                _webView.NavigationCompleted -= OnNavigationCompleted;
            }
        }
        catch
        {
            Services.ClipboardMonitor.Log($"html preview failed key={job.Key}");
            job.Done(null);
        }
    }

    private async Task<bool> EnsureReadyAsync()
    {
        if (_ready) return true;
        if (_webView is null) return false;

        try
        {
            await _webView.EnsureCoreWebView2Async();

            var settings = _webView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;

            _ready = true;
            return true;
        }
        catch
        {
            // 初始化失败（运行时被人为移除、策略禁用……）：以后不再尝试，直接用降级路径
            _failed = true;
            DisposeHost();
            return false;
        }
    }

    private async Task WaitForImagesAsync()
    {
        try
        {
            const string script =
                "Promise.all(Array.from(document.images).map(function (i) {" +
                "  return i.complete ? 1 : new Promise(function (r) { i.onload = r; i.onerror = r; });" +
                "})).then(function () { return 1; })";

            await Task.WhenAny(_webView!.CoreWebView2.ExecuteScriptAsync(script), Task.Delay(1500));
        }
        catch
        {
            // 等不到就直接截，能截多少算多少
        }
    }

    /// <summary>
    /// 建那个看不见的宿主窗口。
    /// 必须 Show（WebView2 在隐藏窗口上不渲染），所以放到屏幕外；
    /// ShowActivated=false 保证它不会抢走用户的焦点。
    /// </summary>
    private void EnsureHost()
    {
        if (_host is not null) return;

        _webView = new WebView2
        {
            Width = RenderWidth,
            Height = RenderHeight,
        };

        _host = new Window
        {
            Width = RenderWidth,
            Height = RenderHeight,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            Title = "ExplorerDock.HtmlPreview",
            Content = _webView,
        };

        // 不进 Alt+Tab、不抢焦点：它只是个离屏渲染器
        _host.SourceInitialized += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(_host).Handle;
            if (handle == IntPtr.Zero) return;

            long style = Interop.NativeMethods.GetWindowLongPtr(handle, Interop.NativeMethods.GWL_EXSTYLE);
            Interop.NativeMethods.SetWindowLongPtr(
                handle,
                Interop.NativeMethods.GWL_EXSTYLE,
                style | Interop.NativeMethods.WS_EX_TOOLWINDOW | Interop.NativeMethods.WS_EX_NOACTIVATE);
        };

        _host.Show();
    }

    private void DisposeHost()
    {
        try
        {
            _host?.Close();
        }
        catch
        {
            // 忽略
        }

        _host = null;
        _webView = null;
        _ready = false;
    }

    public void Dispose()
    {
        try
        {
            DisposeHost();
        }
        catch
        {
            // 忽略
        }
    }

    private sealed record RenderJob(string Key, string Html, Action<byte[]?> Done);

    /// <summary>
    /// CF_HTML 前面带一段头（Version/StartHTML/…），直接丢给渲染器会把这段头也画出来，
    /// 所以先抠出 &lt;!--StartFragment--&gt; 之间的正文。
    /// </summary>
    public static string ExtractFragment(byte[] html)
    {
        try
        {
            var text = Encoding.UTF8.GetString(html);

            const string startTag = "<!--StartFragment-->";
            const string endTag = "<!--EndFragment-->";

            int start = text.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            int end = text.IndexOf(endTag, StringComparison.OrdinalIgnoreCase);

            if (start >= 0 && end > start) return text[(start + startTag.Length)..end];

            return text;
        }
        catch
        {
            return string.Empty;
        }
    }
}
