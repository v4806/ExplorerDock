using System.Runtime.InteropServices;
using ExplorerDock.Services;

namespace ExplorerDock.Interop;

/// <summary>
/// ITaskbarList（shell32）—— 官方提供的任务栏按钮增删接口。
/// DeleteTab 把某个窗口的按钮从任务栏上摘掉，AddTab 再放回去。
///
/// 注意：这是**同步的进程外 COM 调用**（真正干活的是 explorer），没有超时参数 ——
/// 未响应的窗口会让 shell 忙住，调用线程就一起卡住。所以这里一律经
/// <see cref="BoundedCall"/> 走"独立线程 + 硬超时 + 上一发未回来就跳过"：
/// 宁可这一轮补刀没做成（下一轮还会再来），也不能把宿主轮询线程或界面线程拖死。
/// </summary>
[ComImport]
[Guid("56FDF342-FD6D-11d0-958A-006097C9A090")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskbarList
{
    void HrInit();
    void AddTab(IntPtr hwnd);
    void DeleteTab(IntPtr hwnd);
    void ActivateTab(IntPtr hwnd);
    void SetActiveAlt(IntPtr hwnd);
}

internal sealed class TaskbarTweaker : IDisposable
{
    private static readonly Guid Clsid = new("56FDF344-FD6D-11d0-958A-006097C9A090");

    /// <summary>单次 COM 调用的等待上限。增删任务栏按钮本来很快，慢就说明 shell 那边卡住了。</summary>
    private const int CallTimeoutMs = 400;

    /// <summary>COM 对象按线程缓存：调用现在分散在各自的隔离线程上，跨线程共用一个代理并不安全。</summary>
    [ThreadStatic] private static ITaskbarList? _threadList;
    [ThreadStatic] private static bool _threadListBroken;

    private volatile bool _disposed;

    private static ITaskbarList? GetThreadList()
    {
        if (_threadListBroken) return null;
        if (_threadList is not null) return _threadList;

        try
        {
            var type = Type.GetTypeFromCLSID(Clsid);

            if (type is null)
            {
                _threadListBroken = true;
                return null;
            }

            _threadList = (ITaskbarList)Activator.CreateInstance(type)!;
            _threadList.HrInit();
            return _threadList;
        }
        catch
        {
            _threadListBroken = true;
            return null;
        }
    }

    /// <summary>把窗口按钮从任务栏摘掉。返回 false 表示这次没做成（跳过或超时），下一轮会再来。</summary>
    public bool Remove(IntPtr hwnd)
    {
        if (_disposed || hwnd == IntPtr.Zero) return false;

        return BoundedCall.TryRun(
            $"taskbar:0x{hwnd.ToInt64():X}", CallTimeoutMs, () => { GetThreadList()?.DeleteTab(hwnd); }, "taskbar: remove");
    }

    /// <summary>把窗口按钮还给任务栏。返回 false 表示这次没做成（跳过或超时）。</summary>
    public bool Restore(IntPtr hwnd)
    {
        if (_disposed || hwnd == IntPtr.Zero) return false;

        return BoundedCall.TryRun(
            $"taskbar:0x{hwnd.ToInt64():X}", CallTimeoutMs, () => { GetThreadList()?.AddTab(hwnd); }, "taskbar: restore");
    }

    public void Dispose() => _disposed = true;
}
