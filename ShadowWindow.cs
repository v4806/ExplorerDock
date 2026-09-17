using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ExplorerDock.Interop;
using ExplorerDock.Services;

namespace ExplorerDock;

/// <summary>
/// 只负责画一圈投影的辅助窗口。
/// 它带 WS_EX_TRANSPARENT + WS_EX_NOACTIVATE：鼠标点击完全穿透、永远不会被激活，
/// 于是悬浮栏可以「看得见阴影」又「命中区域只等于可见 UI」。
/// </summary>
internal sealed class ShadowWindow : Window
{
    /// <summary>阴影需要的外扩空间（给 BlurRadius + ShadowDepth 留的）。</summary>
    public const double ShadowMargin = 30;

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

        // 兜底外观也走 ThemePalette，不再是写死的 #1B1B1F
        var palette = ThemePalette.Resolve();

        _shape = new Border
        {
            Background = new SolidColorBrush(palette.Background),
            BorderBrush = new SolidColorBrush(palette.Background),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(palette.CornerRadius),
            Margin = new Thickness(ShadowMargin),
            Effect = new DropShadowEffect
            {
                BlurRadius = palette.ShadowBlur,
                ShadowDepth = palette.ShadowDepth,
                Direction = 270,
                Opacity = palette.ShadowOpacity,
                Color = Colors.Black,
                RenderingBias = RenderingBias.Performance,
            },
        };

        Content = _shape;
    }

    public void SetTone(Color background, double cornerRadius, bool light)
    {
        // 背景和描边同色：这一层要和悬浮栏完全重合，两层的圆角抗锯齿叠在一起，
        // 边框边缘（尤其四角）才会显得实，不会因为单层抗锯齿而发虚
        var brush = new SolidColorBrush(background);
        _shape.Background = brush;
        _shape.BorderBrush = brush;
        _shape.CornerRadius = new CornerRadius(cornerRadius);

        if (_shape.Effect is DropShadowEffect shadow)
        {
            // 阴影参数同样以主题为基准，浅色下略强一点（白底上阴影要更明显才看得出层次）
            var palette = ThemePalette.Resolve();
            shadow.Opacity = Math.Min(0.95, palette.ShadowOpacity + (light ? 0.05 : 0.07));
            shadow.BlurRadius = palette.ShadowBlur + (light ? 2 : 0);
            shadow.ShadowDepth = palette.ShadowDepth + (light ? 1 : 0);
        }
    }

    /// <summary>
    /// 阴影矩形相对悬浮栏的内缩量。保持 0：两层必须像素级重合，
    /// 一旦错开，四角就会出现两层弧线的"重影"，看起来就是边框发虚。
    /// </summary>
    public void SetInset(double inset)
    {
        _shape.Margin = new Thickness(ShadowMargin + inset);
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
