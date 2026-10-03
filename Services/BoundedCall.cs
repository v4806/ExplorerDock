using System.Collections.Concurrent;

namespace ExplorerDock.Services;

/// <summary>
/// 把"可能无限阻塞的跨进程调用"关进有硬超时的笼子里。
///
/// 为什么需要它：ITaskbarList 的 DeleteTab/AddTab（对 explorer 的同步进程外 COM）、
/// UI Automation 的 FromHandle/FindAll（对未响应窗口能卡十几秒）都**没有超时参数** ——
/// 只要目标进程不响应，调用线程就一起卡住，而卡住的线程没法安全终止（不能 Thread.Abort）。
/// 这些调用过去直接跑在关键线程上（宿主轮询线程 / 界面线程），于是一个未响应的窗口
/// 就能把悬浮栏拖成"卡死、退出还不归还任务栏"。
///
/// 这里的做法：
///   · 每个"通道"（channel）同一时刻只允许一发在飞。上一发还没回来时，新请求**直接跳过**（不排队）——
///     排队只会把卡顿传播出去；跳过能让其他窗口、其他功能照常工作；
///   · 调用跑在独立后台线程上，等待方最多等 timeoutMs 就返回；
///   · 全局在飞数量有上限，防止反复触发时线程无限增长；
///   · 跳过 / 超时 / 异常都写进诊断日志（界面进程 → alttab.log，宿主 → host.log）。
///
/// 超时的那些线程会留在那里自己烂掉 —— 这是刻意的：它可能正持有 COM 代理与锁，
/// 乱动更危险。代价是该通道在它返回前一直被跳过（宁可功能降级一轮，也不卡死整个界面）。
/// </summary>
internal static class BoundedCall
{
    /// <summary>同时在飞的调用上限。超了就跳过，避免线程与 COM 代理无限堆积。</summary>
    private const int MaxInFlight = 8;

    private static readonly ConcurrentDictionary<string, byte> InFlight = new();
    private static int _inFlightTotal;

    /// <summary>没有返回值的版本。返回 true = 在超时前跑完；false = 跳过或超时。</summary>
    public static bool TryRun(string channel, int timeoutMs, Action work, string tag, bool sta = false)
        => Run(channel, timeoutMs, () => { work(); return true; }, false, tag, sta);

    /// <summary>带返回值的版本：跳过或超时时返回 <paramref name="fallback"/>。</summary>
    public static T Run<T>(string channel, int timeoutMs, Func<T> work, T fallback, string tag, bool sta = false)
    {
        if (!InFlight.TryAdd(channel, 0))
        {
            Diag($"{tag}: 跳过（上一发还没回来）channel={channel}");
            return fallback;
        }

        if (Interlocked.Increment(ref _inFlightTotal) > MaxInFlight)
        {
            Interlocked.Decrement(ref _inFlightTotal);
            InFlight.TryRemove(channel, out _);
            Diag($"{tag}: 跳过（并发已达上限 {MaxInFlight}）channel={channel}");
            return fallback;
        }

        T result = fallback;

        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                Diag($"{tag}: 调用异常 {ex.GetType().Name}: {ex.Message} channel={channel}");
            }
            finally
            {
                InFlight.TryRemove(channel, out _);
                Interlocked.Decrement(ref _inFlightTotal);
            }
        })
        {
            IsBackground = true,
            Name = "BoundedCall",
        };

        if (sta)
        {
            try
            {
                thread.SetApartmentState(ApartmentState.STA);
            }
            catch
            {
                // 线程已启动或不支持的平台：按默认公寓跑
            }
        }

        thread.Start();

        if (thread.Join(timeoutMs)) return result;

        Diag($"{tag}: 超时 {timeoutMs}ms，放弃等待 channel={channel}（该通道在它返回前会被跳过）");
        return fallback;
    }

    /// <summary>诊断日志：跟其他模块共用一份，界面进程与宿主各写各的文件。</summary>
    private static void Diag(string message)
    {
        try
        {
            AltTabController.Log(message);
        }
        catch
        {
            // 日志失败绝不影响功能
        }
    }
}
