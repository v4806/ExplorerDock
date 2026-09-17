using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExplorerDock.Services;

/// <summary>悬浮栏配色：跟随系统 / 强制深色 / 强制浅色 / 自定义。</summary>
public enum DockTheme
{
    Auto = 0,
    Dark = 1,
    Light = 2,
    Custom = 3,
}

public sealed class Settings
{
    /// <summary>悬浮栏与菜单的配色方案。</summary>
    public DockTheme Theme { get; set; } = DockTheme.Auto;

    // ---------- 自定义主题 ----------

    /// <summary>背景色，格式 #AARRGGBB。</summary>
    public string CustomBackground { get; set; } = "#FA1B1B1F";

    /// <summary>描边色。</summary>
    public string CustomBorder { get; set; } = "#402E2E2E";

    /// <summary>普通状态文字色。</summary>
    public string CustomText { get; set; } = "#FFF2F2F2";

    /// <summary>鼠标悬浮时的高亮底色。</summary>
    public string CustomHover { get; set; } = "#1CFFFFFF";

    /// <summary>当前活动窗口的高亮底色。</summary>
    public string CustomActive { get; set; } = "#3DFFFFFF";

    /// <summary>高亮时的文字反色。</summary>
    public string CustomActiveText { get; set; } = "#FFFFFFFF";

    /// <summary>描边线宽。</summary>
    public double CustomBorderThickness { get; set; } = 2;

    /// <summary>整体缩放（1.0 = 100%，3.0 = 300%），图标/字号/间距/圆角等比放大。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>字体名。</summary>
    public string FontFamily { get; set; } = "Microsoft YaHei UI";

    /// <summary>是否把资源管理器窗口从任务栏摘除（核心功能）。</summary>
    public bool TakeoverEnabled { get; set; } = true;

    /// <summary>
    /// 是否接管 Alt+Tab：自己画切换面板。
    /// 任务栏按钮被摘掉后，文件夹窗口会连带着从系统 Alt+Tab 里消失，所以只能自己接管。
    /// </summary>
    public bool AltTabTakeover { get; set; } = true;

    /// <summary>
    /// 全屏应用（游戏等）在前台时，把 Alt+Tab 交还给系统处理。
    /// 默认开：全屏游戏的画面会挡住我们的切换界面，硬接管会变成"按了没反应、切不出去"。
    /// </summary>
    public bool AltTabFullscreenPassthrough { get; set; } = true;

    /// <summary>是否显示悬浮栏。</summary>
    public bool ShowDock { get; set; } = true;

    /// <summary>没有任何文件夹窗口时是否隐藏悬浮栏。</summary>
    public bool HideWhenEmpty { get; set; } = true;

    /// <summary>按钮上显示完整标题而不是截断到 145px。</summary>
    public bool ShowFullTitle { get; set; } = true;

    /// <summary>开机自启。</summary>
    public bool RunAtStartup { get; set; } = true;

    /// <summary>以管理员身份运行（这样才接管得到同样以管理员运行的程序和游戏）。</summary>
    public bool RunElevated { get; set; }

    /// <summary>首次启动的设置向导是否已经走完。</summary>
    public bool SetupCompleted { get; set; }

    // ---------- 剪贴板（Win+V） ----------

    /// <summary>是否接管 Win+V：用本软件的面板替换系统剪贴板历史。</summary>
    public bool ClipboardTakeover { get; set; } = true;

    /// <summary>最多保留多少条非收藏记录（收藏不受这个数限制）。</summary>
    public int ClipboardMaxItems { get; set; } = 100;

    /// <summary>剪贴板数据最多占多少字节，默认 1GB。</summary>
    public long ClipboardMaxBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>剪贴板库是否用主密码保护（可选安全层 L2）。</summary>
    public bool ClipboardPasswordEnabled { get; set; }

    /// <summary>剪贴板库是否绑定 TPM 芯片（可选安全层 L3）。</summary>
    public bool ClipboardTpmEnabled { get; set; }

    /// <summary>面板直接在鼠标位置呼出（关掉时用默认位置 / 上次拖动到的位置）。</summary>
    public bool ClipboardAtCursor { get; set; }

    /// <summary>面板上次被拖到的位置；为空就用默认位置。</summary>
    public double? ClipboardLeft { get; set; }

    public double? ClipboardTop { get; set; }

    public double Opacity { get; set; } = 0.97;

    public double MaxWidthRatio { get; set; } = 0.92;

    public double? DockLeft { get; set; }

    public double? DockTop { get; set; }

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ExplorerDock",
        "settings.json");

    /// <summary>按当前主题设置（必要时读系统设置）判断是否用浅色。</summary>
    public bool ResolveLightTheme()
    {
        if (Theme == DockTheme.Light) return true;
        if (Theme == DockTheme.Dark) return false;
        if (Theme == DockTheme.Custom) return false;

        return IsSystemLightTheme();
    }

    /// <summary>
    /// 读系统的浅色/深色偏好。
    /// 应用要看 AppsUseLightTheme（SystemUsesLightTheme 是给任务栏/开始菜单用的），
    /// 前者读不到时退回后者。
    /// </summary>
    public static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key is null) return false;

            if (key.GetValue("AppsUseLightTheme") is int apps) return apps == 1;
            if (key.GetValue("SystemUsesLightTheme") is int system) return system == 1;
        }
        catch
        {
            // 读不到就当深色
        }

        return false;
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<Settings>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // 配置坏了就用默认值，不影响使用
        }

        return new Settings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // 写配置失败不影响主功能
        }
    }
}
