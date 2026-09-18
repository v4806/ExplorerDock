using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ExplorerDock.Interop;

namespace ExplorerDock.Views;

/// <summary>一张卡片的缩略图要挂哪里：源窗口句柄 + 屏幕物理像素矩形。</summary>
internal readonly record struct ThumbnailSlot(IntPtr Handle, Int32Rect Rect);

/// <summary>
/// Alt+Tab 面板的缩略图宿主。
///
/// 为什么单独开一个窗口：DWM 的实时缩略图（DwmRegisterThumbnail，任务栏预览用的就是它）
/// 要求宿主是**普通合成窗口**，而面板本身是分层窗口（AllowsTransparency），当不了宿主。
/// 所以这里再开一个不透明的置顶小窗，用 SetWindowRgn 把它裁成"只剩下各张缩略图那一块"，
/// 再让鼠标穿透 —— 看上去就是面板上贴着一张实时缩略图，点和滚仍然落在下面的面板上。
///
/// 用它就不用截图了：最小化的窗口照样有画面（DWM 手里留着它最后那帧）。
/// </summary>
internal sealed class AltTabThumbnailHost : IDisposable
{
    private Window? _window;
    private IntPtr _hwnd;
    private readonly List<IntPtr> _registered = new();

    /// <summary>
    /// 这一层压在其他窗口上面，也会挡住 OLE 拖放的落点判定（拖到缩略图上时，
    /// 系统把这一层当落点，下面的面板收不到消息）。所以它自己也接拖放，再把事件转交面板。
    /// </summary>
    public Action<DragEventArgs>? DragOverForward { get; set; }

    /// <summary>见 <see cref="DragOverForward"/>：把落下事件转交给面板处理。</summary>
    public Action<DragEventArgs>? DropForward { get; set; }

    /// <summary>按当前卡片位置摆好缩略图（在面板显示之后调用）。</summary>
    public void Show(IReadOnlyList<ThumbnailSlot> slots, Color background)
    {
        if (slots.Count == 0)
        {
            Hide();
            return;
        }

        EnsureWindow(background);

        var bounds = Union(slots);

        if (_window is null) return;

        _window.Background = new SolidColorBrush(background);

        if (!_window.IsVisible) _window.Show();

        // 位置大小直接用物理像素摆，绕开 DPI 换算；
        // 放在面板之后调到 TOPMOST，保证缩略图压在面板上面
        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        var local = new List<NativeMethods.RECT>(slots.Count);

        foreach (var slot in slots)
        {
            local.Add(new NativeMethods.RECT
            {
                Left = slot.Rect.X - bounds.X,
                Top = slot.Rect.Y - bounds.Y,
                Right = slot.Rect.X - bounds.X + slot.Rect.Width,
                Bottom = slot.Rect.Y - bounds.Y + slot.Rect.Height,
            });
        }

        NativeMethods.SetWindowRegion(_hwnd, local);
        Register(slots, bounds);
    }

    public void Hide()
    {
        Unregister();

        try
        {
            if (_window is { IsVisible: true }) _window.Hide();
        }
        catch
        {
            // 窗口可能已经没了
        }
    }

    public void Dispose()
    {
        Unregister();

        try { _window?.Close(); } catch { }

        _window = null;
        _hwnd = IntPtr.Zero;
    }

    private void EnsureWindow(Color background)
    {
        if (_window is not null) return;

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = false,   // 必须是普通合成窗口，DWM 才肯往上挂缩略图
            Background = new SolidColorBrush(background),
            ShowInTaskbar = false,
            ShowActivated = false,        // 别把前台抢走，否则切窗口的判断会乱
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Title = "ExplorerDock 缩略图层",
        };

        _hwnd = new WindowInteropHelper(_window).EnsureHandle();

        // 拖放转发：这一层的区域（各张缩略图）也要能当"卡片落点"
        _window.AllowDrop = true;
        _window.DragOver += (_, e) => DragOverForward?.Invoke(e);
        _window.Drop += (_, e) => DropForward?.Invoke(e);

        // 鼠标穿透 + 不进任务栏/Alt+Tab
        long style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(
            _hwnd,
            NativeMethods.GWL_EXSTYLE,
            style | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
    }

    private void Register(IReadOnlyList<ThumbnailSlot> slots, Int32Rect bounds)
    {
        Unregister();

        foreach (var slot in slots)
        {
            if (slot.Rect.Width <= 2 || slot.Rect.Height <= 2) continue;
            if (!NativeMethods.IsWindow(slot.Handle)) continue;

            if (!NativeMethods.RegisterThumbnail(_hwnd, slot.Handle, out var id)) continue;

            var properties = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION
                          | NativeMethods.DWM_TNP_VISIBLE
                          | NativeMethods.DWM_TNP_OPACITY
                          | NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = FitInside(slot.Handle, slot.Rect, bounds),
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = true,
            };

            if (NativeMethods.UpdateThumbnail(id, ref properties)) _registered.Add(id);
            else NativeMethods.UnregisterThumbnail(id);
        }
    }

    /// <summary>
    /// DWM 会把源窗口拉伸着填满目标矩形，直接给整块会变形。
    /// 这里按源窗口客户区的比例算一个居中的矩形（左右/上下留边），比例与原来一致。
    /// </summary>
    private static NativeMethods.RECT FitInside(IntPtr source, Int32Rect area, Int32Rect bounds)
    {
        int x = area.X - bounds.X;
        int y = area.Y - bounds.Y;

        int fullRight = x + area.Width;
        int fullBottom = y + area.Height;

        if (!NativeMethods.GetClientSize(source, out int sourceWidth, out int sourceHeight))
        {
            return new NativeMethods.RECT { Left = x, Top = y, Right = fullRight, Bottom = fullBottom };
        }

        double scale = Math.Min(area.Width / (double)sourceWidth, area.Height / (double)sourceHeight);

        int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));

        int left = x + (area.Width - width) / 2;
        int top = y + (area.Height - height) / 2;

        return new NativeMethods.RECT { Left = left, Top = top, Right = left + width, Bottom = top + height };
    }

    private void Unregister()
    {
        foreach (var id in _registered) NativeMethods.UnregisterThumbnail(id);

        _registered.Clear();
    }

    private static Int32Rect Union(IReadOnlyList<ThumbnailSlot> slots)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;

        foreach (var slot in slots)
        {
            left = Math.Min(left, slot.Rect.X);
            top = Math.Min(top, slot.Rect.Y);
            right = Math.Max(right, slot.Rect.X + slot.Rect.Width);
            bottom = Math.Max(bottom, slot.Rect.Y + slot.Rect.Height);
        }

        return new Int32Rect(left, top, right - left, bottom - top);
    }
}
