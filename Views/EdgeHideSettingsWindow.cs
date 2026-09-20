using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ExplorerDock.Services;

namespace ExplorerDock.Views;

/// <summary>
/// 贴边自动隐藏的两个数值设置：收纳后留在屏内的宽度、鼠标触发带宽。
/// 整窗与自定义主题面板同一套风格（无边框 + 圆角 + 自绘标题栏），
/// 底色/文字色一律取自 ThemePalette，跟着主题走。
///
/// 「尺寸变化时保持居中」「锁定位置」两个开关不在这里 —— 它们在右键菜单的「悬浮栏」里。
/// </summary>
internal sealed class EdgeHideSettingsWindow : Window
{
    private readonly App _app;
    private readonly StackPanel _rows = new();
    private readonly ThemePalette _palette = ThemePalette.Resolve();
    private readonly Brush _panelBrush;
    private readonly Brush _borderBrush;
    private readonly Brush _textBrush;
    private readonly Brush _mutedBrush;

    public EdgeHideSettingsWindow(App app)
    {
        _app = app;

        var palette = _palette;

        _panelBrush = new SolidColorBrush(Color.FromArgb(palette.SurfaceAlpha, palette.Background.R, palette.Background.G, palette.Background.B));
        _borderBrush = new SolidColorBrush(palette.Border);
        _textBrush = new SolidColorBrush(palette.Text);
        _mutedBrush = new SolidColorBrush(palette.Muted);

        FontFamily = palette.Typeface;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 460;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var body = new StackPanel { Margin = new Thickness(20, 14, 20, 18) };
        body.Children.Add(Header());

        body.Children.Add(new TextBlock
        {
            Text = "悬浮栏停在屏幕边缘后会自动滑出屏幕；鼠标再回到那条边缘，它就滑回来。改动即时生效。",
            FontSize = palette.FontSizeSmall,
            Foreground = _mutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 14),
        });

        // 先挂上容器再往里面加行，几行滑块才会排在这段说明之后
        body.Children.Add(_rows);

        AddSliderRow(
            "收纳后留在屏内的宽度",
            0,
            24,
            1,
            () => App.Settings.EdgePeekWidth,
            v => App.Settings.EdgePeekWidth = v,
            "px");

        AddSliderRow(
            "鼠标触发带宽",
            1,
            32,
            1,
            () => App.Settings.EdgeTriggerWidth,
            v => App.Settings.EdgeTriggerWidth = v,
            "px");

        AddSliderRow(
            "自动收纳延迟",
            0,
            2000,
            50,
            () => App.Settings.EdgeRetractDelayMs,
            v => App.Settings.EdgeRetractDelayMs = (int)Math.Round(v),
            "ms");

        body.Children.Add(new TextBlock
        {
            Text = "留在屏内的宽度设成 0 就是完全移出屏幕，只靠鼠标碰到屏幕边缘把它召回来；"
                 + "自动收纳延迟是鼠标离开悬浮栏之后多久收回，设成 0 就是立刻收回。",
            FontSize = palette.FontSizeSmall,
            Foreground = _mutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        body.Children.Add(Footer());

        var shell = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = _panelBrush,
            BorderThickness = new Thickness(1),
            BorderBrush = _borderBrush,
            Child = body,
            Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 3,
                Direction = 270,
                Opacity = 0.5,
                Color = Colors.Black,
                RenderingBias = RenderingBias.Performance,
            },
        };

        var host = new Grid { Margin = new Thickness(18) };
        host.Children.Add(shell);
        Content = host;
    }

    private UIElement Header()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10), Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "贴边隐藏",
            FontSize = _palette.FontSizeHeadline,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var close = new Button
        {
            Content = "✕",
            Width = 32,
            Height = 26,
            Style = (Style)Application.Current.Resources["EditorButton"],
        };
        close.Click += (_, _) => Close();

        Grid.SetColumn(title, 0);
        Grid.SetColumn(close, 1);
        grid.Children.Add(title);
        grid.Children.Add(close);

        grid.MouseLeftButtonDown += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        };

        return grid;
    }

    private UIElement Footer()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        var reset = new Button
        {
            Content = "恢复默认",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)Application.Current.Resources["EditorButton"],
        };
        reset.Click += (_, _) => ResetToDefaults();

        var done = new Button
        {
            Content = "完成",
            Padding = new Thickness(22, 6, 22, 6),
            Style = (Style)Application.Current.Resources["EditorButtonAccent"],
            IsDefault = true,
        };
        done.Click += (_, _) => Close();

        panel.Children.Add(reset);
        panel.Children.Add(done);
        return panel;
    }

    private void AddSliderRow(string label, double min, double max, double step, Func<double> getter, Action<double> setter, string unit)
    {
        var grid = NewRow(label);

        var valueText = new TextBlock
        {
            Text = $"{getter():0.#}{unit}",
            Width = 54,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Foreground = _textBrush,
        };

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 2,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Value = getter(),
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["EditorSlider"],
        };

        slider.ValueChanged += (_, e) =>
        {
            setter(e.NewValue);
            valueText.Text = $"{e.NewValue:0.#}{unit}";

            // 立刻作用到悬浮栏上：收纳中的窗口按新宽度重新摆到屏外
            _app.Dock?.ApplyEdgeHideSettings();
        };

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(slider);
        right.Children.Add(valueText);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        _rows.Children.Add(grid);
    }

    private Grid NewRow(string label)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12), Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = _palette.FontSizeBody,
            Foreground = _textBrush,
        });

        Grid.SetColumn((UIElement)grid.Children[0], 0);
        return grid;
    }

    private void ResetToDefaults()
    {
        var defaults = new Settings();

        App.Settings.EdgePeekWidth = defaults.EdgePeekWidth;
        App.Settings.EdgeTriggerWidth = defaults.EdgeTriggerWidth;
        App.Settings.EdgeRetractDelayMs = defaults.EdgeRetractDelayMs;

        _app.Dock?.ApplyEdgeHideSettings();
        App.Settings.Save();

        // 滑块位置跟着回到默认值：重建一遍内容最省事
        Close();
        _app.OpenEdgeHideSettings();
    }

    protected override void OnClosed(EventArgs e)
    {
        App.Settings.Save();
        base.OnClosed(e);
    }
}
