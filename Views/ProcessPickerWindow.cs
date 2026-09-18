using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ExplorerDock.Interop;
using ExplorerDock.Services;

namespace ExplorerDock.Views;

/// <summary>
/// 「接管指定程序」：手动挑要接管的程序。
///
/// 两种挑法都有：① 从当前有窗口的运行中程序列表里勾选；
/// ② 浏览 exe 文件补充现在没在运行的程序（勾上之后它下次开窗就接管）。
/// </summary>
internal sealed class ProcessPickerWindow : Window
{
    private readonly App _app;
    private readonly ThemePalette _palette;

    private readonly Brush _textBrush;
    private readonly Brush _mutedBrush;
    private readonly Brush _cardBrush;
    private readonly Brush _lineBrush;
    private readonly Brush _accentBrush;
    private readonly Brush _panelBrush;

    /// <summary>选中行的高亮底色（与悬浮栏的"当前活动按钮"同一份）。</summary>
    private readonly Brush _activeBrush;

    /// <summary>选中行上的文字色（与悬浮栏同一套反色规则）。</summary>
    private readonly Brush _onActiveTextBrush;

    /// <summary>未选中行的悬浮高亮（鼠标移上去才出现，与悬浮栏按钮一致）。</summary>
    private readonly Brush _hoverBrush;

    private readonly Brush _hoverTextBrush;

    private readonly StackPanel _list = new();
    private readonly TextBlock _status;
    private readonly List<Entry> _entries = new();

    private sealed class Entry
    {
        public string Name { get; init; } = string.Empty;

        public string ExePath { get; init; } = string.Empty;

        public int WindowCount { get; init; }

        public string SampleTitle { get; init; } = string.Empty;

        public bool Selected { get; set; }

        public Border Row { get; set; } = null!;

        public Border Box { get; set; } = null!;

        public TextBlock Check { get; set; } = null!;

        /// <summary>程序名与副标题，选中/悬浮时要跟着换颜色。</summary>
        public TextBlock TitleBlock { get; set; } = null!;

        public TextBlock DetailBlock { get; set; } = null!;
    }

    public ProcessPickerWindow(App app)
    {
        _app = app;

        var palette = ThemePalette.Resolve();
        _palette = palette;

        // 不透明底：半透明会透出后面窗口，字看不清
        _panelBrush = new SolidColorBrush(Color.FromArgb(0xFF, palette.Background.R, palette.Background.G, palette.Background.B));
        _textBrush = new SolidColorBrush(palette.Text);
        _mutedBrush = new SolidColorBrush(palette.Muted);
        _cardBrush = new SolidColorBrush(palette.Chip);
        _lineBrush = new SolidColorBrush(palette.Border);
        _accentBrush = new SolidColorBrush(palette.Accent);
        _activeBrush = new SolidColorBrush(palette.Active);
        _onActiveTextBrush = new SolidColorBrush(palette.ActiveText);
        _hoverBrush = new SolidColorBrush(palette.Hover);
        _hoverTextBrush = new SolidColorBrush(palette.HoverText);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = 560;
        Height = 620;
        Title = "ExplorerDock";
        FontFamily = palette.Typeface;

        var root = new StackPanel { Margin = new Thickness(24, 22, 24, 18) };

        root.Children.Add(new TextBlock
        {
            Text = "接管指定程序",
            FontSize = palette.FontSizeLarge,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
        });

        root.Children.Add(new TextBlock
        {
            Text = "勾选的程序只要开着窗口，按钮就会从任务栏移到悬浮栏（不必凑够两个窗口）。"
               + "这是给自动接管补漏用的，也可以在关掉自动接管时只接管这里选中的程序。",
            FontSize = palette.FontSizeMedium,
            LineHeight = 23,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = _mutedBrush,
            TextWrapping = TextWrapping.Wrap,
        });

        _status = new TextBlock
        {
            Text = "正在扫描运行中的程序…",
            FontSize = palette.FontSizeSmall,
            Margin = new Thickness(0, 14, 0, 8),
            Foreground = _mutedBrush,
        };
        root.Children.Add(_status);

        var scroller = new ScrollViewer
        {
            Height = 396,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _list,
        };

        // 滚动条与悬浮栏/剪贴板面板同一套外观
        ThemeScrollBar.Apply(scroller, palette.Muted, palette);

        // 列表里的点击不再冒泡到窗口那层，否则点一下列表就会变成"拖动窗口"
        scroller.MouseLeftButtonDown += (_, e) => e.Handled = true;

        root.Children.Add(scroller);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        buttons.Children.Add(BuildButton("浏览 exe…", _textBrush, Brushes.Transparent, _lineBrush, Browse));
        buttons.Children.Add(BuildButton("取消", _textBrush, Brushes.Transparent, _lineBrush, Close));
        buttons.Children.Add(BuildButton(
            "保存",
            new SolidColorBrush(ThemePalette.OnColor(palette.Accent, palette.Background)),
            _accentBrush,
            _accentBrush,
            Save));

        root.Children.Add(buttons);

        Content = new Border
        {
            CornerRadius = new CornerRadius(palette.CornerRadius),
            Background = _panelBrush,
            BorderThickness = new Thickness(palette.Custom ? palette.CustomBorderThickness : palette.DockBorderThickness),
            BorderBrush = _lineBrush,
            Child = root,
        };

        // 空白处按住可拖动窗口
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;

            try
            {
                DragMove();
            }
            catch
            {
                // 松手时抛，不用管
            }
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        Loaded += (_, _) => Scan();
    }

