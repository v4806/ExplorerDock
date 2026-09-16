using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ExplorerDock.Interop;

namespace ExplorerDock;

/// <summary>
/// ALT+TAB 里的"文件夹窗口替身"。
///
/// 它把最后在用的那个文件夹窗口的**实时画面**通过 DWM 缩略图注册到自己身上，
/// 自己则停在屏幕外（用户看不见），但会出现在 ALT+TAB 列表里 ——
/// 于是切到这一项看到的就是文件夹窗口的画面，选中它再跳回真正的文件夹窗口。
///
/// 任务栏按钮由 WPF 的 ShowInTaskbar=false 负责摘掉（内部是 ITaskbarList::DeleteTab），
/// 因此它不会占用任务栏位置。
/// </summary>
internal sealed class AltTabProxyWindow : Window
{
    private IntPtr _sourceHandle = IntPtr.Zero;
    private IntPtr _thumbnail = IntPtr.Zero;
    private bool _registered;

    public AltTabProxyWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24));
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "文件夹";

        // 放到屏幕外的固定位置：用户看不到，但 DWM 仍然渲染它，
        // ALT+TAB 的实时缩略图就有内容可看
        Left = -32000;
        Top = -32000;
        Width = 320;
        Height = 200;
    }

    /// <summary>切到"替身"时要跳转的目标窗口。</summary>
    public IntPtr TargetHandle => _sourceHandle;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        LocateOffscreen();
    }

    private void LocateOffscreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        NativeMethods.SetWindowPos(
            handle, IntPtr.Zero, -32000, -32000, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>把替身对准某个文件夹窗口：尺寸跟随它，画面用 DWM 缩略图实时映射。</summary>
    public void AttachTo(IntPtr folderWindow, string title)
    {
        if (folderWindow == IntPtr.Zero)
        {
            Detach();
            return;
        }

        // 尺寸跟真实窗口一致，缩略图才不会变形
        if (NativeMethods.GetWindowRect(folderWindow, out var rect))
        {
            int width = Math.Max(80, rect.Right - rect.Left);
            int height = Math.Max(60, rect.Bottom - rect.Top);
            if (Math.Abs(Width - width) > 0.5 || Math.Abs(Height - height) > 0.5)
            {
                Width = width;
                Height = height;
            }
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            if (Title != title) Title = title;

            // 直接用 Win32 设置窗口标题：ALT+TAB 列表读的就是它，
            // 这样切到这一项时显示的就是那个文件夹的名字
            var titleHandle = new WindowInteropHelper(this).Handle;
            if (titleHandle != IntPtr.Zero) NativeMethods.SetWindowText(titleHandle, title);
        }

        if (folderWindow == _sourceHandle && _registered)
        {
            UpdateThumbnail();
            return;
        }

        Detach();
        _sourceHandle = folderWindow;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        if (DwmRegisterThumbnail(handle, _sourceHandle, out _thumbnail) != 0)
        {
            _thumbnail = IntPtr.Zero;
            return;
        }

        _registered = true;
        UpdateThumbnail();
    }

    public void UpdateThumbnail()
    {
        if (!_registered || _thumbnail == IntPtr.Zero) return;

        var props = new DwmThumbnailProperties
        {
            dwFlags = DwmTnpRectDestination | DwmTnpVisible | DwmTnpSourceClientAreaOnly | DwmTnpOpacity,
            rcDestination = new NativeMethods.RECT
            {
                Left = 0,
                Top = 0,
                Right = (int)Math.Round(Width),
                Bottom = (int)Math.Round(Height),
            },
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = true,
        };

        DwmUpdateThumbnailProperties(_thumbnail, ref props);
    }

    /// <summary>
    /// 把替身提到 Z 序最前。ALT+TAB 的顺序就是 Z 序，
    /// 而替身自己从不被激活，不主动提一下就会永远排在列表末尾。
    /// </summary>
    public void BringToFront()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        NativeMethods.SetWindowPos(
            handle, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 让替身进入 ALT+TAB 的"最近使用"位置。
    ///
    /// ALT+TAB 的顺序由窗口的**激活历史**决定（不是 Z 序），而替身从不被真正使用，
    /// 所以一直垫底。这里先把它激活、再在同一帧里把焦点交还目标文件夹窗口，
    /// 用户基本看不到中间态。
    /// </summary>
    public void Touch()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var folder = _sourceHandle;
        if (handle == IntPtr.Zero || folder == IntPtr.Zero) return;
        if (!NativeMethods.IsWindow(folder)) return;

        // 用和切换文件夹同样的"抢前台"手段激活替身，
        // 让 shell 把它记进 ALT+TAB 的最近使用序列，然后立刻把焦点交还文件夹窗口
        NativeMethods.ForceForeground(handle);
        NativeMethods.ForceForeground(folder);
    }

    public void Detach()
    {
        if (_registered && _thumbnail != IntPtr.Zero)
        {
            DwmUnregisterThumbnail(_thumbnail);
        }

        _thumbnail = IntPtr.Zero;
        _registered = false;
        _sourceHandle = IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        Detach();
        base.OnClosed(e);
    }

    // ---------- DWM 互操作 ----------

    private const uint DwmTnpRectDestination = 0x00000001;
    private const uint DwmTnpOpacity = 0x00000004;
    private const uint DwmTnpVisible = 0x00000008;
    private const uint DwmTnpSourceClientAreaOnly = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmThumbnailProperties
    {
        public uint dwFlags;
        public NativeMethods.RECT rcDestination;
        public NativeMethods.RECT rcSource;

        [MarshalAs(UnmanagedType.U1)]
        public byte opacity;

        [MarshalAs(UnmanagedType.Bool)]
        public bool fVisible;

        [MarshalAs(UnmanagedType.Bool)]
        public bool fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr destWindow, IntPtr sourceWindow, out IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref DwmThumbnailProperties props);
}
