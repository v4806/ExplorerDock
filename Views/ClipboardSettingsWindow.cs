using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ExplorerDock.Services;

namespace ExplorerDock.Views;

/// <summary>
/// 剪贴板设置：历史数量上限 + 磁盘占用上限。
/// 与确认框、设置向导共用一套自绘配色，不用系统对话框。
/// </summary>
internal sealed class ClipboardSettingsWindow : Window
{
    private static readonly int[] ItemPresets = { 25, 50, 100, 200, 500 };

    /// <summary>数量上限的"不限"取值（0 就是不限）。</summary>
    private const int UnlimitedItems = 0;

    private static readonly (string Text, long Value)[] BytePresets =
    {
        ("200 MB", 200L * 1024 * 1024),
        ("500 MB", 500L * 1024 * 1024),
        ("1 GB", 1024L * 1024 * 1024),
        ("2 GB", 2048L * 1024 * 1024),
        ("不限", 0L),
    };

    private readonly List<(int Value, Border Button, TextBlock Label)> _itemButtons = new();
    private readonly List<(long Value, Border Button, TextBlock Label)> _byteButtons = new();

    /// <summary>呼出位置的两个选项（false = 固定位置，true = 鼠标位置）。</summary>
    private readonly List<(bool Value, Border Button, TextBlock Label)> _positionButtons = new();

    private readonly Brush _textBrush;
    private readonly Brush _mutedBrush;
    private readonly Brush _lineBrush;
    private readonly Brush _ghostBrush;
    private readonly Brush _accentBrush;

    /// <summary>选中项的高亮底色（与悬浮栏的"当前活动按钮"同一份）。</summary>
    private readonly Brush _activeBrush;

    /// <summary>选中项上的文字色（与悬浮栏同一套反色规则）。</summary>
    private readonly Brush _onActiveTextBrush;

    /// <summary>未选中项的悬浮高亮（鼠标移上去才出现，与悬浮栏按钮一致）。</summary>
    private readonly Brush _hoverBrush;

    private readonly Brush _hoverTextBrush;

    private int _maxItems;
    private long _maxBytes;
    private bool _atCursor;
    private bool _saved;
    private TextBox? _customBox;
    private TextBox? _customByteBox;

    /// <summary>打开设置窗口，返回 true 表示用户点了保存。</summary>
    public static bool Show(Window? owner)
    {
        var window = new ClipboardSettingsWindow();

        if (owner is { IsVisible: true }) window.Owner = owner;

        window.ShowDialog();
        return window._saved;
    }

    private ClipboardSettingsWindow()
    {
        // 配色统一取自 ThemePalette（和悬浮栏、剪贴板面板同一份定义）
        var palette = ThemePalette.Resolve();

        _textBrush = new SolidColorBrush(palette.Text);
        _mutedBrush = new SolidColorBrush(palette.Muted);
        _lineBrush = new SolidColorBrush(palette.Border);
        _ghostBrush = new SolidColorBrush(palette.Chip);
        _accentBrush = new SolidColorBrush(palette.Accent);
        _activeBrush = new SolidColorBrush(palette.Active);
        _onActiveTextBrush = new SolidColorBrush(palette.ActiveText);
        _hoverBrush = new SolidColorBrush(palette.Hover);
        _hoverTextBrush = new SolidColorBrush(palette.HoverText);

        _maxItems = App.Settings.ClipboardMaxItems;
        _maxBytes = App.Settings.ClipboardMaxBytes;
        _atCursor = App.Settings.ClipboardAtCursor;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "ExplorerDock 剪贴板设置";
        FontFamily = palette.Typeface;

        var stack = new StackPanel { Margin = new Thickness(26, 22, 26, 20) };

        stack.Children.Add(new TextBlock
        {
            Text = "剪贴板设置",
            FontSize = ThemePalette.Resolve().FontSizeLarge,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
        });

        stack.Children.Add(new TextBlock
        {
            Text = "收藏的记录不受数量上限限制；磁盘占用到顶时，从最旧的记录开始删。",
            FontSize = ThemePalette.Resolve().FontSizeBody,
            LineHeight = 20,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = _mutedBrush,
            TextWrapping = TextWrapping.Wrap,
        });

        // ---------- 呼出位置 ----------
        stack.Children.Add(SectionTitle("呼出位置"));
        stack.Children.Add(Hint("面板每次出现在哪里；选\"鼠标位置\"时会跟着光标弹出。"));

        var positionRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };

        positionRow.Children.Add(ChoiceButton("固定位置", !_atCursor, () =>
        {
            _atCursor = false;
            RefreshChoices();
        }, out var fixedButton, out var fixedLabel));
        _positionButtons.Add((false, fixedButton, fixedLabel));

        positionRow.Children.Add(ChoiceButton("鼠标位置", _atCursor, () =>
        {
            _atCursor = true;
            RefreshChoices();
        }, out var cursorButton, out var cursorLabel));
        _positionButtons.Add((true, cursorButton, cursorLabel));

