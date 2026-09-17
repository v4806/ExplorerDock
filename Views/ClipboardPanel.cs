using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExplorerDock.Interop;
using ExplorerDock.Services;

namespace ExplorerDock.Views;

/// <summary>
/// 剪贴板面板（替换系统的 Win+V）。
///
/// 顶部是 Edge 那样的平铺标签页（全部 / 收藏），中间是记录列表，
/// 每条右侧有收藏与删除；底部显示条数与占用。
/// 面板是可激活窗口 —— 它需要键盘（↑↓/Enter/Esc）和"失活即隐藏"这两个能力，
/// 所以打开时会把焦点拿过来，关掉时再由调用方把焦点还给原来的窗口。
/// </summary>
internal sealed class ClipboardPanel : Window
{
    /// <summary>面板宽度：收藏页左侧多一条分组栏，所以整体放得宽一点。</summary>
    private const double PanelWidth = 520;

    /// <summary>面板固定高度：**不随条目数量变化**，项少就留白，项多就滚动。</summary>
    private const double PanelHeight = 580;
    private const double TextItemHeight = 88;
    private const double ImageItemHeight = 96;
    private const double ThumbWidth = 176;

    /// <summary>面板一打开就同步加载缩略图的条数，其余交给异步队列慢慢补。</summary>
    private const int SyncThumbnailCount = 8;

    private const int MaxThumbnailCache = 30;

    private readonly ClipboardStore _store;

    private readonly Border _panel;
    private readonly Grid _scaler = new();
    private readonly Grid _tabRow;
    private readonly StackPanel _tabs;
    private readonly Grid _sidebar;
    private readonly StackPanel _sidebarList;
    private readonly StackPanel _sidebarFooter;
    private readonly ScrollViewer _sidebarScroll;
    private readonly Canvas _overlay;
    private readonly ScrollViewer _scroller;
    private readonly StackPanel _list;
    private readonly TextBlock _status;

    /// <summary>整体缩放（跟悬浮栏的 Scale 设置联动）。</summary>
    private double _scale = 1.0;

    /// <summary>当前主题调色板（圆角、色值全从这里取，界面里不写死数字）。</summary>
    private ThemePalette _palette = ThemePalette.Resolve();

    // 收藏浮窗
    private Border? _favoritePopup;
    private ClipItem? _favoriteItem;

    /// <summary>上次选过的收藏分组（下次点收藏按钮默认还收进这里）。</summary>
    private string? _lastFavoriteGroup;

    /// <summary>当前那条"新建分组"输入行；同一时间只允许存在一行，连点不会叠加。</summary>
    private FrameworkElement? _newGroupRow;

    // ---------- 拖出去粘贴的状态 ----------

    private bool _dragArmed;
    private bool _suppressItemClick;
    private bool _dragging;
    private Point _dragOrigin;
    private ClipItem? _dragItem;

    /// <summary>拖动中轮询"鼠标是不是停在任务栏按钮上"（250ms 一次，够跟手又不费）。</summary>
    private readonly DispatcherTimer _taskbarHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>这次拖动里已经因为悬停激活过的窗口，避免反复抢前台。</summary>
    private IntPtr _hoverActivated;

    /// <summary>任务栏按钮（屏幕矩形 + 名字），拖动中第一次悬停到任务栏时才去取。</summary>
    private readonly List<(Rect Bounds, string Name)> _taskbarButtons = new();
    private bool _taskbarButtonsLoaded;

    /// <summary>
    /// 拖动期间盯着左键有没有松开。
    ///
    /// 不能只靠 WPF 的 MouseLeftButtonUp：悬停任务栏时我们会把目标窗口提到前台，
    /// 面板一失活，鼠标捕获就被系统收走了，之后松手我们根本收不到消息，
    /// 表现就是"拖出去松手毫无反应"。所以这里直接问系统按键状态。
    /// </summary>
    private readonly DispatcherTimer _dragWatchTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    /// <summary>拖动期间面板自己的窗口句柄（把鼠标捕获和光标按回来时要用）。</summary>
    private IntPtr _dragHwnd;

    /// <summary>左侧栏里能当作"拖放分组目标"的一行（GroupId 为 null 表示未分组）。</summary>
    private sealed class SidebarDropTarget
    {
        public string? GroupId;
        public Border? Element;
        public Brush BaseBackground = Brushes.Transparent;
        public Brush BaseBorderBrush = Brushes.Transparent;
    }

    /// <summary>拖动中光标正停在哪个分组上（用来给它加高亮）。</summary>
    private SidebarDropTarget? _dropHoverTarget;

    private const int VK_LBUTTON = 0x01;

    /// <summary>WM_SETCURSOR：拖动中把它拦下来，别让系统画成普通箭头。</summary>
    private const int WM_SETCURSOR = 0x0020;

    private const string AllGroups = "*";
    private const string NoGroup = "";

    /// <summary>收藏页当前筛的分组："*" = 全部，"" = 未分组，其它 = 分组 id。</summary>
    private string _groupFilter = AllGroups;

    // 配色（跟随悬浮栏的深/浅色体系）
    private Brush _panelBrush = Brushes.Transparent;
    private Brush _textBrush = Brushes.White;
    private Brush _mutedBrush = Brushes.Gray;
    private Brush _lineBrush = Brushes.Transparent;
    private Brush _ghostBrush = Brushes.Transparent;
    private Brush _hoverBrush = Brushes.Transparent;
    private Brush _selectBrush = Brushes.Transparent;
    private Brush _accentBrush = Brushes.Transparent;
    private Brush _onActiveTextBrush = Brushes.White;
    private Brush _onHoverTextBrush = Brushes.White;
    private Brush _selectedBorderBrush = Brushes.Transparent;
    private Brush _thumbBack = Brushes.Transparent;

    private readonly List<ItemVisual> _visuals = new();
    private readonly Dictionary<string, ImageSource> _thumbnailCache = new();
    private readonly List<string> _thumbnailCacheOrder = new();
    private bool _showFavorites;
    private bool _suppressDeactivate;
    private int _selected = -1;

    public ClipboardPanel(ClipboardStore store)
    {
        _store = store;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "ExplorerDock 剪贴板";
        FontFamily = _palette.Typeface;

        _tabs = new StackPanel { Orientation = Orientation.Horizontal };

        _tabRow = new Grid { Margin = new Thickness(14, 10, 14, 0) };
        _tabRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _tabRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _tabRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _list = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };

