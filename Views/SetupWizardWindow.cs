using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ExplorerDock.Services;

namespace ExplorerDock.Views;

/// <summary>
/// 首次启动的设置向导。
///
/// 写给普通用户看：文案里不出现"接管""提权""独占全屏"这类词，
/// 每一步的开关都是即时生效的，只有"以管理员身份运行"要等收尾 —— 中途重启会把向导打断。
/// </summary>
internal sealed class SetupWizardWindow : Window
{
    private const int LastStep = 4;

    private readonly App _app;
    private readonly Brush _panelBrush;
    private readonly Brush _cardBrush;
    private readonly Brush _textBrush;
    private readonly Brush _mutedBrush;
    private readonly Brush _accentBrush;
    private readonly Brush _lineBrush;

    private ThemePalette _palette = ThemePalette.Resolve();

    private readonly List<FrameworkElement> _pages = new();
    private readonly List<Border> _dots = new();
    private readonly Border _backButton;
    private readonly TextBlock _nextLabel;
    private readonly TextBlock _stepTitle;
    private readonly TextBlock _stepHint;

    private int _step;

    /// <summary>管理员权限这一项要等向导收尾才应用，所以先记在本地。</summary>
    private bool _runElevated = true;

    public SetupWizardWindow(App app)
    {
        _app = app;

        // 配色/字体/圆角全部取自 ThemePalette —— 与悬浮栏、面板、菜单同源
        _palette = ThemePalette.Resolve();
        FontFamily = _palette.Typeface;
        _panelBrush = Brush(WithAlpha(_palette.Background, 0xFF));   // 向导同样不透明
        _cardBrush = Brush(_palette.Chip);
        _textBrush = Brush(_palette.Text);
        _mutedBrush = Brush(_palette.Muted);
        _accentBrush = Brush(_palette.Accent);
        _lineBrush = Brush(_palette.Border);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = 760;
        Height = 620;
        Title = "ExplorerDock 设置向导";

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _stepTitle = new TextBlock { FontSize = _palette.FontSizeTitle, Foreground = _textBrush, FontWeight = FontWeights.SemiBold };
        _stepHint = new TextBlock { FontSize = _palette.FontSizeMedium, Foreground = _mutedBrush, Margin = new Thickness(0, 9, 0, 0), TextWrapping = TextWrapping.Wrap };

        var header = new StackPanel { Margin = new Thickness(34, 28, 34, 14) };
        header.Children.Add(_stepTitle);
        header.Children.Add(_stepHint);
        Grid.SetRow(header, 0);
        content.Children.Add(header);

        var body = new Grid { Margin = new Thickness(34, 12, 34, 10), VerticalAlignment = VerticalAlignment.Top };
        foreach (var page in BuildPages())
        {
            page.Visibility = Visibility.Collapsed;
            _pages.Add(page);
            body.Children.Add(page);
        }

        Grid.SetRow(body, 1);
        content.Children.Add(body);

        // ---- 底部：步骤圆点 + 按钮 ----
        var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i <= LastStep; i++)
        {
            var dot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(_palette.ThumbCornerRadius),
                Margin = new Thickness(0, 0, 8, 0),
                Background = _lineBrush,
            };

