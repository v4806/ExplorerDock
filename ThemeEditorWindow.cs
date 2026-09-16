using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ExplorerDock.Services;

namespace ExplorerDock;

/// <summary>
/// 自定义主题设置面板。整窗与悬浮栏同一套深色风格（无边框 + 圆角 + 自绘色板），
/// 不再调用 Win32 的老式取色对话框。
/// </summary>
internal sealed class ThemeEditorWindow : Window
{
    private static readonly Color BackgroundColor = (Color)ColorConverter.ConvertFromString("#FF1E1E22");
    private static readonly Color RowColor = (Color)ColorConverter.ConvertFromString("#FF2A2A30");
    private static readonly Color BorderColor = (Color)ColorConverter.ConvertFromString("#FF3A3A42");
    private static readonly Color TextColor = (Color)ColorConverter.ConvertFromString("#FFF0F0F0");
    private static readonly Color AccentColor = (Color)ColorConverter.ConvertFromString("#FF4C8DF6");

    private static readonly string[] Palette =
    {
        "#00000000", "#40202020", "#80202020", "#C0202020", "#FF1B1B1F", "#FF202024", "#FF2A2A30", "#FF3A3A42",
        "#FF000000", "#FFFFFFFF", "#FFF0F0F0", "#FFBFBFBF", "#FF808080", "#FF555555", "#FF333333", "#FF1A1A1A",
        "#FF24476B", "#FF4C8DF6", "#FF2E7D32", "#FF43A047", "#FFB71C1C", "#FFE53935", "#FF6A1B9A", "#FF8E24AA",
        "#FFE65100", "#FFFB8C00", "#FFF9A825", "#FFFFD54F", "#FF00695C", "#FF00897B", "#FF37474F", "#FF546E7A",
    };

    private readonly App _app;
    private readonly StackPanel _rows = new();

    public ThemeEditorWindow(App app)
    {
        _app = app;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 500;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var body = new StackPanel { Margin = new Thickness(20, 14, 20, 18) };
        body.Children.Add(Header());

        body.Children.Add(new TextBlock
        {
            Text = "改动即时生效。颜色写 #AARRGGBB，或点右侧色块从色板里挑。",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 2, 0, 14),
        });

        AddColorRow("背景色", () => App.Settings.CustomBackground, v => App.Settings.CustomBackground = v);
        AddColorRow("边框色", () => App.Settings.CustomBorder, v => App.Settings.CustomBorder = v);
        AddColorRow("文字色", () => App.Settings.CustomText, v => App.Settings.CustomText = v);
        AddColorRow("鼠标悬浮高亮色", () => App.Settings.CustomHover, v => App.Settings.CustomHover = v);
        AddColorRow("活动高亮色", () => App.Settings.CustomActive, v => App.Settings.CustomActive = v);
        AddColorRow("高亮文字反色", () => App.Settings.CustomActiveText, v => App.Settings.CustomActiveText = v);

        AddSliderRow("边框线宽", 0, 8, 0.5, () => App.Settings.CustomBorderThickness, v => App.Settings.CustomBorderThickness = v, "px");
        AddSliderRow("悬浮栏尺寸", 100, 300, 10, () => App.Settings.Scale * 100, v => App.Settings.Scale = v / 100.0, "%");
        AddFontRow();

        body.Children.Add(_rows);
        body.Children.Add(Footer());

        var shell = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(BackgroundColor),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(BorderColor),
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
            Text = "自定义主题",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(TextColor),
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

        var reset = new Button { Content = "恢复默认", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.Resources["EditorButton"] };
        reset.Click += (_, _) => ResetToDefaults();

        var done = new Button { Content = "完成", Padding = new Thickness(22, 6, 22, 6), Style = (Style)Application.Current.Resources["EditorButtonAccent"], IsDefault = true };
        done.Click += (_, _) => Close();