        _scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _list,
            Focusable = false,
        };

        _status = new TextBlock { FontSize = _palette.FontSizeSmall, Margin = new Thickness(16, 8, 16, 10), VerticalAlignment = VerticalAlignment.Center };

        // 收藏页左侧的分组栏（只有收藏页才显示）：
        // 上半截是可滚动的分组列表，下半截固定放"＋ 新建分组" ——
        // 分组多了也不会把新建入口挤出可视区。
        _sidebarList = new StackPanel { Width = 126 };
        _sidebarFooter = new StackPanel { Width = 126, Margin = new Thickness(0, 4, 0, 0) };

        var sidebarScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _sidebarList,
        };

        _sidebarScroll = sidebarScroll;

        _sidebar = new Grid { Width = 126, Margin = new Thickness(10, 4, 2, 4), Visibility = Visibility.Collapsed };
        _sidebar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(sidebarScroll, 0);
        Grid.SetRow(_sidebarFooter, 1);
        _sidebar.Children.Add(sidebarScroll);
        _sidebar.Children.Add(_sidebarFooter);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_sidebar, 0);
        Grid.SetColumn(_scroller, 1);
        content.Children.Add(_sidebar);
        content.Children.Add(_scroller);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_tabRow, 0);
        Grid.SetRow(content, 1);
        Grid.SetRow(_status, 2);
        layout.Children.Add(_tabRow);
        layout.Children.Add(content);
        layout.Children.Add(_status);

        // 收藏浮窗就浮在这一层上；Canvas 自己没有背景，所以不挡列表的鼠标事件
        _overlay = new Canvas();

        var root = new Grid();
        root.Children.Add(layout);
        root.Children.Add(_overlay);

        // 点面板里浮窗以外的任何地方 = 收起浮窗（并按已经选好的分组保存）
        root.PreviewMouseDown += (_, _) =>
        {
            if (_favoritePopup is not null && !_favoritePopup.IsMouseOver) CloseFavoritePopup();
        };

        _panel = new Border
        {
            CornerRadius = new CornerRadius(_palette.CornerRadius),
            BorderThickness = new Thickness(1),
            Child = _scaler,
            Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.45,
                Color = Colors.Black,
                RenderingBias = RenderingBias.Performance,
            },
        };

        // 缩放套在内容层上，边框画在外层 —— 和悬浮栏同一套做法，线宽不会被放大
        _scaler.Children.Add(root);

        Content = _panel;
        ApplyTone();

        _store.Changed += OnStoreChanged;

        // 主题一变（悬浮栏里改主题/缩放）立刻跟着刷新，不用等下次打开
        App.ThemeChanged += OnAppThemeChanged;

        PreviewKeyDown += OnPreviewKeyDown;

        // 拖动时鼠标已经被面板捕获，所以移动与松手都只能在窗口这一层收；
        // 捕获被系统收走时（面板失活）收不到松手，另有 _dragWatchTimer 兜底
        MouseLeftButtonUp += (_, _) => FinishItemDrag();

        // 面板空白处按下 = 按住整块面板挪位置。
        // 条目卡片、标签、按钮自己都把 MouseLeftButtonDown 标记成已处理，
        // 走不到这里；能走到这里的都是真正的空白区域。
        MouseLeftButtonDown += (_, _) =>
        {
            if (_dragging || _dragArmed) return;

            var beforeLeft = Left;
            var beforeTop = Top;

            try { DragMove(); } catch { }

            if (Math.Abs(Left - beforeLeft) > 0.5 || Math.Abs(Top - beforeTop) > 0.5) SavePanelPosition();
        };

        _taskbarHoverTimer.Tick += (_, _) => PollTaskbarHover();
        _dragWatchTimer.Tick += (_, _) =>
        {
            if (!_dragging) return;

            if (!NativeMethods.IsKeyDown(VK_LBUTTON))
            {
                FinishItemDrag();
                return;
            }

            // 拖动中"悬停呼出"（任务栏按钮 / 悬浮栏文件夹按钮）会把前台让给目标窗口，
            // 我们一失活，系统就收走鼠标捕获、WPF 顺手清掉 Mouse.OverrideCursor，
            // 光标于是从拖放样式变回普通箭头。
            // 捕获在我们手上时，鼠标哪怕停在别人的窗口上也照样把 WM_SETCURSOR 发给我们
            // （见 MessageHook 里的拦截），所以这里把捕获和光标按回来，光标就能全程保持。
            if (Mouse.Captured != this) Mouse.Capture(this);
            if (Mouse.OverrideCursor != DragCursor) Mouse.OverrideCursor = DragCursor;

            NativeMethods.ApplyCursor(DragCursorHandle);
            if (!NativeMethods.HasCapture(_dragHwnd)) NativeMethods.CaptureMouse(_dragHwnd);

            // 压在左侧分组上时给那一行加高亮（松手就归到那一组）
            UpdateGroupDropHover();
        };

        // 需求：点到其他任何界面，面板自己消失。
        // 但"刚弹出来的那一下"不算：Show() 激活窗口时系统的前台锁定规则会先把它压下去再抢回来，
        // 这一下会触发一次 Deactivated，直接当真的话面板会自己把自己关掉（弹出即消失）。
        Deactivated += (_, _) =>
        {
            if (_suppressDeactivate) return;

            // 拖动中不算失活：悬停任务栏按钮时我们会主动把目标窗口提到前台，
            // 这时按"点到别处"处理的话，面板会在拖动中途自己消失
            if (_dragging) return;

            // 焦点被我们自己的子窗口（确认框/设置窗/收藏浮窗）抢走时不算"用户离开面板"，
            // 面板不该因此自动关闭
            if (NativeMethods.IsOwnProcessForeground()) return;

            HidePanel();
        };
    }

    /// <summary>某条记录被点击（调用方负责写回剪贴板并粘贴）。</summary>
    public event Action<ClipItem>? ItemActivated;

    /// <summary>点了收藏按钮（第 4 轮的收藏浮窗接这里）。</summary>
    public event Action<ClipItem>? FavoriteRequested;

    /// <summary>点了删除。</summary>
    public event Action<ClipItem>? DeleteRequested;

    /// <summary>
    /// 把条目拖到别的软件窗口上松手（参数是落点窗口句柄）。
    /// 由外部把内容写进剪贴板并给那个窗口补一次 Ctrl+V。
    /// </summary>
    public event Action<ClipItem, IntPtr>? DroppedOnWindow;

    /// <summary>点了"全部清除"。</summary>
    public event Action? ClearRequested;

    /// <summary>面板被收起来了（失活、Esc、点条目之后）。</summary>
    public event Action? Hidden;

    /// <summary>当前选中的记录（键盘操作用）。</summary>
    public ClipItem? SelectedItem =>
        _selected >= 0 && _selected < _visuals.Count ? _visuals[_selected].Item : null;

    /// <summary>预热窗口句柄，省掉第一次弹出时的初始化开销。</summary>
    public void Preload()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        ApplyToolWindowStyle(handle);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        ApplyToolWindowStyle(handle);

        if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(MessageHook);
    }

    private IntPtr MessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 只在排查"面板收不到按键"时才有输出，正常使用看不到
        if (msg is 0x0100 or 0x0104) Services.ClipboardMonitor.Log($"panel WM_KEYDOWN vk={wParam}");

        // 拖动中光标必须一直是拖放样式：悬停呼出会让面板失活，
        // WPF 就不管光标了，系统会按"普通窗口"画成箭头。
        // 自己把这个消息吃掉，直接设回拖动光标。
        if (msg == WM_SETCURSOR && _dragging && DragCursorHandle != IntPtr.Zero)
        {
            NativeMethods.ApplyCursor(DragCursorHandle);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void ApplyToolWindowStyle(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;

        long style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(
            handle,
            NativeMethods.GWL_EXSTYLE,
            (style | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW);
    }

    // ---------- 显示 / 隐藏 ----------

    /// <summary>刷新内容并显示，同时把焦点拿过来。</summary>
    public void Present()
    {
        _suppressDeactivate = true;

        ApplyTone();
        Refresh();

        if (_visuals.Count > 0) SetSelected(0);
        else SetSelected(-1);

        // 缩放跟悬浮栏的 Scale 设置联动，套在内容层上（外层边框线宽不受影响）
        _scale = Math.Clamp(App.Settings.Scale, 1.0, 3.0);
        _scaler.LayoutTransform = new ScaleTransform(_scale, _scale);

        // 高度固定（用户要求）：不再按内容估算，列表里条目增减都不会让窗口抽动
        var area = SystemParameters.WorkArea;
        double logical = Math.Min(PanelHeight, Math.Max(240, area.Height / _scale - 60));
        Height = logical * _scale;
        Width = PanelWidth * _scale;
        PositionPanel(area);

        if (!IsVisible) Show();
        KeepOnTop();

        // 我们是后台进程，系统不允许直接抢焦点，走 ForceForeground 才稳
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) NativeMethods.ForceForeground(handle);

        // 抢到"前台窗口"还不够：按键消息发给的是**线程焦点窗口**，
        // 跨进程抢焦点时这两个不一定是同一个窗口，必须再把键盘焦点落到面板上，
        // 否则面板看着是前台的，按什么都没反应。
        Focus();
        Keyboard.Focus(this);
        NativeMethods.SetFocus(handle);

        // 等这一轮消息彻底处理完再恢复"失活即隐藏"，跳过显示瞬间的那次伪失活
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => _suppressDeactivate = false));

        Services.ClipboardMonitor.Log($"panel handle={handle} foreground={NativeMethods.GetForegroundWindow()} focus={NativeMethods.GetFocus()} active={IsActive}");
    }

    /// <summary>
    /// 打开面板自己的子窗口（确认框、设置窗）。期间失活不关面板（见 Deactivated 里的判断），
    /// 子窗口关掉后必须把焦点抢回面板 —— 否则面板看着还在，实际已经不是激活窗口，
    /// 后面点别处也不会再收到 Deactivated，面板就会赖着不走。
    /// </summary>
    private void ShowChildWindow(Action open)
    {
        try
        {
            open();
        }
        finally
        {
            Refocus();
        }
    }

    /// <summary>把前台和键盘焦点抢回面板。</summary>
    private void Refocus()
    {
        if (!IsVisible) return;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        NativeMethods.ForceForeground(handle);
        Focus();
        Keyboard.Focus(this);
        NativeMethods.SetFocus(handle);
    }

    /// <summary>收起面板。</summary>
    public void HidePanel()
    {
        if (!IsVisible) return;

        // 面板都收了，浮窗跟着收（设置早就即时生效了）
        if (_favoritePopup is not null)
        {
            _overlay.Children.Remove(_favoritePopup);
            _favoritePopup = null;
            _favoriteItem = null;
        }

        Services.ClipboardMonitor.Log("panel hidden");

        // 保险：万一在拖动没收尾的情况下被收起来（异常路径），
        // 把捕获、光标和悬浮栏交互一并还回去，不让"拖放光标"留在屏幕上
        if (_dragging)
        {
            _dragging = false;
            _dragItem = null;
            _dragArmed = false;
            _dragWatchTimer.Stop();
            _taskbarHoverTimer.Stop();
            Mouse.OverrideCursor = null;
            Mouse.Capture(null);
            NativeMethods.ReleaseMouseCapture();
            NativeMethods.RestoreArrowCursor();
            _dragHwnd = IntPtr.Zero;

            if (Application.Current is App app)
            {
                app.ResetDockDragHover();
                app.SetDockInteraction(true);
            }
        }

        Hide();
        Hidden?.Invoke();
    }

    private void OnAppThemeChanged()
    {
        if (!IsVisible) return;

        ApplyTone();
        Refresh();
    }

    /// <summary>库内容变了就刷一下（面板开着的时候才需要）。</summary>
    private void OnStoreChanged()
    {
        if (!IsVisible) return;
        Refresh();
        if (_selected >= _visuals.Count) SetSelected(_visuals.Count - 1);
    }

    /// <summary>
    /// 面板弹出的位置：按设置走"鼠标位置"，否则用上次拖动到的位置，都没有就回到默认位置。
    /// </summary>
    private void PositionPanel(Rect area)
    {
        // 鼠标位置：默认落在光标的右下角，放不下就翻到左上
        if (App.Settings.ClipboardAtCursor && NativeMethods.GetCursorPos(out var cursor))
        {
            double left = cursor.X + 12;
            double top = cursor.Y + 12;

            if (left + Width > area.Right) left = cursor.X - Width - 12;
            if (top + Height > area.Bottom) top = cursor.Y - Height - 12;

            Left = Math.Round(Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - Width)));
            Top = Math.Round(Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
            return;
        }

        // 上次拖动到的位置（还有一部分在屏幕里就用它）
        if (App.Settings.ClipboardLeft is double savedLeft &&
            App.Settings.ClipboardTop is double savedTop &&
            savedLeft + 80 <= area.Right && savedTop + 40 <= area.Bottom &&
            savedLeft >= area.Left - 40 && savedTop >= area.Top - 40)
        {
            Left = savedLeft;
            Top = savedTop;
            return;
        }

        Left = Math.Round(area.Left + (area.Width - Width) / 2);
        Top = Math.Round(area.Top + area.Height * 0.18);
    }

    /// <summary>把面板现在的位置记下来，下次还用这里。</summary>
    private void SavePanelPosition()
    {
        App.Settings.ClipboardLeft = Left;
        App.Settings.ClipboardTop = Top;
        App.Settings.Save();
    }

    private void KeepOnTop()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    // ---------- 列表 ----------

    private void Refresh()
    {
        BuildTabs();
        BuildSidebar();

        _list.Children.Clear();
        _visuals.Clear();

        var items = _showFavorites
            ? _store.Items.Where(i => i.Favorited && MatchesGroupFilter(i)).ToList()
            : _store.Items.ToList();

        if (items.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = _showFavorites ? "还没有收藏。点记录右边的 ☆ 就能收进来。" : "还没有记录。复制点什么就会出现在这里。",
                FontSize = _palette.FontSizeBody,
                Margin = new Thickness(10, 26, 10, 26),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Foreground = _mutedBrush,
            });
        }
        else
        {
            for (int i = 0; i < items.Count; i++)
            {
                // 条目之间一条很淡的分隔线：不然一片同色连在一起，分不清哪条是哪条
                if (i > 0) _list.Children.Add(CreateSeparator());

                var visual = CreateItemVisual(items[i]);
                _visuals.Add(visual);
                _list.Children.Add(visual.Root);
            }
        }

        UpdateStatus();

        // 列表重建后把选中态补回来（否则重建一次高亮就没了）
        if (_selected >= 0 && _selected < _visuals.Count) ApplyItemTone(_visuals[_selected], true);

        // 前几条**同步**加载缩略图：面板一打开就该看见图。
        // 之前全丢给异步队列，而列表一刷新就把队列里没跑完的任务作废了 ——
        // 表现就是"第一次打开没图、第二次打开才有"。
        for (int i = 0; i < _visuals.Count; i++)
        {
            var visual = _visuals[i];

            // 图片记录、"没位图但 HTML 里抠出了预览图"的记录，
            // 以及**复制进来的图片/视频文件**（也要给它做缩略图预览）
            if (visual.Item.Kind != ClipKind.Image
                && visual.Item.PreviewBlobId is null
                && FirstMediaFile(visual.Item) is null)
            {
                continue;
            }

            if (i < SyncThumbnailCount) LoadThumbnail(visual);
            else Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => LoadThumbnail(visual)));
        }
    }

    private void BuildTabs()
    {
        _tabRow.Children.Clear();
        _tabs.Children.Clear();

        _tabs.Children.Add(BuildTab("全部", !_showFavorites, () =>
        {
            _showFavorites = false;
            Refresh();
            SetSelected(_visuals.Count > 0 ? 0 : -1);
        }));

        _tabs.Children.Add(BuildTab("收藏", _showFavorites, () =>
        {
            _showFavorites = true;
            Refresh();
            SetSelected(_visuals.Count > 0 ? 0 : -1);
        }));

        Grid.SetColumn(_tabs, 0);
        _tabRow.Children.Add(_tabs);

        // 设置齿轮：直接开剪贴板设置窗（原来只能从托盘菜单进）
        var gearLabel = new TextBlock
        {
            Text = "\u2699",
            FontFamily = new FontFamily(_palette.IconFontFamily),
            FontSize = _palette.IconFontSize,
            Foreground = _mutedBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var gear = new Border
        {
            Width = 32,
            Height = 32,
            Margin = new Thickness(0, 0, 4, 0),
            CornerRadius = new CornerRadius(_palette.ButtonCornerRadius),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = Cursors.Hand,
            Child = gearLabel,
        };

        gear.MouseEnter += (_, _) =>
        {
            gear.Background = _ghostBrush;
            gearLabel.Foreground = _textBrush;
        };
        gear.MouseLeave += (_, _) =>
        {
            gear.Background = Brushes.Transparent;
            gearLabel.Foreground = _mutedBrush;
        };
        gear.MouseLeftButtonDown += (_, e) => e.Handled = true;
        gear.MouseLeftButtonUp += (_, e) =>
        {
            OpenSettings();
            e.Handled = true;
        };

        Grid.SetColumn(gear, 1);
        _tabRow.Children.Add(gear);

        var clear = new Border
        {
            Height = 32,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(_palette.ButtonCornerRadius),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "全部清除",
                FontSize = _palette.FontSizeBody,
                Foreground = _mutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        clear.MouseEnter += (_, _) =>
        {
            clear.Background = _ghostBrush;
            if (clear.Child is TextBlock label) label.Foreground = _textBrush;
        };
        clear.MouseLeave += (_, _) =>
        {
            clear.Background = Brushes.Transparent;
            if (clear.Child is TextBlock label) label.Foreground = _mutedBrush;
        };
        clear.MouseLeftButtonDown += (_, e) => e.Handled = true;
        clear.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;

            // "全部清除"会弹确认框（外部实现），算我们的子窗口
            ShowChildWindow(() => ClearRequested?.Invoke());
        };

        Grid.SetColumn(clear, 2);
        _tabRow.Children.Add(clear);
    }

    // ---------- 拖出去粘贴 ----------

    /// <summary>
    /// 开始拖动一条记录：鼠标捕获到面板上，这样拖到别的窗口上面时，
    /// 移动和松手仍然是我们收到（用的是自有拖动，不是 OLE 拖放）。
    /// </summary>
    /// <summary>
    /// 拖动条目时用的光标：OLE 的"拖放复制"（箭头 + 虚线框 + 加号），
    /// 和拖动文件时系统给的那个一样。取不到就退回四向箭头。
    /// </summary>
    /// <summary>拖放光标的原生句柄（不经过 WPF 直接设光标时要用）；静态字段按声明顺序初始化，必须在 DragCursor 之前。</summary>
    private static readonly IntPtr DragCursorHandle = NativeMethods.LoadDragCopyCursor();

    private static readonly Cursor DragCursor = CreateDragCursor();

    private static Cursor CreateDragCursor()
    {
        var handle = DragCursorHandle;

        if (handle != IntPtr.Zero)
        {
            try
            {
                var safe = new Microsoft.Win32.SafeHandles.SafeFileHandle(handle, ownsHandle: false);
                return CursorInteropHelper.Create(safe);
            }
            catch
            {
                // 拿不到就退回系统光标
            }
        }

        return Cursors.SizeAll;
    }

    private void BeginItemDrag(ClipItem item)
    {
        _dragging = true;
        _dragItem = item;
        _dragArmed = false;
        _dragHwnd = new WindowInteropHelper(this).Handle;

        Mouse.Capture(this);
        Mouse.OverrideCursor = DragCursor;
        NativeMethods.ApplyCursor(DragCursorHandle);
        _hoverActivated = IntPtr.Zero;
        _taskbarHoverTimer.Start();
        _dragWatchTimer.Start();

        // 拖动期间锁住悬浮栏自己的鼠标交互，否则它会把这串鼠标动作当成"拖动悬浮栏位置"
        if (Application.Current is App app) app.SetDockInteraction(false);
        Services.ClipboardMonitor.Log($"drag start item={item.Id} kind={item.Kind}");
    }

    /// <summary>
    /// 松手：看光标底下是哪个窗口，交给外部去"写剪贴板 + 补 Ctrl+V"。
    /// 不用 OLE 拖放的原因见 <see cref="FinishItemDrag"/> 的调用方说明 —— 图片拖过去目标拿不到内容。
    /// </summary>
    private void FinishItemDrag()
    {
        if (!_dragging) return;

        _dragging = false;
        _dragArmed = false;

        _taskbarHoverTimer.Stop();
        _dragWatchTimer.Stop();
        _hoverActivated = IntPtr.Zero;
        _taskbarButtonsLoaded = false;   // 下次拖动重新读一遍（窗口在此期间会变）
        ClearDropHover();

        if (Application.Current is App app)
        {
            app.ResetDockDragHover();
            app.SetDockInteraction(true);   // 拖动收尾，把悬浮栏的交互还回去
        }

        var item = _dragItem;
        _dragItem = null;

        Mouse.OverrideCursor = null;
        Mouse.Capture(null);

        // 拖动中可能被我们原生抢回过捕获/光标，这里一并收干净，
        // 免得松手后光标还停在"拖放复制"样式上
        NativeMethods.ReleaseMouseCapture();
        NativeMethods.RestoreArrowCursor();
        _dragHwnd = IntPtr.Zero;

        // 松手这一下是"拖放"，不是"点击粘贴"
        Dispatcher.BeginInvoke(new Action(() => _suppressItemClick = false), DispatcherPriority.Background);

        if (item is null) return;

        if (!NativeMethods.GetCursorPos(out var point)) return;

        // 落点还在面板自己身上：落在左侧分组行上就是"换分组"，其它位置什么都不做
        // （这里不走 WindowFromPoint —— 面板是分层窗口，边缘透明处它会返回下面的窗口）
        if (IsPointOnSelf(point.X, point.Y))
        {
            var dropTarget = FindSidebarDropTarget(point.X, point.Y);
            if (dropTarget is not null) MoveItemToGroup(item, dropTarget.GroupId);
            return;
        }

        var under = NativeMethods.WindowFromPoint(point);
        if (under == IntPtr.Zero) return;

        var target = NativeMethods.GetAncestor(under, NativeMethods.GA_ROOT);
        if (target == IntPtr.Zero) target = under;

        // 拖到悬浮栏上不算拖到别的软件
        if (NativeMethods.IsOwnProcess(target)) return;

        Services.ClipboardMonitor.Log($"drag drop item={item.Id} target={target}");
        DroppedOnWindow?.Invoke(item, target);
    }

    /// <summary>
    /// 拖动中悬停在任务栏按钮上：把那个任务对应的窗口提到前台，方便直接把内容拖进去。
    ///
    /// 任务栏自带这个行为，但它只认 OLE 拖放；我们用的是自有拖动（见 BeginItemDrag），
    /// 所以得自己识别：光标底下是不是任务栏 → 那个按钮叫什么名字 → 按标题找回窗口。
    /// </summary>
    private void PollTaskbarHover()
    {
        if (!_dragging) return;

        if (!NativeMethods.GetCursorPos(out var point)) return;

        // 光标还在面板自己身上时不算"悬停到别的东西上"
        if (IsPointOnSelf(point.X, point.Y)) return;

        // 1) 悬浮栏上的文件夹按钮（用户要求：和任务栏一样，悬停就呼出那个文件夹窗口）
        if (Application.Current is App app && app.TryActivateDockItemAt(point.X, point.Y)) return;

        // 2) 任务栏上的任务按钮
        var under = NativeMethods.WindowFromPoint(point);
        if (under == IntPtr.Zero) return;

        var root = NativeMethods.GetAncestor(under, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero) root = under;

        var className = NativeMethods.GetClassNameSafe(root);
        if (className is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")) return;

        if (!_taskbarButtonsLoaded) LoadTaskbarButtons();

        string? title = null;

        foreach (var (bounds, name) in _taskbarButtons)
        {
            if (point.X >= bounds.Left && point.X <= bounds.Right && point.Y >= bounds.Top && point.Y <= bounds.Bottom)
            {
                title = name;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(title)) return;

        var target = FindWindowByTitle(title);

        if (target == IntPtr.Zero)
        {
            Services.ClipboardMonitor.Log($"taskbar hover title='{title}' -> no matching window");
            return;
        }

        if (target == _hoverActivated) return;

        _hoverActivated = target;
        Services.ClipboardMonitor.Log($"taskbar hover -> activate hwnd={target} title='{title}'");
        NativeMethods.ForceForeground(target);
    }

    /// <summary>屏幕坐标是不是落在面板自己身上。</summary>
    private bool IsPointOnSelf(int screenX, int screenY)
    {
        if (!IsVisible) return false;

        try
        {
            var topLeft = PointToScreen(new Point(0, 0));
            var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));

            return screenX >= topLeft.X && screenX <= bottomRight.X
                && screenY >= topLeft.Y && screenY <= bottomRight.Y;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 取任务栏按钮（屏幕矩形 + 名字）。
    ///
    /// Win11 的任务栏是 XAML 画的：按钮不在 MSTaskListWClass 的 toolbar 里
    /// （TB_BUTTONCOUNT 返回 0），ElementFromPoint 也只肯给出任务栏外壳（名字是空的）。
    /// 真正能取到按钮的是 XAML 岛屿窗口 Windows.UI.Composition.DesktopWindowContentBridge
    /// 的 UIA 子树 —— 从它下面把所有 Button 捞出来缓存。
    /// </summary>
    private void LoadTaskbarButtons()
    {
        _taskbarButtonsLoaded = true;
        _taskbarButtons.Clear();

        try
        {
            foreach (var shellClass in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            {
                var tray = NativeMethods.FindWindow(shellClass, null);
                if (tray == IntPtr.Zero) continue;

                var bridge = NativeMethods.FindChildWindow(tray, "Windows.UI.Composition.DesktopWindowContentBridge");
                var host = bridge != IntPtr.Zero ? bridge : tray;

                var root = System.Windows.Automation.AutomationElement.FromHandle(host);
                if (root is null) continue;

                var buttons = root.FindAll(
                    System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ControlTypeProperty,
                        System.Windows.Automation.ControlType.Button));

                foreach (System.Windows.Automation.AutomationElement element in buttons)
                {
                    try
                    {
                        var name = StripWindowCount(element.Current.Name);
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        var bounds = element.Current.BoundingRectangle;
                        if (bounds.IsEmpty || bounds.Width < 4 || bounds.Height < 4) continue;

                        _taskbarButtons.Add((bounds, name));
                    }
                    catch
                    {
                        // 元素在遍历过程中消失，跳过
                    }
                }
            }
        }
        catch
        {
            // UIA 不可用就当没有任务栏按钮
        }

        Services.ClipboardMonitor.Log($"taskbar buttons loaded={_taskbarButtons.Count}");
    }

    /// <summary>去掉任务栏按钮名尾部的「 - 1 个运行窗口」「 和另外 N 个页面」这类计数后缀。</summary>
    private static string StripWindowCount(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var text = name.Trim();

        int and = text.IndexOf(" 和另外 ", StringComparison.Ordinal);
        if (and > 0) text = text[..and];

        int marker = text.LastIndexOf(" - ", StringComparison.Ordinal);
        if (marker > 0 && text.EndsWith("窗口", StringComparison.Ordinal)) text = text[..marker];

        return text.Trim();
    }

    /// <summary>按标题找回顶层窗口（任务栏按钮名和窗口标题可能互为前缀/包含）。</summary>
    private static IntPtr FindWindowByTitle(string title)
    {
        IntPtr found = IntPtr.Zero;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            var text = NativeMethods.GetWindowTextSafe(hwnd);
            if (string.IsNullOrWhiteSpace(text)) return true;

            if (text.Contains(title, StringComparison.OrdinalIgnoreCase)
                || title.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>齿轮按钮：直接打开剪贴板设置（和托盘菜单里那个入口是同一个窗口）。</summary>
    private void OpenSettings()
    {
        // 设置窗是面板的子窗口：面板保持打开，关掉后焦点自动抢回来
        ShowChildWindow(() => ClipboardSettingsWindow.Show(null));
        UpdateStatus();
    }

    private Border BuildTab(string text, bool active, Action onClick)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = _palette.FontSizeMedium,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = active ? _onActiveTextBrush : _mutedBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var tab = new Border
        {
            Height = 38,
            MinWidth = 64,
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(14, 0, 14, 0),
            // 与列表项/侧边栏/悬浮栏同一套高亮：高亮块 + 主题描边，
            // 不再有"蓝色下划线""蓝色胶囊边"这种只属于某处的独立装饰
            CornerRadius = new CornerRadius(_palette.ItemCornerRadius),
            Background = active ? _selectBrush : Brushes.Transparent,
            BorderThickness = new Thickness(_palette.ItemBorderThickness),
            BorderBrush = active ? _selectedBorderBrush : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = label,
        };

        if (!active)
        {
            // 悬浮时背景变高亮，文字按高亮色明暗自动反色（不再是写死的白/黑）
            tab.MouseEnter += (_, _) =>
            {
                tab.Background = _hoverBrush;
                label.Foreground = _onHoverTextBrush;
            };

            tab.MouseLeave += (_, _) =>
            {
                tab.Background = Brushes.Transparent;
                label.Foreground = _mutedBrush;
            };
        }

        tab.MouseLeftButtonDown += (_, e) => e.Handled = true;
        tab.MouseLeftButtonUp += (_, e) =>
        {
            onClick();
            e.Handled = true;
        };

        return tab;
    }

    /// <summary>条目之间的弱分隔线。</summary>
    private Border CreateSeparator() => new()
    {
        Height = 1,
        Margin = new Thickness(12, 3, 12, 3),
        Background = new SolidColorBrush(_palette.Separator),
    };

    private ItemVisual CreateItemVisual(ClipItem item)
    {
        // 文件类条目里如果是图片/视频，就按"媒体"来显示（缩略图 + 更高的行），
        // 而不是一行"1 个文件"
        var mediaPath = FirstMediaFile(item);
        bool isMedia = item.Kind == ClipKind.Image || mediaPath is not null;

        double height = isMedia ? ImageItemHeight : TextItemHeight;

        var thumb = new Image
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
        };

        var thumbHost = new Border
        {
            Width = 88,
            Height = 60,
            Margin = new Thickness(0, 0, 10, 0),
            CornerRadius = new CornerRadius(_palette.ThumbCornerRadius),
            Background = _thumbBack,
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            Child = thumb,
            Visibility = isMedia || item.PreviewBlobId is not null
                ? Visibility.Visible
                : Visibility.Collapsed,
        };

        var preview = new TextBlock
        {
            Text = item.Preview,
            FontSize = _palette.FontSizeMedium,
            LineHeight = 18,
            MaxHeight = 64,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _textBrush,
        };

        var meta = new TextBlock
        {
            Text = Describe(item),
            FontSize = _palette.FontSizeSmall,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = _mutedBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(preview);
        textStack.Children.Add(meta);

        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(thumbHost);
        content.Children.Add(textStack);

        var star = new TextBlock
        {
            Text = item.Favorited ? "★" : "☆",
            FontSize = _palette.FontSizeLarge,
            Foreground = item.Favorited ? _accentBrush : _mutedBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var starButton = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(_palette.ButtonCornerRadius),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = star,
        };

        var deleteButton = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(_palette.ButtonCornerRadius),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "\u2715",
                FontSize = _palette.FontSizeBody,
                Foreground = _mutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(content, 0);
        Grid.SetColumn(starButton, 1);
        Grid.SetColumn(deleteButton, 2);
        grid.Children.Add(content);
        grid.Children.Add(starButton);
        grid.Children.Add(deleteButton);

        var root = new Border
        {
            Height = height,
            Margin = new Thickness(2, 1, 2, 1),
            Padding = new Thickness(10, 6, 6, 6),
            CornerRadius = new CornerRadius(_palette.ItemCornerRadius),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(_palette.ItemBorderThickness),
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = grid,
        };

        var visual = new ItemVisual(item, root, thumbHost, preview, thumb, meta, star, height);

        root.MouseEnter += (_, _) =>
        {
            if (_visuals.IndexOf(visual) == _selected) return;
            root.Background = _hoverBrush;
            ApplyItemTextTone(visual, _onHoverTextBrush);
        };
        root.MouseLeave += (_, _) =>
        {
            if (_visuals.IndexOf(visual) == _selected) return;
            root.Background = Brushes.Transparent;
            ApplyItemTextTone(visual, null);
        };

        // 点击条目 = 粘贴回原来的窗口；按住拖动 = 把这个条目"拖"到别的软件上，松手就在那里粘贴
        root.MouseLeftButtonDown += (_, e) =>
        {
            _dragArmed = true;
            _dragOrigin = e.GetPosition(this);
            e.Handled = true;
        };

        root.MouseMove += (_, e) =>
        {
            if (!_dragArmed || _dragging || e.LeftButton != MouseButtonState.Pressed) return;

            // 按下后要真的移动超过系统拖拽阈值才算拖动，否则"点一下粘贴"会被误判成拖
            var now = e.GetPosition(this);
            if (Math.Abs(now.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(now.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _suppressItemClick = true;
            BeginItemDrag(item);
        };

        root.MouseLeftButtonUp += (_, e) =>
        {
            _dragArmed = false;

            if (_suppressItemClick)
            {
                e.Handled = true;
                return;
            }

            SetSelected(_visuals.IndexOf(visual));
            ItemActivated?.Invoke(item);
            e.Handled = true;
        };

        starButton.MouseLeftButtonDown += (_, e) => e.Handled = true;
        starButton.MouseLeftButtonUp += (_, e) =>
        {
            // 已收藏 → 再点一下直接取消（不弹浮窗）；未收藏 → 弹浮窗选分组
            if (item.Favorited)
            {
                _store.SetFavorited(item, false, null);

                // 浮窗正好开着（刚收藏完又反悔）就一并收起，否则直接刷列表
                if (_favoritePopup is not null) CloseFavoritePopup();
                else Refresh();
            }
            else
            {
                ShowFavoritePopup(item, starButton);
            }

            e.Handled = true;
        };
        starButton.MouseEnter += (_, _) => starButton.Background = _hoverBrush;
        starButton.MouseLeave += (_, _) => starButton.Background = Brushes.Transparent;

        deleteButton.MouseLeftButtonDown += (_, e) => e.Handled = true;
        deleteButton.MouseLeftButtonUp += (_, e) =>
        {
            DeleteRequested?.Invoke(item);
            e.Handled = true;
        };
        deleteButton.MouseEnter += (_, _) => deleteButton.Background = _hoverBrush;
        deleteButton.MouseLeave += (_, _) => deleteButton.Background = Brushes.Transparent;

        return visual;
    }

    private static string Describe(ClipItem item)
    {
        var when = item.CreatedUtc.ToLocalTime().ToString("MM-dd HH:mm");

        return item.Kind switch
        {
            ClipKind.Image => $"{ImageSizeText(item)} · {item.SizeText} · {when}" + (item.Text is not null ? " · 含文字" : string.Empty),
            ClipKind.Files => $"{(item.Files?.Length ?? 0)} 个文件 · {when}",
            _ => $"{(item.Text?.Length ?? 0)} 字 · {when}",
        };
    }

    private static string ImageSizeText(ClipItem item) => item.MeasuredSize ?? "图片";

    // ---------- 缩略图 ----------

    private static readonly string[] ImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic", ".avif",
    };

    private static readonly string[] VideoExtensions =
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg", ".ts", ".rmvb", ".3gp",
    };

    /// <summary>
    /// 文件类条目里第一个图片/视频文件。
    /// 用户要求：就算复制的是文件，只要是图片/视频，就得像图片/视频那样显示缩略图。
    /// </summary>
    private static string? FirstMediaFile(ClipItem item)
    {
        if (item.Files is null) return null;

        foreach (var path in item.Files)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            var extension = Path.GetExtension(path).ToLowerInvariant();

            if (ImageExtensions.Contains(extension) || VideoExtensions.Contains(extension)) return path;
        }

        return null;
    }

    private void LoadThumbnail(ItemVisual visual)
    {
        if (visual.ThumbnailLoaded) return;

        // 列表被重建过：这个视觉对象已经不在界面上了，放弃
        if (visual.Root.Parent is null) return;

        try
        {
            if (_thumbnailCache.TryGetValue(visual.Item.Id, out var cached))
            {
                visual.Thumbnail.Source = cached;
                visual.ThumbnailLoaded = true;
                visual.Meta.Text = Describe(visual.Item);
                return;
            }

            var raw = _store.GetImageBytes(visual.Item);
            var isHtmlPreview = false;

            if (raw is null)
            {
                // 文件类条目：是图片/视频就直接拿系统缩略图 ——
                // 视频会给首帧、图片会给预览图，让它们以媒体该有的样子显示
                if (visual.Item.Kind == ClipKind.Files)
                {
                    var media = FirstMediaFile(visual.Item);
                    var fileThumb = media is null ? null : ShellThumbnail.Get(media, (int)(ThumbWidth * 2));

                    if (fileThumb is not null)
                    {
                        visual.Thumbnail.Source = fileThumb;
                        CacheThumbnail(visual.Item.Id, fileThumb);
                    }

                    visual.ThumbnailLoaded = true;
                    visual.Meta.Text = Describe(visual.Item);
                    return;
                }

                // 剪贴板上没有位图格式（QQ 复制图文）：用从 HTML 里抠出来的那张图当缩略图
                raw = _store.GetPreviewBytes(visual.Item);
                isHtmlPreview = raw is not null;
            }

            if (raw is null)
            {
                // 别的办法都拿不到图了：交给隐藏的浏览器内核去渲染这份网页内容
                RequestHtmlPreview(visual);
                return;
            }

            var format = visual.Item.ImageFormat ?? "DIB";
            ImageSource? source;

            if (isHtmlPreview)
            {
                // 抠出来的是 HTML 里嵌的 PNG/JPEG 原始字节，直接解
                visual.Item.MeasuredSize ??= DibTools.ReadPngSizeText(raw);
                source = DecodeThumbnail(raw);
            }
            else if (string.Equals(format, "EMF", StringComparison.OrdinalIgnoreCase))
            {
                // 矢量图元文件：WPF 不认，借 GDI+ 画一张预览图；存储的仍是原始 EMF 字节
                if (DibTools.TryReadEmfSize(raw, out int emfWidth, out int emfHeight))
                {
                    visual.Item.MeasuredSize = $"{emfWidth}×{emfHeight}";
                }

                source = DibTools.RenderEmfThumbnail(raw, (int)ThumbWidth);
            }
            else if (string.Equals(format, "PNG", StringComparison.OrdinalIgnoreCase))
            {
                visual.Item.MeasuredSize = DibTools.ReadPngSizeText(raw);
                source = DecodeThumbnail(raw);
            }
            else
            {
                if (DibTools.TryReadHeader(raw, out int width, out int height, out _, out _, out _))
                {
                    visual.Item.MeasuredSize = $"{Math.Abs(width)}×{Math.Abs(height)}";
                }

                var bmp = DibTools.WrapAsBmp(raw);
                source = bmp is null ? null : DecodeThumbnail(bmp);
            }

            visual.Meta.Text = Describe(visual.Item);

            if (source is null) return;

            visual.Thumbnail.Source = source;
            visual.ThumbnailLoaded = true;
            CacheThumbnail(visual.Item.Id, source);
        }
        catch
        {
            // 解不出来就只显示文字，不影响其他功能
        }
    }

    /// <summary>
    /// 最后一条退路：让隐藏的 WebView2 把这条记录的 HTML 渲染成一张图。
    /// 覆盖的是"图不在 &lt;img&gt; 里"的情况（CSS 背景图、远程图、复杂排版），
    /// 解析 HTML 抠不出来，但浏览器内核画得出来。
    /// </summary>
    private void RequestHtmlPreview(ItemVisual visual)
    {
        var item = visual.Item;
        if (item.PreviewRenderTried) return;

        var renderer = HtmlPreviewRenderer.Instance;
        if (renderer is null) return;

        var html = item.Formats?
            .FirstOrDefault(f => string.Equals(f.Name, "HTML Format", StringComparison.OrdinalIgnoreCase))?
            .Bytes;

        if (html is not { Length: > 0 }) return;

        // 这条记录只渲染一次：失败也不反复排队
        item.PreviewRenderTried = true;

        renderer.Request(item.Id, HtmlPreviewRenderer.ExtractFragment(html), bytes =>
        {
            if (bytes is null || bytes.Length == 0) return;

            _store.SetPreviewBlob(item, bytes);

            try
            {
                if (visual.Root.Parent is null) return;

                var image = DecodeThumbnail(bytes);
                if (image is null) return;

                visual.Thumbnail.Source = image;
                visual.ThumbnailLoaded = true;
                visual.ThumbHost.Visibility = Visibility.Visible;
                visual.Meta.Text = Describe(visual.Item);
                CacheThumbnail(visual.Item.Id, image);
            }
            catch
            {
                // 渲染结果解不出来也不影响别的
            }
        });
    }

    /// <summary>缩略图缓存：列表重建、面板反复开关时不用重新解码。</summary>
    private void CacheThumbnail(string id, ImageSource image)
    {
        _thumbnailCache[id] = image;
        _thumbnailCacheOrder.Remove(id);
        _thumbnailCacheOrder.Add(id);

        while (_thumbnailCacheOrder.Count > MaxThumbnailCache)
        {
            _thumbnailCache.Remove(_thumbnailCacheOrder[0]);
            _thumbnailCacheOrder.RemoveAt(0);
        }
    }

    private static ImageSource? DecodeThumbnail(byte[] data)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = (int)ThumbWidth;
        image.StreamSource = new MemoryStream(data);
        image.EndInit();
        image.Freeze();
        return image;
    }

    // ---------- 选择与键盘 ----------

    private void SetSelected(int index)
    {
        // 先把上一个还原
        if (_selected >= 0 && _selected < _visuals.Count) ApplyItemTone(_visuals[_selected], false);

        _selected = index;

        if (index < 0 || index >= _visuals.Count) return;

        ApplyItemTone(_visuals[index], true);
        _visuals[index].Root.BringIntoView();
    }

    /// <summary>
    /// 选中/未选中的外观 —— 完全按悬浮栏那一套来：
    /// 底色用主题的高亮色，文字同时换成"高亮块上的文字色"（悬浮栏选中项也是这么反白的）。
    /// </summary>
    private void ApplyItemTone(ItemVisual visual, bool selected)
    {
        visual.Root.Background = selected ? _selectBrush : Brushes.Transparent;
        visual.Root.BorderBrush = selected ? _selectedBorderBrush : Brushes.Transparent;
        ApplyItemTextTone(visual, selected ? _onActiveTextBrush : null);
    }

    /// <summary>
    /// 条目里所有文字的取色：传 null 表示"没有高亮"（恢复普通文字色），
    /// 传高亮反色刷子（悬浮态/选中态各自那支）时整条反色。
    /// 反色值由 <see cref="ThemePalette.OnColor"/> 按高亮色的明暗自动算出来，不再手写白字。
    /// </summary>
    private void ApplyItemTextTone(ItemVisual visual, Brush? onHighlight)
    {
        visual.Preview.Foreground = onHighlight ?? _textBrush;
        visual.Meta.Foreground = onHighlight ?? _mutedBrush;
        visual.Star.Foreground = visual.Item.Favorited ? _accentBrush : (onHighlight ?? _mutedBrush);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 输入法激活时 WPF 会把按键标成 ImeProcessed，真正的键在 ImeProcessedKey 里
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        Services.ClipboardMonitor.Log($"panel key={e.Key}->{key} selected={_selected}");

        switch (key)
        {
            case Key.Escape:
                // 浮窗开着就先收浮窗，再按一次才收面板
                if (_favoritePopup is not null)
                {
                    CloseFavoritePopup();
                }
                else
                {
                    HidePanel();
                }

                e.Handled = true;
                break;

            case Key.Up:
                if (_visuals.Count > 0)
                {
                    SetSelected(_selected <= 0 ? _visuals.Count - 1 : _selected - 1);
                }

                e.Handled = true;
                break;

            case Key.Down:
                if (_visuals.Count > 0)
                {
                    SetSelected(_selected < 0 || _selected >= _visuals.Count - 1 ? 0 : _selected + 1);
                }

                e.Handled = true;
                break;

            case Key.Enter:
                if (SelectedItem is { } item) ItemActivated?.Invoke(item);
                e.Handled = true;
                break;

            case Key.Delete:
                if (SelectedItem is { } target) DeleteRequested?.Invoke(target);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 收藏浮窗：点某条的 ☆ 时弹出来，用一排按钮列出全部分组（不用下拉菜单），
    /// 点哪个高亮哪个并立刻生效；点面板别处 / 切到别的程序 / Esc 都会关掉它，
    /// 关的时候设置早已按最后点的那一下保存好了。
    /// </summary>
    private void ShowFavoritePopup(ClipItem item, FrameworkElement anchor)
    {
        CloseFavoritePopup();

        _favoriteItem = item;

        // 点收藏按钮就先收进来（沿用上次选的分组）；之后改分组只是更新它
        _store.SetFavorited(item, true, _lastFavoriteGroup);

        var stack = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };

        stack.Children.Add(new TextBlock
        {
            Text = "收藏到",
            FontSize = _palette.FontSizeMedium,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
        });

        var groups = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), MaxWidth = 268 };
        BuildFavoriteGroupButtons(groups, item);
        stack.Children.Add(groups);

        _favoritePopup = new Border
        {
            Background = _panelBrush,
            // 边框走主题描边色（和菜单一致），不再单独用强调色描一圈
            BorderBrush = _lineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(_palette.CornerRadius),
            Child = stack,
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 3,
                Direction = 270,
                Opacity = 0.5,
                Color = Colors.Black,
            },
        };

        _overlay.Children.Add(_favoritePopup);
        _favoritePopup.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var size = _favoritePopup.DesiredSize;
        var corner = anchor.TranslatePoint(new Point(0, 0), _overlay);

        // 贴着收藏按钮放；贴边时翻到另一侧，别跑出面板外
        // （overlay 在缩放层里面，所以这里用的是缩放前的坐标）
        double maxLeft = Math.Max(6, _scaler.ActualWidth - size.Width - 8);
        double left = Math.Max(6, Math.Min(corner.X + anchor.ActualWidth - size.Width, maxLeft));
        double top = corner.Y + anchor.ActualHeight + 4;

        if (top + size.Height > _scaler.ActualHeight - 8) top = Math.Max(8, corner.Y - size.Height - 4);

        Canvas.SetLeft(_favoritePopup, left);
        Canvas.SetTop(_favoritePopup, top);
    }

    private void BuildFavoriteGroupButtons(WrapPanel host, ClipItem item)
    {
        host.Children.Clear();

        host.Children.Add(GroupChip("未分组", string.IsNullOrEmpty(item.GroupId), () =>
        {
            _lastFavoriteGroup = null;
            _store.SetGroup(item, null);
            CloseFavoritePopup();   // 点任意分组即收工：分组即时生效，不用再点"完成"
        }));

        foreach (var group in _store.Groups)
        {
            var id = group.Id;

            host.Children.Add(GroupChip(group.Name, string.Equals(item.GroupId, id, StringComparison.Ordinal), () =>
            {
                _lastFavoriteGroup = id;
                _store.SetGroup(item, id);
                CloseFavoritePopup();
            }));
        }

        // 新建分组：建完把这条归到新分组，同样收起浮窗
        host.Children.Add(GroupChip("＋ 新建", false, () => ToggleNewGroup(host, () =>
        {
            _store.SetGroup(item, _lastFavoriteGroup);
            CloseFavoritePopup();
        })));
    }

    /// <summary>关掉浮窗。设置已经即时生效，这里只负责收起来并刷一下列表。</summary>
    private void CloseFavoritePopup()
    {
        if (_favoritePopup is null) return;

        _overlay.Children.Remove(_favoritePopup);
        _favoritePopup = null;
        _favoriteItem = null;
        _newGroupRow = null;   // 浮窗里那行"新建"跟着浮窗一起没了

        Refresh();
    }

    /// <summary>收藏页左侧的分组栏：全部 / 未分组 / 各自定义分组，底部固定"新建分组"。</summary>
    private void BuildSidebar()
    {
        _sidebarList.Children.Clear();
        _sidebarFooter.Children.Clear();
        _newGroupRow = null;   // 输入行刚刚被清掉，指针跟着作废

        if (!_showFavorites)
        {
            _sidebar.Visibility = Visibility.Collapsed;
            return;
        }

        _sidebar.Visibility = Visibility.Visible;

        _sidebarList.Children.Add(SidebarItem("全部", _groupFilter == AllGroups, () =>
        {
            _groupFilter = AllGroups;
            Refresh();
        }));

        _sidebarList.Children.Add(SidebarItem("未分组", _groupFilter == NoGroup, () =>
        {
            _groupFilter = NoGroup;
            Refresh();
        }, new SidebarDropTarget { GroupId = null }));

        foreach (var group in _store.Groups)
        {
            var id = group.Id;

            var row = SidebarItem(group.Name, _groupFilter == id, () =>
            {
                _groupFilter = id;
                Refresh();
            }, new SidebarDropTarget { GroupId = id });

            row.ToolTip = "右键删除这个分组";

            row.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;

                bool ok = false;
                ShowChildWindow(() => ok = Views.ConfirmDialog.Confirm(
                    "删除分组？",
                    $"「{group.Name}」里的收藏会退回未分组，记录本身不会删。",
                    "删除",
                    "取消"));

                if (!ok)
                {
                    return;
                }

                _store.RemoveGroup(group);

                if (_groupFilter == id) _groupFilter = AllGroups;
                Refresh();
            };

            _sidebarList.Children.Add(row);
        }

        _sidebarFooter.Children.Add(SidebarItem("＋ 新建分组", false, () => ToggleNewGroup(_sidebarFooter, Refresh)));
    }

    private Border SidebarItem(string text, bool active, Action onClick, SidebarDropTarget? drop = null)
    {
        var item = new Border
        {
            Height = 30,
            Margin = new Thickness(0, 0, 0, 2),
            Padding = new Thickness(10, 0, 8, 0),
            CornerRadius = new CornerRadius(_palette.ItemCornerRadius),
            Background = active ? _selectBrush : Brushes.Transparent,
            BorderThickness = new Thickness(_palette.ItemBorderThickness),
            BorderBrush = active ? _selectedBorderBrush : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                FontSize = _palette.FontSizeBody,
                Foreground = active ? _onActiveTextBrush : _mutedBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        item.MouseLeftButtonDown += (_, e) => e.Handled = true;
        item.MouseLeftButtonUp += (_, e) =>
        {
            onClick();
            e.Handled = true;
        };

        // 这一行能不能当"拖放分组目标"（"全部"只是筛选，不能）
        if (drop is not null)
        {
            drop.Element = item;
            drop.BaseBackground = active ? _selectBrush : Brushes.Transparent;
            drop.BaseBorderBrush = active ? _selectedBorderBrush : Brushes.Transparent;
            item.Tag = drop;
        }

        return item;
    }

    /// <summary>屏幕坐标落在哪个分组行上（不在左侧栏上就返回 null）。</summary>
    private SidebarDropTarget? FindSidebarDropTarget(int screenX, int screenY)
    {
        if (_sidebar.Visibility != Visibility.Visible) return null;

        foreach (var child in _sidebarList.Children)
        {
            if (child is not Border row || row.Tag is not SidebarDropTarget target) continue;
            if (!row.IsVisible || row.ActualWidth <= 0 || row.ActualHeight <= 0) continue;

            try
            {
                var topLeft = row.PointToScreen(new Point(0, 0));
                var bottomRight = row.PointToScreen(new Point(row.ActualWidth, row.ActualHeight));

                if (screenX < topLeft.X || screenX > bottomRight.X) continue;
                if (screenY < topLeft.Y || screenY > bottomRight.Y) continue;

                return target;
            }
            catch
            {
                // 布局还没算完，跳过这一项
            }
        }

        return null;
    }

    /// <summary>拖动中：光标压在哪个分组上就高亮哪一行，松手就归到那一组。</summary>
    private void UpdateGroupDropHover()
    {
        SidebarDropTarget? hit = null;

        if (NativeMethods.GetCursorPos(out var cursor)) hit = FindSidebarDropTarget(cursor.X, cursor.Y);

        if (ReferenceEquals(hit, _dropHoverTarget)) return;

        ApplyDropHover(_dropHoverTarget, false);
        _dropHoverTarget = hit;
        ApplyDropHover(hit, true);
    }

    private void ClearDropHover()
    {
        if (_dropHoverTarget is null) return;

        ApplyDropHover(_dropHoverTarget, false);
        _dropHoverTarget = null;
    }

    private void ApplyDropHover(SidebarDropTarget? target, bool on)
    {
        if (target?.Element is not Border row) return;

        if (on)
        {
            row.Background = _selectBrush;
            row.BorderBrush = _selectedBorderBrush;
            row.BorderThickness = new Thickness(_palette.ItemBorderThickness + 1);
            return;
        }

        row.Background = target.BaseBackground;
        row.BorderBrush = target.BaseBorderBrush;
        row.BorderThickness = new Thickness(_palette.ItemBorderThickness);
    }

    /// <summary>拖到左侧分组行上松手：把这条记录放进那个分组（还没收藏的会一并收藏）。</summary>
    private void MoveItemToGroup(ClipItem item, string? groupId)
    {
        Services.ClipboardMonitor.Log($"drag -> group item={item.Id} group={groupId ?? "(未分组)"}");

        _store.SetFavorited(item, true, groupId);
        Refresh();
    }

    private bool MatchesGroupFilter(ClipItem item) => _groupFilter switch
    {
        AllGroups => true,
        NoGroup => string.IsNullOrEmpty(item.GroupId),
        _ => string.Equals(item.GroupId, _groupFilter, StringComparison.Ordinal),
    };

    /// <summary>
    /// 在指定容器里就地放一个输入框 + 确定按钮，回车或点确定建分组。
    ///
    /// 这里**故意不挂 LostFocus 自动取消**：输入框刚要焦点就被别的东西抢走一次，
    /// 它就会自己消失，表现成"点了新建分组一点反应都没有"。
    /// 现在只有回车、点确定、或按 Esc 才会收起来。
    /// </summary>
    /// <summary>
    /// "＋ 新建分组"的开关：点一下出现输入行，再点一下收起来（toggle）。
    /// </summary>
    private void ToggleNewGroup(Panel container, Action after)
    {
        if (_newGroupRow is not null && container.Children.Contains(_newGroupRow))
        {
            container.Children.Remove(_newGroupRow);
            _newGroupRow = null;
            return;
        }

        StartNewGroup(container, after);
    }

    private void StartNewGroup(Panel container, Action after)
    {
        // 连点"新建分组"不能越堆越多：先把上一条还没提交的输入行摘掉
        if (_newGroupRow is not null)
        {
            container.Children.Remove(_newGroupRow);
            _newGroupRow = null;
        }

        var box = new TextBox
        {
            Width = 66,
            Height = 26,
            FontSize = _palette.FontSizeBody,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = _ghostBrush,
            Foreground = _textBrush,
            CaretBrush = _textBrush,
            BorderBrush = _accentBrush,
            BorderThickness = new Thickness(1),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

        void Finish(bool create)
        {
            if (!container.Children.Contains(row)) return;

            container.Children.Remove(row);

            if (ReferenceEquals(_newGroupRow, row)) _newGroupRow = null;

            if (create)
            {
                var name = box.Text.Trim();

                if (name.Length > 0)
                {
                    var group = _store.CreateGroup(name);
                    _lastFavoriteGroup = group.Id;
                    _groupFilter = group.Id;
                }
            }

            after();
        }

        var confirm = SmallButton("确定", () => Finish(true));
        confirm.Height = 26;
        confirm.MinWidth = 40;
        confirm.Padding = new Thickness(6, 0, 6, 0);
        confirm.Margin = new Thickness(6, 0, 0, 0);

        row.Children.Add(box);
        row.Children.Add(confirm);

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Finish(true);
            else if (e.Key == Key.Escape) Finish(false);
        };

        _newGroupRow = row;
        container.Children.Add(row);
        box.Focus();
    }

    private Border GroupChip(string text, bool active, Action onClick)
    {
        var chip = new Border
        {
            Height = 26,
            Margin = new Thickness(0, 0, 6, 4),
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(13),
            Background = active ? _selectBrush : _ghostBrush,
            BorderThickness = new Thickness(1),
            BorderBrush = active ? _selectedBorderBrush : Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                FontSize = _palette.FontSizeBody,
                Foreground = active ? _onActiveTextBrush : _mutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        chip.MouseLeftButtonDown += (_, e) => e.Handled = true;
        chip.MouseLeftButtonUp += (_, e) =>
        {
            onClick();
            e.Handled = true;
        };

        return chip;
    }

    private Border SmallButton(string text, Action onClick)
    {
        var button = new Border
        {
            MinWidth = 74,
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(_palette.ButtonCornerRadius),
            Background = _ghostBrush,
            BorderThickness = new Thickness(1),
            BorderBrush = _lineBrush,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                FontSize = _palette.FontSizeBody,
                Foreground = _textBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, e) =>
        {
            onClick();
            e.Handled = true;
        };

        return button;
    }

    private void UpdateStatus()
    {
        int total = _store.Count;
        int favorites = _store.Items.Count(i => i.Favorited);
        int showing = _visuals.Count;

        int limit = App.Settings.ClipboardMaxItems;
        string limitText = limit <= 0 ? "不限" : $"{limit} 条";

        _status.Text = _showFavorites
            ? $"收藏 {showing} 条 · 库内共 {total} 条（含收藏 {favorites} 条） · 占用 {ClipItem.FormatSize(_store.TotalBytes)}"
            : $"共 {total} 条（收藏 {favorites} 条） · 占用 {ClipItem.FormatSize(_store.TotalBytes)} · 上限 {limitText}";
    }

    // ---------- 配色 ----------

    /// <summary>
    /// 配色来自 <see cref="ThemePalette"/>：和悬浮栏是同一份色值定义，
    /// 四种主题（跟随系统 / 深色 / 浅色 / 自定义）以及缩放、字体、透明度全都跟着走。
    /// 顺带把滚动条也换成同一风格的细条 —— 系统默认那根灰色粗滚动条跟这套界面放一起很突兀。
    /// </summary>
    private void ApplyTone()
    {
        var palette = ThemePalette.Resolve();
        _palette = palette;

        // 背景色直接就是悬浮栏那个值 —— 不再做任何"面板专用"的调整
        // 面板不透明（用户反馈半透明太透）：深色主题上半透明会把后面的窗口透出来
        _panelBrush = new SolidColorBrush(Color.FromArgb(0xFF, palette.Background.R, palette.Background.G, palette.Background.B));

        _textBrush = new SolidColorBrush(palette.Text);
        _mutedBrush = new SolidColorBrush(palette.Muted);
        _lineBrush = new SolidColorBrush(palette.Border);
        _hoverBrush = new SolidColorBrush(palette.Hover);
        _ghostBrush = new SolidColorBrush(palette.Chip);
        _selectBrush = new SolidColorBrush(palette.Active);
        _thumbBack = new SolidColorBrush(palette.ThumbBack);
        _accentBrush = new SolidColorBrush(palette.Accent);
        _onActiveTextBrush = new SolidColorBrush(palette.ActiveText);
        _onHoverTextBrush = new SolidColorBrush(palette.HoverText);

        // 选中项的描边：悬浮栏用的是主题描边色（自定义主题下不描边），这里照抄
        _selectedBorderBrush = palette.Custom ? Brushes.Transparent : new SolidColorBrush(palette.Border);

        _panel.BorderThickness = new Thickness(palette.Custom ? palette.CustomBorderThickness : palette.DockBorderThickness);
        _panel.Background = _panelBrush;
        _panel.BorderBrush = _lineBrush;
        _status.Foreground = _mutedBrush;

        // 圆角与投影也照搬悬浮栏那套参数，两个窗口放一起才像一套
        _panel.CornerRadius = new CornerRadius(palette.CornerRadius);
        _panel.Effect = new DropShadowEffect
        {
            BlurRadius = palette.ShadowBlur,
            ShadowDepth = palette.ShadowDepth,
            Direction = 270,
            Opacity = palette.ShadowOpacity,
            Color = Colors.Black,
            RenderingBias = RenderingBias.Performance,
        };

        // 字体与整体透明度也跟悬浮栏一致
        FontFamily = palette.Typeface;
        Opacity = palette.Opacity;

        ApplyScrollBarStyle();
    }

    private static Color ParseColor(string? text, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text) && ColorConverter.ConvertFromString(text) is Color color) return color;
        }
        catch
        {
            // 非法输入用兜底色
        }

        return fallback;
    }

    /// <summary>把两个滚动条换成细的、跟主题同色的版本（系统默认那根太粗、颜色也对不上）。</summary>
    private void ApplyScrollBarStyle()
    {
        try
        {
            var thumb = _mutedBrush is SolidColorBrush muted
                ? $"#{(byte)(muted.Color.A * 0.35):X2}{muted.Color.R:X2}{muted.Color.G:X2}{muted.Color.B:X2}"
                : "#30FFFFFF";

            var xaml = $@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='ScrollBar'>
  <Setter Property='Width' Value='8'/>
  <Setter Property='Margin' Value='0,0,4,0'/>
  <Setter Property='Background' Value='Transparent'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='ScrollBar'>
        <Grid Background='Transparent'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template>
                  <ControlTemplate TargetType='Thumb'>
                    <Border CornerRadius='{_palette.ThumbCornerRadius}' Background='{thumb}'/>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Focusable='False'/>
            </Track.IncreaseRepeatButton>
            <Track.DecreaseRepeatButton>
              <RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Focusable='False'/>
            </Track.DecreaseRepeatButton>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

            var style = (Style)System.Windows.Markup.XamlReader.Parse(xaml);

            _scroller.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = style;
            _sidebarScroll.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = style;
        }
        catch
        {
            // 样式没做出来也能正常滚动，只是样子回到系统的
        }
    }

    private static SolidColorBrush NewBrush(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));

    private sealed class ItemVisual
    {
        public ItemVisual(ClipItem item, Border root, Border thumbHost, TextBlock preview, Image thumbnail, TextBlock meta, TextBlock star, double height)
        {
            Item = item;
            Root = root;
            ThumbHost = thumbHost;
            Preview = preview;
            Thumbnail = thumbnail;
            Meta = meta;
            Star = star;
            Height = height;
        }

        public ClipItem Item { get; }

        public Border Root { get; }

        /// <summary>缩略图容器：渲染出图之后要把它从折叠状态打开。</summary>
        public Border ThumbHost { get; }

        public TextBlock Preview { get; }

        public Image Thumbnail { get; }

        public TextBlock Meta { get; }

        public TextBlock Star { get; }

        public double Height { get; }

        public bool ThumbnailLoaded { get; set; }
    }
}












