using System.IO;
using System.Threading;
using System.Windows.Threading;
using ExplorerDock.Interop;

namespace ExplorerDock.Services;

/// <summary>
/// 剪贴板采集：监听 WM_CLIPBOARDUPDATE，攒一下之后把内容读成一条记录交给库。
///
/// 两个刻意的设计：
/// 1) 收到通知先等 250ms 再读 —— Word / QQ 这类程序是分几步往剪贴板上放数据的，
///    第一次通知就抢着读只能读到半成品（比如"只有文字没有图"）。
/// 2) 真正的读取放在**后台 STA 线程**上 —— 某些格式（网页的 HTML 就是）是延迟渲染的，
///    只有别人来读时才现算，慢起来能到几秒甚至更久；放在 UI 线程上会让整个界面卡住。
///
/// 自己写剪贴板之前先调用 <see cref="Suppress"/>，否则自己写进去的内容会被当成新记录再收一次。
/// </summary>
internal sealed class ClipboardMonitor : IDisposable
{
    private readonly ClipboardStore _store;
    private readonly ClipboardMessageWindow _window;
    private readonly DispatcherTimer _debounce;
    private readonly Dispatcher _dispatcher;
    private readonly Thread _captureThread;
    private readonly AutoResetEvent _captureSignal = new(false);

    private DateTime _suppressUntilUtc = DateTime.MinValue;
    private DateTime _ignoreUntilUtc = DateTime.MinValue;
    private volatile bool _running = true;
    private bool _disposed;

    public ClipboardMonitor(ClipboardStore store)
    {
        _store = store;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _window = new ClipboardMessageWindow();
        _window.Updated += OnClipboardUpdated;

        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            RequestCapture();
        };

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "ExplorerDock.ClipboardCapture",
        };

        _captureThread.SetApartmentState(ApartmentState.STA);
        _captureThread.Start();
    }

    /// <summary>接下来这段时间内发生的剪贴板变化不算数（自己写剪贴板时用）。</summary>
    public void Suppress(TimeSpan duration) => _suppressUntilUtc = DateTime.UtcNow + duration;

    private void OnClipboardUpdated()
    {
        if (_disposed || !_store.IsAvailable) return;

        if (DateTime.UtcNow < _suppressUntilUtc)
        {
            Log("skipped (self-write)");
            return;
        }

        // 每次通知都重置计时：等这一批数据放完再说
        _debounce.Stop();
        _debounce.Start();
    }

    private void RequestCapture()
    {
        if (_disposed || !_store.IsAvailable) return;

        if (DateTime.UtcNow < _ignoreUntilUtc)
        {
            // 刚才那次读取带出来的连带通知，跳过
            Log("skipped (chained notify)");
            return;
        }

        _captureSignal.Set();
    }

    /// <summary>后台采集线程：读剪贴板，读完把结果丢回 UI 线程入库。</summary>
    private void CaptureLoop()
    {
        while (true)
        {
            if (!_captureSignal.WaitOne(250))
            {
                if (!_running) return;
                continue;
            }

            if (!_running) return;

            string formats = string.Empty;
            ClipboardPayload? payload = null;

            try
            {
                formats = ClipboardInterop.DescribeFormats();
                payload = ClipboardInterop.Read();
            }
            catch
            {
                // 读失败就当这次没内容
            }

            try
            {
                _dispatcher.BeginInvoke(new Action(() => Commit(payload, formats)));
            }
            catch
            {
                return;   // 程序在退出
            }
        }
    }

    private void Commit(ClipboardPayload? payload, string formats)
    {
        if (_disposed || !_store.IsAvailable) return;

        // 读一次剪贴板本身就可能让"延迟渲染"的数据真正生成，从而再发一次变化通知。
        // 开个小窗口把这些连带通知挡掉，免得同一份内容被反复入库。
        _ignoreUntilUtc = DateTime.UtcNow.AddMilliseconds(600);

        if (payload is null)
        {
            // 认不出来的格式（OLE 嵌入对象之类）：记一笔，方便以后按需支持
            Log($"unusable formats=[{formats}]");
            return;
        }

        var item = _store.Add(payload);
        if (item is null) return;

        Log($"captured kind={payload.Kind} format={payload.ImageFormat ?? "-"} text={payload.Text?.Length ?? 0} "
            + $"bytes={payload.ImageBytes?.Length ?? 0} preview={payload.PreviewImageBytes?.Length ?? 0} "
            + $"extras={payload.Formats?.Count ?? 0} item={item.Id} formats=[{formats}]");

        // 网页内容的图挂在远程地址上，粘贴时目标程序会挨个去下载（QQ 能卡几分钟）。
        // 入库之后在后台把它们下下来嵌进 HTML，写回时就用内联版 ——
        // 放在后台做，不挡用户马上打开面板看这条记录。
        if (item.Formats is { Count: > 0 })
        {
            var target = item;
            _ = Task.Run(() => InlineHtmlImages(target));
        }
    }

    /// <summary>后台把 HTML 里的远程图片内联进来；失败就保持原样（粘贴照旧，只是慢一点）。</summary>
    private void InlineHtmlImages(ClipItem item)
    {
        try
        {
            var formats = item.Formats;
            if (formats is null) return;

            for (int i = 0; i < formats.Count; i++)
            {
                var format = formats[i];

                if (!string.Equals(format.Name, "HTML Format", StringComparison.OrdinalIgnoreCase)) continue;
                if (format.Bytes is not { Length: > 0 }) continue;

                var inlined = HtmlImageInliner.Inline(format.Bytes);
                if (inlined is null) return;

                _store.ReplaceFormat(item, i, new ClipFormat { Name = format.Name, Bytes = inlined });

                Log($"html images inlined item={item.Id} before={format.Bytes.Length} after={inlined.Length}");
                return;   // 一条记录只处理这一个 HTML 格式
            }
        }
        catch
        {
            // 内联失败不影响任何已有内容
        }
    }

    /// <summary>诊断日志：%TEMP%\ExplorerDock.clipboard.log，超过 200KB 就不再写。</summary>
    internal static void Log(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "ExplorerDock.clipboard.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 200_000) return;

            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败绝不影响功能
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;

        try
        {
            _debounce.Stop();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _captureSignal.Set();
            _captureThread.Join(500);
            _captureSignal.Dispose();
        }
        catch
        {
            // 忽略
        }

        _window.Updated -= OnClipboardUpdated;
        _window.Dispose();
    }
}
