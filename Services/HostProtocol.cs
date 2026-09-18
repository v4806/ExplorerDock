using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace ExplorerDock.Services;

/// <summary>跨进程传的一个悬浮栏按钮（对应 <see cref="ExplorerDock.Models.ExplorerWindowInfo"/>）。</summary>
internal sealed class WindowDto
{
    public long Handle { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Path { get; set; }
    public bool Minimized { get; set; }
    public int TabIndex { get; set; } = -1;
    public bool TabSelected { get; set; } = true;

    /// <summary>所属程序（小写 exe 名）；悬浮栏按它分行。</summary>
    public string Process { get; set; } = "explorer";

    /// <summary>exe 全路径：非文件夹窗口靠它在界面侧取图标。</summary>
    public string? IconPath { get; set; }

    /// <summary>是不是资源管理器文件夹窗口。</summary>
    public bool IsExplorer { get; set; } = true;
}

/// <summary>「接管指定程序」的选择列表里的一项（对应 <see cref="RunningProcessInfo"/>）。</summary>
internal sealed class ProcessDto
{
    public string Name { get; set; } = string.Empty;
    public string ExePath { get; set; } = string.Empty;
    public int WindowCount { get; set; }
    public string SampleTitle { get; set; } = string.Empty;
}

/// <summary>窗口 / 标签页快照。</summary>
internal sealed class SnapshotDto
{
    public long Foreground { get; set; }
    public List<WindowDto> Windows { get; set; } = new();
}

/// <summary>合并卡里的一个预览格。</summary>
internal sealed class CellDto
{
    public long Handle { get; set; }
    public bool ShowThumbnail { get; set; } = true;
}

/// <summary>跨进程传的一张 Alt+Tab 卡片（对应 <see cref="AltTabWindowInfo"/>）。</summary>
internal sealed class CardDto
{
    public long Handle { get; set; }
    public int TabIndex { get; set; } = -1;
    public bool TabSelected { get; set; } = true;
    public string Title { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public List<CellDto> Cells { get; set; } = new();
}

/// <summary>
/// 界面进程（普通权限）与功能进程（提权，<c>--host</c>）之间的一条消息。
///
/// 同一个方向只跑一种消息：<c>Id &gt; 0</c> 是"要回复的请求"，
/// <c>Id == 0</c> 是"不用回复的事件/推送"。
/// </summary>
internal sealed class HostMessage
{
    /// <summary>请求编号；0 表示这是一条推送。</summary>
    public int Id { get; set; }

    public string Cmd { get; set; } = string.Empty;

    /// <summary>请求是否成功（仅回复里有意义）。</summary>
    public bool Ok { get; set; }

    public string? Error { get; set; }

    public bool Enabled { get; set; }
    public long Hwnd { get; set; }
    public int Tab { get; set; } = -1;
    public int Value { get; set; }

    /// <summary>按键动作名（<see cref="HostProtocol.KeyAdvance"/> 等）。</summary>
    public string? Action { get; set; }

    public bool TakeoverAltTab { get; set; } = true;
    public bool TakeoverClipboard { get; set; } = true;
    public bool PanelOpen { get; set; }
    public bool FullscreenPassthrough { get; set; } = true;

    /// <summary>「多窗口程序自动接管」开关。</summary>
    public bool AutoTakeover { get; set; }

    /// <summary>手动指定要接管的程序名（小写、不带扩展名）。</summary>
    public List<string>? Processes { get; set; }

    /// <summary>运行中程序清单（<see cref="HostProtocol.CmdRunningProcesses"/> 的回复）。</summary>
    public List<ProcessDto>? ProcessList { get; set; }

    public SnapshotDto? Snapshot { get; set; }
    public List<CardDto>? Cards { get; set; }
}

/// <summary>管道名、帧格式与两边共用的命令名。</summary>
internal static class HostProtocol
{
    /// <summary>
    /// 管道名。v3：按钮多了「程序名 / exe 路径 / 是不是文件夹窗口」三个字段，
    /// 接管范围也多了两条 —— 旧版本的功能进程连上来会缺字段，
    /// 所以换个名字，别让新旧两边悄悄对上。
    /// </summary>
    public const string PipeName = "ExplorerDock.Host.v3";