            _dots.Add(dot);
            dots.Children.Add(dot);
        }

        _backButton = BuildButton("上一步", false, () => Go(_step - 1));
        _nextLabel = new TextBlock
        {
            Text = "下一步",
            FontSize = _palette.FontSizeMedium,
            Foreground = new SolidColorBrush(ThemePalette.OnColor(_palette.Accent, _palette.Background)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var nextButton = BuildButton(_nextLabel, true, () => Go(_step + 1));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_backButton);
        buttons.Children.Add(nextButton);

        var footer = new Grid { Margin = new Thickness(34, 10, 34, 24) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(dots, 0);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(dots);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        content.Children.Add(footer);

        var root = new Border
        {
            CornerRadius = new CornerRadius(_palette.CornerRadius),
            Background = _panelBrush,
            BorderThickness = new Thickness(1),
            BorderBrush = _lineBrush,
            Child = content,
        };

        // 自绘标题栏：按住顶部能拖走，右上角一个关闭（关闭 = 就按现在这样收尾）
        var close = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(_palette.ButtonCornerRadius),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 16, 18, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "\u2715",
                FontSize = _palette.FontSizeMedium,
                Foreground = _mutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        close.MouseLeftButtonUp += (_, _) => Finish();

        var dragger = new Border { Height = 52, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Top };
        dragger.MouseLeftButtonDown += (_, _) =>
        {
            try
            {
                DragMove();
            }
            catch
            {
                // 拖拽时松手会抛，无所谓
            }
        };

        var layers = new Grid();
        layers.Children.Add(root);
        layers.Children.Add(dragger);
        layers.Children.Add(close);
        Content = layers;

        ShowStep(0);
    }

    private static SolidColorBrush Brush(Color color) => new(color);

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    // ---------- 每一步的内容 ----------

    private IEnumerable<FrameworkElement> BuildPages()
    {
        // 0. 欢迎
        var welcome = new StackPanel();
        welcome.Children.Add(Paragraph("ExplorerDock 把文件夹窗口的按钮从任务栏移到悬浮栏上，任务栏就不会被塞满了。"));
        welcome.Children.Add(Paragraph("点悬浮栏上的按钮，就能切到对应的文件夹窗口。"));
        welcome.Children.Add(Paragraph("下面 4 步用来设置接管范围、外观和启动方式。"));
        yield return welcome;

        // 1. 接管范围
        var takeover = new StackPanel();
        takeover.Children.Add(BuildToggle(
            "接管任务栏按钮",
            "把文件夹窗口的按钮从任务栏移到悬浮栏上。",
            App.Settings.TakeoverEnabled,
            v => _app.SetTakeover(v)));

        takeover.Children.Add(BuildToggle(
            "接管 Alt+Tab",
            "不接管的话，文件夹窗口在系统的 Alt+Tab 里看不到也切不到；接管后，用本软件的 Alt+Tab 就能正常切换它们。",
            App.Settings.AltTabTakeover,
            v => _app.SetAltTabTakeover(v)));

        takeover.Children.Add(BuildToggle(
            "全屏游戏时交给系统处理",
            "不勾选时，在有些全屏游戏里按 Alt+Tab 可能切不出去（切换界面会被游戏画面挡住）。勾上后这类游戏交给系统处理。",
            App.Settings.AltTabFullscreenPassthrough,
            v => _app.SetAltTabFullscreenPassthrough(v)));
        yield return takeover;

        // 2. 外观与行为
        var looks = new StackPanel();
        looks.Children.Add(BuildToggle(
            "没有文件夹时自动隐藏",
            "没有打开任何文件夹时自动隐藏悬浮栏；打开任意文件夹后自动显示。",
            App.Settings.HideWhenEmpty,
            v => _app.SetHideWhenEmpty(v)));

        looks.Children.Add(BuildToggle(
            "显示完整标题",
            "按钮上显示完整的文件夹名字。",
            App.Settings.ShowFullTitle,
            v =>
            {
                App.Settings.ShowFullTitle = v;
                App.Settings.Save();
                _app.RebuildDockItems();
            }));

        looks.Children.Add(new TextBlock
        {
            Text = "配色",
            FontSize = _palette.FontSizeHeadline,
            Foreground = _textBrush,
            Margin = new Thickness(2, 14, 0, 10),
        });

        var group = new List<Action<bool>>();
        var themeChoices = new StackPanel { Orientation = Orientation.Horizontal };
        themeChoices.Children.Add(BuildRadio("跟随系统", App.Settings.Theme == DockTheme.Auto, () => _app.SetTheme(DockTheme.Auto), group));
        themeChoices.Children.Add(BuildRadio("深色", App.Settings.Theme == DockTheme.Dark, () => _app.SetTheme(DockTheme.Dark), group));
        themeChoices.Children.Add(BuildRadio("浅色", App.Settings.Theme == DockTheme.Light, () => _app.SetTheme(DockTheme.Light), group));
        themeChoices.Children.Add(BuildRadio("自定义", App.Settings.Theme == DockTheme.Custom, () => _app.SetTheme(DockTheme.Custom), group));
        looks.Children.Add(themeChoices);

        var themeButton = BuildButton("自定义颜色…", false, () => _app.OpenThemeEditor());
        themeButton.HorizontalAlignment = HorizontalAlignment.Left;
        themeButton.Margin = new Thickness(0, 14, 0, 0);
        looks.Children.Add(themeButton);
        yield return looks;

        // 3. 启动设置
        var startup = new StackPanel();
        startup.Children.Add(BuildToggle(
            "开机自动启动",
            "开机后自动运行，不用手动打开。",
            App.Settings.RunAtStartup,
            v => _app.SetRunAtStartup(v)));

        startup.Children.Add(BuildToggle(
            "以管理员身份运行",
            "有些程序是用管理员身份打开的，本软件也得用管理员身份，才能显示它们的窗口画面、用 Alt+Tab 切换。打开后每次开机，系统会问一次是否允许。",
            _runElevated,
            v => _runElevated = v,
            next => next || ShowElevatedWarning()));
        yield return startup;

        // 4. 完成
        var done = new StackPanel();
        done.Children.Add(Paragraph("设置已经生效，悬浮栏会马上按新的设置显示。"));
        done.Children.Add(Paragraph("要修改时，右键悬浮栏左边的 ⠿ 把手，选「设置向导…」就能重新打开。"));
        done.Children.Add(Paragraph(
            "「以管理员身份运行」还勾着的话，点完成之后系统会问一次是否允许，同意后软件会重新启动。",
            true));
        yield return done;
    }

    private TextBlock Paragraph(string text, bool accent = false)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = _palette.FontSizeHeadline,
            LineHeight = 25,
            TextWrapping = TextWrapping.Wrap,
            Foreground = accent ? _accentBrush : _textBrush,
            Margin = new Thickness(0, 0, 0, 13),
        };
    }

    // ---------- 控件 ----------

    private Border BuildToggle(string title, string description, bool value, Action<bool> onChange, Func<bool, bool>? confirm = null)
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
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
            Child = check,
        };

        var text = new StackPanel { Margin = new Thickness(13, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = _palette.FontSizeHeadline, Foreground = _textBrush, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = _palette.FontSizeMedium,
            LineHeight = 21,
            Foreground = _mutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(box, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(box);
        grid.Children.Add(text);

        var row = new Border
        {
            CornerRadius = new CornerRadius(_palette.ItemCornerRadius),
            Padding = new Thickness(17, 15, 17, 15),
            Margin = new Thickness(0, 0, 0, 12),
            Background = _cardBrush,
            Cursor = Cursors.Hand,
            Child = grid,
        };

        bool current = value;
        ApplyToggle(box, check, current);

        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;

            bool next = !current;

            // confirm 说"不"（比如用户看到后果提示又改主意了），界面就一点都不动
            if (confirm is not null && !confirm(next)) return;

            current = next;
            ApplyToggle(box, check, current);
            onChange(current);
        };

        return row;
    }

    private void ApplyToggle(Border box, TextBlock check, bool on)
    {
        box.Background = on ? _accentBrush : Brushes.Transparent;
        box.BorderBrush = on ? _accentBrush : _lineBrush;
        check.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border BuildRadio(string title, bool selected, Action onSelect, List<Action<bool>> group)
    {
        var dot = new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(_palette.ButtonCornerRadius),
            Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var circle = new Border
        {
            Width = 19,
            Height = 19,
            CornerRadius = new CornerRadius(_palette.CornerRadius),
            BorderThickness = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Top,
            Child = dot,
        };

        var label = new TextBlock
        {
            Text = title,
            FontSize = _palette.FontSizeHeadline,
            Foreground = _textBrush,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(circle, 0);
        Grid.SetColumn(label, 1);
        grid.Children.Add(circle);
        grid.Children.Add(label);

        var row = new Border
        {
            CornerRadius = new CornerRadius(_palette.ItemCornerRadius),
            Padding = new Thickness(15, 12, 18, 12),
            Margin = new Thickness(0, 0, 10, 0),
            Background = _cardBrush,
            Cursor = Cursors.Hand,
            Child = grid,
        };

        void Apply(bool on)
        {
            circle.Background = on ? _accentBrush : Brushes.Transparent;
            circle.BorderBrush = on ? _accentBrush : _lineBrush;
            dot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        Apply(selected);
        group.Add(Apply);   // 让同一组里的其它选项能把我关掉

        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;

            foreach (var set in group) set(false);
            Apply(true);
            onSelect();
        };

        return row;
    }

    private Border BuildButton(string text, bool primary, Action onClick)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = _palette.FontSizeMedium,
            Foreground = primary ? new SolidColorBrush(ThemePalette.OnColor(_palette.Accent, _palette.Background)) : _textBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        return BuildButton(label, primary, onClick);
    }

    private Border BuildButton(TextBlock label, bool primary, Action onClick)
    {
        var button = new Border
        {
            MinWidth = 112,
            Height = 38,
            CornerRadius = new CornerRadius(_palette.ButtonCornerRadius),
            Background = primary ? _accentBrush : _cardBrush,
            BorderThickness = new Thickness(1),
            BorderBrush = primary ? _accentBrush : _lineBrush,
            Cursor = Cursors.Hand,
            Padding = new Thickness(18, 0, 18, 0),
            Child = label,
        };

        button.MouseLeftButtonUp += (_, _) => onClick();
        return button;
    }

    // ---------- 流程 ----------

    private void Go(int step)
    {
        if (step < 0) return;

        if (step > LastStep)
        {
            Finish();
            return;
        }

        ShowStep(step);
    }

    private void ShowStep(int step)
    {
        _step = step;

        for (int i = 0; i < _pages.Count; i++)
        {
            _pages[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        }

        for (int i = 0; i < _dots.Count; i++)
        {
            _dots[i].Background = i == step ? _accentBrush : _lineBrush;
        }

        (_stepTitle.Text, _stepHint.Text) = step switch
        {
            0 => ("欢迎使用 ExplorerDock", "按下面几步完成初始设置。"),
            1 => ("接管范围", "选择由 ExplorerDock 接管的功能。"),
            2 => ("外观与行为", "设置悬浮栏的显示方式与配色。"),
            3 => ("启动设置", "设置开机启动与运行权限。"),
            _ => ("设置完成", "所有设置之后都可以随时修改。"),
        };

        _backButton.Visibility = step == 0 ? Visibility.Collapsed : Visibility.Visible;
        _nextLabel.Text = step == LastStep ? "完成" : "下一步";
    }

    /// <summary>取消"以管理员身份运行"之前的提醒。返回 true 表示用户确认要取消。</summary>
    private static bool ShowElevatedWarning()
    {
        return ConfirmDialog.Confirm(
            "取消以管理员身份运行？",
            "取消后，用管理员身份打开的程序在应用切换列表里看不到窗口画面，也无法用 Alt+Tab 切换。",
            "确定取消",
            "保持勾选");
    }

    private void Finish()
    {
        App.Settings.SetupCompleted = true;
        App.Settings.RunElevated = _runElevated;
        App.Settings.Save();

        // 权限状态变了，开机自启的注册方式（普通启动 / 计划任务）也要跟着换
        _app.RefreshStartupRegistration();

        Close();

        if (_runElevated && !App.IsElevated)
        {
            _app.RestartElevated();
        }
        else if (!_runElevated && App.IsElevated)
        {
            _app.RestartNormal();
        }
    }
}
