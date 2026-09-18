using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ExplorerDock.Interop;
using ExplorerDock.Models;
using ExplorerDock.Services;

namespace ExplorerDock;

/// <summary>悬浮栏本体：一行按钮，每个按钮是一个打开着的文件夹窗口。</summary>
public partial class DockWindow : Window
{
    private readonly StackPanel _itemsHost;
    private readonly ScrollViewer _scroller;
    private readonly Border _grip;

    /// <summary>把手上的点阵图标（把手已去掉，这里只保留创建逻辑）。</summary>
    private TextBlock? _gripDots;
    private Border? _scrollLeft;
    private Border? _scrollRight;
    private Border? _scrollUp;
    private Border? _scrollDown;
    private readonly Dictionary<(IntPtr Handle, int TabIndex), ItemVisual> _items = new();

    /// <summary>
    /// 悬浮栏的行：每个程序独占一行（行内横向排该程序的窗口按钮）。
    /// 资源管理器固定第一行（它天然排在快照最前面），其余按程序首次出现顺序。
    /// </summary>
    private readonly Dictionary<string, StackPanel> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _rowOrder = new();
    private bool _menuOpen;
    private bool _autoCenter;
    private IntPtr _ourHandle;
    private IntPtr _activeWindow;
    private PlacementMode _tooltipPlacement = PlacementMode.Bottom;
    private ShadowWindow? _shadow;
    private ExplorerSnapshot? _lastSnapshot;
    private readonly DispatcherTimer _topmostTimer;
    private DateTime _lastTopmostFix = DateTime.MinValue;
    private bool _dragging;
    private Point _pressPoint;

    // ---------- 贴边自动隐藏 ----------

    /// <summary>当前靠在哪条屏幕边上（None = 没贴边）。</summary>
    private DockEdge _edgeSide = DockEdge.None;

    /// <summary>当前是不是"已滑出屏幕外"的收纳状态。</summary>
    private bool _edgeHidden;

    /// <summary>滑出/滑回动画是否正在进行。</summary>
    private bool _edgeAnimating;

    private double _edgeFromLeft;
    private double _edgeFromTop;
    private double _edgeToLeft;
    private double _edgeToTop;
    private DateTime _edgeAnimStart;

    /// <summary>这次动画是"收藏"（往屏外去）还是"呼出"（往屏内回），缓动曲线不同。</summary>
    private bool _edgeAnimHiding;

    /// <summary>未收纳时悬浮栏所在的屏幕内位置（呼出动画要回到这里）。</summary>
    private double _edgeDockLeft;
    private double _edgeDockTop;

    /// <summary>
    /// 贴边逻辑自己改坐标期间不做判定。
    ///
    /// 必须要有：呼出动画落定的最后一下也是"位置变化"，若不屏蔽，
    /// 判定会看到"又在贴边位置"而立刻把它收回去，形成收-放死循环。
    /// </summary>
    private bool _edgeSelfMove;

    /// <summary>动画时长。逐帧插值，阴影层每一帧都跟着重算，不会留残影。</summary>
    private const double EdgeAnimMs = 200;

