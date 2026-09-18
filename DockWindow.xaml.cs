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
    private Border? _scrollLeft;
    private Border? _scrollRight;
    private readonly Dictionary<IntPtr, ItemVisual> _items = new();
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
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Focusable = false,
            Content = _itemsHost,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _grip = CreateGrip();

        // 窗口宽度有上限，窗口多了就会被裁在右边。滚动条是隐藏的（外观干净），
        // 这里补一对左右箭头当可见的滚动入口，内容没溢出时它们自己收起来。
        _scrollLeft = CreateScrollButton("\u2039", -1);
        _scrollRight = CreateScrollButton("\u203A", 1);

        RootPanel.Children.Add(_grip);
        RootPanel.Children.Add(_scrollLeft);
        RootPanel.Children.Add(_scroller);
        RootPanel.Children.Add(_scrollRight);

        UpdateScrollerMaxWidth();

        _scroller.ScrollChanged += (_, _) => UpdateScrollButtons();
        _itemsHost.SizeChanged += (_, _) => QueueScrollButtonUpdate();

        PreviewMouseWheel += (_, e) =>
        {
            // 一格滚轮 = 3 个按钮的宽度。直接拿 e.Delta 当像素量的话一次只挪几十像素，
            // 窗口一多就感觉"滚不动"。
            ScrollBy(-Math.Sign(e.Delta) * StepWidth() * 3);
            e.Handled = true;
        };

        Loaded += OnLoaded;
        LocationChanged += (_, _) =>
        {
            // 拖动过程中就把窗口限制在屏幕内，免得被拖出屏幕或贴死在边缘
            if (_dragging) ClampToScreen();
            UpdateShadowBounds();
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
            try
            {
                DragMove();
            }
            catch
            {
                // 拖动被打断，忽略
            }

            _dragging = false;
            _autoCenter = false;
            ClampToScreen();
            SavePosition();
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

        foreach (var visual in _items.Values)
        {
            _itemsHost.Children.Remove(visual.Container);
        }

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

        double room = (MaxWidth - chrome) / Math.Max(1.0, _scale);
        room -= _grip?.Width ?? 0;
        room -= (_scrollLeft?.Width ?? 0) + (_scrollRight?.Width ?? 0);

        _scroller.MaxWidth = Math.Max(120, room);
    }

    /// <summary>溢出时才出现的左右滚动箭头，配色跟着主题走。</summary>
    private Border CreateScrollButton(string glyph, int direction)
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
            ToolTip = direction < 0 ? "往前翻" : "往后翻",
        };

        border.MouseEnter += (_, _) => text.Foreground = _textBrush;
        border.MouseLeave += (_, _) => text.Foreground = _mutedBrush;
        border.MouseLeftButtonUp += (_, e) =>
        {
            ScrollBy(direction * StepWidth() * 3);
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
    }

    private void ScrollBy(double delta)
    {
        double max = Math.Max(0, _scroller.ScrollableWidth);
        _scroller.ScrollToHorizontalOffset(Math.Clamp(_scroller.HorizontalOffset + delta, 0, max));
        UpdateScrollButtons();
    }

    /// <summary>一个按钮连左右间距占多宽 —— 滚动步长按它的整数倍走，不会停在半个按钮上。</summary>
    private double StepWidth()
    {
        if (_itemsHost.Children.Count > 0 &&
            _itemsHost.Children[0] is FrameworkElement first &&
            first.ActualWidth > 1)
        {
            return first.ActualWidth + first.Margin.Left + first.Margin.Right;
        }

        return 120;
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
        };

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

            _autoCenter = false;
            ClampToScreen();
            SavePosition();
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

        // 布局完成之前 ScrollableWidth 还是 0，箭头显隐要等这一轮布局走完再算
        QueueScrollButtonUpdate();
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
        if (_autoCenter) MoveToDefaultPosition();

        UpdateShadowBounds();
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
        Top = area.Top + 6;
        UpdateTooltipPlacement();
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
        App.Settings.DockLeft = null;
        App.Settings.DockTop = null;
        App.Settings.Save();
        _autoCenter = true;
        MoveToDefaultPosition();
    }

    // ---------- 数据刷新 ----------

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

        var seen = new HashSet<IntPtr>();
        bool added = false;
        bool wasAtRight = _scroller.ScrollableWidth - _scroller.HorizontalOffset < 24;

        foreach (var info in snapshot.Windows)
        {
            seen.Add(info.Handle);

            if (!_items.TryGetValue(info.Handle, out var visual))
            {
                visual = CreateItemVisual(info);
                _items[info.Handle] = visual;
                _itemsHost.Children.Add(visual.Container);
                added = true;
            }

            visual.Update(info, _activeWindow == info.Handle);
            visual.Label.MaxWidth = App.Settings.ShowFullTitle ? 340 : 145;
        }

        foreach (var handle in _items.Keys.ToList())
        {
            if (seen.Contains(handle)) continue;
            _itemsHost.Children.Remove(_items[handle].Container);
            _items.Remove(handle);
        }

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
            MaxWidth = App.Settings.ShowFullTitle ? 340 : 145,
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

        container.MouseLeftButtonUp += (_, e) =>
        {
            ToggleWindow(visual.Handle);
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
    /// 左键行为与任务栏一致：窗口已经在我面前就最小化，否则切到它面前。
    /// </summary>
    private void ToggleWindow(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) return;

        bool front = IsFrontApplicationWindow(hwnd);

        // 点击行为留一条日志：别的机器上"点了不最小化"时，靠它看清是哪一步判错的
        DiagClick(
            $"toggle hwnd=0x{hwnd.ToInt64():X} class={NativeMethods.GetClassNameSafe(hwnd)} " +
            $"iconic={NativeMethods.IsIconic(hwnd)} front={front} fg=0x{NativeMethods.GetForegroundWindow().ToInt64():X}");

        if (!NativeMethods.IsIconic(hwnd) && front)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
            Diag($"minimize: 0x{hwnd.ToInt64():X}");
            SetActiveWindow(IntPtr.Zero);
            return;
        }

        Activate(hwnd);
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

    /// <summary>拖动悬停时已经激活过的文件夹窗口，避免每 250ms 反复抢前台。</summary>
    private IntPtr _dragHoverActivated;

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
    /// 拖动悬停用：屏幕坐标落在某个文件夹按钮上时，把对应的窗口提到前台
    /// （和任务栏按钮悬停是同一个意思，只是这个入口在悬浮栏自己身上）。
    /// 命中返回 true。
    /// </summary>
    public bool TryActivateItemAtScreenPoint(int screenX, int screenY)
    {
        if (!IsVisible || _items.Count == 0) return false;

        foreach (var pair in _items)
        {
            if (pair.Value.Container is not FrameworkElement element) continue;
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;

            try
            {
                var topLeft = element.PointToScreen(new Point(0, 0));
                var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));

                if (screenX < topLeft.X || screenX > bottomRight.X) continue;
                if (screenY < topLeft.Y || screenY > bottomRight.Y) continue;

                if (_dragHoverActivated == pair.Key) return true;

                _dragHoverActivated = pair.Key;
                Diag($"drag hover -> activate 0x{pair.Key.ToInt64():X}");
                Activate(pair.Key);
                SetActiveWindow(pair.Key);
                return true;
            }
            catch
            {
                // 布局还没算完之类，跳过这一项
            }
        }

        return false;
    }

    /// <summary>拖动结束时清掉"悬停激活过"的记录。</summary>
    public void ResetDragHover() => _dragHoverActivated = IntPtr.Zero;

    private void Activate(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) return;

        var ok = NativeMethods.ForceForeground(hwnd);
        Diag($"activate: target=0x{hwnd.ToInt64():X} ok={ok} fg=0x{NativeMethods.GetForegroundWindow().ToInt64():X}");

        if (ok) SetActiveWindow(hwnd);
    }

    /// <summary>立刻刷新高亮，不必等下一轮轮询。</summary>
    private void SetActiveWindow(IntPtr hwnd)
    {
        _activeWindow = hwnd;

        foreach (var pair in _items)
        {
            pair.Value.SetActive(pair.Key == hwnd);
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

    private static void CloseWindow(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)) return;
        NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
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

        menu.Items.Add(MenuAction("切换到该文件夹", () => Activate(visual.Handle)));
        menu.Items.Add(MenuAction("最小化窗口", () =>
        {
            if (NativeMethods.IsWindow(visual.Handle) && !NativeMethods.IsIconic(visual.Handle))
            {
                NativeMethods.ShowWindow(visual.Handle, NativeMethods.SW_MINIMIZE);
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
        copy.IsEnabled = !string.IsNullOrWhiteSpace(visual.Info.LocationPath);
        menu.Items.Add(copy);

        menu.Items.Add(MenuSeparator());
        menu.Items.Add(MenuAction("关闭这个文件夹窗口", () => CloseWindow(visual.Handle)));
        menu.Items.Add(MenuAction("关闭所有文件夹窗口", App.CloseAllExplorerWindows));

        menu.PlacementTarget = visual.Container;
        OpenMenu(menu);
    }

    private void ShowMenu()
    {
        var menu = BuildAppMenu();
        menu.PlacementTarget = this;

        // 跟着鼠标弹：这样托盘的右键也能复用同一套菜单
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;

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

        menu.Items.Add(CheckItem("接管任务栏", App.Settings.TakeoverEnabled, Host.SetTakeover));
        menu.Items.Add(CheckItem("接管 Alt+Tab 切换", App.Settings.AltTabTakeover, Host.SetAltTabTakeover));
        menu.Items.Add(CheckItem("全屏应用时让系统处理 Alt+Tab", App.Settings.AltTabFullscreenPassthrough, Host.SetAltTabFullscreenPassthrough));
        menu.Items.Add(CheckItem("接管 Win+V 剪贴板", App.Settings.ClipboardTakeover, Host.SetClipboardTakeover));
        menu.Items.Add(MenuAction("打开剪贴板历史", Host.ToggleClipboardPanel));
        menu.Items.Add(MenuAction("剪贴板设置…", Host.OpenClipboardSettings));
        menu.Items.Add(MenuAction("清空剪贴板历史", Host.ClearClipboardHistory));
        menu.Items.Add(CheckItem("没有文件夹时自动隐藏", App.Settings.HideWhenEmpty, Host.SetHideWhenEmpty));
        menu.Items.Add(CheckItem("显示完整标题", App.Settings.ShowFullTitle, v =>
        {
            App.Settings.ShowFullTitle = v;
            App.Settings.Save();
            Host.RebuildDockItems();
        }));
        menu.Items.Add(MenuSeparator());

        // 主题四项必须互斥：WPF 的 IsCheckable 会自己翻转勾选，所以点完要统一刷新一遍
        MenuItem? themeAuto = null;
        MenuItem? themeDark = null;
        MenuItem? themeLight = null;
        MenuItem? themeCustom = null;

        themeAuto = CheckItem("主题：跟随系统", App.Settings.Theme == DockTheme.Auto, _ => SelectTheme(DockTheme.Auto));
        themeDark = CheckItem("主题：深色", App.Settings.Theme == DockTheme.Dark, _ => SelectTheme(DockTheme.Dark));
        themeLight = CheckItem("主题：浅色", App.Settings.Theme == DockTheme.Light, _ => SelectTheme(DockTheme.Light));
        themeCustom = CheckItem("主题：自定义", App.Settings.Theme == DockTheme.Custom, _ => SelectTheme(DockTheme.Custom));

        menu.Items.Add(themeAuto);
        menu.Items.Add(themeDark);
        menu.Items.Add(themeLight);
        menu.Items.Add(themeCustom);
        menu.Items.Add(MenuAction("自定义主题设置…", Host.OpenThemeEditor));

        void SelectTheme(DockTheme theme)
        {
            Host.SetTheme(theme);

            if (themeAuto is not null) themeAuto.IsChecked = App.Settings.Theme == DockTheme.Auto;
            if (themeDark is not null) themeDark.IsChecked = App.Settings.Theme == DockTheme.Dark;
            if (themeLight is not null) themeLight.IsChecked = App.Settings.Theme == DockTheme.Light;
            if (themeCustom is not null) themeCustom.IsChecked = App.Settings.Theme == DockTheme.Custom;
        }

        menu.Items.Add(MenuSeparator());
        menu.Items.Add(CheckItem("开机自动启动", App.Settings.RunAtStartup, Host.SetRunAtStartup));
        menu.Items.Add(MenuAction("回到屏幕顶部居中", ResetPosition));
        // 这一项随状态换文案，隐藏之后还能从同一个位置再点回来
        menu.Items.Add(MenuAction(
            App.Settings.ShowDock ? "隐藏悬浮栏" : "显示悬浮栏",
            () => Host.SetShowDock(!App.Settings.ShowDock)));
        menu.Items.Add(MenuSeparator());
        menu.Items.Add(MenuAction("设置向导…", Host.OpenSetupWizard));
        menu.Items.Add(MenuAction("查看日志", Host.OpenLogs));
        menu.Items.Add(MenuSeparator());
        menu.Items.Add(CheckItem("以管理员身份运行", App.Settings.RunElevated, enabled =>
        {
            // 取消勾选是"降权"，会丢掉对管理员程序（不少游戏）的接管能力，先说清楚再动手
            if (!enabled && App.IsElevated && !App.ConfirmElevatedOff()) return;

            Host.SetRunElevated(enabled);
        }));
        menu.Items.Add(MenuSeparator());
        menu.Items.Add(MenuAction("关闭所有文件夹窗口", App.CloseAllExplorerWindows));
        menu.Items.Add(MenuAction("重启资源管理器", App.RestartExplorer));
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
        public ExplorerWindowInfo Info { get; private set; } = new();

        public void Update(ExplorerWindowInfo info, bool isActive)
        {
            Info = info;
            Handle = info.Handle;
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





