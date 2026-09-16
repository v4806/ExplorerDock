using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExplorerDock.Services;

/// <summary>悬浮栏配色：跟随系统 / 强制深色 / 强制浅色。</summary>
public enum DockTheme
{
    Auto = 0,
    Dark = 1,
    Light = 2,
}

public sealed class Settings
{
    /// <summary>悬浮栏与菜单的配色方案。</summary>
    public DockTheme Theme { get; set; } = DockTheme.Auto;

    /// <summary>是否把资源管理器窗口从任务栏摘除（核心功能）。</summary>
    public bool TakeoverEnabled { get; set; } = true;

    /// <summary>是否显示悬浮栏。</summary>
    public bool ShowDock { get; set; } = true;

    /// <summary>没有任何文件夹窗口时是否隐藏悬浮栏。</summary>
    public bool HideWhenEmpty { get; set; } = true;

    /// <summary>按钮上显示完整标题而不是截断到 145px。</summary>
    public bool ShowFullTitle { get; set; } = true;

    /// <summary>开机自启。</summary>
    public bool RunAtStartup { get; set; } = true;

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

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
        catch
        {
            return false;
        }
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
