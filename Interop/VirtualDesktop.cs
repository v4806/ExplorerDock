using System.Runtime.InteropServices;

namespace ExplorerDock.Interop;

/// <summary>
/// IVirtualDesktopManager（Windows 10 起系统自带的虚拟桌面 COM 接口）—— 只回答一个问题：
/// 某个顶层窗口是不是在**当前**虚拟桌面上。
///
/// 为什么需要它：窗口属于哪个虚拟桌面并不影响 EnumWindows，所有桌面的窗口都枚举得到；
/// 而任务栏按钮被摘掉之后，别的桌面上的窗口也不会自己消失。不按桌面过滤的话，
/// 悬浮栏和 Alt+Tab 就会把每个桌面的窗口堆在一起（issue #9：多虚拟桌面下窗口全堆在一起）。
///
/// 失败策略（关键，不要改）：COM 创建/调用失败、系统不支持、窗口已销毁时**一律返回 true**
/// （当成"在当前桌面"）。宁可退化成这个功能加进来之前的行为（所有桌面的窗口都显示），
/// 也绝不能因为 COM 出问题把窗口从悬浮栏里丢掉、更不能让异常打断监视线程的轮询。
///
/// 调用方约定：只在监视线程（STA）里调用，结果由 <see cref="Services.ExplorerWatcher"/> 每轮
/// 发布成快照；别的线程查 <see cref="Services.TakeoverState"/> 就行，别直接调这里 ——
/// 每按一次 Alt+Tab 都现场跨进程调 COM 不划算，也容易在非 STA 线程上踩公寓问题。
/// </summary>
internal static class VirtualDesktop
{
    /// <summary>VirtualDesktopManager 的 CLSID（Windows 10/11 通用，系统自带）。</summary>
    private static readonly Guid Clsid = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");

    /// <summary>COM 创建/释放、以及每次调用的串行锁（监视线程调用，进程退出线程释放）。</summary>
    private static readonly object Gate = new();

    /// <summary>缓存的 COM 对象；null 表示还没建出来过。</summary>
    private static IVirtualDesktopManager? _manager;

    /// <summary>上次创建失败后允许再试的时刻（Environment.TickCount64）；0 = 没失败过。</summary>
    private static long _retryAt;

    /// <summary>
    /// 创建失败后的重试间隔。explorer 重启后 COM 可能又可用，不能永久放弃过滤；
    /// 但也不能每个窗口每轮都去撞一次激活失败，所以隔一会儿再试。
    /// </summary>
    private const long RetryIntervalMs = 30_000;

    private static bool _exitHookRegistered;

    /// <summary>
    /// 这个窗口是不是在当前虚拟桌面上。返回 true = 在当前桌面，**或**判定失败（按不过滤处理）。
    /// 线程安全；任何异常都在内部吞掉，调用方不需要 try/catch。
    /// </summary>
    public static bool IsOnCurrentDesktop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return true;

        var manager = GetManager();
        if (manager is null) return true;

