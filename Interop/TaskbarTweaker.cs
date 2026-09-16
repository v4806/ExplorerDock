using System.Runtime.InteropServices;

namespace ExplorerDock.Interop;

/// <summary>
/// ITaskbarList（shell32）—— 官方提供的任务栏按钮增删接口。
/// DeleteTab 把某个窗口的按钮从任务栏上摘掉，AddTab 再放回去。
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

    private readonly object _gate = new();
    private ITaskbarList? _list;
    private bool _broken;

    private ITaskbarList? GetList()
    {
        if (_broken) return null;
        if (_list is not null) return _list;
        try
        {
            var type = Type.GetTypeFromCLSID(Clsid);
            if (type is null)
            {
                _broken = true;
                return null;
            }

            _list = (ITaskbarList)Activator.CreateInstance(type)!;
            _list.HrInit();
            return _list;
        }
        catch
        {
            _broken = true;
            return null;
        }
    }

    /// <summary>把窗口按钮从任务栏摘掉。</summary>
    public bool Remove(IntPtr hwnd)
    {
        lock (_gate)
        {
            try
            {
                GetList()?.DeleteTab(hwnd);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>把窗口按钮还给任务栏。</summary>
    public bool Restore(IntPtr hwnd)
    {
        lock (_gate)
        {
            try
            {
                GetList()?.AddTab(hwnd);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _list = null;
        }
    }
}
