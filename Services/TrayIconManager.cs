using System.Windows;
using ExplorerDock.Interop;

namespace ExplorerDock.Services;

/// <summary>
/// 托盘图标：右键弹与悬浮栏完全相同的那套菜单，双击显示/隐藏悬浮栏。
///
/// 底下用的是 <see cref="TrayIcon"/>（Shell_NotifyIcon 的封装），不依赖 WinForms ——
/// 那一个组件的代价是十几 MB 的运行时。
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly TrayIcon? _icon;

    public TrayIconManager(App app, Window owner)
    {
        _icon = new TrayIcon(
            owner,
            "ExplorerDock — 文件夹悬浮栏",
            () => app.ShowTrayMenu(),
            () => app.SetShowDock(!App.Settings.ShowDock));
    }

    /// <summary>
    /// 主题变化时的钩子。
    ///
    /// 托盘图标与提示文字都不跟主题走（图标就是程序自身图标），所以这里是空的；
    /// 留着是为了让调用方（App.ApplyMenuTheme）不必关心具体实现。
    /// </summary>
    public void ApplyTheme()
    {
    }

    public void Dispose() => _icon?.Dispose();
}
