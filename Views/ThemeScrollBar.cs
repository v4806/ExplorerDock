using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ExplorerDock.Services;

namespace ExplorerDock.Views;

/// <summary>
/// 细的、跟主题同色的滚动条。
///
/// 系统默认那根又粗、颜色也对不上主题，所以剪贴板面板和 Alt+Tab 面板共用这一份样式，
/// 两处外观完全同源。
/// </summary>
internal static class ThemeScrollBar
{
    /// <summary>把主题滚动条样式塞进这个元素的资源里（它自己的和子元素上的滚动条都会用）。</summary>
    public static void Apply(FrameworkElement host, Color thumb, ThemePalette palette)
    {
        try
        {
            // 滚动条滑块用"弱化色"，和文字灰一个来源，只是在主题里再压暗一档
            var slider = $"#{(byte)(thumb.A * 0.35):X2}{thumb.R:X2}{thumb.G:X2}{thumb.B:X2}";

            var xaml = $@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='ScrollBar'>
  <Setter Property='Width' Value='8'/>
  <Setter Property='Margin' Value='0,0,4,0'/>
  <Setter Property='Background' Value='Transparent'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='ScrollBar'>
        <Grid Background='Transparent'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template>
                  <ControlTemplate TargetType='Thumb'>
                    <Border CornerRadius='{palette.ThumbCornerRadius}' Background='{slider}'/>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Focusable='False'/>
            </Track.IncreaseRepeatButton>
            <Track.DecreaseRepeatButton>
              <RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Focusable='False'/>
            </Track.DecreaseRepeatButton>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

            var style = (Style)System.Windows.Markup.XamlReader.Parse(xaml);

            host.Resources[typeof(ScrollBar)] = style;
        }
        catch
        {
            // 样式没做出来也能正常滚动，只是样子回到系统的
        }
    }
}
