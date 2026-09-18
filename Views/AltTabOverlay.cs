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
/// </summary>
internal sealed class AltTabOverlay : Window
{
    private const double CardWidth = 236;
    private const double CardHeight = 178;
    private const double CardGap = 12;
    private const int MaxPerRow = 5;
    private const double PanelPadding = 20;
    private const double PanelBorder = 1;

    /// <summary>抓缩略图的目标宽度（像素）。</summary>
    public const int ThumbnailWidth = 560;

    private readonly Border _panel;
    private readonly WrapPanel _wrap;
    private readonly List<CardVisual> _cards = new();
    private readonly Dictionary<IntPtr, CardVisual> _byHandle = new();

    private ThemePalette _palette = ThemePalette.Resolve();
    private int _selected = -1;

    /// <summary>
    /// 鼠标锚点：面板刚弹出来时 WPF 会补发一次 MouseMove（光标其实一动没动），
    /// 不放它过去的话，选中项会被光标底下的那张卡抢走。
    /// </summary>
    private bool _cursorArmed;
    private Brush _cardNormal = Brushes.Transparent;
    private Brush _cardSelected = Brushes.Transparent;
    private Brush _accentBrush = Brushes.Transparent;
    private Brush _textBrush = Brushes.White;
    private Brush _onActiveTextBrush = Brushes.White;
    private Brush _thumbBack = Brushes.Transparent;

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

        FontFamily = _palette.Typeface;

        _panel = new Border
        {
            CornerRadius = new CornerRadius(_palette.CornerRadius),
            BorderThickness = new Thickness(PanelBorder),
            Padding = new Thickness(PanelPadding),
            Child = _wrap,
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
    }

    /// <summary>鼠标移到某张卡上（系统行为：悬停即选中）。</summary>
    public event Action<int>? Hovered;

    /// <summary>鼠标点了某张卡（系统行为：点一下立即切过去）。</summary>
    public event Action<int>? Clicked;

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

        int columns = Math.Clamp(items.Count, 1, MaxPerRow);
        int rows = Math.Max(1, (int)Math.Ceiling(items.Count / (double)columns));

        double panelWidth = columns * (CardWidth + CardGap) + PanelPadding * 2 + PanelBorder * 2;
        double panelHeight = rows * (CardHeight + CardGap) + PanelPadding * 2 + PanelBorder * 2;

        var area = SystemParameters.WorkArea;
        double maxHeight = area.Height * 0.78;
        double maxWidth = area.Width * 0.94;

        // 窗口太多时整体缩小（系统也是这么做的），而不是溢出屏幕
        double scale = 1;
        if (panelHeight > maxHeight || panelWidth > maxWidth)
        {
            scale = Math.Max(0.4, Math.Min(maxHeight / panelHeight, maxWidth / panelWidth));
        }

        _panel.LayoutTransform = new ScaleTransform(scale, scale);

        double width = Math.Round(panelWidth * scale);
        double height = Math.Round(panelHeight * scale);

        Width = width;
        Height = height;
        Left = Math.Round(area.Left + (area.Width - width) / 2);
        Top = Math.Round(area.Top + Math.Max(0, (area.Height - height) / 2 - area.Height * 0.05));

