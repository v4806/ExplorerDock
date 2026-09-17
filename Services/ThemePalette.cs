using System.Windows.Media;

namespace ExplorerDock.Services;

/// <summary>
/// 主题配色的**唯一来源**：悬浮栏、剪贴板面板、菜单都从这里取色。
///
/// 以前是各画各的 —— 自定义主题下勉强能对上，跟随系统/深/浅那两档则是两套字面量，
/// 改一处另一处不动，所谓"主题联动"其实是半截的。现在只留这一份。
/// </summary>
internal sealed class ThemePalette
{
    public Color Background { get; init; }

    public Color Border { get; init; }

    public Color Text { get; init; }

    /// <summary>次要文字（说明、时间戳这类）。</summary>
    public Color Muted { get; init; }

    public Color Hover { get; init; }

    public Color Active { get; init; }

    /// <summary>高亮块上的文字色（选中/激活态）。</summary>
    public Color ActiveText { get; init; }

    /// <summary>高亮块上的文字色（鼠标悬浮态）。</summary>
    public Color HoverText { get; init; }

    /// <summary>小圆片/标签的底色。</summary>
    public Color Chip { get; init; }

    /// <summary>强调色（选中框、星标、链接）。</summary>
    public Color Accent { get; init; }

    /// <summary>缩略图 / 图片占位的底色。</summary>
    public Color ThumbBack { get; init; }

    /// <summary>分隔线。</summary>
    public Color Separator { get; init; }

    // ---------- 圆角梯度：所有界面共用这一套，别再各写各的数字 ----------

    /// <summary>外层容器圆角。</summary>
    /// <summary>菜单/面板这类压在别的窗口上的界面用的不透明度（比悬浮栏实，太透看不清）。</summary>
    public byte SurfaceAlpha { get; init; } = 0xF8;
    public double CornerRadius { get; init; } = 9;

    /// <summary>列表项 / 标签页圆角。</summary>
    public double ItemCornerRadius { get; init; } = 7;

    /// <summary>按钮圆角。</summary>
    public double ButtonCornerRadius { get; init; } = 6;

    /// <summary>缩略图 / 滚动条圆角。</summary>
    public double ThumbCornerRadius { get; init; } = 4;

    /// <summary>列表项描边粗细（悬浮栏的条目就是 2）。</summary>
    public double ItemBorderThickness { get; init; } = 2;

    public bool Light { get; init; }

    public bool Custom { get; init; }

    /// <summary>自定义主题下的描边粗细（其它主题由各窗口自己定默认值）。</summary>
    public double CustomBorderThickness { get; init; }

    public double Opacity { get; init; } = 0.97;

    // ---------- 字体梯度：和色值一样，所有界面只许从这里取 ----------

    public string FontFamily { get; init; } = "Microsoft YaHei UI";

    /// <summary>图标字形用的字体（悬浮栏里的箭头/图标）。</summary>
    public string IconFontFamily { get; init; } = "Segoe UI Symbol";

    public double IconFontSize { get; init; } = 14;

    /// <summary>角标、脚注。</summary>
    public double FontSizeTiny { get; init; } = 10.5;

    /// <summary>次要说明、状态栏。</summary>
    public double FontSizeSmall { get; init; } = 11.5;

    /// <summary>正文：列表条目、按钮文字。</summary>
    public double FontSizeBody { get; init; } = 12.5;

    /// <summary>标签页、小节标题。</summary>
    public double FontSizeMedium { get; init; } = 13.5;

    /// <summary>小节标题（向导卡片标题这类）。</summary>
    public double FontSizeHeadline { get; init; } = 15;

    /// <summary>图标按钮上的字形（星标、加号）。</summary>
    public double FontSizeLarge { get; init; } = 16;

    /// <summary>向导/对话框的大标题。</summary>
    public double FontSizeTitle { get; init; } = 22;

    /// <summary>WPF 的 FontFamily 对象，省得每个调用点都 new 一次。</summary>
    public FontFamily Typeface => new(FontFamily);

    // ---------- 悬浮栏那几个固定外观参数，一并搬过来共用 ----------

    /// <summary>非自定义主题下的描边粗细（悬浮栏用的就是这个值）。</summary>
    public double DockBorderThickness { get; init; } = 4;

    public double ShadowBlur { get; init; } = 20;

    public double ShadowDepth { get; init; } = 3;

    public double ShadowOpacity { get; init; } = 0.55;

