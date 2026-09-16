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
    private readonly Dictionary<IntPtr, ItemVisual> _items = new();
    private bool _autoCenter;
    private IntPtr _ourHandle;
    private IntPtr _activeWindow;
    private PlacementMode _tooltipPlacement = PlacementMode.Bottom;
    private ShadowWindow? _shadow;
    private ExplorerSnapshot? _lastSnapshot;
    private readonly DispatcherTimer _topmostTimer;
    private DateTime _lastTopmostFix = DateTime.MinValue;
    private bool _dragging;
    private IntPtr _lastActiveFolder;
    private AltTabProxyWindow? _altTabProxy;

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

        RootPanel.Children.Add(_grip);
        RootPanel.Children.Add(_scroller);

        PreviewMouseWheel += (_, e) =>
        {
            _scroller.ScrollToHorizontalOffset(_scroller.HorizontalOffset - (e.Delta * 0.6));
            e.Handled = true;
        };

        Loaded += OnLoaded;
        LocationChanged += (_, _) =>
        {
            // 拖动过程中就把窗口限制在屏幕内，免得被拖出屏幕或贴死在边缘
            if (_dragging) ClampToScreen();
            UpdateShadowBounds();
        };

        // 拖动别的窗口时 Windows 会临时把被拖窗口提到最前，我们的置顶可能被挤掉；
        // 定时重新钉一遍，被遮住也能自己回来
        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(800),
        };
        _topmostTimer.Tick += (_, _) => EnsureTopmost();

        MouseRightButtonUp += (_, e) =>
        {
            ShowMenu();
            e.Handled = true;
        };
    }

    /// <summary>
    /// 悬浮栏自己从 ALT+TAB 里退出去（加 WS_EX_TOOLWINDOW），
    /// 改由 <see cref="AltTabProxyWindow"/> 当替身：它显示最后在用的文件夹窗口的实时画面，
    /// 选中它就跳回那个文件夹窗口。
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
        bool light = App.Settings.ResolveLightTheme();

        if (light)
        {
            // 浅色：白底 + 深色描边，活动项用深色块反白，对比明确
            _backgroundBrush = new SolidColorBrush(Color.FromArgb(0xFA, 0xFA, 0xFA, 0xFA));
            _borderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x24, 0x24, 0x24));
            _textBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));
            _hoverBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x55, 0x55, 0x55));
            _activeBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x24, 0x24, 0x24));
            _activeBorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x24, 0x24, 0x24));
            _onActiveTextBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            _onHoverTextBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            _chipBrush = new SolidColorBrush(Color.FromArgb(0x24, 0x00, 0x00, 0x00));
            _mutedBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x55, 0x55, 0x55));
        }
        else
        {
            _backgroundBrush = new SolidColorBrush(Color.FromArgb(0xF2, 0x1B, 0x1B, 0x1F));
            _borderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            _textBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2));
            _hoverBrush = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
            _activeBrush = new SolidColorBrush(Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF));
            _activeBorderBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
            _onActiveTextBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            _onHoverTextBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            _chipBrush = new SolidColorBrush(Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF));
            _mutedBrush = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
        }

        RootBorder.Background = _backgroundBrush;
        RootBorder.BorderBrush = _borderBrush;

        if (_grip is not null && _grip.Child is TextBlock dots) dots.Foreground = _mutedBrush;
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
                Color.FromArgb(0xFF, borderColor.R, borderColor.G, borderColor.B),
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

    private Border CreateGrip()
    {
        var dots = new TextBlock
        {
            Text = "\u283F",
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 14,
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
        _topmostTimer.Start();
    }

    /// <summary>阴影由独立窗口绘制：它鼠标穿透，所以阴影区不会吃掉点击。</summary>
    private void EnsureShadow()
    {
        if (_shadow is null)
        {
            _shadow = new ShadowWindow();
            var borderColor = (_borderBrush as SolidColorBrush)?.Color ?? Color.FromArgb(0xFF, 0x24, 0x24, 0x24);
            _shadow.SetTone(
                Color.FromArgb(0xFF, borderColor.R, borderColor.G, borderColor.B),
                RootBorder.CornerRadius.TopLeft,
                App.Settings.ResolveLightTheme());
            _shadow.SetInset(0);
            _shadow.Show();
        }

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

    /// <summary>ALT+TAB 替身：显示最后在用文件夹窗口的画面，选中即跳回它。</summary>
    private void EnsureAltTabProxy()
    {
        if (_altTabProxy is not null) return;

        _altTabProxy = new AltTabProxyWindow();
        _altTabProxy.Activated += (_, _) =>
        {
            var target = _altTabProxy.TargetHandle;
            if (target == IntPtr.Zero || !NativeMethods.IsWindow(target)) return;
            Activate(target);
        };

        // 注意：这里不 Show()。要等 AttachTo 把标题设成文件夹名之后再显示，
        // 否则 ALT+TAB 会一直用首次登记时的默认标题。
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

        // 单独记下"最后在用的文件夹窗口"：ALT+TAB 替身要显示它、切到它
        if (snapshot.Foreground != IntPtr.Zero &&
            snapshot.Windows.Any(w => w.Handle == snapshot.Foreground))
        {
            _lastActiveFolder = snapshot.Foreground;
            EnsureAltTabProxy();
        }

        // 还没记录过"最后在用"的文件夹时，先拿列表里第一个顶上，
        // 保证 ALT+TAB 替身一启动就有内容
        if (_lastActiveFolder == IntPtr.Zero && snapshot.Windows.Count > 0)
        {
            _lastActiveFolder = snapshot.Windows[0].Handle;
            EnsureAltTabProxy();
        }

        if (_altTabProxy is not null && _lastActiveFolder != IntPtr.Zero)
        {
            var info = snapshot.Windows.FirstOrDefault(w => w.Handle == _lastActiveFolder);
            _altTabProxy.AttachTo(_lastActiveFolder, info?.Title ?? "文件夹");

            // 关键：先把标题设成文件夹名，再让窗口第一次出现 ——
            // ALT+TAB 只在窗口首次登记时读一次标题，之后再改它不会刷新
            if (!_altTabProxy.IsVisible)
            {
                _altTabProxy.Show();

                // 对"尚未显示"的窗口注册 DWM 缩略图是不生效的（ALT+TAB 会是黑块），
                // 所以显示之后重新注册一次
                var proxy = _altTabProxy;
                var target = _lastActiveFolder;
                var title = info?.Title ?? "文件夹";
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    proxy.Detach();
                    proxy.AttachTo(target, title);
                }), DispatcherPriority.Background);
            }
            else
            {
                _altTabProxy.UpdateThumbnail();
            }
        }

        var seen = new HashSet<IntPtr>();

        foreach (var info in snapshot.Windows)
        {
            seen.Add(info.Handle);

            if (!_items.TryGetValue(info.Handle, out var visual))
            {
                visual = CreateItemVisual(info);
                _items[info.Handle] = visual;
                _itemsHost.Children.Add(visual.Container);
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
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 12.5,
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
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrWhiteSpace(info.LocationPath))
        {
            panel.Children.Add(new TextBlock
            {
                Text = info.LocationPath,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                FontSize = 11.5,
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
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                FontSize = 10.5,
                Foreground = _textBrush,
            },
        });

        panel.Children.Add(new TextBlock
        {
            Text = description,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 11,
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

        if (!NativeMethods.IsIconic(hwnd) && IsFrontApplicationWindow(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
            Diag($"minimize: 0x{hwnd.ToInt64():X}");
            SetActiveWindow(IntPtr.Zero);
            return;
        }

        Activate(hwnd);
    }

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
        var handle = EnsureOurHandle();
        if (handle == IntPtr.Zero) return false;

        var current = handle;
        int guard = 0;

        while (guard++ < 300 && (current = NativeMethods.GetWindow(current, NativeMethods.GW_HWNDNEXT)) != IntPtr.Zero)
        {
            if (!NativeMethods.IsWindowVisible(current)) continue;
            if (NativeMethods.GetWindow(current, NativeMethods.GW_OWNER) != IntPtr.Zero) continue;
            if (IsShellWindow(NativeMethods.GetClassNameSafe(current))) continue;

            return current == target;
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
        menu.Items.Add(new Separator());

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

        menu.Items.Add(MenuAction("临时放回任务栏", () => Host.RestoreToTaskbar(visual.Handle)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuAction("关闭这个文件夹窗口", () => CloseWindow(visual.Handle)));
        menu.Items.Add(MenuAction("关闭所有文件夹窗口", App.CloseAllExplorerWindows));

        menu.PlacementTarget = visual.Container;
        menu.IsOpen = true;
    }

    private void ShowMenu()
    {
        var menu = BuildAppMenu();
        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    private ContextMenu BuildAppMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(CheckItem("接管任务栏（合并文件夹按钮）", App.Settings.TakeoverEnabled, Host.SetTakeover));
        menu.Items.Add(CheckItem("没有文件夹时自动隐藏", App.Settings.HideWhenEmpty, Host.SetHideWhenEmpty));
        menu.Items.Add(CheckItem("显示完整标题", App.Settings.ShowFullTitle, v =>
        {
            App.Settings.ShowFullTitle = v;
            App.Settings.Save();
            Host.RebuildDockItems();
        }));
        menu.Items.Add(new Separator());

        // 主题三项必须互斥：WPF 的 IsCheckable 会自己翻转勾选，所以点完要统一刷新一遍
        MenuItem? themeAuto = null;
        MenuItem? themeDark = null;
        MenuItem? themeLight = null;

        themeAuto = CheckItem("主题：跟随系统", App.Settings.Theme == DockTheme.Auto, _ => SelectTheme(DockTheme.Auto));
        themeDark = CheckItem("主题：深色", App.Settings.Theme == DockTheme.Dark, _ => SelectTheme(DockTheme.Dark));
        themeLight = CheckItem("主题：浅色", App.Settings.Theme == DockTheme.Light, _ => SelectTheme(DockTheme.Light));

        menu.Items.Add(themeAuto);
        menu.Items.Add(themeDark);
        menu.Items.Add(themeLight);

        void SelectTheme(DockTheme theme)
        {
            Host.SetTheme(theme);

            if (themeAuto is not null) themeAuto.IsChecked = App.Settings.Theme == DockTheme.Auto;
            if (themeDark is not null) themeDark.IsChecked = App.Settings.Theme == DockTheme.Dark;
            if (themeLight is not null) themeLight.IsChecked = App.Settings.Theme == DockTheme.Light;
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(CheckItem("开机自动启动", App.Settings.RunAtStartup, Host.SetRunAtStartup));
        menu.Items.Add(MenuAction("回到屏幕顶部居中", ResetPosition));
        menu.Items.Add(MenuAction("隐藏悬浮栏", () => Host.SetShowDock(false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuAction("关闭所有文件夹窗口", App.CloseAllExplorerWindows));
        menu.Items.Add(MenuAction("重启资源管理器", App.RestartExplorer));
        menu.Items.Add(MenuAction("退出 ExplorerDock", Host.ExitApp));

        return menu;
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