    /// <summary>扫描运行中的程序。放在后台线程做：这一步要枚举全部窗口，跨进程时还有管道往返。</summary>
    private async void Scan()
    {
        List<RunningProcessInfo> processes;

        try
        {
            processes = await System.Threading.Tasks.Task.Run(() => _app.WindowHost.RunningProcesses());
        }
        catch
        {
            // 宿主不可用（还在启动 / 已掉线）：出一份空列表，用户仍能点"浏览 exe…"
            processes = new List<RunningProcessInfo>();
        }

        if (!IsLoaded) return;

        Build(processes);
    }

    private void Build(List<RunningProcessInfo> processes)
    {
        _list.Children.Clear();
        _entries.Clear();

        var selected = new HashSet<string>(
            App.Settings.TakeoverProcesses.Select(TakeoverState.Normalize),
            StringComparer.OrdinalIgnoreCase);

        foreach (var process in processes)
        {
            // 资源管理器本来就接管，列出来只会让人以为还要单独勾一次
            if (string.Equals(process.Name, "explorer", StringComparison.OrdinalIgnoreCase)) continue;

            var entry = new Entry
            {
                Name = process.Name,
                ExePath = process.ExePath,
                WindowCount = process.WindowCount,
                SampleTitle = process.SampleTitle,
                Selected = selected.Contains(process.Name),
            };

            _entries.Add(entry);
            AddEntry(entry, running: true);
        }

        // 已指定但当前没运行的程序也列出来，否则用户没法取消勾选
        foreach (var name in selected)
        {
            if (name.Length == 0 || name == "explorer") continue;
            if (_entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))) continue;

            var entry = new Entry { Name = name, Selected = true };

