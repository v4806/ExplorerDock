using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ExplorerDock.Services;

namespace ExplorerDock.Views;

/// <summary>
/// 检查更新小窗口：一打开就去查 GitHub 上的 tag。
///
/// 有新版本时先弹一次确认框，用户点"下载"才真正下载；
/// 下载完只把安装程序启动起来，是否继续安装由用户在安装界面自己决定。
/// </summary>
internal sealed class UpdateWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly ThemePalette _palette = ThemePalette.Resolve();

    public UpdateWindow()
    {
        var palette = _palette;

        var panelBrush = new SolidColorBrush(Color.FromArgb(0xFF, palette.Background.R, palette.Background.G, palette.Background.B));
        var borderBrush = new SolidColorBrush(palette.Border);
        var textBrush = new SolidColorBrush(palette.Text);
        var mutedBrush = new SolidColorBrush(palette.Muted);
        var accentBrush = new SolidColorBrush(palette.Accent);

        FontFamily = palette.Typeface;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 470;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "ExplorerDock";

        var body = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };
        body.Children.Add(Header());

        _status = new TextBlock
        {
            Text = $"正在检查更新…（当前版本 {UpdateChecker.CurrentVersionText}）",
            FontSize = palette.FontSizeMedium,
            LineHeight = 22,
            Foreground = mutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };

        body.Children.Add(_status);

        _progress = new ProgressBar
        {
            Height = 4,
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Foreground = accentBrush,
            Background = new SolidColorBrush(palette.Separator),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 14, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        body.Children.Add(_progress);

        var close = new Button
        {
            Content = "关闭",
            Padding = new Thickness(22, 6, 22, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
            Style = (Style)Application.Current.Resources["EditorButton"],
        };

        close.Click += (_, _) => Close();
        body.Children.Add(close);

        var shell = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = panelBrush,
            BorderThickness = new Thickness(1),
            BorderBrush = borderBrush,
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

        // 打开就开始查（只有用户主动点菜单打开时才会走到这里，没有后台自动检查）
        Loaded += async (_, _) => await RunCheckAsync();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private UIElement Header()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10), Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "检查更新",
            FontSize = _palette.FontSizeHeadline,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(_palette.Text),
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

    private async Task RunCheckAsync()
    {
        try
        {
            var info = await UpdateChecker.CheckAsync(_cts.Token);

            if (_cts.IsCancellationRequested) return;

            if (info is null)
            {
                SetStatus($"当前已是最新版本（{UpdateChecker.CurrentVersionText}）。");
                return;
            }

            if (info.Setup is null)
            {
                SetStatus($"发现新版本 {info.Tag}，但这个版本还没有可下载的安装包。");
                return;
            }

            SetStatus($"发现新版本 {info.Tag}（当前 {UpdateChecker.CurrentVersionText}）。");

            bool download = ConfirmDialog.Confirm(
                $"发现新版本 {info.Tag}",
                "是否下载这个版本的安装包？下载完成后会自动运行它，是否继续安装由你决定。",
                "下载",
                "取消");

            if (!download)
            {
                SetStatus($"发现新版本 {info.Tag}，已取消下载。");
                return;
            }

            await DownloadAndRunAsync(info.Setup);
        }
        catch (OperationCanceledException)
        {
            // 窗口被关掉了，不用管
        }
        catch (Exception ex)
        {
            SetStatus($"检查更新失败：{ex.Message}");
        }
    }

    private async Task DownloadAndRunAsync(UpdateAsset asset)
    {
        _progress.Value = 0;
        _progress.Visibility = Visibility.Visible;

        var progress = new Progress<double>(value =>
        {
            _progress.Value = value;
            SetStatus($"正在下载 {asset.Name}… {value:P0}");
        });

        try
        {
            var path = await UpdateChecker.DownloadAsync(asset, progress, _cts.Token);

            if (_cts.IsCancellationRequested) return;

            SetStatus($"安装包已下载到：{path}" + Environment.NewLine +
                      "正在启动安装程序 —— 是否继续安装由你在安装界面里决定。");

            // 只运行安装包，不静默安装
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (OperationCanceledException)
        {
            // 窗口被关掉了
        }
        catch (Exception ex)
        {
            SetStatus($"下载失败：{ex.Message}");
        }
        finally
        {
            _progress.Visibility = Visibility.Collapsed;
        }
    }

    private void SetStatus(string text) => _status.Text = text;

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // 取消失败无所谓
        }

        base.OnClosed(e);
    }
}