        panel.Children.Add(reset);
        panel.Children.Add(done);
        return panel;
    }

    // ---------- 行 ----------

    private void AddColorRow(string label, Func<string> getter, Action<string> setter)
    {
        var grid = NewRow(label);

        var box = new TextBox { Text = getter(), Width = 118, Style = (Style)Application.Current.Resources["EditorTextBox"] };

        var swatch = new Border
        {
            Width = 30,
            Height = 24,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(BorderColor),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            Background = ParseBrush(getter()),
        };

        void Apply(string value)
        {
            setter(value);
            swatch.Background = ParseBrush(value);
            _app.PreviewTheme();
        }

        box.LostFocus += (_, _) => Apply(box.Text.Trim());
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Apply(box.Text.Trim());
            e.Handled = true;
        };

        var popup = new Popup
        {
            PlacementTarget = swatch,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };

        var palettePanel = new WrapPanel { Width = 264, Margin = new Thickness(8, 6, 8, 8) };

        foreach (var hex in Palette)
        {
            var chip = new Border
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(BorderColor),
                Background = ParseBrush(hex),
                Cursor = Cursors.Hand,
                ToolTip = hex,
            };

            var captured = hex;
            chip.MouseLeftButtonUp += (_, _) =>
            {
                box.Text = captured;
                Apply(captured);
                popup.IsOpen = false;
            };

            palettePanel.Children.Add(chip);
        }

        popup.Child = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(RowColor),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(BorderColor),
            Margin = new Thickness(10),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Direction = 270, Opacity = 0.5, Color = Colors.Black, RenderingBias = RenderingBias.Performance },
            Child = palettePanel,
        };

        swatch.MouseLeftButtonUp += (_, _) => popup.IsOpen = !popup.IsOpen;

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(box);
        right.Children.Add(swatch);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        _rows.Children.Add(grid);
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
            Foreground = new SolidColorBrush(TextColor),
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
            _app.PreviewTheme();
        };

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(slider);
        right.Children.Add(valueText);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        _rows.Children.Add(grid);
    }

    private void AddFontRow()
    {
        var grid = NewRow("字体");

        var combo = new ComboBox
        {
            Width = 190,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.Resources["EditorComboBox"],
            ItemContainerStyle = (Style)Application.Current.Resources["EditorComboBoxItem"],
        };

        foreach (var family in Fonts.SystemFontFamilies.OrderBy(f => f.Source))
        {
            combo.Items.Add(family.Source);
        }

        combo.SelectedItem = App.Settings.FontFamily;
        if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not string name) return;
            App.Settings.FontFamily = name;
            _app.PreviewTheme();
        };

        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);

        _rows.Children.Add(grid);
    }

    private static Grid NewRow(string label)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10), Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(TextColor),
        };

        Grid.SetColumn(text, 0);
        grid.Children.Add(text);
        return grid;
    }

    // ---------- 深色控件样式 ----------

    private static Style DarkTextBoxStyle()
    {
        var style = new Style(typeof(TextBox));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(TextColor)));
        style.Setters.Add(new Setter(TextBox.CaretBrushProperty, new SolidColorBrush(TextColor)));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(@"
<ControlTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                 xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"" TargetType=""TextBox"">
  <Border x:Name=""Bd"" CornerRadius=""6"" Background=""#FF2A2A30"" BorderBrush=""#FF3A3A42"" BorderThickness=""1"">
    <ScrollViewer x:Name=""PART_ContentHost"" Margin=""8,5,8,5"" VerticalAlignment=""Center""/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property=""IsKeyboardFocused"" Value=""True"">
      <Setter TargetName=""Bd"" Property=""BorderBrush"" Value=""#FF4C8DF6""/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>")));
        return style;
    }

    private static Style DarkSliderStyle()
    {
        var style = new Style(typeof(Slider));
        style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(@"
<ControlTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                 xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"" TargetType=""Slider"">
  <Grid VerticalAlignment=""Center"" Height=""22"">
    <Border Height=""4"" CornerRadius=""2"" Background=""#FF3A3A42"" VerticalAlignment=""Center""/>
    <Track x:Name=""PART_Track"">
      <Track.DecreaseRepeatButton>
        <RepeatButton Command=""Slider.DecreaseLarge"" Style=""{x:Null}"" Opacity=""0"" Width=""0""/>
      </Track.DecreaseRepeatButton>
      <Track.IncreaseRepeatButton>
        <RepeatButton Command=""Slider.IncreaseLarge"" Opacity=""0""/>
      </Track.IncreaseRepeatButton>
      <Track.Thumb>
        <Thumb Width=""14"" Height=""14"" Cursor=""Hand"">
          <Thumb.Template>
            <ControlTemplate TargetType=""Thumb"">
              <Border CornerRadius=""7"" Background=""#FF4C8DF6"" BorderBrush=""#FFFFFFFF"" BorderThickness=""1""/>
            </ControlTemplate>
          </Thumb.Template>
        </Thumb>
      </Track.Thumb>
    </Track>
  </Grid>
</ControlTemplate>")));
        return style;
    }

    private static Style FlatButtonStyle(bool accent = false)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));

        var template = accent
            ? @"<ControlTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" TargetType=""Button"">
  <Border x:Name=""Bd"" CornerRadius=""6"" Background=""#FF4C8DF6"" Padding=""{TemplateBinding Padding}"">
    <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""Bd"" Property=""Background"" Value=""#FF659BF8""/></Trigger>
    <Trigger Property=""IsPressed"" Value=""True""><Setter TargetName=""Bd"" Property=""Background"" Value=""#FF3A7BE0""/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>"
            : @"<ControlTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" TargetType=""Button"">
  <Border x:Name=""Bd"" CornerRadius=""6"" Background=""#FF2A2A30"" BorderBrush=""#FF3A3A42"" BorderThickness=""1"" Padding=""{TemplateBinding Padding}"">
    <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""Bd"" Property=""Background"" Value=""#FF35353D""/></Trigger>
    <Trigger Property=""IsPressed"" Value=""True""><Setter TargetName=""Bd"" Property=""Background"" Value=""#FF202025""/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>";

        style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(template)));
        return style;
    }

    private void ResetToDefaults()
    {
        var d = new Settings();
        var s = App.Settings;

        s.CustomBackground = d.CustomBackground;
        s.CustomBorder = d.CustomBorder;
        s.CustomText = d.CustomText;
        s.CustomHover = d.CustomHover;
        s.CustomActive = d.CustomActive;
        s.CustomActiveText = d.CustomActiveText;
        s.CustomBorderThickness = d.CustomBorderThickness;
        s.Scale = d.Scale;
        s.FontFamily = d.FontFamily;

        _app.PreviewTheme();
        App.Settings.Save();
        Close();
        _app.OpenThemeEditor();
    }

    protected override void OnClosed(EventArgs e)
    {
        App.Settings.Save();
        base.OnClosed(e);
    }

    private static Brush ParseBrush(string? text)
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
            // 非法输入
        }

        return Brushes.Transparent;
    }
}
