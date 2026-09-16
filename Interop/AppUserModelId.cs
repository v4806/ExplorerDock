using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ExplorerDock.Interop;

/// <summary>
/// 给单个窗口设置 AppUserModelID。
///
/// 任务栏是按 AppUserModelID 给窗口分组的：把一堆文件夹窗口设成同一个 ID，
/// 它们就会合并成一个任务栏按钮 —— 而且**不碰 ITaskbarList**，
/// 所以 ALT+TAB 里每个窗口依旧都在（这是摘除按钮做不到的）。
/// </summary>
internal static class AppUserModelId
{
    /// <summary>文件夹窗口统一归到这个名字下。</summary>
    public const string FolderGroupId = "ExplorerDock.FolderGroup";

    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        void Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, out IPropertyStore propertyStore);

    private static readonly Guid IIDIPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    private static readonly Guid FmtidAppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint PidAppUserModelId = 5;
    private const ushort VtLpwstr = 31;
    private const ushort VtEmpty = 0;

    /// <summary>把窗口的 AppUserModelID 设为指定值；传 null 表示清掉（恢复系统默认分组）。</summary>
    public static bool TrySet(IntPtr hwnd, string? appId)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            var iid = IIDIPropertyStore;
            int hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out var store);
            if (hr != 0 || store is null)
            {
                Log($"hwnd=0x{hwnd.ToInt64():X} SHGetPropertyStoreForWindow hr=0x{hr:X8}");
                return false;
            }

            var key = new PROPERTYKEY { fmtid = FmtidAppUserModel, pid = PidAppUserModelId };
            var value = new PROPVARIANT();

            if (string.IsNullOrEmpty(appId))
            {
                value.vt = VtEmpty;
                value.pointerValue = IntPtr.Zero;
            }
            else
            {
                value.vt = VtLpwstr;
                value.pointerValue = Marshal.StringToCoTaskMemUni(appId);
            }

            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
                Log($"hwnd=0x{hwnd.ToInt64():X} set '{appId}' ok");
                return true;
            }
            finally
            {
                if (value.pointerValue != IntPtr.Zero) Marshal.FreeCoTaskMem(value.pointerValue);
            }
        }
        catch (Exception ex)
        {
            Log($"hwnd=0x{hwnd.ToInt64():X} exception: {ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    private static int _logCount;

    private static void Log(string message)
    {
        if (Interlocked.Increment(ref _logCount) > 30) return;

        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "ExplorerDock.aumid.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 忽略
        }
    }
}
