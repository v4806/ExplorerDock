using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using ExplorerDock.Interop;

namespace ExplorerDock.Services;

/// <summary>托盘图标与右键菜单。</summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly App _app;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _showDockItem;
    private readonly ToolStripMenuItem _takeoverItem;
    private readonly ToolStripMenuItem _hideWhenEmptyItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _themeAutoItem;
    private readonly ToolStripMenuItem _themeDarkItem;
    private readonly ToolStripMenuItem _themeLightItem;

    public TrayIconManager(App app)
    {
        _app = app;

        _showDockItem = new ToolStripMenuItem("显示悬浮栏");
        _showDockItem.Click += (_, _) => _app.SetShowDock(!App.Settings.ShowDock);

        _takeoverItem = new ToolStripMenuItem("接管任务栏按钮");
        _takeoverItem.Click += (_, _) => _app.SetTakeover(!App.Settings.TakeoverEnabled);

        _hideWhenEmptyItem = new ToolStripMenuItem("没有文件夹时自动隐藏");
        _hideWhenEmptyItem.Click += (_, _) => _app.SetHideWhenEmpty(!App.Settings.HideWhenEmpty);

        _startupItem = new ToolStripMenuItem("开机自动启动");
        _startupItem.Click += (_, _) => _app.SetRunAtStartup(!App.Settings.RunAtStartup);

        _themeAutoItem = new ToolStripMenuItem("主题：跟随系统");
        _themeAutoItem.Click += (_, _) => _app.SetTheme(DockTheme.Auto);

        _themeDarkItem = new ToolStripMenuItem("主题：深色");
        _themeDarkItem.Click += (_, _) => _app.SetTheme(DockTheme.Dark);

        _themeLightItem = new ToolStripMenuItem("主题：浅色");
        _themeLightItem.Click += (_, _) => _app.SetTheme(DockTheme.Light);

        _menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Renderer = new DarkMenuRenderer(),
            BackColor = Color.FromArgb(27, 27, 31),
            ForeColor = Color.FromArgb(242, 242, 242),
            Font = new Font("Microsoft YaHei UI", 9f),
        };
        _menu.Items.Add(_showDockItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_takeoverItem);
        _menu.Items.Add(_hideWhenEmptyItem);
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_themeAutoItem);
        _menu.Items.Add(_themeDarkItem);
        _menu.Items.Add(_themeLightItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("回到屏幕顶部居中", null, (_, _) => _app.ResetDockPosition()));
        _menu.Items.Add(new ToolStripMenuItem("把窗口还原到任务栏", null, (_, _) => _app.RestoreAllToTaskbar()));
        _menu.Items.Add(new ToolStripMenuItem("关闭所有文件夹窗口", null, (_, _) => App.CloseAllExplorerWindows()));
        _menu.Items.Add(new ToolStripMenuItem("重启资源管理器", null, (_, _) => App.RestartExplorer()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("退出 ExplorerDock", null, (_, _) => _app.ExitApp()));

        _menu.Opening += (_, _) => RefreshChecks();

        _notifyIcon = new NotifyIcon
        {
            Text = "ExplorerDock — 文件夹悬浮栏",
            Icon = LoadIcon(),
            ContextMenuStrip = _menu,
            Visible = true,
        };

        _notifyIcon.DoubleClick += (_, _) => _app.SetShowDock(!App.Settings.ShowDock);

        RefreshChecks();
        ApplyTheme();
    }

    /// <summary>托盘菜单跟随主题：深色用自定义渲染器，浅色交回系统默认。</summary>
    public void ApplyTheme()
    {
        bool light = App.Settings.ResolveLightTheme();

        _menu.Renderer = light ? new ToolStripProfessionalRenderer() : new DarkMenuRenderer();
        _menu.BackColor = light ? Color.FromArgb(0xF7, 0xF7, 0xF8) : Color.FromArgb(27, 27, 31);
        _menu.ForeColor = light ? Color.FromArgb(26, 26, 26) : Color.FromArgb(242, 242, 242);
    }

    private void RefreshChecks()
    {
        _showDockItem.Checked = App.Settings.ShowDock;
        _takeoverItem.Checked = App.Settings.TakeoverEnabled;
        _hideWhenEmptyItem.Checked = App.Settings.HideWhenEmpty;
        _startupItem.Checked = App.Settings.RunAtStartup;
        _themeAutoItem.Checked = App.Settings.Theme == DockTheme.Auto;
        _themeDarkItem.Checked = App.Settings.Theme == DockTheme.Dark;
        _themeLightItem.Checked = App.Settings.Theme == DockTheme.Light;
    }

    private static Icon LoadIcon()
    {
        var handle = ShellInterop.GetFolderHIcon(small: false);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var source = Icon.FromHandle(handle);
                return (Icon)source.Clone();
            }
            finally
            {
                ShellInterop.DestroyIcon(handle);
            }
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}

/// <summary>托盘菜单的深色配色，跟悬浮栏保持一致。</summary>
internal sealed class DarkMenuColorTable : ProfessionalColorTable
{
    private static readonly Color Background = Color.FromArgb(27, 27, 31);
    private static readonly Color Hover = Color.FromArgb(38, 255, 255, 255);
    private static readonly Color Line = Color.FromArgb(31, 255, 255, 255);

    public override Color ToolStripDropDownBackground => Background;
    public override Color ImageMarginGradientBegin => Background;
    public override Color ImageMarginGradientMiddle => Background;
    public override Color ImageMarginGradientEnd => Background;
    public override Color MenuBorder => Color.FromArgb(51, 255, 255, 255);
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => Hover;
    public override Color MenuItemSelectedGradientBegin => Hover;
    public override Color MenuItemSelectedGradientEnd => Hover;
    public override Color MenuItemPressedGradientBegin => Background;
    public override Color MenuItemPressedGradientEnd => Background;
    public override Color SeparatorDark => Line;
    public override Color SeparatorLight => Line;
    public override Color CheckBackground => Hover;
    public override Color CheckSelectedBackground => Hover;
}

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer()
        : base(new DarkMenuColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled
            ? Color.FromArgb(242, 242, 242)
            : Color.FromArgb(110, 242, 242, 242);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var bounds = e.Item.Bounds;
        using var pen = new Pen(Color.FromArgb(240, 240, 240), 1.8f);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        float x = bounds.Left + 3f;
        float y = bounds.Top + (bounds.Height / 2f);

        e.Graphics.DrawLines(pen, new[]
        {
            new PointF(x, y),
            new PointF(x + 3.5f, y + 4f),
            new PointF(x + 9f, y - 4.5f),
        });
    }
}
