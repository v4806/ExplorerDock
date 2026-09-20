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

/// <summary>Alt+Tab 里"同名进程窗口打包"的范围。</summary>
public enum AltTabGroupMode
{
    /// <summary>只合并被悬浮栏接管的程序（当前就是资源管理器文件夹窗口）。</summary>
    TakeoverOnly = 0,

    /// <summary>所有同名 exe 的窗口都合并成一张卡。</summary>
    AllProcesses = 1,

    /// <summary>完全不合并：每个窗口（多标签窗口是每个标签页）各占一张卡。</summary>
    None = 2,
}

/// <summary>悬浮栏贴边隐藏时靠在哪条屏幕边上。</summary>
public enum DockEdge
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 3,
    Bottom = 4,
}

/// <summary>悬浮栏的排列方式。</summary>
public enum DockLayout
{
    /// <summary>横幅：每个程序一行，行内横向排这个程序的窗口按钮（默认）。</summary>
    Banner = 0,

    /// <summary>堆叠：所有窗口按钮竖着排一列，程序之间用分隔线隔开。</summary>
    Stack = 1,
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
    /// 多窗口程序自动接管：桌面上某个程序（按 exe 名判定）的可见窗口达到 2 个及以上时，
    /// 自动把这些窗口的按钮从任务栏移到悬浮栏。默认关，避免升级后任务栏突然少一堆按钮。
    /// </summary>
    public bool AutoTakeoverMultiWindow { get; set; }

    /// <summary>
    /// 手动指定要接管的程序（小写 exe 名，不带扩展名）。
    ///
    /// 这些程序**只要有 1 个窗口**就接管任务栏与窗口：用来补自动接管漏掉的，
    /// 或者在关掉自动接管时只接管指定程序。
    /// </summary>
    public List<string> TakeoverProcesses { get; set; } = new();

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

    /// <summary>
    /// 同名进程窗口在 Alt+Tab 里合并成一张卡的范围。
    /// 默认只合并"被悬浮栏接管"的程序 —— 这样浏览器、Office 之类多窗口程序
    /// 的切换习惯跟原来一样，与"以后把别的程序也搬上悬浮栏"的路线也一致：
    /// 悬浮栏接管谁，谁才参与打包。
    /// </summary>
    public AltTabGroupMode AltTabGroupScope { get; set; } = AltTabGroupMode.TakeoverOnly;

    /// <summary>是否显示悬浮栏。</summary>
    public bool ShowDock { get; set; } = true;

    /// <summary>悬浮栏的排列方式：横幅 / 堆叠。</summary>
    public DockLayout Layout { get; set; } = DockLayout.Banner;

    /// <summary>没有任何文件夹窗口时是否隐藏悬浮栏。</summary>
    public bool HideWhenEmpty { get; set; } = true;

    /// <summary>
    /// 贴边自动隐藏：悬浮栏停靠在屏幕边缘时自动滑出屏幕外，
    /// 鼠标移到那条边缘再滑回来。默认开。
    /// </summary>
    public bool EdgeAutoHide { get; set; } = true;

    /// <summary>贴边隐藏后，屏内保留的悬浮栏宽度（px），0 = 完全移出屏幕。</summary>
    public double EdgePeekWidth { get; set; } = 8;

    /// <summary>鼠标离屏幕边缘多少像素以内就把悬浮栏召回来（px）。</summary>
    public double EdgeTriggerWidth { get; set; } = 4;

    /// <summary>呼出之后，鼠标离开悬浮栏多久自动收回屏外（毫秒），0 = 立刻收回。</summary>
    public int EdgeRetractDelayMs { get; set; } = 100;

    /// <summary>上次贴边隐藏靠在哪条边；None 表示当前没有处于隐藏态。</summary>
    public DockEdge EdgeHiddenSide { get; set; } = DockEdge.None;

    /// <summary>按钮上显示完整标题而不是截断到 145px。</summary>
    public bool ShowFullTitle { get; set; } = true;

    /// <summary>
    /// 「居中模式」（菜单里的名字）：横幅模式对齐屏幕**水平**中线（宽度中点），堆叠模式对齐**垂直**中线。
    /// 默认关。开着的时候另一条轴仍由用户自己摆放 —— 只把该居中的那条轴钉在中线上。
    /// </summary>
    public bool CenterOnScreen { get; set; }

    /// <summary>
    /// 锁定悬浮栏位置。
    /// 居中模式下只锁"不被自动居中的那条轴"（横幅锁上下、堆叠锁左右）——
    /// 被居中的那条轴本来就不听拖动的，锁它没有意义。
    /// </summary>
    public bool DockLocked { get; set; }

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
