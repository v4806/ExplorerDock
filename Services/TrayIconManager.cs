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
            // 勾号需要左侧那条 check 边距，ShowCheckMargin 默认是 false，
            // 不打开的话 ToolStripDropDownMenu 根本不会去画勾（渲染器里的 OnRenderItemCheck 也就白写了）
            ShowCheckMargin = true,
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

        // 统一左右内边距：左侧那条是 ShowCheckMargin 留出的勾号列，
        // 文字到右边缘的间距跟它取齐，免得左边一大块空白、右边贴着边
        foreach (ToolStripItem item in _menu.Items)
        {
            if (item is ToolStripMenuItem menuItem)
            {
                menuItem.Padding = new Padding(4, 5, 12, 5);
            }
        }

        _notifyIcon = new NotifyIcon
        {
            Text = "ExplorerDock — 文件夹悬浮栏",
            Icon = LoadIcon(),
            Visible = true,
        };

        // 托盘右键弹出与悬浮栏完全相同的 WPF 现代菜单
        // （不再用 WinForms 的原生菜单：圆角、阴影、间距、高亮全做不了）
        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) _app.ShowTrayMenu();
        };

        // 双击 = 显示/隐藏悬浮栏（单键快捷开关）
        _notifyIcon.DoubleClick += (_, _) => _app.SetShowDock(!App.Settings.ShowDock);

        // 托盘左键/双击不绑定动作：悬浮栏自动显隐并保持置顶，用不到这个入口；

        RefreshChecks();
        ApplyTheme();
    }

    /// <summary>托盘菜单同样保持深色：切回系统默认会变成白底，与整体割裂。</summary>
    public void ApplyTheme()
    {
        _menu.Renderer = new DarkMenuRenderer();
        _menu.BackColor = Color.FromArgb(27, 27, 31);
        _menu.ForeColor = Color.FromArgb(242, 242, 242);
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
        // 用应用自身的图标（csproj 里的 ApplicationIcon）。
        // 之前是直接取系统的文件夹图标，和资源管理器长得一样，太难认。
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch
        {
            // 取不到就退回系统图标
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
    internal static readonly Color Background = Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1F);
    internal static readonly Color Hover = Color.FromArgb(0xFF, 0x3A, 0x3A, 0x42);
    private static readonly Color Line = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);

    // 注：ProfessionalColorTable.UseSystemColors 不是 virtual，改不了；
    // 选中色由 DarkMenuRenderer.OnRenderMenuItemBackground 自绘保证

    public override Color ToolStripDropDownBackground => Background;
    public override Color ImageMarginGradientBegin => Background;
    public override Color ImageMarginGradientMiddle => Background;
    public override Color ImageMarginGradientEnd => Background;
    public override Color MenuBorder => Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => Hover;
    public override Color MenuItemSelectedGradientBegin => Hover;
    public override Color MenuItemSelectedGradientEnd => Hover;
    public override Color MenuItemPressedGradientBegin => Hover;
    public override Color MenuItemPressedGradientEnd => Hover;
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

    /// <summary>
    /// 选中/按下的背景自己画，不依赖颜色表回退 —— 之前那样在某些情况下会回退成系统强调色（亮蓝）。
    /// </summary>
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var bounds = new Rectangle(Point.Empty, e.Item.Size);
        var color = e.Item.Selected || e.Item.Pressed
            ? DarkMenuColorTable.Hover
            : DarkMenuColorTable.Background;

        using var brush = new SolidBrush(color);
        e.Graphics.FillRectangle(brush, bounds);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // 在左侧 check 边距里画一个白色对勾（勾选状态在深色底上必须看得清）
        var bounds = e.Item.Bounds;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        float x = bounds.Left + 4f;
        float y = bounds.Top + (bounds.Height / 2f);

        using var pen = new Pen(Color.FromArgb(245, 245, 245), 2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        e.Graphics.DrawLines(pen, new[]
        {
            new PointF(x, y),
            new PointF(x + 3.5f, y + 4f),
            new PointF(x + 9f, y - 4.5f),
        });
    }
}
