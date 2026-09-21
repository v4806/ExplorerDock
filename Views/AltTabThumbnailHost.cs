using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ExplorerDock.Interop;

namespace ExplorerDock.Views;

/// <summary>一张卡片的缩略图要挂哪里：源窗口句柄 + 屏幕物理像素矩形。</summary>
/// <summary>
/// 一张卡片的缩略图要挂哪里：源窗口句柄 + 格子的完整矩形 + 被列表裁掉之后剩下的那一块。
///
/// Rect 是格子本身的完整屏幕矩形：缩略图按它的比例摆放，才不会因为格子被列表切掉一截而变形；
/// Clip 是与列表可视区的交集：宿主窗口的大小和裁剪区域按它来，超出列表的部分就这样被裁掉。
/// Clip 为空表示这一格完全滚出了可视区 —— 它照样注册着，只是设成不可见，
/// 这样滚动时格子数量不变，就不会反复全量重挂（那是"滚动时卡片闪烁"的来源）。
/// </summary>
internal readonly record struct ThumbnailSlot(IntPtr Handle, Int32Rect Rect, Int32Rect Clip);

/// <summary>
/// Alt+Tab 面板的缩略图宿主。
///
/// 为什么单独开一个窗口：DWM 的实时缩略图（DwmRegisterThumbnail，任务栏预览用的就是它）
/// 要求宿主是**普通合成窗口**，而面板本身是分层窗口（AllowsTransparency），当不了宿主。
/// 所以这里再开一个不透明的置顶小窗，用 SetWindowRgn 把它裁成"只剩下各张缩略图那一块"，
/// 再让鼠标穿透（WM_NCHITTEST 回 HTTRANSPARENT）—— 看上去就是面板上贴着一张实时缩略图，
/// 点和滚仍然落在下面的面板上。
///
/// 注意：WS_EX_TRANSPARENT 只是"延后绘制"，**不提供鼠标穿透**；
/// 真正让鼠标消息继续往下走的是 WM_NCHITTEST → HTTRANSPARENT（见 <see cref="WndProc"/>）。
///
/// 用它就不用截图了：最小化的窗口照样有画面（DWM 手里留着它最后那帧）。
/// </summary>
internal sealed class AltTabThumbnailHost : IDisposable
{
    private Window? _window;
    private IntPtr _hwnd;

    /// <summary>已挂上去的缩略图：DWM 句柄 + 它对应的源窗口（用来判断"还是这批窗口吗"）。</summary>
    private readonly List<Entry> _entries = new();

    /// <summary>上一次的裁剪区域（窗口相对坐标）。滚动多是纯平移，靠它判断要不要重设 region。</summary>
    private List<NativeMethods.RECT> _lastLocal = new();

    private readonly record struct Entry(IntPtr Id, IntPtr Source);

    /// <summary>
    /// 这一层压在其他窗口上面，也会挡住 OLE 拖放的落点判定（拖到缩略图上时，
    /// 系统把这一层当落点，下面的面板收不到消息）。所以它自己也接拖放，再把事件转交面板。
    /// </summary>
    public Action<DragEventArgs>? DragOverForward { get; set; }

    /// <summary>见 <see cref="DragOverForward"/>：把落下事件转交给面板处理。</summary>
    public Action<DragEventArgs>? DropForward { get; set; }

    /// <summary>
    /// 这一层同样会吃掉鼠标滚轮（它是独立的窗口），所以滚轮也要转发给面板，
    /// 否则鼠标停在卡片上就滚不动列表、只有卡片之间的缝里能滚。
    /// </summary>
    public Action<System.Windows.Input.MouseWheelEventArgs>? WheelForward { get; set; }

    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    /// <summary>按当前卡片位置摆好缩略图（全量重挂；面板刚显示、或卡片增减之后调用）。</summary>
    public void Show(IReadOnlyList<ThumbnailSlot> slots, Color background)
        => Apply(slots, background, force: true);

    /// <summary>
    /// 归属窗口（也就是 Alt+Tab 面板）。挂上它之后系统保证缩略图层永远显示在面板之上 ——
    /// 否则"点一下面板空白处 / 滚动一下"会把面板提升到 z 序顶部，缩略图层被压到下面，
    /// 看上去就是预览上蒙了一层深色半透明遮罩加一个兜底图标（用户报的现象）。
    /// </summary>
    public Window? OwnerWindow { get; set; }

