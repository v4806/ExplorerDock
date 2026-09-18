using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ExplorerDock.Interop;
using ExplorerDock.Services;

namespace ExplorerDock.Views;

/// <summary>
/// Alt+Tab 切换面板。
///
/// 它是 WS_EX_NOACTIVATE 且 ShowActivated=false 的：**绝不抢前台焦点**。
/// 一旦它把自己激活了，用户松开 Alt 时前台已经变成我们自己，
/// "切到哪个窗口"就无从谈起；鼠标点击也照样能收到，不需要焦点。
///
/// 卡片尺寸恒定、面板不整体缩放：放不下就垂直滚动（面板外轮廓不因为卡片变大而变大）。
/// </summary>
internal sealed class AltTabOverlay : Window
{
    private const double CardWidth = 300;
    private const double CardHeight = 226;
    private const double CardGap = 12;

    /// <summary>一行最多几张卡。卡片放大后取 4，面板宽度才和改动前基本一致。</summary>
    private const int MaxPerRow = 4;

    /// <summary>合并卡里最多摆几个窗口（3×3）；更多的用角标显示总数，靠 Alt+~ 切换。</summary>
    private const int MaxGridCells = 9;

    /// <summary>一次最多挂多少个实时缩略图，防止窗口特别多时开销失控。</summary>
    private const int MaxThumbnailSlots = 60;

    private const double PanelPadding = 20;

    private readonly Border _panel;
    private readonly WrapPanel _wrap;
    private readonly ScrollViewer _scroll;
    private readonly List<CardVisual> _cards = new();

    private ThemePalette _palette = ThemePalette.Resolve();
    private int _selected = -1;

    /// <summary>
    /// 鼠标锚点：面板刚弹出来时 WPF 会补发一次 MouseMove（光标其实一动没动），
    /// 不放它过去的话，选中项会被光标底下的那张卡抢走。
    /// </summary>
    private bool _cursorArmed;
    private Brush _cardNormal = Brushes.Transparent;
    private Brush _cardSelected = Brushes.Transparent;
    private Brush _selectedBorderBrush = Brushes.Transparent;
    private Brush _textBrush = Brushes.White;
    private Brush _onActiveTextBrush = Brushes.White;
    private Brush _thumbBack = Brushes.Transparent;
    private Brush _chipBrush = Brushes.Transparent;