    public static ThemePalette Resolve()
    {
        var settings = App.Settings;
        double opacity = Math.Clamp(settings.Opacity, 0.4, 1.0);
        string font = string.IsNullOrWhiteSpace(settings.FontFamily) ? "Microsoft YaHei UI" : settings.FontFamily;

        if (settings.Theme == DockTheme.Custom)
        {
            var background = Parse(settings.CustomBackground, Color.FromArgb(0xFA, 0x1B, 0x1B, 0x1F));
            var text = Parse(settings.CustomText, Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2));

            return new ThemePalette
            {
                Background = background,
                Border = Parse(settings.CustomBorder, Color.FromArgb(0x40, 0x2E, 0x2E, 0x2E)),
                Text = text,
                Muted = Color.FromArgb(0xB4, text.R, text.G, text.B),
                Hover = Parse(settings.CustomHover, Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
                Active = Parse(settings.CustomActive, Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF)),
                ActiveText = OnColor(Parse(settings.CustomActive, Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF)), background),
                HoverText = OnColor(Parse(settings.CustomHover, Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)), background),
                Chip = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF),
                Accent = Color.FromArgb(0xFF, 0x00, 0x78, 0xD4),
                ThumbBack = Color.FromArgb(0x24, 0x00, 0x00, 0x00),
                Separator = Color.FromArgb(0x26, text.R, text.G, text.B),
                Light = false,
                Custom = true,
                CustomBorderThickness = Math.Clamp(settings.CustomBorderThickness, 0, 8),
                Opacity = opacity,
                FontFamily = font,
            };
        }

        bool light = settings.ResolveLightTheme();

        return light
            ? new ThemePalette
            {
                // 浅色：纯白底 + #333 深描边，活动项深灰块反白
                Background = Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF),
                Border = Color.FromArgb(0xFF, 0x33, 0x33, 0x33),
                Text = Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
                Muted = Color.FromArgb(0x99, 0x00, 0x00, 0x00),
                Hover = Color.FromArgb(0xFF, 0x55, 0x55, 0x55),
                Active = Color.FromArgb(0xFF, 0x33, 0x33, 0x33),
                // 高亮上的文字按高亮色明暗自动反色（深灰块 → 白字）
                ActiveText = OnColor(Color.FromArgb(0xFF, 0x33, 0x33, 0x33), Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                HoverText = OnColor(Color.FromArgb(0xFF, 0x55, 0x55, 0x55), Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                Chip = Color.FromArgb(0x24, 0x00, 0x00, 0x00),
                Accent = Color.FromArgb(0xFF, 0x00, 0x5F, 0xB8),
                ThumbBack = Color.FromArgb(0x1E, 0x00, 0x00, 0x00),
                Separator = Color.FromArgb(0x22, 0x00, 0x00, 0x00),
                Light = true,
                Opacity = opacity,
                FontFamily = font,
            }
            : new ThemePalette
            {
                // 深色：近黑底 + #555 灰描边
                Background = Color.FromArgb(0xD8, 0x1A, 0x1A, 0x1A),
                Border = Color.FromArgb(0xFF, 0x55, 0x55, 0x55),
                Text = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                Muted = Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF),
                Hover = Color.FromArgb(0xFF, 0x33, 0x33, 0x33),
                Active = Color.FromArgb(0xFF, 0x55, 0x55, 0x55),
                ActiveText = OnColor(Color.FromArgb(0xFF, 0x55, 0x55, 0x55), Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A)),
                HoverText = OnColor(Color.FromArgb(0xFF, 0x33, 0x33, 0x33), Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A)),
                Chip = Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF),
                Accent = Color.FromArgb(0xFF, 0x00, 0x78, 0xD4),
                ThumbBack = Color.FromArgb(0x24, 0x00, 0x00, 0x00),
                Separator = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
                Light = false,
                Opacity = opacity,
                FontFamily = font,
            };
    }

    /// <summary>
    /// 高亮块上的文字色：**由高亮色本身的明暗自动决定** —— 深色块给白字，浅色块给黑字。
    /// 半透明高亮先跟底色合成再判断（否则 0x1C 的白色高亮会被误判成"浅色块"，给出黑字压黑底）。
    /// 所有悬浮态/选中态的反色都走这里，别再手写白字。
    /// </summary>
    public static Color OnColor(Color highlight, Color surface)
    {
        double a = highlight.A / 255.0;
        double r = highlight.R * a + surface.R * (1 - a);
        double g = highlight.G * a + surface.G * (1 - a);
        double b = highlight.B * a + surface.B * (1 - a);

        // 感知亮度（BT.601）
        double luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
        return luminance > 0.6 ? Color.FromRgb(0x14, 0x14, 0x14) : Colors.White;
    }

    public static Color Parse(string? text, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text) && ColorConverter.ConvertFromString(text) is Color color) return color;
        }
        catch
        {
            // 非法输入用兜底色
        }

        return fallback;
    }
}


