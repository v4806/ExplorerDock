using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ExplorerDock.Interop;

namespace ExplorerDock;

/// <summary>
/// 只负责画一圈投影的辅助窗口。
/// 它带 WS_EX_TRANSPARENT + WS_EX_NOACTIVATE：鼠标点击完全穿透、永远不会被激活，
/// 于是悬浮栏可以「看得见阴影」又「命中区域只等于可见 UI」。
/// </summary>
internal sealed class ShadowWindow : Window
{
    /// <summary>阴影需要的外扩空间（给 BlurRadius + ShadowDepth 留的）。</summary>
    public const double ShadowMargin = 24;

    private readonly Border _shape;

    public ShadowWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Focusable = false;

        _shape = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1F)),
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(ShadowMargin),
            Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 3,
                Direction = 270,
                Opacity = 0.55,
                Color = Colors.Black,
                RenderingBias = RenderingBias.Performance,
            },
        };

        Content = _shape;
    }

    public void SetTone(Color background, double cornerRadius)
    {
        _shape.Background = new SolidColorBrush(background);
        _shape.CornerRadius = new CornerRadius(cornerRadius);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(
            handle,
            NativeMethods.GWL_EXSTYLE,
            style | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE);
    }
}