        if (!IsVisible) Show();
        KeepOnTop();
        SetSelection(selected);
    }

    public void SetSelection(int index)
    {
        _selected = index;

        for (int i = 0; i < _cards.Count; i++)
        {
            _cards[i].Apply(_cardNormal, _cardSelected, _accentBrush, _textBrush, _onActiveTextBrush, _thumbBack, i == index);
        }
    }

    /// <summary>
    /// 每张卡的缩略图区域（屏幕物理像素）+ 对应的源窗口句柄，交给缩略图宿主去挂 DWM 缩略图。
    /// 必须在面板已经显示、布局算完之后调用。
    /// </summary>
    public List<ThumbnailSlot> GetThumbnailSlots()
    {
        var slots = new List<ThumbnailSlot>(_cards.Count);

        foreach (var card in _cards)
        {
            if (card.Handle == IntPtr.Zero) continue;

            var host = card.ThumbHost;

            if (host.ActualWidth <= 2 || host.ActualHeight <= 2) continue;

            try
            {
                var topLeft = host.PointToScreen(new Point(0, 0));
                var bottomRight = host.PointToScreen(new Point(host.ActualWidth, host.ActualHeight));

                int width = (int)Math.Round(bottomRight.X - topLeft.X);
                int height = (int)Math.Round(bottomRight.Y - topLeft.Y);

                if (width <= 2 || height <= 2) continue;

                slots.Add(new ThumbnailSlot(
                    card.Handle,
                    new Int32Rect((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y), width, height)));
            }
            catch
            {
                // 布局还没算完，这张先跳过
            }
        }

        return slots;
    }

    public void Dismiss()
    {
        if (IsVisible) Hide();
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

    // ---------- 卡片 ----------

    private void BuildCards(IReadOnlyList<AltTabWindowInfo> items)
    {
        _wrap.Children.Clear();
        _cards.Clear();
        _byHandle.Clear();

        for (int i = 0; i < items.Count; i++)
        {
            var card = CreateCard(items[i], i);
            _cards.Add(card);
            _byHandle[items[i].Handle] = card;
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

        var header = new Grid { Margin = new Thickness(9, 7, 9, 5) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(title, 1);
        header.Children.Add(icon);
        header.Children.Add(title);

        // 缩略图由 DWM 实时渲染后盖在这块区域上；拿不到缩略图的窗口就显示大图标
        var fallback = new Image
        {
            Source = info.Icon,
            Width = 48,
            Height = 48,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var thumbHost = new Grid
        {
            Margin = new Thickness(9, 0, 9, 9),
            ClipToBounds = true,
        };
        thumbHost.Children.Add(fallback);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(thumbHost, 1);
        layout.Children.Add(header);
        layout.Children.Add(thumbHost);

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

        return new CardVisual(info.Handle, root, fallback, title, thumbHost);
    }

    private void ApplyTone()
    {
        // 配色与悬浮栏、菜单、剪贴板面板完全同源
        var palette = _palette;
        FontFamily = palette.Typeface;

        _panel.CornerRadius = new CornerRadius(palette.CornerRadius);
        _panel.Background = NewBrush(palette.SurfaceAlpha, palette.Background.R, palette.Background.G, palette.Background.B);
        _panel.BorderBrush = NewBrush(palette.Border.A, palette.Border.R, palette.Border.G, palette.Border.B);
        _cardNormal = NewBrush(palette.Hover.A, palette.Hover.R, palette.Hover.G, palette.Hover.B);
        _cardSelected = NewBrush(palette.Active.A, palette.Active.R, palette.Active.G, palette.Active.B);
        _accentBrush = NewBrush(palette.Accent.A, palette.Accent.R, palette.Accent.G, palette.Accent.B);
        _textBrush = NewBrush(palette.Text.A, palette.Text.R, palette.Text.G, palette.Text.B);
        _onActiveTextBrush = NewBrush(palette.ActiveText.A, palette.ActiveText.R, palette.ActiveText.G, palette.ActiveText.B);
        _thumbBack = NewBrush(palette.ThumbBack.A, palette.ThumbBack.R, palette.ThumbBack.G, palette.ThumbBack.B);
        Opacity = palette.Opacity;

        for (int i = 0; i < _cards.Count; i++)
        {
            _cards[i].Apply(_cardNormal, _cardSelected, _accentBrush, _textBrush, _onActiveTextBrush, _thumbBack, i == _selected);
        }
    }

    private static SolidColorBrush NewBrush(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));

    private sealed class CardVisual
    {
        private readonly Image _fallback;
        private readonly TextBlock _title;
        private readonly Grid _thumbHost;

        public CardVisual(IntPtr handle, Border root, Image fallback, TextBlock title, Grid thumbHost)
        {
            Handle = handle;
            Root = root;
            _fallback = fallback;
            _title = title;
            _thumbHost = thumbHost;
        }

        public IntPtr Handle { get; }

        public Border Root { get; }

        /// <summary>缩略图区域：DWM 的实时缩略图会盖在这块上（它是宿主窗口，压在面板上面）。</summary>
        public FrameworkElement ThumbHost => _thumbHost;

        public void Apply(Brush normal, Brush selected, Brush accent, Brush text, Brush onSelectedText, Brush thumbBack, bool isSelected)
        {
            Root.Background = isSelected ? selected : normal;
            Root.BorderBrush = isSelected ? accent : Brushes.Transparent;
            _title.Foreground = isSelected ? onSelectedText : text;
            _thumbHost.Background = thumbBack;
        }
    }
}