        stack.Children.Add(positionRow);

        // ---------- 数量上限 ----------
        stack.Children.Add(SectionTitle("记住多少条"));
        stack.Children.Add(Hint("剪贴板历史最多保留多少条（不算收藏）。"));

        var itemRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };

        foreach (int preset in ItemPresets)
        {
            itemRow.Children.Add(ChoiceButton(preset.ToString(), _maxItems == preset, () =>
            {
                _maxItems = preset;
                if (_customBox is not null) _customBox.Text = preset.ToString();
                RefreshChoices();
            }, out var button, out var label));
            _itemButtons.Add((preset, button, label));
        }

        itemRow.Children.Add(ChoiceButton("不限", _maxItems <= 0, () =>
        {
            _maxItems = UnlimitedItems;
            if (_customBox is not null) _customBox.Text = "0";
            RefreshChoices();
        }, out var unlimitedButton, out var unlimitedLabel));
        _itemButtons.Add((UnlimitedItems, unlimitedButton, unlimitedLabel));

        stack.Children.Add(itemRow);

        // 自定义那一行单独占一行：和预设按钮挤在同一个 WrapPanel 里，窗口一窄就会压到一起
        var itemCustomRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
        };

        _customBox = new TextBox
        {
            Width = 76,
            Height = 34,
            Margin = new Thickness(10, 0, 6, 0),
            FontSize = ThemePalette.Resolve().FontSizeMedium,
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = _maxItems.ToString(),
            Background = _ghostBrush,
            Foreground = _textBrush,
            BorderBrush = _lineBrush,
            BorderThickness = new Thickness(1),
            CaretBrush = _textBrush,
        };

        _customBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        DataObject.AddPastingHandler(_customBox, (_, e) =>
        {
            if (e.DataObject.GetData(typeof(string)) is string pasted && pasted.Any(c => !char.IsDigit(c))) e.CancelCommand();
        });
        _customBox.TextChanged += (_, _) =>
        {
            if (int.TryParse(_customBox.Text, out int value)) _maxItems = value <= 0 ? UnlimitedItems : Math.Min(value, 1_000_000);
            RefreshChoices();
        };

        itemCustomRow.Children.Add(_customBox);
        itemCustomRow.Children.Add(new TextBlock
        {
            Text = "条（自定义，0 = 不限）",
            FontSize = ThemePalette.Resolve().FontSizeBody,
            Foreground = _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        stack.Children.Add(itemCustomRow);

        // ---------- 磁盘上限 ----------
        stack.Children.Add(SectionTitle("最多占用多少磁盘"));

        var byteRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };

        foreach (var (text, value) in BytePresets)
        {
            byteRow.Children.Add(ChoiceButton(text, _maxBytes == value, () =>
            {
                _maxBytes = value;
                if (_customByteBox is not null) _customByteBox.Text = (value / (1024 * 1024)).ToString();
                RefreshChoices();
            }, out var button, out var label));
            _byteButtons.Add((value, button, label));
        }

        stack.Children.Add(byteRow);

        // 磁盘上限也能自己填（MB，0 = 不限），同样独占一行
        var byteCustomRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
        };

        _customByteBox = new TextBox
        {
            Width = 76,
            Height = 34,
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = ThemePalette.Resolve().FontSizeMedium,
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = (_maxBytes / (1024 * 1024)).ToString(),
            Background = _ghostBrush,
            Foreground = _textBrush,
            BorderBrush = _lineBrush,
            BorderThickness = new Thickness(1),
            CaretBrush = _textBrush,
        };

        _customByteBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        DataObject.AddPastingHandler(_customByteBox, (_, e) =>
        {
            if (e.DataObject.GetData(typeof(string)) is string pasted && pasted.Any(c => !char.IsDigit(c))) e.CancelCommand();
        });
        _customByteBox.TextChanged += (_, _) =>
        {
            if (int.TryParse(_customByteBox.Text, out int mb))
            {
                _maxBytes = mb <= 0 ? 0L : Math.Min(mb, 1024 * 1024) * 1024L * 1024L;
            }

            RefreshChoices();
        };

        byteCustomRow.Children.Add(_customByteBox);
        byteCustomRow.Children.Add(new TextBlock
        {
            Text = "MB（自定义，0 = 不限）",
            FontSize = ThemePalette.Resolve().FontSizeBody,
            Foreground = _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        stack.Children.Add(byteCustomRow);

        stack.Children.Add(new TextBlock
        {
            Text = $"当前占用 {Services.ClipItem.FormatSize(App.CurrentClipboardBytes)}",
            FontSize = ThemePalette.Resolve().FontSizeBody,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = _mutedBrush,
        });

        // ---------- 按钮 ----------
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };

        buttons.Children.Add(BigButton("取消", _textBrush, _ghostBrush, _lineBrush, () => Close()));
        buttons.Children.Add(BigButton("保存", new SolidColorBrush(ThemePalette.OnColor(palette.Accent, palette.Background)), _accentBrush, _accentBrush, Save));

        stack.Children.Add(buttons);

        Content = new Border
        {
            CornerRadius = new CornerRadius(palette.CornerRadius),
            // 设置窗口不透明：半透明底在浅色内容上会透出后面窗口的花纹，字都糊了
            Background = new SolidColorBrush(Color.FromArgb(0xFF, palette.Background.R, palette.Background.G, palette.Background.B)),
            BorderThickness = new Thickness(palette.Custom ? palette.CustomBorderThickness : palette.DockBorderThickness),
            BorderBrush = _lineBrush,
            Child = stack,
        };

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
            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    break;
                case Key.Enter:
                    Save();
                    break;
            }
        };

        RefreshChoices();
    }

    private void Save()
    {
        // 0 = 不设限（内存里用 int/long 上限表达，写入设置时保留 0）
        App.Settings.ClipboardMaxItems = _maxItems <= 0 ? 0 : Math.Clamp(_maxItems, 1, 1_000_000);
        App.Settings.ClipboardMaxBytes = _maxBytes <= 0 ? 0 : _maxBytes;
        App.Settings.ClipboardAtCursor = _atCursor;
        App.Settings.Save();

        _saved = true;
        Close();
    }

    private void RefreshChoices()
    {
        foreach (var (value, button, label) in _itemButtons)
        {
            ApplyChoice(button, label, value == _maxItems);
        }

        foreach (var (value, button, label) in _byteButtons)
        {
            ApplyChoice(button, label, value == _maxBytes);
        }

        foreach (var (value, button, label) in _positionButtons)
        {
            ApplyChoice(button, label, value == _atCursor);
        }
    }

    private void ApplyChoice(Border button, TextBlock label, bool active)
    {
        // 与悬浮栏按钮同一套：高亮块**只改底色、不描边**
        // （底色与描边同色时圆角会画两遍，边缘看着残缺）；文字跟着高亮色反色
        button.Tag = active;

        button.Background = active ? _activeBrush : Brushes.Transparent;
        button.BorderBrush = Brushes.Transparent;
        label.Foreground = active ? _onActiveTextBrush : _textBrush;
    }

    private TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = ThemePalette.Resolve().FontSizeMedium,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 22, 0, 0),
        Foreground = _textBrush,
    };

    private TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = ThemePalette.Resolve().FontSizeBody,
        LineHeight = 19,
        Margin = new Thickness(0, 6, 0, 0),
        Foreground = _mutedBrush,
        TextWrapping = TextWrapping.Wrap,
    };

    private Border ChoiceButton(string text, bool active, Action onClick, out Border button, out TextBlock label)
    {
        label = new TextBlock
        {
            Text = text,
            FontSize = ThemePalette.Resolve().FontSizeMedium,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        button = new Border
        {
            MinWidth = 66,
            Height = 34,
            Margin = new Thickness(2, 0, 2, 0),
            Padding = new Thickness(12, 0, 12, 0),
            // 圆角/线宽/无描边都照悬浮栏按钮来，这样两边的高亮块长得一样
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(2),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = label,
        };

        // lambda 里不能直接用 out 参数，先转成局部变量
        var box = button;
        var caption = label;

        // 悬浮高亮：和悬浮栏按钮一样，鼠标移上去才亮（选中项已经有高亮底，不再叠）
        button.MouseEnter += (_, _) =>
        {
            if (box.Tag is true) return;

            box.Background = _hoverBrush;
            caption.Foreground = _hoverTextBrush;
        };

        button.MouseLeave += (_, _) =>
        {
            if (box.Tag is true) return;

            box.Background = Brushes.Transparent;
            caption.Foreground = _textBrush;
        };

        label.Foreground = _textBrush;

        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, e) =>
        {
            onClick();
            e.Handled = true;
        };

        ApplyChoice(button, label, active);
        return button;
    }

    private Border BigButton(string text, Brush foreground, Brush background, Brush border, Action onClick)
    {
        var button = new Border
        {
            MinWidth = 96,
            Height = 36,
            CornerRadius = new CornerRadius(ThemePalette.Resolve().ButtonCornerRadius),
            Background = background,
            BorderThickness = new Thickness(1),
            BorderBrush = border,
            Cursor = Cursors.Hand,
            Padding = new Thickness(18, 0, 18, 0),
            Margin = new Thickness(10, 0, 0, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = ThemePalette.Resolve().FontSizeMedium,
                Foreground = foreground,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        // 窗口那层挂了 DragMove，按钮必须把 Down 拦下来，否则点不动
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, _) => onClick();
        return button;
    }

    private static SolidColorBrush Brush(Color color) => new(color);
}
