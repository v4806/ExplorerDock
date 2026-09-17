using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ExplorerDock.Services;

namespace ExplorerDock.Views;

/// <summary>
/// 自绘的确认框 / 提示框。
///
/// 不用系统 MessageBox：那个是白底、带标题栏、字体也对不上，
/// 在深色界面旁边像贴了张便条。这里跟向导、切换面板同一套配色和圆角。
/// </summary>
internal sealed class ConfirmDialog : Window
{
    private bool _confirmed;

    /// <summary>问一句"要不要"。返回 true 表示用户点了确定。</summary>
    public static bool Confirm(string title, string message, string yesText = "确定", string noText = "取消")
    {
        var dialog = new ConfirmDialog(title, message, yesText, noText);
        dialog.ShowDialog();
        return dialog._confirmed;
    }

    /// <summary>只提示一件事，用户点一下关掉。</summary>
    public static void Notify(string title, string message, string okText = "知道了")
    {
        var dialog = new ConfirmDialog(title, message, okText, null);
        dialog.ShowDialog();
    }

    private ConfirmDialog(string title, string message, string primaryText, string? secondaryText)
    {
        // 配色与圆角统一取自 ThemePalette（悬浮栏那套），不再各写各的
        var palette = ThemePalette.Resolve();

        // 同样用不透明底：对话框半透明会透出后面的窗口，字看不清
        var panel = new SolidColorBrush(Color.FromArgb(0xFF, palette.Background.R, palette.Background.G, palette.Background.B));
        var textBrush = new SolidColorBrush(palette.Text);
        var mutedBrush = new SolidColorBrush(palette.Muted);
        var accentBrush = new SolidColorBrush(palette.Accent);
        var lineBrush = new SolidColorBrush(palette.Border);
        var ghostBrush = new SolidColorBrush(palette.Chip);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.Height;
        Width = 470;
        Title = "ExplorerDock";
        FontFamily = palette.Typeface;

        var stack = new StackPanel { Margin = new Thickness(26, 24, 26, 20) };

        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = ThemePalette.Resolve().FontSizeLarge,
            FontWeight = FontWeights.SemiBold,
            Foreground = textBrush,
            TextWrapping = TextWrapping.Wrap,
        });

        stack.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = ThemePalette.Resolve().FontSizeMedium,
            LineHeight = 23,
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = mutedBrush,
            TextWrapping = TextWrapping.Wrap,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0),
        };

        if (secondaryText is not null)
        {
            buttons.Children.Add(BuildButton(secondaryText, textBrush, ghostBrush, lineBrush, () =>
            {
                _confirmed = false;
                Close();
            }));
        }

        buttons.Children.Add(BuildButton(primaryText, new SolidColorBrush(ThemePalette.OnColor(palette.Accent, palette.Background)), accentBrush, accentBrush, () =>
        {
            _confirmed = true;
            Close();
        }));

        stack.Children.Add(buttons);

        Content = new Border
        {
            CornerRadius = new CornerRadius(palette.CornerRadius),
            Background = panel,
            BorderThickness = new Thickness(palette.Custom ? palette.CustomBorderThickness : palette.DockBorderThickness),
            BorderBrush = lineBrush,
            Child = stack,
        };

        // 挂到当前活动窗口上。没有 Owner 的话，它可能排在同样是置顶窗口的向导后面 ——
        // 用户看得见对话框，鼠标却全落在下面的向导上，怎么点都没反应。
        if (Application.Current is { } application)
        {
            foreach (Window window in application.Windows)
            {
                if (ReferenceEquals(window, this) || !window.IsVisible) continue;
                if (!window.IsActive) continue;

                Owner = window;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                break;
            }
        }

        // 按住任意空白处能拖动，Esc = 取消
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
            if (e.Key != Key.Escape) return;

            _confirmed = false;
            Close();
        };
    }

    private static Border BuildButton(string text, Brush foreground, Brush background, Brush border, Action onClick)
    {
        var button = new Border
        {
            MinWidth = 96,
            Height = 36,
            CornerRadius = new CornerRadius(6),
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

        // Down 必须拦下来：窗口那层为了能拖着走挂了 MouseLeftButtonDown -> DragMove，
        // 而 DragMove 会把鼠标捕获到窗口上，按钮的抬起事件就再也收不到 —— 表现就是"点了没反应"。
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, _) => onClick();
        return button;
    }

    private static SolidColorBrush Brush(Color color) => new(color);
}