    public const string CmdStart = "start";
    public const string CmdTakeover = "takeover";
    public const string CmdReapply = "reapply";
    public const string CmdRestoreAll = "restore_all";
    public const string CmdActivate = "activate";
    public const string CmdMinimize = "minimize";
    public const string CmdSelectedTab = "selected_tab";
    public const string CmdCards = "cards";
    public const string CmdProcessCards = "process_cards";
    public const string CmdExit = "exit";

    /// <summary>给当前前台窗口补一次 Ctrl+V（对管理员窗口必须由提权进程发）。</summary>
    public const string CmdSendPaste = "send_paste";

    /// <summary>补发一次 Alt 抬起。</summary>
    public const string CmdSendAltUp = "send_alt_up";

    /// <summary>
    /// 界面 → 功能进程：让文件夹窗口执行它自己的"粘贴"命令（UI Automation）。
    ///
    /// 不能只靠 Ctrl+V：它落在"有焦点的控件"上，焦点不在文件列表时粘贴会落空。
    /// </summary>
    public const string CmdPasteInto = "paste_into";

    /// <summary>界面 → 功能进程：关掉一个窗口（普通权限进程关不掉管理员程序窗口）。</summary>
    public const string CmdCloseWindow = "close_window";

    /// <summary>界面 → 功能进程：关掉这个窗口所属程序的所有顶层窗口。</summary>
    public const string CmdCloseProcessWindows = "close_process_windows";

    /// <summary>
    /// 界面 → 功能进程：结束这个窗口所属程序的所有进程（按 exe 路径匹配，多个独立进程一起结束）。
    /// 破坏性操作，只在用户明确选择"结束进程"时发。
    /// </summary>
    public const string CmdKillProcesses = "kill_processes";

    /// <summary>在光标位置点一下左键。</summary>
    public const string CmdClickCursor = "click_cursor";

    /// <summary>问一句"这个窗口现在是不是前台"（界面进程自己查不准，见实现注释）。</summary>
    public const string CmdIsForeground = "is_foreground";

    /// <summary>问一句"左键还按着吗"（同上，前台是高权限程序时界面进程读不准）。</summary>
    public const string CmdIsLeftDown = "is_left_down";

    /// <summary>界面 → 功能进程：接管开关与面板状态（钩子靠它判断吞不吞）。</summary>
    public const string CmdKeyState = "key_state";

    /// <summary>
    /// 界面 → 功能进程：接管范围（自动接管开关 + 手动指定的程序名单）。
    /// 两个新设置项都是主机侧判定用的，改了要推过去。
    /// </summary>
    public const string CmdTakeoverScope = "takeover_scope";

    /// <summary>界面 → 功能进程：要一份"运行中的程序"清单（选择接管程序用）。</summary>
    public const string CmdRunningProcesses = "running_processes";

    /// <summary>
    /// 界面 → 功能进程：Alt+Tab 的合并范围（<see cref="HostMessage.Enabled"/> = 合并全部同名进程窗口）。
    ///
    /// 卡片列表在功能进程里生成，不推过去的话改设置就没反应。
    /// </summary>
    public const string CmdAltTabGroup = "alt_tab_group";

    /// <summary>功能进程 → 界面：接管到的按键动作。</summary>
    public const string CmdKeyAction = "key_action";

    /// <summary>按键动作名。</summary>
    public const string KeyAdvance = "advance";
    public const string KeyAdvanceBack = "advance_back";
    public const string KeyAdvanceProcess = "advance_process";
    public const string KeyAdvanceProcessBack = "advance_process_back";
    public const string KeyCommit = "commit";
    public const string KeyCancel = "cancel";
    public const string KeyToggleClipboard = "toggle_clipboard";

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>一条消息一行 JSON（JSON 本身不含换行，所以按行读就够了）。</summary>
    public static string Encode(HostMessage message) => JsonSerializer.Serialize(message, Options);

    public static HostMessage? Decode(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            return JsonSerializer.Deserialize<HostMessage>(line, Options);
        }
        catch
        {
            return null;
        }
    }