        try
        {
            // 调用也放在锁里：进程退出时的释放要等这一发结束，避免对着已释放的 RCW 调方法
            lock (Gate)
            {
                // 等锁期间可能已经走完释放流程
                if (_manager is null) return true;

                int hr = manager.IsWindowOnCurrentVirtualDesktop(hwnd, out int onCurrentDesktop);

                // 明确的 COM 断连错误（explorer 重启后代理失效）：清缓存，下一轮重建再试。
                // 其余失败（窗口无效 / 不是顶层窗口 / 接口拒绝）按"不过滤"处理，
                // 不能因此丢掉缓存 —— 否则每个无效窗口都会触发一次重建。
                if (hr == unchecked((int)0x80010108)      // RPC_E_DISCONNECTED
                    || hr == unchecked((int)0x800401FD)   // CO_E_OBJNOTCONNECTED
                    || hr == unchecked((int)0x80000013))  // RO_E_CLOSED
                {
                    ResetManager();
                    return true;
                }

                // 非 S_OK（窗口无效、不是顶层窗口、接口拒绝）一律按"不过滤"处理
                return hr != 0 || onCurrentDesktop != 0;
            }
        }
        catch
        {
            // 调用抛异常多半是 explorer 重启导致 COM 代理失效：丢掉缓存，下一轮重建再试
            ResetManager();

            return true;
        }
    }

    /// <summary>懒创建并缓存 COM 对象。创建失败按"没有桌面信息"处理，过一会儿再试。</summary>
    private static IVirtualDesktopManager? GetManager()
    {
        lock (Gate)
        {
            if (_manager is not null) return _manager;

            if (Environment.TickCount64 < _retryAt) return null;

            try
            {
                var type = Type.GetTypeFromCLSID(Clsid);

                if (type is null)
                {
                    _retryAt = Environment.TickCount64 + RetryIntervalMs;
                    return null;
                }

                _manager = (IVirtualDesktopManager)Activator.CreateInstance(type)!;

                if (!_exitHookRegistered)
                {
                    _exitHookRegistered = true;
                    AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                }

                return _manager;
            }
            catch
            {
                // 系统不支持 / COM 激活失败：退化成"不过滤"，过一段时间再试
                _retryAt = Environment.TickCount64 + RetryIntervalMs;
                return null;
            }
        }
    }

    /// <summary>进程退出时释放缓存的 COM 对象（进程结束后系统也会回收，这里只是不留悬空引用）。</summary>
    private static void OnProcessExit(object? sender, EventArgs e) => Release();

    /// <summary>丢掉缓存对象，下一轮调用会重新创建（COM 代理失效时用）。</summary>
    private static void ResetManager()
    {
        lock (Gate)
        {
            var manager = _manager;
            _manager = null;

            if (manager is null) return;

            try
            {
                Marshal.ReleaseComObject(manager);
            }
            catch
            {
                // 代理已经失效：忽略，交给 GC
            }
        }
    }

    private static void Release()
    {
        lock (Gate)
        {
            var manager = _manager;
            _manager = null;

            if (manager is null) return;

            try
            {
                Marshal.ReleaseComObject(manager);
            }
            catch
            {
                // 已经释放过 / RCW 不可用：进程正要退出，忽略
            }
        }
    }

    /// <summary>
    /// 为调用线程创建一个独立的即时查询器。Alt+Tab 的新一次枚举必须用本次结果，
    /// 不能读取监视线程上一轮发布的桌面快照。
    /// </summary>
    public static VirtualDesktopProbe? CreateProbe()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(Clsid);
            if (type is null) return null;

            return new VirtualDesktopProbe(
                (IVirtualDesktopManager)Activator.CreateInstance(type)!);
        }
        catch
        {
            // 系统不支持或 COM 暂不可用：调用方按"不过滤"处理。
            return null;
        }
    }

    /// <summary>新查询器为空时退化为不过滤；创建失败绝不丢窗口。</summary>
    public static bool IsOnCurrentDesktop(IntPtr hwnd, VirtualDesktopProbe? probe)
        => hwnd == IntPtr.Zero || probe is null || probe.IsOnCurrentDesktop(hwnd);
}

/// <summary>
/// 绑定在创建线程上的虚拟桌面查询器，供 Alt+Tab 这种一次性问题即时使用。
/// </summary>
internal sealed class VirtualDesktopProbe : IDisposable
{
    private readonly IVirtualDesktopManager? _manager;
    private readonly Func<IntPtr, bool>? _query;
    private bool _disposed;

    internal VirtualDesktopProbe(
        IVirtualDesktopManager? manager = null,
        Func<IntPtr, bool>? query = null)
    {
        _manager = manager;
        _query = query;
    }

    public bool IsOnCurrentDesktop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _disposed) return true;

        try
        {
            if (_query is not null) return _query(hwnd);
            if (_manager is null) return true;

            int hr = _manager.IsWindowOnCurrentVirtualDesktop(hwnd, out int onCurrentDesktop);
            return hr != 0 || onCurrentDesktop != 0;
        }
        catch
        {
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_manager is null) return;

        try
        {
            Marshal.ReleaseComObject(_manager);
        }
        catch
        {
            // 代理已失效：交给 GC。
        }
    }
}

/// <summary>IVirtualDesktopManager（只声明到需要的方法；vtable 顺序必须和系统接口一致）。</summary>
[ComImport]
[Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    /// <summary>窗口是否在当前虚拟桌面上：S_OK 且 out 非 0 = 是；HRESULT 非 0 时 out 无意义。</summary>
    [PreserveSig]
    int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);

    /// <summary>取窗口所在的虚拟桌面 ID（用不到，但 vtable 顺序必须完整声明）。</summary>
    [PreserveSig]
    int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

    /// <summary>把窗口移到指定虚拟桌面（用不到，但 vtable 顺序必须完整声明）。</summary>
    [PreserveSig]
    int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}