    public AltTabOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _wrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = CardWidth + CardGap,
            ItemHeight = CardHeight + CardGap,
        };

        _scroll = new ScrollViewer
        {
            // 常驻滚动条，不用 Auto：Auto 会在"出现/消失"之间切换内容区宽度，
            // 而卡片宽度是固定的，宽度一少几个像素，一行里最后一张就被挤到下一行 ——
            // 卡片一多（正好要滚动的时候）整个布局就跟着挪位。
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = _wrap,
        };

        // 滚动时要重新摆缩略图（DWM 缩略图贴的是屏幕坐标，不跟着 WPF 滚动走）
        _scroll.ScrollChanged += (_, _) => Scrolled?.Invoke();

        // 面板是分层窗口（AllowsTransparency），WPF 在这种窗口里默认**不裁剪**内容：
        // 滚动区放不下的卡片会直接画到面板外面（用户报的"卡片根本没被裁剪"）。
        // 显式开裁剪，滑动条才真的把超出的卡片收纳掉。
        _scroll.ClipToBounds = true;

        FontFamily = _palette.Typeface;

        _panel = new Border
        {
            CornerRadius = new CornerRadius(_palette.CornerRadius),
            BorderThickness = new Thickness(_palette.Custom ? _palette.CustomBorderThickness : _palette.DockBorderThickness),
            Child = _scroll,
            Effect = new DropShadowEffect
            {
                BlurRadius = _palette.ShadowBlur,
                ShadowDepth = _palette.ShadowDepth,
                Direction = 270,
                Opacity = _palette.ShadowOpacity,
                Color = Colors.Black,
                RenderingBias = RenderingBias.Performance,
            },
        };

        Content = _panel;
        Title = "ExplorerDock";

        // 接收系统拖放：从别的程序把内容拖到某张卡上 → 粘到那张卡对应的窗口
        AllowDrop = true;
        DragOver += OnCardDragOver;
        Drop += OnCardDrop;
    }

    /// <summary>鼠标移到某张卡上（系统行为：悬停即选中）。</summary>
    public event Action<int>? Hovered;

    /// <summary>鼠标点了某张卡（系统行为：点一下立即切过去）。</summary>
    public event Action<int>? Clicked;

    /// <summary>面板滚动了（控制器据此重新摆一遍缩略图）。</summary>
    public event Action? Scrolled;

    /// <summary>
    /// 拖放悬停在某张卡上：控制器据此把那张卡的窗口切到前台 ——
    /// 和悬浮栏按钮"拖动经过就把窗口呼出来"是同一个行为，让用户在松手前就看得见目标窗口。
    ///
    /// 面板自己不抢前台（WS_EX_NOACTIVATE），所以被激活的始终是目标窗口。
    /// </summary>
    public event Action<int>? DragHovered;

    /// <summary>
    /// 拖放落在某张卡上（index = 卡片序号，data = 拖来的数据）。
    /// 由控制器决定粘到哪个窗口并收起面板 —— 面板自己不知道卡片对应的是哪个句柄。
    /// </summary>
    public event Action<int, IDataObject>? Dropped;

    /// <summary>预热窗口句柄：省掉第一次弹出时的窗口初始化开销（不显示，所以不会闪）。</summary>
    public void Preload() => new WindowInteropHelper(this).EnsureHandle();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        long style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(
            handle,
            NativeMethods.GWL_EXSTYLE,
            (style | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE) & ~NativeMethods.WS_EX_APPWINDOW);
    }

    /// <summary>按当前列表重建卡片并显示，selected 是默认选中的那张。</summary>
    public void Present(IReadOnlyList<AltTabWindowInfo> items, int selected)
    {
        _palette = ThemePalette.Resolve();
        _cursorArmed = false;   // 每次弹出都重新等一次"鼠标真的动了"
        BuildCards(items);
        ApplyTone();

        // 外框粗细跟悬浮栏/剪贴板面板一致，尺寸里也要按实际值预留，否则面板会差几个像素
        double border = _palette.Custom ? _palette.CustomBorderThickness : _palette.DockBorderThickness;
        double chrome = PanelPadding * 2 + border * 2;

        // 滚动条的位置一直预留出来：它出现/消失时不会把每行挤成少一张卡
        double reserve = SystemParameters.VerticalScrollBarWidth;

        // 再留几个像素的余量：取整、边框、DPI 换算各差一点，正好卡在"放得下/放不下"的边界上，
        // 一行就会少一张卡（用户报的"变成滚动列表之后布局就乱了"）。
        const double ScrollSlack = 8;

        var area = SystemParameters.WorkArea;
        double maxWidth = area.Width * 0.94;
        double maxHeight = area.Height * 0.78;

        // 列数：既不超过 MaxPerRow，也不超过屏幕放得下的数量
        int fit = (int)((maxWidth - chrome - reserve - ScrollSlack) / (CardWidth + CardGap));
        int columns = Math.Clamp(items.Count, 1, Math.Max(1, Math.Min(MaxPerRow, fit)));
        int rows = Math.Max(1, (int)Math.Ceiling(items.Count / (double)columns));

        double scrollWidth = columns * (CardWidth + CardGap) + reserve + ScrollSlack;
        double scrollHeight = Math.Min(rows * (CardHeight + CardGap), Math.Max(CardHeight, maxHeight - chrome));

        _scroll.Width = scrollWidth;
        _scroll.Height = scrollHeight;

        // 右内边距减掉预留的滚动条宽度，左右留白看上去才一样宽
        _panel.Padding = new Thickness(PanelPadding, PanelPadding, Math.Max(0, PanelPadding - reserve), PanelPadding);

        double width = Math.Round(chrome + scrollWidth);
        double height = Math.Round(chrome + scrollHeight);

        Width = width;
        Height = height;
        Left = Math.Round(area.Left + (area.Width - width) / 2);
        Top = Math.Round(area.Top + Math.Max(0, (area.Height - height) / 2 - area.Height * 0.05));

        _scroll.ScrollToTop();

        if (!IsVisible) Show();
        KeepOnTop();
        SetSelection(selected);
    }

    public void SetSelection(int index)
    {
        _selected = index;

        for (int i = 0; i < _cards.Count; i++)
        {
            _cards[i].Apply(_cardNormal, _cardSelected, _selectedBorderBrush, _textBrush, _onActiveTextBrush, _thumbBack, _chipBrush, i == index);
        }
    }

    /// <summary>
    /// 每张卡里每个预览格的屏幕物理像素矩形 + 对应的源窗口句柄，交给缩略图宿主挂 DWM 缩略图。
    /// 必须在面板已经显示、布局算完之后调用；滚出可视区的格子不收集（省掉没必要的注册）。
    /// </summary>
    public List<ThumbnailSlot> GetThumbnailSlots()
    {
        var slots = new List<ThumbnailSlot>();
        if (_cards.Count == 0) return slots;

        var visible = VisibleBounds();

        foreach (var card in _cards)
        {
            foreach (var cell in card.Cells)
            {
                if (slots.Count >= MaxThumbnailSlots) return slots;
                if (cell.Handle == IntPtr.Zero) continue;

                var element = cell.Element;
                if (element.ActualWidth <= 2 || element.ActualHeight <= 2) continue;

                try
                {
                    var topLeft = element.PointToScreen(new Point(0, 0));
                    var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));

                    int width = (int)Math.Round(bottomRight.X - topLeft.X);
                    int height = (int)Math.Round(bottomRight.Y - topLeft.Y);

                    if (width <= 2 || height <= 2) continue;

                    var rect = new Int32Rect((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y), width, height);

                    if (visible is { } area)
                    {
                        // 只保留落在可视区里的那一段：不然宿主窗口会被"只露出一角的格子"撑到面板之外，
                        // 缩略图就贴着画到面板外面去了（跟上面 ClipToBounds 一起解决"没裁剪"）
                        rect = Intersect(rect, area);
                        if (rect.Width <= 2 || rect.Height <= 2) continue;
                    }

                    slots.Add(new ThumbnailSlot(cell.Handle, rect));
                }
                catch
                {
                    // 布局还没算完，这一格先跳过
                }
            }
        }

        return slots;
    }

    public void Dismiss()
    {
        if (IsVisible) Hide();
    }

    /// <summary>
    /// 缩略图层收到拖放时转过来：命中判定仍走面板自己的卡片矩形
    /// （坐标取的是全局光标位置，跟事件来自哪个窗口无关）。
    /// </summary>
    public void ForwardDragOver(DragEventArgs e) => OnCardDragOver(this, e);

    /// <summary>见 <see cref="ForwardDragOver"/>。</summary>
    public void ForwardDrop(DragEventArgs e) => OnCardDrop(this, e);

    /// <summary>
    /// 缩略图层收到滚轮时转过来滚列表（那层窗口盖在卡片上，滚轮到不了这里）。
    /// 一格滚轮滚一行，跟 ScrollViewer 自己的手感一致。
    /// </summary>
    public void ForwardMouseWheel(System.Windows.Input.MouseWheelEventArgs e)
    {
        try
        {
            var step = CardHeight + CardGap;
            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - (Math.Sign(e.Delta) * step));
            e.Handled = true;
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 屏幕坐标落在哪张卡上（拖放落点判定）。
    /// 用屏幕坐标判：卡片滚出可视区时它的屏幕矩形也在面板外，天然不会命中。
    /// </summary>
    public bool TryGetCardAt(int screenX, int screenY, out int index)
    {
        index = -1;

        if (!IsVisible) return false;

        for (int i = 0; i < _cards.Count; i++)
        {
            var root = _cards[i].Root;

            try
            {
                if (!root.IsVisible || root.ActualWidth <= 0 || root.ActualHeight <= 0) continue;

                var topLeft = root.PointToScreen(new Point(0, 0));
                var bottomRight = root.PointToScreen(new Point(root.ActualWidth, root.ActualHeight));

                if (screenX < topLeft.X || screenX > bottomRight.X) continue;
                if (screenY < topLeft.Y || screenY > bottomRight.Y) continue;

                index = i;
                return true;
            }
            catch
            {
                // 布局还没算完，跳过这张卡
            }
        }

        return false;
    }

    /// <summary>
    /// 拖着东西悬停在卡片上：回"可放下"的光标，并把这颗卡选中（给用户一个明确的目标提示）。
    /// </summary>
    private void OnCardDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;

        if (!WindowPaste.CanPaste(e.Data)) return;

        if (!NativeMethods.GetCursorPos(out var point)) return;
        if (!TryGetCardAt(point.X, point.Y, out int index)) return;

        e.Effects = DragDropEffects.Copy;
        Hovered?.Invoke(index);
        DragHovered?.Invoke(index);
    }

    private void OnCardDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (!NativeMethods.GetCursorPos(out var point)) return;
        if (!TryGetCardAt(point.X, point.Y, out int index)) return;

        Dropped?.Invoke(index, e.Data);
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

    /// <summary>面板可视区的屏幕物理像素矩形（用来判断哪些格子还看得见）。</summary>
    private Int32Rect? VisibleBounds()
    {
        if (_scroll.ActualWidth <= 2 || _scroll.ActualHeight <= 2) return null;

        try
        {
            var topLeft = _scroll.PointToScreen(new Point(0, 0));
            var bottomRight = _scroll.PointToScreen(new Point(_scroll.ActualWidth, _scroll.ActualHeight));

            return new Int32Rect(
                (int)Math.Round(topLeft.X),
                (int)Math.Round(topLeft.Y),
                (int)Math.Round(bottomRight.X - topLeft.X),
                (int)Math.Round(bottomRight.Y - topLeft.Y));
        }
        catch
        {
            return null;
        }
    }

    private static bool Intersects(Int32Rect a, Int32Rect b)
        => a.X < b.X + b.Width && b.X < a.X + a.Width
        && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    /// <summary>两块矩形的交集；不相交时返回零尺寸。</summary>
    private static Int32Rect Intersect(Int32Rect a, Int32Rect b)
    {
        int left = Math.Max(a.X, b.X);
        int top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.X + a.Width, b.X + b.Width);
        int bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);

        if (right <= left || bottom <= top) return new Int32Rect(left, top, 0, 0);

        return new Int32Rect(left, top, right - left, bottom - top);
    }

    // ---------- 卡片 ----------

    private void BuildCards(IReadOnlyList<AltTabWindowInfo> items)
    {
        _wrap.Children.Clear();
        _cards.Clear();

        for (int i = 0; i < items.Count; i++)
        {
            var card = CreateCard(items[i], i);
            _cards.Add(card);
            _wrap.Children.Add(card.Root);
        }
    }

    private CardVisual CreateCard(AltTabWindowInfo info, int index)
    {
        var icon = new Image
        {
            Source = info.Icon,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var title = new TextBlock
        {
            Text = info.Title,
            Margin = new Thickness(7, 0, 0, 0),
            FontSize = _palette.FontSizeBody,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // 角标：合并卡显示一共几个窗口（卡里最多摆 9 个，多出来的靠角标看出来）
        Border? badge = null;
        TextBlock? badgeText = null;

        if (info.GroupCount > 1)
        {
            badgeText = new TextBlock
            {
                Text = info.GroupCount.ToString(),
                FontSize = _palette.FontSizeSmall,
                VerticalAlignment = VerticalAlignment.Center,
            };

            badge = new Border
            {
                CornerRadius = new CornerRadius(_palette.ItemCornerRadius),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = badgeText,
            };
        }

        var header = new Grid { Margin = new Thickness(9, 7, 9, 5) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(title, 1);
        header.Children.Add(icon);
        header.Children.Add(title);

        if (badge is not null)
        {
            Grid.SetColumn(badge, 2);
            header.Children.Add(badge);
        }

        // 预览区：合并卡按网格摆组内各窗口，普通卡就是一格
        var cells = new List<ThumbCell>();
        var preview = BuildPreview(info, cells);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(preview, 1);
        layout.Children.Add(header);
        layout.Children.Add(preview);

        var root = new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            Margin = new Thickness(CardGap / 2),
            CornerRadius = new CornerRadius(_palette.ItemCornerRadius),
            BorderThickness = new Thickness(_palette.ItemBorderThickness),
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = layout,
        };

        // 用 MouseMove 而不是 MouseEnter，并且只认"真的动过"的那次：
        // 面板刚弹出来时鼠标往往正好压在某张卡上，直接认了就会把用户的键盘选择打乱
        root.MouseMove += (_, _) =>
        {
            if (!_cursorArmed)
            {
                _cursorArmed = true;
                return;
            }

            Hovered?.Invoke(index);
        };
        root.MouseLeftButtonUp += (_, e) =>
        {
            Clicked?.Invoke(index);
            e.Handled = true;
        };

        return new CardVisual(info.Handle, root, title, badge, badgeText, cells);
    }

    /// <summary>
    /// 预览区网格：n 个窗口排成 ceil(sqrt(n)) 列，最多 3×3 格，顺序就是 Z 序
    /// （左上角是最后活动过的那个窗口，也就是这张卡真正会切过去的窗口）。
    /// </summary>
    private Grid BuildPreview(AltTabWindowInfo info, List<ThumbCell> cells)
    {
        var preview = new Grid
        {
            Margin = new Thickness(9, 0, 9, 9),
            ClipToBounds = true,
        };

        int shown = Math.Min(Math.Max(info.GroupCount, 1), MaxGridCells);
        int columns = (int)Math.Ceiling(Math.Sqrt(shown));
        int rows = (int)Math.Ceiling(shown / (double)columns);

        for (int i = 0; i < columns; i++)
        {
            preview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int i = 0; i < rows; i++)
        {
            preview.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        for (int i = 0; i < shown; i++)
        {
            // 同一个窗口的多个标签页共用一张画面：只有"窗口当前显示的那个标签页"挂缩略图，
            // 其余标签页这一格只显示图标（不然几张格子会是同一张图，反而分不清）
            var showThumbnail = i < info.GroupCount && info.Cells[i].ShowThumbnail;
            var handle = showThumbnail ? info.Cells[i].Handle : IntPtr.Zero;

            // 缩略图由 DWM 实时渲染后盖在这块区域上；拿不到缩略图的窗口显示大图标
            var fallback = new Image
            {
                Source = info.Icon,
                Width = 32,
                Height = 32,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var cell = new Border
            {
                CornerRadius = new CornerRadius(_palette.ThumbCornerRadius),
                Margin = new Thickness(1.5),
                ClipToBounds = true,
                Child = fallback,
            };

            Grid.SetColumn(cell, i % columns);
            Grid.SetRow(cell, i / columns);
            preview.Children.Add(cell);

            cells.Add(new ThumbCell(handle, cell));
        }

        return preview;
    }

    private void ApplyTone()
    {
        // 配色与悬浮栏、菜单、剪贴板面板完全同源
        var palette = _palette;
        FontFamily = palette.Typeface;

        _panel.CornerRadius = new CornerRadius(palette.CornerRadius);

        // 面板外框跟悬浮栏、剪贴板面板用同一套参数（自定义主题走自定义粗细，否则走 DockBorderThickness）
        _panel.BorderThickness = new Thickness(palette.Custom ? palette.CustomBorderThickness : palette.DockBorderThickness);
        _panel.Background = NewBrush(palette.SurfaceAlpha, palette.Background.R, palette.Background.G, palette.Background.B);
        _panel.BorderBrush = NewBrush(palette.Border.A, palette.Border.R, palette.Border.G, palette.Border.B);
        // 普通卡底色用 ThumbBack（"缩略图底"那个弱色块）：它贴近面板底色，
        // 这样选中卡的 Active 才拉得开对比 —— 跟剪贴板面板一个道理
        // （那边普通条目是透明的、只有选中的才是亮块）。
        // 之前用 Chip：Chip 和 Active 在自定义主题下只差几个百分点，选中项根本看不出来。
        _cardNormal = NewBrush(palette.ThumbBack.A, palette.ThumbBack.R, palette.ThumbBack.G, palette.ThumbBack.B);
        _cardSelected = NewBrush(palette.Active.A, palette.Active.R, palette.Active.G, palette.Active.B);
        // 选中卡的描边跟剪贴板面板一致：主题描边色（自定义主题下不描边），不再用独立的高亮蓝
        _selectedBorderBrush = palette.Custom ? Brushes.Transparent : NewBrush(palette.Border.A, palette.Border.R, palette.Border.G, palette.Border.B);
        _textBrush = NewBrush(palette.Text.A, palette.Text.R, palette.Text.G, palette.Text.B);
        _onActiveTextBrush = NewBrush(palette.ActiveText.A, palette.ActiveText.R, palette.ActiveText.G, palette.ActiveText.B);
        _thumbBack = NewBrush(palette.ThumbBack.A, palette.ThumbBack.R, palette.ThumbBack.G, palette.ThumbBack.B);
        _chipBrush = NewBrush(palette.Chip.A, palette.Chip.R, palette.Chip.G, palette.Chip.B);
        Opacity = palette.Opacity;

        // 滚动条用剪贴板面板同一份主题样式
        ThemeScrollBar.Apply(_scroll, palette.Muted, palette);

        for (int i = 0; i < _cards.Count; i++)
        {
            _cards[i].Apply(_cardNormal, _cardSelected, _selectedBorderBrush, _textBrush, _onActiveTextBrush, _thumbBack, _chipBrush, i == _selected);
        }
    }

    private static SolidColorBrush NewBrush(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));

    /// <summary>卡片里的一个预览格：源窗口句柄 + 那个格子的元素。</summary>
    private readonly record struct ThumbCell(IntPtr Handle, Border Element);

    private sealed class CardVisual
    {
        private readonly TextBlock _title;
        private readonly Border? _badge;
        private readonly TextBlock? _badgeText;

        public CardVisual(IntPtr handle, Border root, TextBlock title, Border? badge, TextBlock? badgeText, List<ThumbCell> cells)
        {
            Handle = handle;
            Root = root;
            _title = title;
            _badge = badge;
            _badgeText = badgeText;
            Cells = cells;
        }

        public IntPtr Handle { get; }

        public Border Root { get; }

        /// <summary>预览格（合并卡有多个）：DWM 的实时缩略图会盖在这些格子上（宿主窗口压在面板上面）。</summary>
        public List<ThumbCell> Cells { get; }

        public void Apply(Brush normal, Brush selected, Brush accent, Brush text, Brush onSelectedText, Brush thumbBack, Brush chip, bool isSelected)
        {
            Root.Background = isSelected ? selected : normal;
            Root.BorderBrush = isSelected ? accent : Brushes.Transparent;
            _title.Foreground = isSelected ? onSelectedText : text;

            foreach (var cell in Cells) cell.Element.Background = thumbBack;

            if (_badge is not null) _badge.Background = chip;
            if (_badgeText is not null) _badgeText.Foreground = text;
        }
    }
}