            _entries.Add(entry);
            AddEntry(entry, running: false);
        }

        RefreshStatus();
    }

    private void AddEntry(Entry entry, bool running)
    {
        var check = new TextBlock
        {
            Text = "\u2713",
            FontSize = _palette.FontSizeBody,
            Foreground = new SolidColorBrush(ThemePalette.OnColor(_palette.Accent, _palette.Background)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var box = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(_palette.ButtonCornerRadius),
            BorderThickness = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = check,
        };

        var icon = new Image
        {
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var source = ShellInterop.GetExeIcon(entry.ExePath);
        if (source is not null) icon.Source = source;

        var texts = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        var titleBlock = new TextBlock
        {
            Text = running ? entry.Name : entry.Name + "（当前未运行）",
            FontSize = _palette.FontSizeHeadline,
            Foreground = _textBrush,
        };

        var detailBlock = new TextBlock
        {
            Text = running && entry.WindowCount > 0 ? $"{entry.WindowCount} 个窗口" : entry.SampleTitle.Length > 0 ? entry.SampleTitle : "下次开窗时接管",
            FontSize = _palette.FontSizeSmall,
            Foreground = _mutedBrush,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        texts.Children.Add(titleBlock);
        texts.Children.Add(detailBlock);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(box, 0);
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(texts, 2);
        grid.Children.Add(box);
        grid.Children.Add(icon);
        grid.Children.Add(texts);

        var row = new Border
        {
            // 圆角/线宽/无描边都照悬浮栏按钮来，这样两边的高亮块长得一样
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(13, 9, 13, 9),
            Margin = new Thickness(2, 0, 2, 6),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = grid,
        };

        entry.Row = row;
        entry.Box = box;
        entry.Check = check;
        entry.TitleBlock = titleBlock;
        entry.DetailBlock = detailBlock;
        ApplyRow(entry);

        // 悬浮高亮：和悬浮栏按钮一样，鼠标移上去才亮；移开就恢复（选中行保持高亮）
        row.MouseEnter += (_, _) =>
        {
            if (entry.Selected) return;

            row.Background = _hoverBrush;
            titleBlock.Foreground = _hoverTextBrush;
            detailBlock.Foreground = _hoverTextBrush;
        };

        row.MouseLeave += (_, _) => ApplyRow(entry);

        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;

            entry.Selected = !entry.Selected;
            ApplyRow(entry);
            RefreshStatus();
        };

        _list.Children.Add(row);
    }

    private void ApplyRow(Entry entry)
    {
        // 与悬浮栏按钮同一套：高亮块**只改底色、不描边**，文字跟着高亮色反色
        entry.Row.Background = entry.Selected ? _activeBrush : Brushes.Transparent;
        entry.Row.BorderBrush = Brushes.Transparent;

        // 勾选框代表"已勾选"，跟行高亮是两回事，保留
        entry.Box.Background = entry.Selected ? _accentBrush : Brushes.Transparent;
        entry.Box.BorderBrush = entry.Selected ? _accentBrush : _lineBrush;
        entry.Check.Visibility = entry.Selected ? Visibility.Visible : Visibility.Collapsed;

        entry.TitleBlock.Foreground = entry.Selected ? _onActiveTextBrush : _textBrush;
        entry.DetailBlock.Foreground = entry.Selected ? _onActiveTextBrush : _mutedBrush;
    }

    private void RefreshStatus()
    {
        int selected = _entries.Count(e => e.Selected);
        int running = _entries.Count(e => e.WindowCount > 0);

        _status.Text = _entries.Count == 0
            ? "没有发现可接管的程序；可以点「浏览 exe…」手动指定。"
            : $"扫描到 {running} 个有窗口的程序，已勾选 {selected} 个。";
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要接管的程序",
            Filter = "程序 (*.exe)|*.exe",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        var path = dialog.FileName;
        var name = TakeoverState.Normalize(path);
        if (name.Length == 0) return;

        var existing = _entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Selected = true;
            ApplyRow(existing);
            RefreshStatus();
            return;
        }

        var entry = new Entry { Name = name, ExePath = path, Selected = true };

        _entries.Add(entry);
        AddEntry(entry, running: false);
        RefreshStatus();
    }

    private void Save()
    {
        _app.SetTakeoverProcesses(_entries.Where(e => e.Selected).Select(e => e.Name).ToList());
        Close();
    }

    private Border BuildButton(string text, Brush foreground, Brush background, Brush border, Action onClick)
    {
        var button = new Border
        {
            MinWidth = 96,
            Height = 36,
            // 圆角与悬浮栏按钮一致
            CornerRadius = new CornerRadius(7),
            Background = background,
            BorderThickness = new Thickness(1),
            BorderBrush = border,
            Cursor = Cursors.Hand,
            Padding = new Thickness(18, 0, 18, 0),
            Margin = new Thickness(10, 0, 0, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = _palette.FontSizeMedium,
                Foreground = foreground,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        // 边框色 = 背景色（主按钮）时说明这是"实心按钮"，不叠悬浮高亮；其余给悬浮反馈
        bool plain = ReferenceEquals(background, Brushes.Transparent);

        if (plain && button.Child is TextBlock caption)
        {
            button.MouseEnter += (_, _) =>
            {
                button.Background = _hoverBrush;
                caption.Foreground = _hoverTextBrush;
            };

            button.MouseLeave += (_, _) =>
            {
                button.Background = background;
                caption.Foreground = foreground;
            };
        }

        // Down 必须拦下来：窗口那层为了能拖着走挂了 MouseLeftButtonDown -> DragMove，
        // 而 DragMove 会把鼠标捕获到窗口上，按钮的抬起事件就再也收不到。
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, _) => onClick();
        return button;
    }
}