    /// <summary>
    /// 卡片位置变了、但窗口还是那批（滚动中）：就地更新每张缩略图的目标矩形，不注销重挂。
    ///
    /// 原来"整层收掉再重挂"的做法在滚动期间会让所有缩略图消失、只剩程序图标
    /// （用户报的"滚动列表时预览全部变成图标"），所以滚动走这条路，
    /// 只有停手之后才由 StartThumbnails 全量重挂一次。
    /// </summary>
    public void Update(IReadOnlyList<ThumbnailSlot> slots)
    {
        // 窗口还没建起来（面板刚弹出、全量重挂那一次还没跑）就跳过，交给 StartThumbnails。
        if (_window is null || _hwnd == IntPtr.Zero) return;

        Apply(slots, null, force: false);
    }

    private void Apply(IReadOnlyList<ThumbnailSlot> slots, Color? background, bool force)
    {
        if (slots.Count == 0)
        {
            Hide();
            return;
        }

        if (_window is null)
        {
            if (background is not { } seed) return;

            EnsureWindow(seed);
        }

        if (_window is null) return;

        if (background is { } color) _window.Background = new SolidColorBrush(color);

        if (!_window.IsVisible) _window.Show();

        // 窗口只包住"还看得见的那部分"格子的包围盒；一个都看不见就不用摆了。
        if (Union(slots) is not { } bounds)
        {
            Hide();
            return;
        }

        var local = BuildLocal(slots, bounds);

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

        // 裁剪区域（窗口相对坐标）没变就别重设 region：
        // 滚动多数时候是纯平移，每帧 SetWindowRgn 会让这一层反复重绘、闪得厉害。
        bool sameRegion = !force && SameRegion(local, _lastLocal);

        if (!sameRegion) NativeMethods.SetWindowRegion(_hwnd, local);

        _lastLocal = local;

        // 纯平移：缩略图跟着宿主窗口一起走，一个 DWM 调用都不用发。
        if (!force && sameRegion && EntriesMatch(slots)) return;

        // 位置变了但窗口还是那批：就地改目标矩形，别注销重挂。
        if (!force && EntriesMatch(slots) && UpdateDestinations(slots, bounds)) return;

        Register(slots, bounds);
    }

    /// <summary>已挂的缩略图和这一批格子是不是一一对应（滚动中数量与句柄都不该变）。</summary>
    private bool EntriesMatch(IReadOnlyList<ThumbnailSlot> slots)
    {
        if (_entries.Count != slots.Count) return false;

        for (int i = 0; i < slots.Count; i++)
        {
            if (_entries[i].Source != slots[i].Handle) return false;
        }

        return true;
    }

    /// <summary>就地更新各张缩略图的落点。中途失败返回 false，交给调用方退回去全量重挂。</summary>
    private bool UpdateDestinations(IReadOnlyList<ThumbnailSlot> slots, Int32Rect bounds)
    {
        for (int i = 0; i < slots.Count; i++)
        {
            var properties = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION
                          | NativeMethods.DWM_TNP_VISIBLE
                          | NativeMethods.DWM_TNP_OPACITY
                          | NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = FitInside(slots[i].Handle, slots[i].Rect, bounds),
                opacity = 255,
                fVisible = IsVisible(slots[i]),
                fSourceClientAreaOnly = true,
            };

            if (!NativeMethods.UpdateThumbnail(_entries[i].Id, ref properties)) return false;
        }