    public static SnapshotDto ToDto(ExplorerDock.Models.ExplorerSnapshot snapshot)
    {
        var dto = new SnapshotDto { Foreground = snapshot.Foreground.ToInt64() };

        foreach (var window in snapshot.Windows)
        {
            dto.Windows.Add(new WindowDto
            {
                Handle = window.Handle.ToInt64(),
                Title = window.Title,
                Path = window.LocationPath,
                Minimized = window.IsMinimized,
                TabIndex = window.TabIndex,
                TabSelected = window.TabSelected,
                Process = window.ProcessName,
                IconPath = window.IconPath,
                IsExplorer = window.IsExplorer,
            });
        }

        return dto;
    }

    public static ExplorerDock.Models.ExplorerSnapshot FromDto(SnapshotDto dto)
    {
        var list = new List<ExplorerDock.Models.ExplorerWindowInfo>(dto.Windows.Count);

        foreach (var window in dto.Windows)
        {
            var handle = new IntPtr(window.Handle);
            var path = window.Path;

            list.Add(new ExplorerDock.Models.ExplorerWindowInfo
            {
                Handle = handle,
                Title = window.Title,
                LocationPath = path,
                IsMinimized = window.Minimized,
                TabIndex = window.TabIndex,
                TabSelected = window.TabSelected,
                ProcessName = window.Process,
                IconPath = window.IconPath,
                IsExplorer = window.IsExplorer,

                // 图标在这一侧生成：ImageSource 过不了管道，路径可以。
                // 文件夹窗口按路径取文件夹图标 —— 路径拿不到时（"此电脑"这类 shell 命名空间、
                // 多标签窗口里非当前显示的那些标签页）退回通用文件夹图标，
                // 不能给 null：拆进程之后图标是在这边生成的，null 就是"这个按钮没有图标"。
                Icon = window.IsExplorer
                    ? Interop.ShellInterop.GetFolderIcon(path is { Length: > 0 } ? path : null)
                    : Interop.ShellInterop.GetExeIcon(window.IconPath),
            });
        }

        return new ExplorerDock.Models.ExplorerSnapshot
        {
            Windows = list,
            Foreground = new IntPtr(dto.Foreground),
        };
    }

    public static CardDto ToDto(AltTabWindowInfo card)
    {
        var dto = new CardDto
        {
            Handle = card.Handle.ToInt64(),
            TabIndex = card.TabIndex,
            TabSelected = card.TabSelected,
            Title = card.Title,
            ProcessName = card.ProcessName,
        };

        foreach (var cell in card.Cells)
        {
            dto.Cells.Add(new CellDto { Handle = cell.Handle.ToInt64(), ShowThumbnail = cell.ShowThumbnail });
        }

        return dto;
    }

    public static AltTabWindowInfo FromDto(CardDto dto)
    {
        var cells = new List<AltTabPreviewCell>(dto.Cells.Count);

        foreach (var cell in dto.Cells)
        {
            cells.Add(new AltTabPreviewCell(new IntPtr(cell.Handle), cell.ShowThumbnail));
        }

        var handle = new IntPtr(dto.Handle);

        return new AltTabWindowInfo
        {
            Handle = handle,
            TabIndex = dto.TabIndex,
            TabSelected = dto.TabSelected,
            Title = dto.Title,
            ProcessName = dto.ProcessName,
            Cells = cells,

            // 图标在界面进程这边按窗口句柄取（跨权限拿不到时自然降级）
            Icon = Interop.ShellInterop.GetWindowIcon(handle),
        };
    }

    /// <summary>
    /// 按行读一条消息（管道两边都用它）。
    ///
    /// 必须是**异步**读：NamedPipeStream 的同步 Read 会一直占着它内部的同步锁，
    /// 直到数据到达 —— 那样另一个线程的 Write 会被活活卡死（实测两边都"写了但收不到"）。
    /// </summary>
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var builder = new StringBuilder(256);
        var buffer = new byte[1];

        while (true)
        {
            int read;

            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }

            if (read <= 0) return builder.Length == 0 ? null : builder.ToString();

            if (buffer[0] == (byte)'\n') return builder.ToString();

            if (buffer[0] != (byte)'\r') builder.Append((char)buffer[0]);
        }
    }

    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    /// <summary>同步版本（内部还是走异步写，避免和读抢锁）。</summary>
    public static void WriteLine(Stream stream, string line)
        => WriteLineAsync(stream, line, CancellationToken.None).GetAwaiter().GetResult();
}
