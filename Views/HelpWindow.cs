using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ExplorerDock.Services;

namespace ExplorerDock.Views;

/// <summary>
/// 使用说明窗口：面向普通用户 —— 只说"这个功能有什么用、怎么用"。
///
/// 外观与剪贴板面板、切换面板同一套主题（含滚动条）；点别处失活就自己关掉，不用按钮。
/// </summary>
internal sealed class HelpWindow : Window
{
    public HelpWindow()
    {
        var palette = ThemePalette.Resolve();

        // 不透明底：半透明会透出后面窗口，字看不清
        var panel = new SolidColorBrush(Color.FromArgb(0xFF, palette.Background.R, palette.Background.G, palette.Background.B));
        var textBrush = new SolidColorBrush(palette.Text);
        var mutedBrush = new SolidColorBrush(palette.Muted);
        var lineBrush = new SolidColorBrush(palette.Border);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = 580;
        Height = 660;
        Title = "ExplorerDock";
        FontFamily = palette.Typeface;

        var body = new StackPanel();

        AddSection(body, textBrush, mutedBrush, "悬浮栏",
            "鼠标左键点按钮：点一下显示窗口，再点一下隐藏。",
            "鼠标中键点按钮：关闭窗口。",
            "鼠标右键点按钮：菜单。",
            "双击左边的点阵把手：隐藏悬浮栏；右键把手：打开设置菜单。",
            "窗口或行太多时，用滚轮或两端的箭头滚动查看。");

        AddSection(body, textBrush, mutedBrush, "贴边隐藏",
            "悬浮栏停在屏幕边缘时，会自动滑出屏幕外收纳隐藏，鼠标靠近该边缘区域，滑进屏幕内显示。");

        AddSection(body, textBrush, mutedBrush, "Alt+Tab 切换窗口",
            "Alt+Tab：弹出切换面板；再按 Tab 往后选，Shift+Alt+Tab 往前选。",
            "松开 Alt：切到选中的窗口；按 Esc：取消。",
            "鼠标直接点某张卡片：立刻切过去。",
            "Alt+~（` 键）：在同一个程序的多个窗口之间切换。");

        AddSection(body, textBrush, mutedBrush, "Win+V 剪贴板历史",
            "Win+V：打开剪贴板。",
            "点一条：粘贴到当前窗口。",
            "拖动：拖动粘贴到任意窗口。",
            "拖到悬浮栏按钮或 Alt+Tab 卡片上：粘贴到该窗口。",
            "拖放文件到悬浮栏按钮 / 应用切换卡片上。");

        AddSection(body, textBrush, mutedBrush, "接管哪些程序",
            "接管任务栏按钮：把窗口按钮从任务栏移到悬浮栏，文件夹窗口默认接管。",
            "自动接管多窗口程序：同一个程序开了 2 个以上窗口时自动接管，不用一个个设置。",
            "接管指定程序：手动挑程序，它只要开窗口就接管。");

        AddSection(body, textBrush, mutedBrush, null,
            "以管理员身份运行：要接管以管理员身份运行的程序窗口的操作，就离不开开它。");

        var scroller = new ScrollViewer
        {
            Height = 528,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = body,
        };

        // 滚动条与剪贴板面板/切换面板同一套外观
        ThemeScrollBar.Apply(scroller, palette.Muted, palette);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        root.Children.Add(new TextBlock
        {
            Text = "使用说明",
            FontSize = palette.FontSizeLarge,
            FontWeight = FontWeights.SemiBold,
            Foreground = textBrush,
        });

        root.Children.Add(new TextBlock
        {
            Text = "ExplorerDock 把窗口按钮从任务栏搬到一条悬浮栏上，任务栏不再被塞满。",
            FontSize = palette.FontSizeMedium,
            LineHeight = 23,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = mutedBrush,
            TextWrapping = TextWrapping.Wrap,
        });

        root.Children.Add(scroller);

        Content = new Border
        {
            CornerRadius = new CornerRadius(palette.CornerRadius),
            Background = panel,
            BorderThickness = new Thickness(palette.Custom ? palette.CustomBorderThickness : palette.DockBorderThickness),
            BorderBrush = lineBrush,
            Child = root,
        };

        // 按住任意空白处能拖动
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

        // 只是看一眼的东西：点到别处（失去焦点）就自己关掉；Esc 也能关
        Deactivated += (_, _) => Close();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    /// <summary>
    /// 一段"小标题 + 若干条说明"。
    /// 段落之间只用一条细分隔线，不加卡片底色（用户要求：别给每一项套背景）。
    /// </summary>
    private static void AddSection(StackPanel body, Brush textBrush, Brush mutedBrush, string? title, params string[] lines)
    {
        var palette = ThemePalette.Resolve();

        // 第一段之前不加分隔线
        if (body.Children.Count > 0)
        {
            body.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 15, 0, 15),
                Background = new SolidColorBrush(palette.Separator),
            });
        }

        if (!string.IsNullOrEmpty(title))
        {
            body.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = palette.FontSizeHeadline,
                FontWeight = FontWeights.SemiBold,
                Foreground = textBrush,
                Margin = new Thickness(0, 0, 0, 7),
            });
        }

        foreach (var line in lines)
        {
            body.Children.Add(new TextBlock
            {
                Text = "· " + line,
                FontSize = palette.FontSizeMedium,
                LineHeight = 22,
                TextWrapping = TextWrapping.Wrap,
                Foreground = mutedBrush,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }
    }
}
