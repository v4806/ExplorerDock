namespace ExplorerDock.Services;

/// <summary>
/// 在后台等待后发送粘贴，避免 WPF DispatcherTimer 被界面渲染/布局队列拖慢。
/// </summary>
internal static class ClipboardPasteScheduler
{
    public static Task SendAsync(TimeSpan delay, Action sendPaste, Action<long>? sent = null)
    {
        return Task.Run(async () =>
        {
            long started = Environment.TickCount64;
            await Task.Delay(delay).ConfigureAwait(false);

            try
            {
                sendPaste();
                sent?.Invoke(Environment.TickCount64 - started);
            }
            catch (Exception ex)
            {
                ClipboardMonitor.Log($"scheduled paste failed: {ex.GetType().Name} {ex.Message}");
            }
        });
    }
}