    /// <summary>鼠标探测：光标是不是挪到贴边那条屏幕边上了。</summary>
    private readonly DispatcherTimer _edgeProbe = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(120),
    };

    /// <summary>光标离开悬浮栏的时刻，用来算"过一会儿自动收回"。</summary>
    private DateTime _edgeLeaveAt = DateTime.MinValue;

    /// <summary>滑出之后光标离开多久自动收回（可在「贴边隐藏设置」里调）。</summary>
    private static double EdgeRetractMs => Math.Clamp(App.Settings.EdgeRetractDelayMs, 0, 10000);
    private IntPtr _lastActiveFolder;
    private double _scale = 1.0;
    private FontFamily _fontFamily = new("Microsoft YaHei UI");

    /// <summary>当前主题：字体、字号、圆角、色值都从这一份里取。</summary>
    private ThemePalette _palette = ThemePalette.Resolve();
    private bool _lastSystemLight;

    /// <summary>
    /// "跟随系统"要真的跟：定时比对系统浅色/深色偏好，变了就整体换色。
    /// （之前只在启动时读一次，所以系统切换后毫无反应。）
    /// </summary>
    private void FollowSystemTheme()
    {
        if (App.Settings.Theme != DockTheme.Auto) return;

        bool light = Settings.IsSystemLightTheme();
        if (light == _lastSystemLight) return;

        _lastSystemLight = light;
        Host.PreviewTheme();
    }

    private Brush _backgroundBrush = Brushes.Transparent;
    private Brush _borderBrush = Brushes.Transparent;
    private Brush _textBrush = Brushes.White;
    private Brush _hoverBrush = Brushes.Transparent;
    private Brush _activeBrush = Brushes.Transparent;
    private Brush _activeBorderBrush = Brushes.Transparent;
    private Brush _chipBrush = Brushes.Transparent;
    private Brush _mutedBrush = Brushes.Gray;
    private Brush _onActiveTextBrush = Brushes.White;
    private Brush _onHoverTextBrush = Brushes.White;

    private static App Host => (App)Application.Current;

    public DockWindow()
    {
        InitializeComponent();

        MaxWidth = Math.Max(360, SystemParameters.WorkArea.Width * Math.Clamp(App.Settings.MaxWidthRatio, 0.3, 1.0));
        Opacity = Math.Clamp(App.Settings.Opacity, 0.4, 1.0);

        ApplyTheme();

        _itemsHost = new StackPanel
        {
            // 每个程序一行：行是横向的按钮条，行与行纵向堆叠
            Orientation = Orientation.Vertical,
        };

        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            CanContentScroll = false,
            Focusable = false,
            Content = _itemsHost,
        };

        _grip = CreateGrip();

        // 窗口宽度有上限，窗口多了就会被裁在右边。滚动条是隐藏的（外观干净），
        // 这里补一对左右箭头当可见的滚动入口，内容没溢出时它们自己收起来。
        _scrollLeft = CreateScrollButton("\u2039", -1, vertical: false);
        _scrollRight = CreateScrollButton("\u203A", 1, vertical: false);

        // 接管的程序多了行数会超出高度上限，再补一对上下箭头
        _scrollUp = CreateScrollButton("\u2303", -1, vertical: true);
        _scrollDown = CreateScrollButton("\u2304", 1, vertical: true);

        // 怎么摆（箭头在哪条边）由 ApplyLayoutOrientation 决定
        ApplyLayoutOrientation();

        _scroller.ScrollChanged += (_, _) => UpdateScrollButtons();
        _itemsHost.SizeChanged += (_, _) => QueueScrollButtonUpdate();

        PreviewMouseWheel += (_, e) =>
        {
            // 行多到装不下时优先纵向滚动；否则保持原来的横向滚动。
            // 一格滚轮 = 3 个按钮/3 行的尺寸。直接拿 e.Delta 当像素量的话一次只挪几十像素，
            // 窗口一多就感觉"滚不动"。
            if (_scroller.ScrollableHeight > 1)
            {
                ScrollByVertical(-Math.Sign(e.Delta) * StepHeight() * 3);
            }
            else
            {
                ScrollBy(-Math.Sign(e.Delta) * StepWidth() * 3);
            }

            e.Handled = true;
        };

        Loaded += OnLoaded;

        // 拖动悬停的延迟呼出（见 _dragHoverTimer 的注释）
        _dragHoverTimer.Tick += (_, _) => OnDragHoverTimerTick();

        // 贴边隐藏后靠光标位置把它召回来
        _edgeProbe.Tick += (_, _) => OnEdgeProbeTick();

        // 接收系统拖放：拖着文件悬停在某个按钮上时，把对应的文件夹窗口呼到前台
        // （和 Windows 任务栏一个行为）。松手不会替用户搬文件 ——
        // 接着把文件拖到刚呼出来的那个窗口里就行。
        AllowDrop = true;
        DragOver += OnDockDragOver;
        DragLeave += (_, _) => ResetDragHover();
        Drop += (_, e) =>
        {
            e.Handled = true;

            // 落在按钮上 = 把拖来的内容粘到那个窗口（多标签窗口还会先切到对应标签页）；
            // 落在把手/空白/箭头上没有目标，什么都不做 —— 悬停呼出那个行为照旧
            if (NativeMethods.GetCursorPos(out var point) &&
                TryGetItemAt(point.X, point.Y, out var handle, out var tabIndex) &&
                Host.WriteDroppedDataToClipboard(e.Data))
            {
                WindowPaste.Deliver(Host, handle, tabIndex);
            }

            ResetDragHover();
        };

        LocationChanged += (_, _) =>
        {
            // 拖动过程中就把窗口限制在屏幕内，免得被拖出屏幕或贴死在边缘
            if (_dragging) ClampToScreen();
            UpdateShadowBounds();

            // 贴边即判定：位置一变就重新判断靠没靠边（拖动中会被内部跳过，
            // 因为 DragMove() 自己也在改坐标，这时插动画会互相打架）
            UpdateEdgeState();
        };

        // 整条悬浮栏任意位置按住都能拖：先记下按下的位置，
        // 只有鼠标移动超过阈值时才升级成拖动 —— 直接调 DragMove() 会捕获鼠标，
        // 按钮的"松开"就收不到、点击会失效。
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_interactionLocked) return;
            _pressPoint = e.GetPosition(this);
        };

        PreviewMouseMove += (_, e) =>
        {
            if (_interactionLocked) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (_dragging) return;

            var current = e.GetPosition(this);
            if (Math.Abs(current.X - _pressPoint.X) < 4 && Math.Abs(current.Y - _pressPoint.Y) < 4) return;

            _dragging = true;

            // 拖之前先把前台抢回悬浮栏。
            //
            // 按住按钮时我们会"把前台还给点之前那个窗口"（见 RestoreForegroundOnPress），
            // 而系统的窗口拖动循环要求窗口是激活的 —— 不抢回来，拖动期间窗口根本不跟随鼠标，
            // 只有松手之后才"瞬间跳"到鼠标位置（用户报的就是这个现象）。
            // 把手没这问题，因为把手不还前台。
            try
            {
                NativeMethods.SetForegroundWindow(EnsureOurHandle());
            }
            catch
            {
                // 抢不回来也要继续拖，最多是手感差一点
            }

            try
            {
                DragMove();
            }
            catch
            {
                // 拖动被打断，忽略
            }

            FinishDrag();
        };

        // 拖动别的窗口时 Windows 会临时把被拖窗口提到最前，我们的置顶可能被挤掉；
        // 定时重新钉一遍，被遮住也能自己回来
        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(800),
        };
        _topmostTimer.Tick += (_, _) =>
        {
            // Alt+Tab 面板、剪贴板面板也开着的时候别去抢 Z 序：它们同样是置顶窗口，
            // 悬浮栏硬挤上来会盖住面板的一角
            if (Host.AltTabActive || Host.ClipboardActive || _menuOpen) return;

            EnsureTopmost();
            FollowSystemTheme();
        };

        MouseRightButtonUp += (_, e) =>
        {
            ShowMenu();
            e.Handled = true;
        };
    }

    /// <summary>
    /// 悬浮栏不进 ALT+TAB：加 WS_EX_TOOLWINDOW。
    /// 任务栏按钮本来就已经被 ShowInTaskbar=false 摘掉了，这里再把切换列表里的那一项也去掉。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        long style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(
            handle,
            NativeMethods.GWL_EXSTYLE,
            (style | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW);
    }

    // ---------- 外观 ----------

    private void ApplyTheme()
    {
        // 配色统一从 ThemePalette 取：剪贴板面板 / 菜单用的是同一份，
        // 改主题时不会出现"一边变了一边没变"
        var palette = ThemePalette.Resolve();

        _backgroundBrush = new SolidColorBrush(palette.Background);
        _borderBrush = new SolidColorBrush(palette.Border);
        _textBrush = new SolidColorBrush(palette.Text);
        _hoverBrush = new SolidColorBrush(palette.Hover);
        _activeBrush = new SolidColorBrush(palette.Active);
        _activeBorderBrush = palette.Custom ? Brushes.Transparent : new SolidColorBrush(palette.Border);
        _onActiveTextBrush = new SolidColorBrush(palette.ActiveText);
        _onHoverTextBrush = new SolidColorBrush(palette.HoverText);
        _chipBrush = new SolidColorBrush(palette.Chip);
        _mutedBrush = new SolidColorBrush(palette.Muted);

        RootBorder.BorderThickness = new Thickness(palette.Custom ? palette.CustomBorderThickness : palette.DockBorderThickness);

        // 字号 / 图标 / 间距等比缩放：直接给内容层套一个 LayoutTransform，
        // 边框画在外层 Border 上，所以线宽不会被这个缩放放大。
        _scale = Math.Clamp(App.Settings.Scale, 1.0, 3.0);
        if (RootPanel.LayoutTransform is not ScaleTransform transform ||
            Math.Abs(transform.ScaleX - _scale) > 0.001)
        {
            RootPanel.LayoutTransform = new ScaleTransform(_scale, _scale);
        }

        _palette = palette;
        _fontFamily = palette.Typeface;

        RootBorder.Background = _backgroundBrush;
        RootBorder.BorderBrush = _borderBrush;

        if (_grip is not null && _grip.Child is TextBlock dots) dots.Foreground = _mutedBrush;
        if (_scrollLeft?.Child is TextBlock leftArrow) leftArrow.Foreground = _mutedBrush;
        if (_scrollRight?.Child is TextBlock rightArrow) rightArrow.Foreground = _mutedBrush;
        if (_scrollUp?.Child is TextBlock upArrow) upArrow.Foreground = _mutedBrush;
        if (_scrollDown?.Child is TextBlock downArrow) downArrow.Foreground = _mutedBrush;

        UpdateScrollerMaxWidth();
    }

    /// <summary>把 #AARRGGBB 之类的字符串解析成刷子，解析失败就用兜底色。</summary>
    private static Brush ParseBrush(string? text, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text) && ColorConverter.ConvertFromString(text) is Color color)
            {
                return new SolidColorBrush(color);
            }
        }
        catch
        {
            // 用户输入了非法颜色，用兜底
        }

        return new SolidColorBrush(fallback);
    }

    /// <summary>主题切换后整体换色：按钮视觉持有刷子引用，所以直接重建一批。</summary>
    public void ApplyThemeAndRebuild()
    {
        ApplyTheme();

        // 按钮现在挂在"行"里，整行一起撤掉，再让 ApplySnapshot 按当前快照重建
        foreach (var key in _rowOrder)
        {
            if (_rows.TryGetValue(key, out var row)) _itemsHost.Children.Remove(row);
        }

        _rows.Clear();
        _rowOrder.Clear();
        _items.Clear();

        if (_shadow is not null)
        {
            var borderColor = (_borderBrush as SolidColorBrush)?.Color ?? Color.FromArgb(0xFF, 0x24, 0x24, 0x24);
            _shadow.SetTone(
                Color.FromArgb((_backgroundBrush as SolidColorBrush)?.Color.A ?? (byte)0xFF, borderColor.R, borderColor.G, borderColor.B),
                RootBorder.CornerRadius.TopLeft,
                App.Settings.ResolveLightTheme());
            _shadow.SetInset(0);
        }

        if (_lastSnapshot is not null) ApplySnapshot(_lastSnapshot);
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _shadow?.Close();
        }
        catch
        {
            // 忽略
        }

        base.OnClosed(e);
    }

    private static bool IsLightTheme_deprecated() => false;

    /// <summary>
    /// 给滚动区一个明确的最大宽度 —— 这是"窗口一多就滚不动"的正解。
    /// 只靠 Window.MaxWidth 不行：SizeToContent 会把无限约束一路传下来，
    /// ScrollViewer 以为自己有无限空间，ScrollableWidth 恒为 0，
    /// 于是多出来的按钮被窗口直接裁掉、滚轮怎么滚都没反应。
    /// 这里把可用宽度换算成 RootPanel 内部坐标（要除以 LayoutTransform 的倍率），
    /// 再扣掉把手和两个箭头，剩下的才是滚动区该有的上限。
    /// </summary>
    private void UpdateScrollerMaxWidth()
    {
        if (_scroller is null) return;

        double chrome = RootBorder.BorderThickness.Left + RootBorder.BorderThickness.Right
                      + RootBorder.Padding.Left + RootBorder.Padding.Right;

        if (StackMode)
        {
            // 堆叠模式：内容就是一列按钮，滚动区按行宽算就行。
            // 按"窗口最大宽度"算的话右边会空出一大块（用户报的"不显示完整标题时右边很宽"）。
            _scroller.MaxWidth = Math.Max(120, StackMaxWidth);
        }
        else
        {
            double room = (MaxWidth - chrome) / Math.Max(1.0, _scale);
            room -= (_scrollLeft?.Width ?? 0) + (_scrollRight?.Width ?? 0);

            _scroller.MaxWidth = Math.Max(120, room);
        }

        // 高度上限：横幅模式约屏幕一半；堆叠模式给到四分之三（一列能摆下更多窗口才滚动）。
        // SizeToContent=WidthAndHeight 下不显式给 MaxHeight 就没有可滚空间，行一多会被窗口直接裁掉。
        double verticalChrome = RootBorder.BorderThickness.Top + RootBorder.BorderThickness.Bottom
                              + RootBorder.Padding.Top + RootBorder.Padding.Bottom;

        double ratio = StackMode ? 0.75 : 0.5;

        double tall = (SystemParameters.WorkArea.Height * ratio - verticalChrome) / Math.Max(1.0, _scale);

        _scroller.MaxHeight = Math.Max(80, tall);
    }

    /// <summary>溢出时才出现的滚动箭头，配色跟着主题走。vertical = 上下箭头。</summary>
    private Border CreateScrollButton(string glyph, int direction, bool vertical)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily(_palette.IconFontFamily),
            FontSize = _palette.IconFontSize,
            Foreground = _mutedBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var border = new Border
        {
            Width = 16,
            CornerRadius = new CornerRadius(7),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = text,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            ToolTip = vertical
                ? (direction < 0 ? "往上翻" : "往下翻")
                : (direction < 0 ? "往前翻" : "往后翻"),
        };

        border.MouseEnter += (_, _) => text.Foreground = _textBrush;
        border.MouseLeave += (_, _) => text.Foreground = _mutedBrush;
        border.MouseLeftButtonUp += (_, e) =>
        {
            if (vertical) ScrollByVertical(direction * StepHeight() * 3);
            else ScrollBy(direction * StepWidth() * 3);

            e.Handled = true;
        };

        return border;
    }

    private void UpdateScrollButtons()
    {
        if (_scrollLeft is null || _scrollRight is null) return;

        double max = _scroller.ScrollableWidth;
        double offset = _scroller.HorizontalOffset;

        // ScrollableWidth 偶尔比实际布局慢一拍，再用内容宽度兜一次底
        if (max <= 1 && _itemsHost.ActualWidth > _scroller.ActualWidth + 1)
        {
            max = _itemsHost.ActualWidth - _scroller.ActualWidth;
        }

        bool scrollable = max > 1;

        _scrollLeft.Visibility = scrollable && offset > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        _scrollRight.Visibility = scrollable && offset < max - 0.5 ? Visibility.Visible : Visibility.Collapsed;

        if (_scrollUp is null || _scrollDown is null) return;

        double maxVertical = _scroller.ScrollableHeight;
        double offsetVertical = _scroller.VerticalOffset;

        if (maxVertical <= 1 && _itemsHost.ActualHeight > _scroller.ActualHeight + 1)
        {
            maxVertical = _itemsHost.ActualHeight - _scroller.ActualHeight;
        }

        bool scrollableVertical = maxVertical > 1;

        _scrollUp.Visibility = scrollableVertical && offsetVertical > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        _scrollDown.Visibility = scrollableVertical && offsetVertical < maxVertical - 0.5 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScrollBy(double delta)
    {
        double max = Math.Max(0, _scroller.ScrollableWidth);
        _scroller.ScrollToHorizontalOffset(Math.Clamp(_scroller.HorizontalOffset + delta, 0, max));
        UpdateScrollButtons();
    }

    private void ScrollByVertical(double delta)
    {
        double max = Math.Max(0, _scroller.ScrollableHeight);
        _scroller.ScrollToVerticalOffset(Math.Clamp(_scroller.VerticalOffset + delta, 0, max));
        UpdateScrollButtons();
    }

    /// <summary>一个按钮连左右间距占多宽 —— 横向滚动步长按它的整数倍走，不会停在半个按钮上。</summary>
    private double StepWidth()
    {
        if (_itemsHost.Children.Count > 0 &&
            _itemsHost.Children[0] is Panel row &&
            row.Children.Count > 0 &&
            row.Children[0] is FrameworkElement first &&
            first.ActualWidth > 1)
        {
            return first.ActualWidth + first.Margin.Left + first.Margin.Right;
        }

        return 120;
    }

    /// <summary>一行连上下间距占多高 —— 纵向滚动步长按它的整数倍走。</summary>
    private double StepHeight()
    {
        if (_itemsHost.Children.Count > 0 &&
            _itemsHost.Children[0] is FrameworkElement row &&
            row.ActualHeight > 1)
        {
            return row.ActualHeight + row.Margin.Top + row.Margin.Bottom;
        }

        return 40;
    }

    private Border CreateGrip()
    {
        var dots = new TextBlock
        {
            Text = "\u283F",
            FontFamily = new FontFamily(_palette.IconFontFamily),
            FontSize = _palette.IconFontSize,
            Foreground = _mutedBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,

            // 旋转中心放在自身中心，堆叠模式下转 90° 才不会歪出去
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        _gripDots = dots;

        var grip = new Border
        {
            Width = 22,
            CornerRadius = new CornerRadius(7),
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            Child = dots,
            ToolTip = "拖动移动位置　·　右键打开菜单　·　双击隐藏",
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        grip.MouseLeftButtonDown += (_, e) =>
        {
            if (_interactionLocked) return;

            if (e.ClickCount == 2)
            {
                Host.SetShowDock(false);
                e.Handled = true;
                return;
            }

            _dragging = true;

            try
            {
                DragMove();
            }
            catch
            {
                // 拖动被打断，忽略
            }

            _dragging = false;

            FinishDrag();
        };

        grip.MouseEnter += (_, _) => grip.Background = _hoverBrush;
        grip.MouseLeave += (_, _) => grip.Background = Brushes.Transparent;
        grip.MouseRightButtonUp += (_, e) =>
        {
            ShowMenu();
            e.Handled = true;
        };

        return grip;
    }

    // ---------- 位置 ----------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Diag($"loaded: saved=({App.Settings.DockLeft},{App.Settings.DockTop}) actual=({ActualWidth}x{ActualHeight})");

        if (App.Settings.DockLeft is double left && App.Settings.DockTop is double top && IsOnScreen(left, top))
        {
            Left = left;
            Top = top;
            _autoCenter = false;
        }
        else
        {
            _autoCenter = true;
            MoveToDefaultPosition();
        }

        EnsureShadow();
        _lastSystemLight = Settings.IsSystemLightTheme();
        _topmostTimer.Start();
        _edgeProbe.Start();

        // 布局完成之前 ScrollableWidth 还是 0，箭头显隐要等这一轮布局走完再算
        QueueScrollButtonUpdate();

        // 等布局算出真实尺寸后再判定位置：位置合格（含"程序自己摆的位置"）就收纳
        Dispatcher.BeginInvoke(new Action(() => RestoreEdgeStateWithRetry(0)), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 恢复收纳状态前先等尺寸出來。
    ///
    /// SizeToContent 的窗口在 Loaded 之后尺寸还可能短暂为 0，这时算"屏内留 N 像素"
    /// 会算出个错位置（甚至留在屏幕里），所以拿不到尺寸就下一轮再试。
    /// </summary>
    private void RestoreEdgeStateWithRetry(int attempt)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            if (attempt < 10)
            {
                Dispatcher.BeginInvoke(new Action(() => RestoreEdgeStateWithRetry(attempt + 1)), DispatcherPriority.Background);
            }

            return;
        }

        RestoreEdgeState();
        UpdateEdgeState();
    }

    /// <summary>
    /// 箭头显隐取决于 ScrollableWidth，而这个值是在布局过程中才算出来的，
    /// 所以统一延后到后台优先级再读，免得拿到上一轮的旧值。
    /// </summary>
    private void QueueScrollButtonUpdate()
    {
        Dispatcher.BeginInvoke(new Action(UpdateScrollButtons), DispatcherPriority.Background);
    }

    /// <summary>阴影由独立窗口绘制：它鼠标穿透，所以阴影区不会吃掉点击。</summary>
    private void EnsureShadow()
    {
        if (_shadow is null)
        {
            _shadow = new ShadowWindow();
            var borderColor = (_borderBrush as SolidColorBrush)?.Color ?? Color.FromArgb(0xFF, 0x24, 0x24, 0x24);
            _shadow.SetTone(
                Color.FromArgb((_backgroundBrush as SolidColorBrush)?.Color.A ?? (byte)0xFF, borderColor.R, borderColor.G, borderColor.B),
                RootBorder.CornerRadius.TopLeft,
                App.Settings.ResolveLightTheme());
            _shadow.SetInset(0);
            _shadow.Show();
        }

        UpdateShadowBounds();
    }

    /// <summary>
    /// 重新排一次层级：阴影窗口必须压在悬浮栏下面。
    /// Show() 之后 z 序可能反转，不排一次就会看到整条变成一块纯色（那是垫底的阴影矩形）。
    /// </summary>
    public void ReorderLayers()
    {
        EnsureShadow();
        UpdateShadowBounds();
        UpdateShadowBounds();
    }

    private void UpdateShadowBounds()
    {
        if (_shadow is null) return;

        if (!IsVisible || ActualWidth <= 0 || ActualHeight <= 0)
        {
            if (_shadow.IsVisible) _shadow.Hide();
            return;
        }

        if (!_shadow.IsVisible) _shadow.Show();

        _shadow.Left = Left - ShadowWindow.ShadowMargin;
        _shadow.Top = Top - ShadowWindow.ShadowMargin;
        _shadow.Width = ActualWidth + (ShadowWindow.ShadowMargin * 2);
        _shadow.Height = ActualHeight + (ShadowWindow.ShadowMargin * 2);

        // 始终把阴影窗口压在悬浮栏下面，免得盖住它
        var dockHandle = EnsureOurHandle();
        var shadowHandle = new WindowInteropHelper(_shadow).Handle;

        if (dockHandle != IntPtr.Zero && shadowHandle != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(
                shadowHandle,
                dockHandle,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    /// <summary>
    /// 把悬浮栏重新钉回最上层，并保持阴影层压在它下面。
    /// 只在"命中测试发现真的被压在下面"时才动手：光看 WS_EX_TOPMOST 样式不够，
    /// 因为别的置顶窗口（照片、播放器等）会把它挤到 Topmost 组内的后面，
    /// 那时样式还在，但点上去的其实是别人。
    /// </summary>
    private void EnsureTopmost()
    {
        if (!IsVisible) return;

        var dockHandle = EnsureOurHandle();
        if (dockHandle == IntPtr.Zero) return;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var shadowHandle = ShadowHandle();

        var probe = new NativeMethods.POINT
        {
            X = (int)Math.Round(Left + (ActualWidth / 2)),
            Y = (int)Math.Round(Top + (ActualHeight / 2)),
        };

        var top = NativeMethods.WindowFromPoint(probe);
        bool covered = top != IntPtr.Zero && top != dockHandle && top != shadowHandle;

        if (covered && (DateTime.UtcNow - _lastTopmostFix).TotalSeconds > 1.5)
        {
            _lastTopmostFix = DateTime.UtcNow;

            // 先落到普通层再重新置顶，才能挤进 Topmost 组的最前面（只改样式是插不了队的）
            NativeMethods.SetWindowPos(dockHandle, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            NativeMethods.SetWindowPos(dockHandle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            NativeMethods.SetWindowPos(shadowHandle, dockHandle, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

            Diag($"topmost: covered by 0x{top.ToInt64():X}, re-pinned");
            return;
        }

        // 没被盖住时只需保证阴影层还在自己下面
        if (shadowHandle != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(shadowHandle, dockHandle, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    private IntPtr ShadowHandle()
    {
        if (_shadow is null) return IntPtr.Zero;
        return new WindowInteropHelper(_shadow).Handle;
    }

    /// <summary>
    /// 把悬浮栏限制在屏幕范围内：四周都保加强制间距，
    /// 既不能被拖出屏幕被裁掉，也不能贴死在屏幕边缘上。
    /// </summary>
    private void ClampToScreen()
    {
        const double margin = 10;

        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;

        double minLeft = vLeft + margin;
        double minTop = vTop + margin;
        double maxLeft = vRight - ActualWidth - margin;
        double maxTop = vBottom - ActualHeight - margin;

        if (maxLeft < minLeft) maxLeft = minLeft;
        if (maxTop < minTop) maxTop = minTop;

        double left = Math.Clamp(Left, minLeft, maxLeft);
        double top = Math.Clamp(Top, minTop, maxTop);

        if (Math.Abs(left - Left) > 0.1) Left = left;
        if (Math.Abs(top - Top) > 0.1) Top = top;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);

        // 用户没手动挪过位置时，宽度变化后保持在底部居中
        // （已经收纳到屏外时不要摆回来，否则会闪一下再被挪出去）
        if (_autoCenter && !_edgeHidden) MoveToDefaultPosition();

        UpdateShadowBounds();

        // 收纳状态下尺寸变了（多了几行、滚动高度变化）：按新尺寸重算屏外位置，
        // 保证留在屏内的宽度还是设置的那个值
        if (_edgeHidden && _edgeSide != DockEdge.None)
        {
            ComputeEdgeHiddenPosition(_edgeSide, out var hiddenLeft, out var hiddenTop);

            _edgeSelfMove = true;
            Left = hiddenLeft;
            Top = hiddenTop;
            _edgeSelfMove = false;

            UpdateShadowBounds();
        }
    }

    private static bool IsOnScreen(double left, double top)
    {
        return left >= SystemParameters.VirtualScreenLeft - 40
            && top >= SystemParameters.VirtualScreenTop - 40
            && left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
            && top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 20;
    }

    private void MoveToDefaultPosition()
    {
        var area = SystemParameters.WorkArea;

        Left = area.Left + Math.Max(0, (area.Width - ActualWidth) / 2);

        // 不贴屏幕最上沿：贴边自动隐藏的判定阈值是 12px，落到 6px 会一启动就被收纳起来。
        // 留 28px —— 还是在上边，但稳稳在阈值之外。
        Top = area.Top + 28;

        UpdateTooltipPlacement();
    }

    // ---------- 贴边自动隐藏 ----------

    /// <summary>贴边判定与收纳所依据的最小间距：与 ClampToScreen 保持一致。</summary>
    private const double EdgeSnapThreshold = 12;

    private bool EdgeHideEnabled => App.Settings.EdgeAutoHide && App.Settings.ShowDock;

    /// <summary>拖动结束的收尾：两处拖动（把手、按钮）都用这一份，顺带做一次贴边判定。</summary>
    private void FinishDrag()
    {
        _dragging = false;
        _autoCenter = false;

        ClampToScreen();
        SavePosition();
        UpdateEdgeState();
    }

    /// <summary>
    /// 贴边即判定：只要悬浮栏现在的位置贴住了某条屏幕外沿，就算贴边成功 ——
    /// 不限于"用户手拖过去"，程序自己摆到那儿（启动默认位置、回到顶部居中）同样算。
    ///
    /// 调用点：位置变化时（LocationChanged）与拖动结束。
    /// 拖动**过程中**会被跳过 —— DragMove() 自己也在写 Left/Top，这时插动画两边会打架。
    /// </summary>
    private void UpdateEdgeState()
    {
        if (_edgeSelfMove) return;

        if (!EdgeHideEnabled || !IsVisible)
        {
            if (!EdgeHideEnabled) ClearEdgeState();
            return;
        }

        if (_dragging || _edgeAnimating) return;

        var side = DetectEdge();

        if (side == DockEdge.None)
        {
            // 从贴边位置拖走了：解除收纳状态，窗口现在就在屏幕内，不用再挪
            if (_edgeSide != DockEdge.None)
            {
                StopEdgeAnimation();
                _edgeSide = DockEdge.None;
                _edgeHidden = false;
                _edgeLeaveAt = DateTime.MinValue;

                if (App.Settings.EdgeHiddenSide != DockEdge.None)
                {
                    App.Settings.EdgeHiddenSide = DockEdge.None;
                    App.Settings.Save();
                }
            }

            return;
        }

        _edgeSide = side;
        _edgeLeaveAt = DateTime.MinValue;

        if (_edgeHidden) return;

        HideToEdge(animated: true);
    }

    /// <summary>
    /// 悬浮栏贴住的是哪条屏幕边：距离那条边 ≤ 12 DIP 就算贴住了。
    /// 只认整块虚拟桌面的外沿 —— 两块屏幕之间的交界不算，
    /// 否则贴到交界处会"藏到另一块屏幕"上，照样看得见。
    /// </summary>
    private DockEdge DetectEdge()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return DockEdge.None;

        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;

        var best = DockEdge.None;
        double bestDistance = EdgeSnapThreshold + 1;

        void Consider(DockEdge side, double distance)
        {
            if (distance > EdgeSnapThreshold) return;
            if (distance >= bestDistance) return;

            best = side;
            bestDistance = distance;
        }

        Consider(DockEdge.Left, Left - vLeft);
        Consider(DockEdge.Right, vRight - (Left + ActualWidth));
        Consider(DockEdge.Top, Top - vTop);
        Consider(DockEdge.Bottom, vBottom - (Top + ActualHeight));

        return best;
    }

    /// <summary>
    /// 收纳位置：沿贴边那一侧移出屏幕外，屏内留 <see cref="Settings.EdgePeekWidth"/> 那一段。
    ///
    /// 判定成立时**不**把窗口吸到边缘上，仍然用现在的最小间距（ClampToScreen 的 10px），
    /// 所以这里是"从当前位置再往外挪"。
    /// </summary>
    private void ComputeEdgeHiddenPosition(DockEdge side, out double left, out double top)
    {
        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;

        double peek = Math.Clamp(App.Settings.EdgePeekWidth, 0, 64);
        double width = Math.Max(0, ActualWidth);
        double height = Math.Max(0, ActualHeight);

        left = _edgeDockLeft;
        top = _edgeDockTop;

        switch (side)
        {
            case DockEdge.Left:
                left = vLeft + peek - width;
                break;

            case DockEdge.Right:
                left = vRight - peek;
                break;

            case DockEdge.Top:
                top = vTop + peek - height;
                break;

            case DockEdge.Bottom:
                top = vBottom - peek;
                break;
        }
    }

    /// <summary>贴边判定成立后挪出去。</summary>
    private void HideToEdge(bool animated)
    {
        if (_edgeSide == DockEdge.None) return;

        if (!_edgeHidden)
        {
            // 记住屏幕内的原位，呼出时要回到这儿
            _edgeDockLeft = Left;
            _edgeDockTop = Top;
            _edgeHidden = true;

            App.Settings.EdgeHiddenSide = _edgeSide;
            App.Settings.Save();
        }

        _edgeLeaveAt = DateTime.MinValue;

        ComputeEdgeHiddenPosition(_edgeSide, out var left, out var top);
        AnimateEdgeTo(left, top, animated, hiding: true);
    }

    /// <summary>鼠标碰到那条屏幕边，把悬浮栏放回屏幕内。</summary>
    private void ShowFromEdge(bool animated)
    {
        if (_edgeSide == DockEdge.None || !_edgeHidden) return;

        _edgeHidden = false;
        _edgeLeaveAt = DateTime.MinValue;

        AnimateEdgeTo(_edgeDockLeft, _edgeDockTop, animated, hiding: false);
    }

    /// <summary>解除贴边状态（关设置、回到顶部居中等），必要时先无动画地放回屏幕内。</summary>
    public void ClearEdgeState()
    {
        StopEdgeAnimation();

        if (_edgeHidden)
        {
            _edgeSelfMove = true;
            Left = _edgeDockLeft;
            Top = _edgeDockTop;
            _edgeSelfMove = false;

            UpdateShadowBounds();
        }

        _edgeHidden = false;
        _edgeSide = DockEdge.None;
        _edgeLeaveAt = DateTime.MinValue;

        if (App.Settings.EdgeHiddenSide != DockEdge.None)
        {
            App.Settings.EdgeHiddenSide = DockEdge.None;
            App.Settings.Save();
        }
    }

    /// <summary>设置窗口改了残余宽度/触发带宽后立刻生效。</summary>
    public void ApplyEdgeHideSettings()
    {
        if (!App.Settings.EdgeAutoHide)
        {
            ClearEdgeState();
            return;
        }

        if (_edgeSide == DockEdge.None) return;

        // 正在收纳状态：按新的残余宽度重新算一次屏外位置
        if (_edgeHidden)
        {
            ComputeEdgeHiddenPosition(_edgeSide, out var left, out var top);

            _edgeSelfMove = true;
            Left = left;
            Top = top;
            _edgeSelfMove = false;

            UpdateShadowBounds();
        }
    }

    /// <summary>启动时恢复上次的贴边收纳状态（布局完成后再调用，否则拿不到真实尺寸）。</summary>
    private void RestoreEdgeState()
    {
        if (!EdgeHideEnabled)
        {
            ClearEdgeState();
            return;
        }

        var side = App.Settings.EdgeHiddenSide;
        if (side == DockEdge.None) return;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        // 屏幕内的原位就是上次保存的位置
        _edgeDockLeft = App.Settings.DockLeft ?? Left;
        _edgeDockTop = App.Settings.DockTop ?? Top;

        _edgeSide = side;
        _edgeHidden = true;

        ComputeEdgeHiddenPosition(side, out var left, out var top);

        _edgeSelfMove = true;
        Left = left;
        Top = top;
        _edgeSelfMove = false;

        UpdateShadowBounds();

        Diag($"edge-restore: side={side} size=({ActualWidth:0}x{ActualHeight:0}) dock=({_edgeDockLeft:0},{_edgeDockTop:0}) -> ({left:0},{top:0})");
    }

    /// <summary>鼠标探测：收纳状态看要不要呼出，显示状态看要不要自动收回。</summary>
    private void OnEdgeProbeTick()
    {
        if (!EdgeHideEnabled || !IsVisible) return;
        if (_edgeSide == DockEdge.None) return;
        if (_edgeAnimating || _dragging || _menuOpen) return;

        if (CursorInDip() is not { } cursor) return;

        if (_edgeHidden)
        {
            if (CursorInEdgeBand(cursor)) ShowFromEdge(animated: true);
            return;
        }

        // 光标还在悬浮栏上、或者还贴在触发带里（鼠标就放在屏幕边上没动）：
        // 都算"还在用"，别急着收回 —— 否则贴着边缘不动会一直滑出又收回
        if (CursorInsideDock(cursor, 4) || CursorInEdgeBand(cursor))
        {
            _edgeLeaveAt = DateTime.MinValue;
            return;
        }

        // 鼠标走开了：等一小会儿再收回，免得从按钮间划过就哗啦收走
        if (_edgeLeaveAt == DateTime.MinValue)
        {
            _edgeLeaveAt = DateTime.UtcNow;
            return;
        }

        if ((DateTime.UtcNow - _edgeLeaveAt).TotalMilliseconds >= EdgeRetractMs)
        {
            HideToEdge(animated: true);
        }
    }

    /// <summary>
    /// 光标在屏幕上的逻辑坐标。
    ///
    /// 用窗口自己换算（PointFromScreen + Left/Top）而不是去查系统 DPI：
    /// 项目里其它按屏幕坐标做的命中判定也是这个路子，缩放到 125%/150% 一样准。
    /// </summary>
    private Point? CursorInDip()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var point)) return null;

            var local = PointFromScreen(new Point(point.X, point.Y));
            return new Point(Left + local.X, Top + local.Y);
        }
        catch
        {
            // 窗口句柄还没建好之类，这一轮跳过
            return null;
        }
    }

    /// <summary>光标是否落在贴边那条边的触发带里（触发带宽可设置）。</summary>
    private bool CursorInEdgeBand(Point cursor)
    {
        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;

        double band = Math.Clamp(App.Settings.EdgeTriggerWidth, 0, 64);
        const double slack = 8;

        bool inVerticalSpan = cursor.Y >= Top - slack && cursor.Y <= Top + ActualHeight + slack;
        bool inHorizontalSpan = cursor.X >= Left - slack && cursor.X <= Left + ActualWidth + slack;

        return _edgeSide switch
        {
            DockEdge.Left => cursor.X <= vLeft + band && inVerticalSpan,
            DockEdge.Right => cursor.X >= vRight - band && inVerticalSpan,
            DockEdge.Top => cursor.Y <= vTop + band && inHorizontalSpan,
            DockEdge.Bottom => cursor.Y >= vBottom - band && inHorizontalSpan,
            _ => false,
        };
    }

    private bool CursorInsideDock(Point cursor, double tolerance)
    {
        return cursor.X >= Left - tolerance
            && cursor.X <= Left + ActualWidth + tolerance
            && cursor.Y >= Top - tolerance
            && cursor.Y <= Top + ActualHeight + tolerance;
    }

    private void AnimateEdgeTo(double left, double top, bool animated, bool hiding)
    {
        StopEdgeAnimation();

        if (!animated)
        {
            _edgeSelfMove = true;
            Left = left;
            Top = top;
            _edgeSelfMove = false;

            UpdateShadowBounds();
            return;
        }

        _edgeFromLeft = Left;
        _edgeFromTop = Top;
        _edgeToLeft = left;
        _edgeToTop = top;
        _edgeAnimStart = DateTime.UtcNow;
        _edgeAnimHiding = hiding;
        _edgeAnimating = true;

        // 动画期间自己改坐标，别让判定又跑一轮（否则刚滑出来就被收回去）
        _edgeSelfMove = true;
        CompositionTarget.Rendering += EdgeAnimRendering;
    }

    /// <summary>
    /// 逐帧插值移动窗口。
    ///
    /// 为什么不用 DoubleAnimation：阴影是**另一个窗口**（ShadowWindow），
    /// 动画驱动 Left/Top 时它不会自己跟，还得在每帧回调里补一次 UpdateShadowBounds；
    /// 那还不如自己插值，顺带随时可中断、不用管 WPF 动画的 FillBehavior。
    /// </summary>
    private void EdgeAnimRendering(object? sender, EventArgs e)
    {
        double progress = (DateTime.UtcNow - _edgeAnimStart).TotalMilliseconds / EdgeAnimMs;
        if (progress > 1) progress = 1;

        double eased = _edgeAnimHiding ? EaseOutCubic(progress) : EaseInOutCubic(progress);

        Left = _edgeFromLeft + ((_edgeToLeft - _edgeFromLeft) * eased);
        Top = _edgeFromTop + ((_edgeToTop - _edgeFromTop) * eased);
        UpdateShadowBounds();

        if (progress < 1) return;

        CompositionTarget.Rendering -= EdgeAnimRendering;
        _edgeAnimating = false;

        // 落定终值，避免逐帧插值的累积误差
        Left = _edgeToLeft;
        Top = _edgeToTop;
        UpdateShadowBounds();

        // 位置落定之后才放开判定
        _edgeSelfMove = false;
    }

    private static double EaseOutCubic(double t)
    {
        var inv = 1 - t;
        return 1 - (inv * inv * inv);
    }

    private static double EaseInOutCubic(double t)
    {
        return t < 0.5
            ? 4 * t * t * t
            : 1 - (Math.Pow((-2 * t) + 2, 3) / 2);
    }

    private void StopEdgeAnimation()
    {
        if (_edgeAnimating)
        {
            CompositionTarget.Rendering -= EdgeAnimRendering;
            _edgeAnimating = false;
        }

        _edgeSelfMove = false;
    }

    /// <summary>贴在屏幕上半边时，气泡往下弹，免得顶出屏幕。</summary>
    private void UpdateTooltipPlacement()
    {
        var middle = SystemParameters.VirtualScreenTop + (SystemParameters.VirtualScreenHeight / 2);
        _tooltipPlacement = Top < middle ? PlacementMode.Bottom : PlacementMode.Top;

        foreach (var visual in _items.Values)
        {
            ToolTipService.SetPlacement(visual.Container, _tooltipPlacement);
        }
    }

    private static int _diagCount;

    private static void Diag(string message)
    {
        // 只留少量定位日志，便于排查"悬浮栏跑到屏幕外"这类问题
        if (Interlocked.Increment(ref _diagCount) > 40) return;

        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "ExplorerDock.dock.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败无所谓
        }
    }

    private void SavePosition()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;
        App.Settings.DockLeft = Left;
        App.Settings.DockTop = Top;
        App.Settings.Save();
        UpdateTooltipPlacement();
    }

    /// <summary>丢掉记住的位置，回到屏幕顶部居中。</summary>
    public void ResetPosition()
    {
        // 收纳状态也要一并解除，否则摆回顶部后又被贴边逻辑收走
        ClearEdgeState();

        App.Settings.DockLeft = null;
        App.Settings.DockTop = null;
        App.Settings.Save();
        _autoCenter = true;
        MoveToDefaultPosition();
    }

    // ---------- 数据刷新 ----------

    /// <summary>按钮归哪一行：按程序名（同名程序的窗口同一行）。</summary>
    private static string RowKey(ExplorerWindowInfo info)
        => info.ProcessName.Length > 0 ? info.ProcessName : "explorer";

    /// <summary>取某个程序的行；没有就新建 —— 行的顺序 = 程序第一次出现的顺序。</summary>
    /// <summary>堆叠模式：所有窗口按钮竖着一列排，不同程序之间夹分隔线。</summary>
    private bool StackMode => App.Settings.Layout == DockLayout.Stack;

    /// <summary>
    /// 堆叠模式的**最高宽度**。
    ///
    /// 列宽本身是动态的（按标题文字自己撑开，标题短就窄），但再长也不超过这个宽度 ——
    /// 不然一条长路径/浏览器标签就把整列拉满屏幕。
    /// </summary>
    private const double StackMaxWidth = 260;

    /// <summary>标题的最大宽度 = 最高宽度扣掉图标(18)、间距(9)、左右内边距(18) 和边框余量。</summary>
    private const double StackTitleWidth = 260 - 18 - 9 - 18 - 6;

    /// <summary>分隔线的标记：重排时靠它把旧的分隔线挑出来清掉。</summary>
    private const string RowSeparatorTag = "row-separator";

    private StackPanel EnsureRow(string rowKey)
    {
        if (_rows.TryGetValue(rowKey, out var existing)) return existing;

        var row = new StackPanel
        {
            // 横幅：这个程序的窗口横着排一行；堆叠：竖着往下摞
            Orientation = StackMode ? Orientation.Vertical : Orientation.Horizontal,
        };

        // 堆叠模式不给行设固定宽度：列宽由标题文字自己撑开（长的宽、短的窄），
        // 上限由 _scroller.MaxWidth 兜着。

        _rows[rowKey] = row;
        _rowOrder.Add(rowKey);
        _itemsHost.Children.Add(row);

        UpdateRowSeparators();

        return row;
    }

    /// <summary>
    /// 堆叠模式下在**不同程序之间**夹一条分隔线。
    /// 同一个程序的多个窗口是同一个行容器里的兄弟，挨着排，不画线。
    /// </summary>
    private void UpdateRowSeparators()
    {
        for (int i = _itemsHost.Children.Count - 1; i >= 0; i--)
        {
            if (_itemsHost.Children[i] is Border { Tag: RowSeparatorTag })
            {
                _itemsHost.Children.RemoveAt(i);
            }
        }

        // 两种模式都要：不同程序之间画一条线（同一程序的多个窗口挨着，不画）
        bool first = true;

        foreach (var key in _rowOrder.ToList())
        {
            if (!_rows.TryGetValue(key, out var row)) continue;
            if (row.Children.Count == 0) continue;

            if (!first)
            {
                var index = _itemsHost.Children.IndexOf(row);
                if (index >= 0) _itemsHost.Children.Insert(index, CreateRowSeparator());
            }

            first = false;
        }
    }

    private Border CreateRowSeparator() => new()
    {
        Height = 1,
        Margin = new Thickness(10, 3, 10, 3),
        Background = new SolidColorBrush(_palette.Separator),
        Tag = RowSeparatorTag,
    };

    /// <summary>切换布局模式后重建一遍：行容器的方向变了，旧的行不能接着用。</summary>
    public void RebuildLayout()
    {
        _itemsHost.Children.Clear();
        _rows.Clear();
        _rowOrder.Clear();
        _items.Clear();

        ApplyLayoutOrientation();
        QueueScrollButtonUpdate();

        if (_lastSnapshot is not null) ApplySnapshot(_lastSnapshot);
        else RefreshVisibility();
    }

    /// <summary>
    /// 按布局模式摆内容（**把手整个去掉了**：整条任意位置都能按住拖、都能右键出菜单，用不着它）：
    /// 横幅 = 左右箭头分列两侧 + 滚动区；堆叠 = 上下箭头分列顶部/底部 + 滚动区。
    ///
    /// 箭头只在"那边还能滚"的时候才显示（显隐由 UpdateScrollButtons 管），
    /// 所以顶部那个一定是向上、底部那个一定是向下。
    /// </summary>
    private void ApplyLayoutOrientation()
    {
        RootPanel.Children.Clear();

        if (StackMode)
        {
            RootPanel.Orientation = Orientation.Vertical;

            LayoutArrow(_scrollUp, horizontal: true);
            LayoutArrow(_scrollDown, horizontal: true);

            RootPanel.Children.Add(_scrollUp);
            RootPanel.Children.Add(_scroller);
            RootPanel.Children.Add(_scrollDown);
        }
        else
        {
            RootPanel.Orientation = Orientation.Horizontal;

            LayoutArrow(_scrollLeft, horizontal: false);
            LayoutArrow(_scrollRight, horizontal: false);

            RootPanel.Children.Add(_scrollLeft);
            RootPanel.Children.Add(_scroller);
            RootPanel.Children.Add(_scrollRight);
        }

        UpdateScrollerMaxWidth();
    }

    /// <summary>滚动箭头的外形：左右箭头是窄竖条，上下箭头是扁横条。</summary>
    private static void LayoutArrow(Border? arrow, bool horizontal)
    {
        if (arrow is null) return;

        if (horizontal)
        {
            arrow.Width = double.NaN;
            arrow.Height = 16;
            arrow.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            arrow.Width = 16;
            arrow.Height = double.NaN;
            arrow.HorizontalAlignment = HorizontalAlignment.Center;
        }
    }

    /// <summary>
    /// 按快照顺序重排行的物理位置。
    ///
    /// 行只在"第一次出现"时被插进面板，之后不重排的话，先出现的程序就会一直压在别人上面 ——
    /// 表现就是"行顺序飘来飘去、跟规则对不上"。快照里资源管理器在最前、其余按进程名升序，
    /// 这里把面板里的顺序也拉成同一个顺序。
    /// </summary>
    private void ApplyRowOrder(List<string> sequence)
    {
        var desired = new List<string>();

        foreach (var key in sequence)
        {
            if (_rows.ContainsKey(key)) desired.Add(key);
        }

        // 快照里没有、字典里却还留着的行（正常已被 RemoveEmptyRows 清掉）：排在最后
        foreach (var key in _rowOrder)
        {
            if (!desired.Contains(key, StringComparer.OrdinalIgnoreCase)) desired.Add(key);
        }

        // 只比"行"：堆叠模式下 _itemsHost 里还夹着分隔线
        var rowsInPanel = _itemsHost.Children.OfType<StackPanel>().ToList();
        bool same = desired.Count == rowsInPanel.Count;

        if (same)
        {
            for (int i = 0; i < desired.Count; i++)
            {
                if (!_rows.TryGetValue(desired[i], out var row)) continue;
                if (ReferenceEquals(rowsInPanel[i], row)) continue;

                same = false;
                break;
            }
        }

        if (same)
        {
            UpdateRowSeparators();
            return;
        }

        // 重新挂一遍：行里的按钮不受影响，只是换个先后
        _itemsHost.Children.Clear();

        foreach (var key in desired)
        {
            if (_rows.TryGetValue(key, out var row)) _itemsHost.Children.Add(row);
        }

        _rowOrder.Clear();
        _rowOrder.AddRange(desired);

        UpdateRowSeparators();
    }

    /// <summary>窗口全关掉的程序，把它的整行撤掉。</summary>
    private void RemoveEmptyRows()
    {
        foreach (var key in _rowOrder.ToList())
        {
            if (!_rows.TryGetValue(key, out var row)) continue;
            if (row.Children.Count > 0) continue;

            _itemsHost.Children.Remove(row);
            _rows.Remove(key);
            _rowOrder.Remove(key);
        }

        UpdateRowSeparators();
    }

    public void ApplySnapshot(ExplorerSnapshot snapshot)
    {
        _lastSnapshot = snapshot;

        // 悬浮栏自己成为前台时不算"活动窗口"，沿用上一次已知的前台窗口，
        // 免得点一下按钮高亮就闪没了
        if (snapshot.Foreground != EnsureOurHandle())
        {
            _activeWindow = snapshot.Foreground;
        }

        // 记下"最后在用的文件夹窗口"：从 ALT+TAB 切回悬浮栏时要跳回它
        if (snapshot.Foreground != IntPtr.Zero &&
            snapshot.Windows.Any(w => w.Handle == snapshot.Foreground))
        {
            _lastActiveFolder = snapshot.Foreground;
        }

        var seen = new HashSet<(IntPtr Handle, int TabIndex)>();
        var rowSequence = new List<string>();
        bool added = false;
        bool wasAtRight = _scroller.ScrollableWidth - _scroller.HorizontalOffset < 24;

        foreach (var info in snapshot.Windows)
        {
            var key = (info.Handle, info.TabIndex);
            seen.Add(key);

            // 每个程序一行：先确保它的行在
            var rowKey = RowKey(info);
            var row = EnsureRow(rowKey);

            if (!rowSequence.Contains(rowKey, StringComparer.OrdinalIgnoreCase)) rowSequence.Add(rowKey);

            if (!_items.TryGetValue(key, out var visual))
            {
                visual = CreateItemVisual(info);
                _items[key] = visual;
                row.Children.Add(visual.Container);
                added = true;
            }
            else if (!ReferenceEquals(visual.Container.Parent, row))
            {
                // 程序归属变了（极少见）：把按钮挪到新的行里
                if (visual.Container.Parent is Panel previous) previous.Children.Remove(visual.Container);
                row.Children.Add(visual.Container);
            }

            // 只有"前台窗口 + 它当前显示的那个标签页"才是活动按钮
            visual.Update(info, _activeWindow == info.Handle && info.TabSelected);
            visual.Label.MaxWidth = StackMode ? StackTitleWidth : App.Settings.ShowFullTitle ? 340 : 145;
        }

        foreach (var key in _items.Keys.ToList())
        {
            if (seen.Contains(key)) continue;

            var visual = _items[key];
            if (visual.Container.Parent is Panel parent) parent.Children.Remove(visual.Container);
            _items.Remove(key);
        }

        RemoveEmptyRows();
        ApplyRowOrder(rowSequence);

        // 新窗口排在右边：用户本来就停在最右端的话，继续跟着最右端走
        if (added && wasAtRight)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _scroller.ScrollToHorizontalOffset(_scroller.ScrollableWidth);
                UpdateScrollButtons();
            }), DispatcherPriority.Background);
        }
        else
        {
            QueueScrollButtonUpdate();
        }

        RefreshVisibility();
    }

    /// <summary>按当前设置与窗口数量决定悬浮栏是否可见。</summary>
    public void RefreshVisibility()
    {
        bool empty = _items.Count == 0;
        bool shouldShow = App.Settings.ShowDock && !(empty && App.Settings.HideWhenEmpty);

        if (shouldShow)
        {
            if (!IsVisible) Show();
            EnsureShadow();
        }
        else if (IsVisible)
        {
            Hide();
            _shadow?.Hide();

            // 收纳/呼出的动画要停掉：窗口都藏起来了，再往屏外移没意义
            StopEdgeAnimation();
        }
    }

    private ItemVisual CreateItemVisual(ExplorerWindowInfo info)
    {
        var image = new Image
        {
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var label = new TextBlock
        {
            FontFamily = _fontFamily,
            FontSize = _palette.FontSizeBody,
            Foreground = _textBrush,
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            // 横幅模式要限制按钮宽度，得给标题设上限；
            // 堆叠模式按列宽算（扣掉图标 18、间距 9、左右内边距 18 和一点余量），
            // 让文字在列内截断 —— 既不会右空一大块，也不会把列撑宽、招来左右箭头。
            MaxWidth = StackMode ? StackTitleWidth
                     : App.Settings.ShowFullTitle ? 340 : 145,
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(image);
        content.Children.Add(label);

        var container = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(2, 0, 2, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = content,
        };

        var visual = new ItemVisual(container, image, label, _hoverBrush, _activeBrush, _activeBorderBrush, _textBrush, _onActiveTextBrush, _onHoverTextBrush, BuildTooltipContent);
        visual.Update(info, false);

        ToolTipService.SetPlacement(container, _tooltipPlacement);
        ToolTipService.SetInitialShowDelay(container, 320);
        ToolTipService.SetShowDuration(container, 15000);
        ToolTipService.SetBetweenShowDelay(container, 0);

        container.MouseEnter += (_, _) => visual.SetHover(true);
        container.MouseLeave += (_, _) => visual.SetHover(false);

        container.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;

            // 按下的一瞬间悬浮栏会激活自己，正在用的文件夹窗口就闪一下失活。
            // 这里立刻把前台还给它，松开时的切换/最小化判断只看 Z 序，不受影响。
            RestoreForegroundOnPress();
        };

        container.MouseLeftButtonUp += (_, e) =>
        {
            ToggleTab(visual.Handle, visual.TabIndex);
            e.Handled = true;
        };

        container.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                CloseWindow(visual.Handle);
                e.Handled = true;
            }
        };

        container.MouseRightButtonUp += (_, e) =>
        {
            ShowItemMenu(visual);
            e.Handled = true;
        };

        return visual;
    }

    /// <summary>提示气泡：文件夹名 / 完整路径 / 操作说明，分层显示。</summary>
    private object BuildTooltipContent(ExplorerWindowInfo info)
    {
        var panel = new StackPanel { MaxWidth = 460 };

        panel.Children.Add(new TextBlock
        {
            Text = info.Title,
            FontFamily = _fontFamily,
            FontSize = _palette.FontSizeBody,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrWhiteSpace(info.LocationPath))
        {
            panel.Children.Add(new TextBlock
            {
                Text = info.LocationPath,
                FontFamily = _fontFamily,
                FontSize = _palette.FontSizeSmall,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = _mutedBrush,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        panel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 8, 0, 7),
            Background = _borderBrush,
        });

        var hints = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 440 };
        hints.Children.Add(BuildHint("左键", "切换 / 最小化"));
        hints.Children.Add(BuildHint("中键", "关闭"));
        hints.Children.Add(BuildHint("右键", "更多"));
        panel.Children.Add(hints);

        return panel;
    }

    /// <summary>一个「按键 + 说明」的小胶囊，让左/中/右键一眼分得开。</summary>
    private StackPanel BuildHint(string key, string description)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 16, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };

        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = _chipBrush,
            Padding = new Thickness(6, 1, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = key,
                FontFamily = _fontFamily,
                FontSize = _palette.FontSizeTiny,
                Foreground = _textBrush,
            },
        });

        panel.Children.Add(new TextBlock
        {
            Text = description,
            FontFamily = _fontFamily,
            FontSize = _palette.FontSizeSmall,
            Margin = new Thickness(6, 0, 0, 0),
            Foreground = _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return panel;
    }

    // ---------- 操作 ----------

    /// <summary>
    /// 鼠标按在悬浮栏按钮上时，把前台还给"点之前正在用的那个文件夹窗口"。
    ///
    /// 悬浮栏被点击会激活自己，前台那个资源管理器窗口于是先失活一下、
    /// 松开鼠标才因为切换标签而重新激活 —— 看起来就是闪一下。
    /// 这里在按下的当口就还回去（Z 序没变，松开时的判断照样准）。
    /// </summary>
    private void RestoreForegroundOnPress()
    {
        try
        {
            var target = _activeWindow;
            if (target == IntPtr.Zero || !NativeMethods.IsWindow(target)) return;
            if (NativeMethods.GetForegroundWindow() == target) return;

            // 之前的前台得确实是悬浮栏上的某个文件夹窗口，才值得还
            if (!_items.Keys.Any(key => key.Handle == target)) return;
            if (!IsFrontApplicationWindow(target)) return;

            NativeMethods.SetForegroundWindow(target);
            Diag($"press: restore foreground 0x{target.ToInt64():X}");
        }
        catch
        {
            // 还不了就算了，不影响松开后的切换
        }
    }

    /// <summary>
    /// 左键行为与任务栏一致，但要认到标签页这一层：
    /// 窗口已经在我面前、而且点的就是这个标签页 → 最小化整个窗口；
    /// 否则把窗口切到前面来，并切到点的那一个标签页。
    /// </summary>
    private void ToggleTab(IntPtr hwnd, int tabIndex)
    {
        if (!NativeMethods.IsWindow(hwnd)) return;

        bool front = IsFrontApplicationWindow(hwnd);
        bool currentTab = tabIndex < 0 || Host.WindowHost.SelectedTab(hwnd) == tabIndex;

        // 点击行为留一条日志：别的机器上"点了不最小化"时，靠它看清是哪一步判错的
        DiagClick(
            $"toggle hwnd=0x{hwnd.ToInt64():X} tab={tabIndex} class={NativeMethods.GetClassNameSafe(hwnd)} " +
            $"iconic={NativeMethods.IsIconic(hwnd)} front={front} currentTab={currentTab} fg=0x{NativeMethods.GetForegroundWindow().ToInt64():X}");

        if (!NativeMethods.IsIconic(hwnd) && front && currentTab)
        {
            Host.WindowHost.Minimize(hwnd);
            Diag($"minimize: 0x{hwnd.ToInt64():X}");
            SetActiveWindow(IntPtr.Zero);
            return;
        }

        Activate(hwnd, currentTab ? -1 : tabIndex);
    }

    /// <summary>
    /// 点悬浮栏按钮的诊断日志（单独一个文件，不占 Diag 的 40 条额度）。
    /// 超过 512KB 就不再写，免得长时间使用把文件撑大。
    /// </summary>
    private static void DiagClick(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "ExplorerDock.click.log");

            var info = new FileInfo(path);
            if (info.Exists && info.Length > 512 * 1024) return;

            File.AppendAllText(path, $"{DateTime.Now:MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败无所谓
        }
    }

    /// <summary>拖动悬停时已经激活过的按钮，避免反复抢前台。</summary>
    private (IntPtr Handle, int TabIndex) _dragHoverActivated;

    /// <summary>当前悬停着的按钮（等悬停计时器到点再激活）。</summary>
    private (IntPtr Handle, int TabIndex) _dragHoverPending;

    /// <summary>
    /// 悬停多久才把窗口呼到前台。
    ///
    /// 为什么要延迟：拖动时鼠标会在按钮之间快速划过，每个按钮都立刻抢前台的话，
    /// 桌面上的窗口就会跟着闪来闪去（用户报的"拖放悬停时窗口莫名其妙闪动"）。
    /// 停住一小会儿再呼出，跟任务栏按钮的预览延迟是同一个道理。
    /// </summary>
    private readonly DispatcherTimer _dragHoverTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(220),
    };

    /// <summary>剪贴板面板正在拖条目时锁住悬浮栏自己的鼠标交互。</summary>
    private bool _interactionLocked;

    /// <summary>
    /// 剪贴板拖动期间锁住/解锁悬浮栏的鼠标交互。
    ///
    /// 为什么需要：拖动中悬停会在任务栏那边把目标窗口提到前台，面板因此失活、
    /// 系统把鼠标捕获收走；这之后鼠标（还按着左键）飘到悬浮栏上时，
    /// 悬浮栏原本"按住在任意位置都能拖动自己"的逻辑会立刻接管 ——
    /// 结果就是拖放悬停时悬浮栏自己跑起来，拖放也乱了。
    /// 锁住期间它的几何命中测试仍然有效（悬停呼出文件夹窗口照旧能用）。
    /// </summary>
    public void SetInteractionEnabled(bool enabled)
    {
        _interactionLocked = !enabled;

        try
        {
            RootBorder.IsHitTestVisible = enabled;
        }
        catch
        {
            // 退出流程里可能已经没有这个元素了
        }
    }

    /// <summary>
    /// 拖动悬停用：屏幕坐标落在某个按钮上时，**稍等一下**再把对应窗口提到前台
    /// （和任务栏按钮悬停是同一个意思，只是这个入口在悬浮栏自己身上）。
    /// 命中返回 true。
    /// </summary>
    public bool TryActivateItemAtScreenPoint(int screenX, int screenY)
    {
        if (FindItemAtScreenPoint(screenX, screenY) is not { } visual) return false;

        var key = (visual.Handle, visual.TabIndex);
        if (_dragHoverActivated == key) return true;

        // 同一颗按钮上继续悬停：让计时器接着跑，别重启（重启就永远等不到了）
        if (_dragHoverPending == key) return true;

        _dragHoverPending = key;
        _dragHoverTimer.Stop();
        _dragHoverTimer.Start();

        return true;
    }

    /// <summary>悬停计时器到点：把悬停住的这个窗口呼到前台（已经在前面就不动它）。</summary>
    private void OnDragHoverTimerTick()
    {
        _dragHoverTimer.Stop();

        var pending = _dragHoverPending;
        if (pending.Handle == IntPtr.Zero) return;
        if (_dragHoverActivated == pending) return;

        _dragHoverActivated = pending;

        // 已经在前台就别再折腾一遍：SetForegroundWindow/SetFocus 会让窗口闪一下
        if (pending.TabIndex < 0 && Host.WindowHost.IsForegroundWindow(pending.Handle)) return;

        Diag($"drag hover -> activate 0x{pending.Handle.ToInt64():X} tab={pending.TabIndex}");
        Activate(pending.Handle, pending.TabIndex);
    }

    /// <summary>屏幕坐标落在哪个按钮上（悬停呼出与拖放落点共用一套命中）。</summary>
    private ItemVisual? FindItemAtScreenPoint(int screenX, int screenY)
    {
        if (!IsVisible || _items.Count == 0) return null;

        foreach (var visual in _items.Values)
        {
            if (visual.Container is not FrameworkElement element) continue;
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;

            try
            {
                var topLeft = element.PointToScreen(new Point(0, 0));
                var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));

                if (screenX < topLeft.X || screenX > bottomRight.X) continue;
                if (screenY < topLeft.Y || screenY > bottomRight.Y) continue;

                return visual;
            }
            catch
            {
                // 布局还没算完之类，跳过这一项
            }
        }

        return null;
    }

    /// <summary>
    /// 屏幕坐标落在哪个按钮上：拖放粘贴的落点判定。
    /// 落不到按钮上就没有目标，调用方按"什么都不做"处理。
    /// </summary>
    public bool TryGetItemAt(int screenX, int screenY, out IntPtr handle, out int tabIndex)
    {
        handle = IntPtr.Zero;
        tabIndex = -1;

        if (FindItemAtScreenPoint(screenX, screenY) is not { } visual) return false;

        handle = visual.Handle;
        tabIndex = visual.TabIndex;
        return true;
    }

    /// <summary>拖动结束/离开时清掉"悬停激活"与"待激活"的记录。</summary>
    public void ResetDragHover()
    {
        _dragHoverTimer.Stop();
        _dragHoverPending = (IntPtr.Zero, -1);
        _dragHoverActivated = (IntPtr.Zero, -1);
    }

    /// <summary>
    /// 拖着东西悬停在悬浮栏上：光标底下的那个按钮对应的窗口被呼到前台（跟 Windows 任务栏一样），
    /// 这样用户看得见自己要把内容粘到哪儿；真正的粘贴在 <see cref="Drop"/> 里做。
    ///
    /// 必须回一个"可放下"的效果：回 None 的话系统的拖放光标会变成带叉的禁止样子，
    /// 等于告诉用户"这儿不能放"。
    /// </summary>
    private void OnDockDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = WindowPaste.CanPaste(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;

        if (e.Effects == DragDropEffects.None) return;

        if (!NativeMethods.GetCursorPos(out var point)) return;

        // 没落在按钮上（把手、空白、箭头）：把待激活的取消掉，免得计时器到点后
        // 去呼出上一颗按钮的窗口
        if (!TryActivateItemAtScreenPoint(point.X, point.Y))
        {
            _dragHoverTimer.Stop();
            _dragHoverPending = (IntPtr.Zero, -1);
        }
    }

    private void Activate(IntPtr hwnd, int tabIndex = -1)
    {
        if (!NativeMethods.IsWindow(hwnd)) return;

        var ok = Host.WindowHost.Activate(hwnd, tabIndex);
        Diag($"activate: target=0x{hwnd.ToInt64():X} tab={tabIndex} ok={ok} fg=0x{NativeMethods.GetForegroundWindow().ToInt64():X}");

        if (ok) SetActiveWindow(hwnd, tabIndex);
    }

    /// <summary>立刻刷新高亮，不必等下一轮轮询。</summary>
    private void SetActiveWindow(IntPtr hwnd, int tabIndex = -1)
    {
        _activeWindow = hwnd;

        foreach (var pair in _items)
        {
            bool active = pair.Key.Handle == hwnd
                && (tabIndex >= 0 ? pair.Key.TabIndex == tabIndex : pair.Value.Info.TabSelected);

            pair.Value.SetActive(active);
        }
    }

    private IntPtr EnsureOurHandle()
    {
        if (_ourHandle == IntPtr.Zero)
        {
            _ourHandle = new WindowInteropHelper(this).Handle;
        }

        return _ourHandle;
    }

    /// <summary>
    /// 悬浮栏一直置顶，所以从它沿 Z 序往下遇到的第一个"真正的应用窗口"，
    /// 就是用户点击之前正在看的那个窗口。用它来判断该切换还是该最小化。
    /// </summary>
    private bool IsFrontApplicationWindow(IntPtr target)
    {
        // 前台窗口直接就是它（或它的根窗口）：最可靠的情况。
        // Win10 上点悬浮栏不一定会把前台抢走，这时前台还是那个文件夹窗口。
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != IntPtr.Zero && NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOT) == target) return true;

        var handle = EnsureOurHandle();
        if (handle == IntPtr.Zero) return false;

        var current = handle;
        int guard = 0;

        while (guard++ < 300 && (current = NativeMethods.GetWindow(current, NativeMethods.GW_HWNDNEXT)) != IntPtr.Zero)
        {
            if (!NativeMethods.IsWindowVisible(current)) continue;

            // 先认目标本身，再按 owner/类名过滤别的窗口。
            // Win10 的文件资源管理器窗口带 owner，原来会被下面的过滤掉，
            // 结果"最前的应用窗口"永远不是它 → 点悬浮栏只能切前台、最小化不了。
            if (current == target) return true;

            if (NativeMethods.GetWindow(current, NativeMethods.GW_OWNER) != IntPtr.Zero) continue;
            if (IsShellWindow(NativeMethods.GetClassNameSafe(current))) continue;

            return false;
        }

        return false;
    }

    private static bool IsShellWindow(string className) => className switch
    {
        "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW" or "Button"
            or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow" or "ForegroundStaging"
            or "MultitaskingViewFrame" or "TaskListThumbnailWnd" or "TopLevelWindowForOverflowXamlIsland"
            or "Xaml_WindowedPopupClass" or "Shell_InputSwitchTopLevelWindow" or "EdgeUiInputTopWndClass"
            or "NarratorHelperWindow" or "ApplicationManager_DesktopShellWindow" or "SysShadow"
            or "Windows.Internal.Shell.TabProxyWindow" => true,
        _ => false,
    };

    /// <summary>
    /// 关掉一个窗口。
    ///
    /// 交给宿主执行：普通权限进程往管理员程序窗口发 WM_CLOSE 会被 UIPI 挡掉，
    /// 那就是"右键/中键关不掉其他程序"的原因。
    /// </summary>
    private static void CloseWindow(IntPtr hwnd)
    {
        Host.WindowHost.CloseWindow(hwnd);
    }

    /// <summary>关掉这个按钮所属程序的所有窗口。</summary>
    private static void CloseProcessWindows(IntPtr hwnd)
    {
        Host.WindowHost.CloseProcessWindows(hwnd);
    }

    /// <summary>
    /// 结束按钮所属程序的进程（同一个 exe 启动出来的其他独立进程一起结束）。
    ///
    /// 这是破坏性操作（未保存的内容会丢），所以先让用户确认一次。
    /// </summary>
    private static void EndProcess(ItemVisual visual)
    {
        var name = visual.Info.ProcessName;
        var label = name.Length > 0 ? name : "该程序";

        if (!Views.ConfirmDialog.Confirm(
                "结束进程",
                $"将结束所有 {label} 进程（未保存的内容会丢失）。确定吗？",
                "结束进程",
                "取消"))
        {
            return;
        }

        Host.WindowHost.KillProcesses(visual.Handle);
    }

    // ---------- 菜单 ----------

    /// <summary>
    /// 弹出菜单（所有菜单都走这里）：
    /// 1) 先收起剪贴板面板 —— 它抢了前台，会让菜单的'点别处关闭'失灵；
    /// 2) 装上兜底监听 —— 按下鼠标时指针若不在本进程窗口上，直接收菜单。
    /// </summary>
    private void OpenMenu(ContextMenu menu)
    {
        Host.HideClipboardPanel();

        var watcher = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };

        watcher.Tick += (_, _) =>
        {
            if (!menu.IsOpen)
            {
                watcher.Stop();
                return;
            }

            // 只在按键按下的那一刻判，鼠标划过不算
            if (!NativeMethods.IsKeyDown(0x01) && !NativeMethods.IsKeyDown(0x02)) return;
            if (!NativeMethods.GetCursorPos(out var point)) return;

            var under = NativeMethods.WindowFromPoint(point);
            if (under == IntPtr.Zero) return;

            NativeMethods.GetWindowThreadProcessId(under, out uint pid);

            if ((int)pid != Environment.ProcessId)
            {
                menu.IsOpen = false;
                watcher.Stop();
            }
        };

        _menuOpen = true;
        menu.Opened += (_, _) => watcher.Start();
        menu.Closed += (_, _) =>
        {
            watcher.Stop();
            _menuOpen = false;
        };

        menu.IsOpen = true;
    }


    private void ShowItemMenu(ItemVisual visual)
    {
        var menu = new ContextMenu();

        // 文件夹窗口与其他程序窗口的文案分开：这里不再只有资源管理器
        bool explorer = visual.Info.IsExplorer;

        menu.Items.Add(MenuAction(explorer ? "切换到该文件夹" : "切换到该窗口", () => Activate(visual.Handle)));
        menu.Items.Add(MenuAction("最小化窗口", () =>
        {
            if (NativeMethods.IsWindow(visual.Handle) && !NativeMethods.IsIconic(visual.Handle))
            {
                Host.WindowHost.Minimize(visual.Handle);
            }
        }));
        menu.Items.Add(MenuSeparator());

        var copy = MenuAction("复制文件夹路径", () =>
        {
            var path = visual.Info.LocationPath;
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                Clipboard.SetText(path);
            }
            catch
            {
                // 剪贴板被占用，忽略
            }
        });
        copy.IsEnabled = explorer && !string.IsNullOrWhiteSpace(visual.Info.LocationPath);
        menu.Items.Add(copy);

        menu.Items.Add(MenuSeparator());
        menu.Items.Add(MenuAction("关闭这个窗口", () => CloseWindow(visual.Handle)));

        // 文件夹窗口：把那一个程序的所有窗口关掉（效果就是"关闭所有文件夹窗口"）；
        // 其他程序：直接结束进程 —— 同一个 exe 启动出来的多个独立进程会一起结束
        if (explorer)
        {
            menu.Items.Add(MenuAction("关闭该程序所有窗口", () => CloseProcessWindows(visual.Handle)));
        }
        else
        {
            menu.Items.Add(MenuAction("结束进程", () => EndProcess(visual)));
        }

        menu.PlacementTarget = visual.Container;
        OpenMenu(menu);
    }

    private void ShowMenu()
    {
        var menu = BuildAppMenu();

        // 跟着鼠标弹：贴边或靠近屏幕边缘时，按窗口定位会被挤出屏幕、还会被裁掉
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;

        OpenMenu(menu);
    }

    /// <summary>托盘右键用：弹出与悬浮栏完全相同的深色菜单。</summary>
    public void ShowAppMenuAtMouse()
    {
        if (!IsVisible) Show();
        ShowMenu();
    }

    private Window? _trayMenuHost;

    /// <summary>
    /// 托盘右键的菜单：用一个贴在鼠标位置、可被激活的宿主窗口来承载，
    /// 而不是让菜单孤零零地挂在悬浮栏上 ——
    /// 之前那样在托盘右键时悬浮栏不是前台窗口，菜单拿不到焦点，
    /// 就判断不出"点击落在菜单外"，于是关不掉。
    /// </summary>
    public void ShowTrayMenu()
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) return;

        if (_trayMenuHost is null)
        {
            _trayMenuHost = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = true,
                Topmost = true,
                Width = 1,
                Height = 1,
                Left = -32000,
                Top = -32000,
                Title = "ExplorerDock",
            };

            _trayMenuHost.Closed += (_, _) => _trayMenuHost = null;
            _trayMenuHost.Show();
        }

        // 用物理坐标直接摆位，绕开 DPI 逻辑单位换算
        var handle = new WindowInteropHelper(_trayMenuHost).Handle;
        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HWND_TOPMOST,
            cursor.X,
            cursor.Y,
            1,
            1,
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);

        _trayMenuHost.Activate();

        var menu = BuildAppMenu();
        menu.PlacementTarget = _trayMenuHost;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private ContextMenu BuildAppMenu()
    {
        var menu = new ContextMenu();

        // 设置项多到一屏放不下，所以按用途分成几个一级项，组内设置项进各自的二级菜单
        menu.Items.Add(MenuGroup(
            "接管窗口",
            CheckItem("接管任务栏按钮", App.Settings.TakeoverEnabled, Host.SetTakeover),
            CheckItem("自动接管多窗口程序", App.Settings.AutoTakeoverMultiWindow, Host.SetAutoTakeoverMultiWindow),
            MenuAction("接管指定程序…", Host.OpenProcessPicker)));

        // 合并范围三项互斥：点完统一刷新一遍勾选状态
        MenuItem? groupNone = null;
        MenuItem? groupTakeover = null;
        MenuItem? groupAll = null;

        groupNone = CheckItem(
            "合并范围：不合并",
            App.Settings.AltTabGroupScope == AltTabGroupMode.None,
            _ => SelectGroupScope(AltTabGroupMode.None));

        groupTakeover = CheckItem(
            "合并范围：只合并悬浮栏接管的程序",
            App.Settings.AltTabGroupScope == AltTabGroupMode.TakeoverOnly,
            _ => SelectGroupScope(AltTabGroupMode.TakeoverOnly));

        groupAll = CheckItem(
            "合并范围：合并全部同名进程窗口",
            App.Settings.AltTabGroupScope == AltTabGroupMode.AllProcesses,
            _ => SelectGroupScope(AltTabGroupMode.AllProcesses));

        menu.Items.Add(MenuGroup(
            "Alt+Tab 切换",
            CheckItem("接管 Alt+Tab 切换", App.Settings.AltTabTakeover, Host.SetAltTabTakeover),
            CheckItem("全屏应用时让系统处理 Alt+Tab", App.Settings.AltTabFullscreenPassthrough, Host.SetAltTabFullscreenPassthrough),
            MenuSeparator(),
            groupNone,
            groupTakeover,
            groupAll));

        void SelectGroupScope(AltTabGroupMode mode)
        {
            Host.SetAltTabGroupScope(mode);

            if (groupNone is not null) groupNone.IsChecked = App.Settings.AltTabGroupScope == AltTabGroupMode.None;
            if (groupTakeover is not null) groupTakeover.IsChecked = App.Settings.AltTabGroupScope == AltTabGroupMode.TakeoverOnly;
            if (groupAll is not null) groupAll.IsChecked = App.Settings.AltTabGroupScope == AltTabGroupMode.AllProcesses;
        }

        menu.Items.Add(MenuGroup(
            "剪贴板（Win+V）",
            CheckItem("接管 Win+V 剪贴板", App.Settings.ClipboardTakeover, Host.SetClipboardTakeover),
            MenuAction("打开剪贴板历史", Host.ToggleClipboardPanel),
            MenuAction("剪贴板设置…", Host.OpenClipboardSettings),
            MenuAction("清空剪贴板历史", Host.ClearClipboardHistory)));

        // 布局两项互斥：WPF 的 IsCheckable 会自己翻转，点完统一刷新
        MenuItem? layoutBanner = null;
        MenuItem? layoutStack = null;

        layoutBanner = CheckItem(
            "布局：横幅（每个程序一行）",
            App.Settings.Layout == DockLayout.Banner,
            _ => SelectLayout(DockLayout.Banner));

        layoutStack = CheckItem(
            "布局：堆叠（竖着一列）",
            App.Settings.Layout == DockLayout.Stack,
            _ => SelectLayout(DockLayout.Stack));

        menu.Items.Add(MenuGroup(
            "悬浮栏",
            // 这一项随状态换文案，隐藏之后还能从同一个位置再点回来
            MenuAction(
                App.Settings.ShowDock ? "隐藏悬浮栏" : "显示悬浮栏",
                () => Host.SetShowDock(!App.Settings.ShowDock)),
            CheckItem("没有窗口时自动隐藏", App.Settings.HideWhenEmpty, Host.SetHideWhenEmpty),
            CheckItem("显示完整标题", App.Settings.ShowFullTitle, v =>
            {
                App.Settings.ShowFullTitle = v;
                App.Settings.Save();
                Host.RebuildDockItems();
            }),
            MenuSeparator(),
            layoutBanner,
            layoutStack,
            MenuSeparator(),
            CheckItem("贴边自动隐藏", App.Settings.EdgeAutoHide, Host.SetEdgeAutoHide),
            MenuAction("贴边隐藏设置…", Host.OpenEdgeHideSettings),
            MenuAction("回到屏幕顶部居中", ResetPosition)));

        void SelectLayout(DockLayout layout)
        {
            Host.SetDockLayout(layout);

            if (layoutBanner is not null) layoutBanner.IsChecked = App.Settings.Layout == DockLayout.Banner;
            if (layoutStack is not null) layoutStack.IsChecked = App.Settings.Layout == DockLayout.Stack;
        }

        // 主题四项必须互斥：WPF 的 IsCheckable 会自己翻转勾选，所以点完要统一刷新一遍
        MenuItem? themeAuto = null;
        MenuItem? themeDark = null;
        MenuItem? themeLight = null;
        MenuItem? themeCustom = null;

        themeAuto = CheckItem("主题：跟随系统", App.Settings.Theme == DockTheme.Auto, _ => SelectTheme(DockTheme.Auto));
        themeDark = CheckItem("主题：深色", App.Settings.Theme == DockTheme.Dark, _ => SelectTheme(DockTheme.Dark));
        themeLight = CheckItem("主题：浅色", App.Settings.Theme == DockTheme.Light, _ => SelectTheme(DockTheme.Light));
        themeCustom = CheckItem("主题：自定义", App.Settings.Theme == DockTheme.Custom, _ => SelectTheme(DockTheme.Custom));

        menu.Items.Add(MenuGroup(
            "外观",
            themeAuto,
            themeDark,
            themeLight,
            themeCustom,
            MenuSeparator(),
            MenuAction("自定义主题设置…", Host.OpenThemeEditor)));

        void SelectTheme(DockTheme theme)
        {
            Host.SetTheme(theme);

            if (themeAuto is not null) themeAuto.IsChecked = App.Settings.Theme == DockTheme.Auto;
            if (themeDark is not null) themeDark.IsChecked = App.Settings.Theme == DockTheme.Dark;
            if (themeLight is not null) themeLight.IsChecked = App.Settings.Theme == DockTheme.Light;
            if (themeCustom is not null) themeCustom.IsChecked = App.Settings.Theme == DockTheme.Custom;
        }

        menu.Items.Add(MenuGroup(
            "启动与权限",
            CheckItem("开机自动启动", App.Settings.RunAtStartup, Host.SetRunAtStartup),
            CheckItem("以管理员身份运行", App.Settings.RunElevated, enabled =>
            {
                // 取消勾选是"降权"，会丢掉对管理员程序（不少游戏）的接管能力，先说清楚再动手
                if (!enabled && App.IsElevated && !App.ConfirmElevatedOff()) return;

                Host.SetRunElevated(enabled);
            })));

        menu.Items.Add(MenuGroup(
            "维护",
            MenuAction("设置向导…", Host.OpenSetupWizard),
            MenuAction("检查更新", Host.OpenUpdateWindow),
            MenuAction("查看日志", Host.OpenLogs),
            MenuAction("重启资源管理器", App.RestartExplorer)));

        menu.Items.Add(MenuAction("使用说明", Host.OpenHelp));

        menu.Items.Add(MenuSeparator());
        menu.Items.Add(MenuAction("退出 ExplorerDock", Host.ExitApp));

        return menu;
    }

    /// <summary>菜单分隔线：系统默认那条又偏右又贴边，这里换成左右对称的细线。</summary>
    private static Separator MenuSeparator()
    {
        var separator = new Separator
        {
            Margin = new Thickness(10, 4, 10, 4),
            Height = 1,
            Background = Host.Resources["DockMenuSeparator"] as Brush ?? Brushes.Gray,
        };

        var template = new ControlTemplate(typeof(Separator));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(HeightProperty, 1.0);
        border.SetValue(BackgroundProperty, new TemplateBindingExtension(Separator.BackgroundProperty));
        template.VisualTree = border;
        separator.Template = template;

        return separator;
    }

    /// <summary>
    /// 一级分组菜单：设置项多到一屏放不下，所以每个分组做成一个一级项，组内的设置进它的二级菜单。
    /// </summary>
    private static MenuItem MenuGroup(string header, params object[] children)
    {
        var group = new MenuItem { Header = header };

        foreach (var child in children) group.Items.Add(child);

        return group;
    }

    private static MenuItem CheckItem(string header, bool isChecked, Action<bool> onChange)
    {
        var item = new MenuItem
        {
            Header = header,
            IsCheckable = true,
            IsChecked = isChecked,
            StaysOpenOnClick = true,
        };

        item.Click += (_, _) => onChange(item.IsChecked);
        return item;
    }

    private static MenuItem MenuAction(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    // ---------- 单项控件 ----------

    private sealed class ItemVisual
    {
        private readonly Brush _hover;
        private readonly Brush _active;
        private readonly Brush _activeBorder;
        private readonly Brush _text;
        private readonly Brush _onActiveText;
        private readonly Brush _onHoverText;
        private readonly Func<ExplorerWindowInfo, object> _tooltipFactory;
        private bool _hovering;
        private bool _isActive;
        private string _tooltipKey = string.Empty;

        public ItemVisual(
            Border container,
            Image icon,
            TextBlock label,
            Brush hover,
            Brush active,
            Brush activeBorder,
            Brush text,
            Brush onActiveText,
            Brush onHoverText,
            Func<ExplorerWindowInfo, object> tooltipFactory)
        {
            Container = container;
            Icon = icon;
            Label = label;
            _hover = hover;
            _active = active;
            _activeBorder = activeBorder;
            _text = text;
            _onActiveText = onActiveText;
            _onHoverText = onHoverText;
            _tooltipFactory = tooltipFactory;
        }

        public Border Container { get; }
        public Image Icon { get; }
        public TextBlock Label { get; }
        public IntPtr Handle { get; private set; }

        /// <summary>这个按钮代表窗口里的第几个标签页；-1 表示整个窗口。</summary>
        public int TabIndex { get; private set; } = -1;

        public ExplorerWindowInfo Info { get; private set; } = new();

        public void Update(ExplorerWindowInfo info, bool isActive)
        {
            Info = info;
            Handle = info.Handle;
            TabIndex = info.TabIndex;
            _isActive = isActive;

            if (Label.Text != info.Title) Label.Text = info.Title;
            if (!ReferenceEquals(Icon.Source, info.Icon)) Icon.Source = info.Icon;

            // 标题或路径变了才重建气泡，别每轮轮询都重造控件
            var key = info.Title + "\u0001" + info.LocationPath;
            if (key != _tooltipKey)
            {
                _tooltipKey = key;
                Container.ToolTip = _tooltipFactory(info);
            }

            Refresh();
        }

        public void SetHover(bool value)
        {
            _hovering = value;
            Refresh();
        }

        public void SetActive(bool value)
        {
            _isActive = value;
            Refresh();
        }

        private void Refresh()
        {
            // 高亮块只改背景：背景与描边同色时圆角会画两遍，边缘会显得残缺
            if (_isActive)
            {
                Container.Background = _active;
                Container.BorderBrush = Brushes.Transparent;
                Label.Foreground = _onActiveText;
                return;
            }

            if (_hovering)
            {
                Container.Background = _hover;
                Container.BorderBrush = Brushes.Transparent;
                Label.Foreground = _onHoverText;
                return;
            }

            Container.Background = Brushes.Transparent;
            Container.BorderBrush = Brushes.Transparent;
            Label.Foreground = _text;
        }
    }
}