        return true;
    }

    /// <summary>这一格还有没有露在列表可视区里。</summary>
    private static bool IsVisible(ThumbnailSlot slot) => slot.Clip.Width > 0 && slot.Clip.Height > 0;

    private static List<NativeMethods.RECT> BuildLocal(IReadOnlyList<ThumbnailSlot> slots, Int32Rect bounds)
    {
        var local = new List<NativeMethods.RECT>(slots.Count);

        foreach (var slot in slots)
        {
            local.Add(new NativeMethods.RECT
            {
                Left = slot.Clip.X - bounds.X,
                Top = slot.Clip.Y - bounds.Y,
                Right = slot.Clip.X - bounds.X + slot.Clip.Width,
                Bottom = slot.Clip.Y - bounds.Y + slot.Clip.Height,
            });
        }

        return local;
    }

    private static bool SameRegion(List<NativeMethods.RECT> a, List<NativeMethods.RECT> b)
    {
        if (a.Count != b.Count) return false;

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Left != b[i].Left || a[i].Top != b[i].Top
                || a[i].Right != b[i].Right || a[i].Bottom != b[i].Bottom)
            {
                return false;
            }
        }

        return true;
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

        // 附属窗口永远显示在 owner 之上（系统保证），所以面板被点到/被提升时
        // 也不会把它压下去。见 OwnerWindow 的说明。
        if (OwnerWindow is { } owner) _window.Owner = owner;

        _hwnd = new WindowInteropHelper(_window).EnsureHandle();

        // 拖放转发：这一层的区域（各张缩略图）也要能当"卡片落点"
        _window.AllowDrop = true;
        _window.DragOver += (_, e) => DragOverForward?.Invoke(e);
        _window.Drop += (_, e) => DropForward?.Invoke(e);

        // 滚轮也转发（见 WheelForward 的注释）
        _window.MouseWheel += (_, e) => WheelForward?.Invoke(e);

        // 不进任务栏 / Alt+Tab。
        //
        // 故意**不**加 WS_EX_TRANSPARENT：它只表示"等下面的兄弟窗口画完再画"，
        // 既不穿透鼠标，又会让这一层和面板（同线程的兄弟窗口）抢绘制顺序 ——
        // 表现就是"有时候整块缩略图不显示"。鼠标穿透改由下面 WndProc 的 HTTRANSPARENT 负责。
        long style = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(
            _hwnd,
            NativeMethods.GWL_EXSTYLE,
            style | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);

        // 这一层只是"把画面贴上去"，不参与任何交互。
        //
        // 卡片预览区占了卡片大约八成的面积，全被它盖着；鼠标消息必须继续往下走交给面板，
        // 否则点在预览图上既选不中卡片、也切不了窗口 —— 用户报的"只能用鼠标点卡片头部那一条才能切换"。
        // 这里给窗口挂 WndProc 钩子，命中测试一律回 HTTRANSPARENT，让消息传给下面的面板。
        if (HwndSource.FromHwnd(_hwnd) is { } source) source.AddHook(WndProc);
    }

    /// <summary>命中测试一律"透明"：鼠标消息交给下面的 Alt+Tab 面板处理。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            handled = true;
            return new IntPtr(HTTRANSPARENT);
        }

        return IntPtr.Zero;
    }

    private void Register(IReadOnlyList<ThumbnailSlot> slots, Int32Rect bounds)
    {
        Unregister();

        int ok = 0, failed = 0, small = 0, dead = 0;

        foreach (var slot in slots)
        {
            if (slot.Rect.Width <= 2 || slot.Rect.Height <= 2) { small++; continue; }
            if (!NativeMethods.IsWindow(slot.Handle)) { dead++; continue; }

            if (!NativeMethods.RegisterThumbnail(_hwnd, slot.Handle, out var id)) { failed++; continue; }

            var properties = new NativeMethods.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION
                          | NativeMethods.DWM_TNP_VISIBLE
                          | NativeMethods.DWM_TNP_OPACITY
                          | NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = FitInside(slot.Handle, slot.Rect, bounds),
                opacity = 255,
                fVisible = IsVisible(slot),
                fSourceClientAreaOnly = true,
            };

            if (NativeMethods.UpdateThumbnail(id, ref properties))
            {
                _entries.Add(new Entry(id, slot.Handle));
                ok++;
            }
            else
            {
                NativeMethods.UnregisterThumbnail(id);
                failed++;
            }
        }

        // 卡片多的时候这里最容易出问题（缩略图一半是黑的），把账目记下来
        Log($"register ok={ok} failed={failed} small={small} dead={dead} slots={slots.Count} bounds={bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}");
    }

    private static int _logCount;

    private static void Log(string message)
    {
        if (Interlocked.Increment(ref _logCount) > 200) return;

        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "ExplorerDock.thumbs.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 忽略
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
        foreach (var entry in _entries) NativeMethods.UnregisterThumbnail(entry.Id);

        _entries.Clear();
    }

    /// <summary>
    /// 宿主窗口要盖住的屏幕范围：所有"还看得见"的格子（<see cref="ThumbnailSlot.Clip"/>）的包围盒。
    /// 一个都看不见时返回 null，调用方据此把这一层收起来。
    /// </summary>
    private static Int32Rect? Union(IReadOnlyList<ThumbnailSlot> slots)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;

        foreach (var slot in slots)
        {
            var clip = slot.Clip;

            if (clip.Width <= 0 || clip.Height <= 0) continue;

            left = Math.Min(left, clip.X);
            top = Math.Min(top, clip.Y);
            right = Math.Max(right, clip.X + clip.Width);
            bottom = Math.Max(bottom, clip.Y + clip.Height);
        }

        if (left >= right || top >= bottom) return null;

        return new Int32Rect(left, top, right - left, bottom - top);
    }
}
