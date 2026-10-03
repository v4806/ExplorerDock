using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Xml;
using Microsoft.Win32;

namespace ExplorerDock.Services;

/// <summary>
/// 开机自启的注册。两个去处：
///   ① 普通权限启动 → 写 <c>HKCU\...\Run</c>，登录时拉起本程序；
///   ② 勾了「以管理员身份运行」→ 改用计划任务（<c>RunLevel=HighestAvailable</c>），登录时以管理员身份静默启动、不弹 UAC。
///
/// 为什么抽成一个独立类：创建最高权限的计划任务**需要管理员权限**，而界面进程从拆成
/// 双进程之后就永远是普通权限（见 <c>App.OnStartup</c> 的注释）。所以 ② 只能交给提权的 <c>--host</c> 进程：
/// 界面进程写 Run 键、尽力创建任务；host 启动时再 <see cref="EnsureElevatedTask"/> 校验并补齐。
///
/// 2026-09-20 实测到的两个坑（1.0.12 及更早版本都有）：
///   ① <c>/TR</c> 的值必须**自带一层双引号**。只给 <c>ProcessStartInfo.ArgumentList</c> 加参数不够 ——
///      它只保证"这是一个参数"，schtasks 仍会在路径里第一个空格处把命令行拆成 Command + Arguments 两截，
///      任务建得出来、状态还是 Ready，但登录时永远起不来：
///          &lt;Command&gt;D:\Program&lt;/Command&gt;&lt;Arguments&gt;Files\ExplorerDock\ExplorerDock.exe&lt;/Arguments&gt;
///   ② 普通权限进程调 <c>schtasks /Create /RL HIGHEST</c> 直接返回「错误: 拒绝访问。」，
///      而旧代码只有一个 <c>catch {}</c> —— 于是"开机不自启"既没线索也没救。
///
/// 2026-09-29 实测到的第三个坑（1.0.x 一直有，「开机自启经常不生效」的主因）：
///   <c>schtasks /Create</c> 命令行建出来的任务带 Task Scheduler 的**默认设置**，与本程序的需求正好相反：
///     · <c>&lt;DisallowStartIfOnBatteries&gt;true&lt;/DisallowStartIfOnBatteries&gt;</c> —— 笔记本用电池时登录压根不启动；
///     · <c>&lt;StopIfGoingOnBatteries&gt;true&lt;/StopIfGoingOnBatteries&gt;</c> —— 插电启动后一拔电源就被系统结束；
///     · <c>&lt;ExecutionTimeLimit&gt;PT72H&lt;/ExecutionTimeLimit&gt;</c> —— 常驻程序跑满 3 天被系统杀掉。
///   本机实测（<c>schtasks /Query /TN ExplorerDock /V</c>）：
///     "Power Management: Stop On Battery Mode, No Start On Batteries" / "Stop Task If Runs X Hours and X Mins: 72:00:00"。
///   所以现在改成**整份 XML 模板**建任务（<c>schtasks /Create /XML</c>）：上面三条一次性写成 false / false / PT0S，
///   顺带把 <c>&lt;Command&gt;</c> 写成不带引号的完整路径（XML 里没有命令行拆分的问题，坑 ① 从根上消失）。
///   <see cref="TaskIsUpToDate"/> 会连这些设置一起校验，于是历史上留下的坏任务在下次启动时被自动纠正。
///
/// 还有一条兜底（2026-09-29 加）：勾了「以管理员身份运行」时，注册链路依赖一次 UAC 同意 ——
///   界面进程先删 Run 键，任务却只有提权进程建得出来；用户在 UAC 上点「否」就变成两处都没有，
///   下次开机什么都不启动，而且没有下一次自愈机会。现在改成 **任务确认合规之前不删 Run 键**：
///   最坏情况退化成普通权限自启（单实例锁会吃掉重复启动的进程），不会出现"彻底不自启"。
///
/// 下面每个结果都写进 <c>%TEMP%\ExplorerDock.startup.log</c>，用户报"开机不自启"时一眼能看出坏在哪一步；
/// <see cref="LogSnapshot"/> 还会把 Run 键 / StartupApproved / 任务设置 / 电源状态一起记下来。
/// </summary>
internal static class StartupRegistration
{
    private const string ValueName = "ExplorerDock";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string TaskXmlFileName = "ExplorerDock.task.xml";
    private const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>界面进程调用：按当前设置把 Run 键与计划任务同步成该有的样子。</summary>
    public static void Apply(bool runAtStartup, bool runElevated, string? exe)
    {
        bool valid = !string.IsNullOrWhiteSpace(exe);

        // 关掉自启：两个去处都清干净
        if (!runAtStartup || !valid)
        {
            DeleteRunValue();
            DeleteTask();
            return;
        }

        // 普通权限启动：Run 键就够了，任务不能留（否则登录时启动两次）
        if (!runElevated)
        {
            WriteRunValue(exe!);
            DeleteTask();
            return;
        }

        // 提权启动：优先计划任务；任务没建成就把 Run 键留着兜底，下次开机至少还能以普通权限起来
        bool taskReady = EnsureElevatedTask(exe);

        if (taskReady)
        {
            DeleteRunValue();
        }
        else
        {
            WriteRunValue(exe!, fallback: true);
        }
    }

    /// <summary>
    /// 保证「登录时以最高权限运行」的计划任务存在、命令行完整、且电源/时限设置正确。
    /// 已经正确就直接返回 <c>true</c>，不做多余动作。返回 false 表示任务当前不可用（多半是没权限或被杀软拦了）。
    /// </summary>
    public static bool EnsureElevatedTask(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;

        if (TaskIsUpToDate(exe))
        {
            AppendLog("startup-task: 已合规，跳过重建");
            return true;
        }

        string? userId = ResolveUserId();
        string xml = BuildTaskXml(exe, userId);
        string xmlPath = Path.Combine(Path.GetTempPath(), TaskXmlFileName);

        try
        {
            // schtasks 读 /XML 最稳的是 UTF-16LE + BOM
            File.WriteAllText(xmlPath, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        }
        catch (Exception ex)
        {
            AppendLog($"startup-task: 写 XML 失败 {ex.Message}");
            return false;
        }

        var (rc, output) = RunSchtasks("/Create", "/TN", ValueName, "/XML", xmlPath, "/F");

        AppendLog(
            $"startup-task: create/xml rc={rc} user={Environment.UserName} sid={userId} elevated={IsElevated()} " +
            $"out={Shorten(output)} pid={Environment.ProcessId}");

        if (rc != 0) return false;

        var (verifyRc, verified) = RunSchtasks("/Query", "/TN", ValueName, "/XML");

        bool ok = verifyRc == 0 && TaskIsUpToDate(verified, exe);

        if (ok)
        {
            AppendLog("startup-task: verify ok（Command 完整 + 电源/时限已解除）");
            return true;
        }

        AppendLog($"startup-task: verify MISMATCH exe={exe} xml={Shorten(verified, 1200)}");
        return false;
    }

    /// <summary>
    /// 任务是否已经是"我们要的那一份"：命令行指向当前 exe，且三条关键设置都已解除限制。
    /// 旧版本用 schtasks 命令行建的任务在这里会被判为不合规 —— 于是被自动重建纠正。
    /// </summary>
    private static bool TaskIsUpToDate(string exe) => TaskIsUpToDate(ReadTaskXml() ?? string.Empty, exe);

    /// <summary>同上的显式 XML 版本（校验刚查询回来的那份）。</summary>
    private static bool TaskIsUpToDate(string xml, string exe)
    {
        if (string.IsNullOrWhiteSpace(xml)) return false;
        if (!CommandMatches(xml, exe)) return false;

        return ReadNodeText(xml, "DisallowStartIfOnBatteries") is "false"
            && ReadNodeText(xml, "StopIfGoingOnBatteries") is "false"
            && string.Equals(ReadNodeText(xml, "ExecutionTimeLimit"), "PT0S", StringComparison.OrdinalIgnoreCase)
            && string.Equals(ReadNodeText(xml, "RunLevel"), "HighestAvailable", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>查询任务的 XML；任务不存在或查询失败返回 null。</summary>
    private static string? ReadTaskXml()
    {
        var (rc, xml) = RunSchtasks("/Query", "/TN", ValueName, "/XML");

        return rc == 0 ? xml : null;
    }

    /// <summary>
    /// 计划任务里的 &lt;Command&gt; 是不是这个 exe。
    ///
    /// 注意：老任务（用 <c>/TR</c> 建的那批）存进去的值可能**自带引号**，读回来长这样 ——
    ///   &lt;Command&gt;"D:\Program Files\ExplorerDock\ExplorerDock.exe"&lt;/Command&gt;
    /// 所以不能直接拿裸路径去比（会把正确任务误报成 MISMATCH）。这里去掉首尾引号再比，
    /// 并把 XML 实体（<c>&amp;amp;</c> 这类）解回来。
    /// </summary>
    private static bool CommandMatches(string xml, string exe)
    {
        var command = ReadNodeText(xml, "Command");
        if (command is null) return false;

        return string.Equals(WebUtility.HtmlDecode(command).Trim().Trim('"'), exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>取某个节点的文本；节点不存在或自闭合时返回 null / 空串。</summary>
    private static string? ReadNodeText(string xml, string tag)
    {
        int start = xml.IndexOf("<" + tag, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        int openEnd = xml.IndexOf('>', start);
        if (openEnd < 0) return null;

        if (xml[openEnd - 1] == '/') return string.Empty;

        int end = xml.IndexOf("</" + tag, openEnd, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;

        return xml[(openEnd + 1)..end].Trim();
    }

    /// <summary>
    /// 整份任务 XML。除了命令行，重点是三类设置：
    ///   · 电源：<c>false/false</c> —— 笔记本用电池时也启动、拔电源也不被结束（以前这里是默认的 true/true）；
    ///   · 时限：<c>PT0S</c>（无限）—— 常驻程序不会被"跑满 72 小时"杀掉（以前默认 PT72H）；
    ///   · 触发：登录后延迟 10 秒，给资源管理器留出就绪时间；错过触发（关机中触发的登录）时补跑。
    /// </summary>
    private static string BuildTaskXml(string exe, string? userId)
    {
        var builder = new StringBuilder();

        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = Encoding.Unicode,
        };

        using (var writer = XmlWriter.Create(builder, settings))
        {
            writer.WriteStartDocument();

            writer.WriteStartElement("Task", TaskNamespace);
            writer.WriteAttributeString("version", "1.2");

            writer.WriteStartElement("RegistrationInfo");
            writer.WriteElementString("Author", Environment.UserName);
            writer.WriteEndElement();

            writer.WriteStartElement("Triggers");
            writer.WriteStartElement("LogonTrigger");
            writer.WriteElementString("Enabled", "true");

            if (!string.IsNullOrWhiteSpace(userId)) writer.WriteElementString("UserId", userId);

            writer.WriteElementString("Delay", "PT10S");
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteStartElement("Principals");
            writer.WriteStartElement("Principal");
            writer.WriteAttributeString("id", "Author");

            if (!string.IsNullOrWhiteSpace(userId)) writer.WriteElementString("UserId", userId);

            writer.WriteElementString("LogonType", "InteractiveToken");
            writer.WriteElementString("RunLevel", "HighestAvailable");
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteStartElement("Settings");
            writer.WriteElementString("MultipleInstancesPolicy", "IgnoreNew");
            writer.WriteElementString("DisallowStartIfOnBatteries", "false");
            writer.WriteElementString("StopIfGoingOnBatteries", "false");
            writer.WriteElementString("AllowHardTerminate", "true");
            writer.WriteElementString("StartWhenAvailable", "true");
            writer.WriteElementString("RunOnlyIfNetworkAvailable", "false");

            writer.WriteStartElement("IdleSettings");
            writer.WriteElementString("StopOnIdleEnd", "false");
            writer.WriteElementString("RestartOnIdle", "false");
            writer.WriteEndElement();

            writer.WriteElementString("AllowStartOnDemand", "true");
            writer.WriteElementString("Enabled", "true");
            writer.WriteElementString("Hidden", "false");
            writer.WriteElementString("RunOnlyIfIdle", "false");
            writer.WriteElementString("WakeToRun", "false");
            writer.WriteElementString("ExecutionTimeLimit", "PT0S");
            writer.WriteElementString("Priority", "7");
            writer.WriteEndElement();

            writer.WriteStartElement("Actions");
            writer.WriteAttributeString("Context", "Author");
            writer.WriteStartElement("Exec");

            // XML 里写完整路径（不写引号）—— schtasks 不会再按空格拆成 Command + Arguments
            writer.WriteElementString("Command", exe);
            writer.WriteEndElement();
            writer.WriteEndElement();

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return builder.ToString();
    }

    /// <summary>当前登录用户的标识（优先 SID，SID 拿不到才退回 域\用户名）。</summary>
    private static string? ResolveUserId()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();

            var sid = identity.User?.Value;
            if (!string.IsNullOrWhiteSpace(sid)) return sid;
        }
        catch
        {
            // 忽略，退回域\用户名
        }

        try
        {
            return $"{Environment.UserDomainName}\\{Environment.UserName}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>写 Run 键。<paramref name="fallback"/> = true 表示提权任务还没就绪，这条只是兜底。</summary>
    private static void WriteRunValue(string exe, bool fallback = false)
    {
        string note = fallback ? "（提权任务未就绪，保留兜底）" : string.Empty;

        try
        {
            // 关键：用 CreateSubKey。旧代码用 OpenSubKey(writable:true)，Run 键不存在时返回 null 被静默跳过，
            // 于是"开关开着却不自启"，连一行日志都没有。
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (key is null)
            {
                AppendLog($"run-key: 打开/创建 Run 键失败（返回 null）{note}");
                return;
            }

            var before = key.GetValue(ValueName) as string;
            var value = $"\"{exe}\"";

            if (string.Equals(before, value, StringComparison.Ordinal))
            {
                AppendLog($"run-key: 已是目标值 value={value}{note}");
                return;
            }

            key.SetValue(ValueName, value);

            AppendLog($"run-key: 写入 value={value} 原值={(before is null ? "(无)" : before)}{note}");
        }
        catch (Exception ex)
        {
            AppendLog($"run-key: 写入失败 {ex.Message}{note}");
        }
    }

    private static void DeleteRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

            if (key is null)
            {
                AppendLog("run-key: 清除跳过（Run 键不存在）");
                return;
            }

            var before = key.GetValue(ValueName) as string;

            key.DeleteValue(ValueName, throwOnMissingValue: false);

            AppendLog($"run-key: 清除 原值={(before is null ? "(无)" : before)}");
        }
        catch (Exception ex)
        {
            AppendLog($"run-key: 清除失败 {ex.Message}");
        }
    }

    private static void DeleteTask()
    {
        var (rc, output) = RunSchtasks("/Delete", "/TN", ValueName, "/F");

        AppendLog($"startup-task: delete rc={rc} out={Shorten(output)}");
    }

    /// <summary>
    /// 把这一刻的自启相关状态记进启动日志（界面上"查看日志"会导出这份）。
    ///
    /// 为什么需要它：光有"创建失败 rc=5"还不够 —— Run 键是不是被别的东西删了、任务管理器里是不是被禁用过、
    /// 机器此刻在不在电池上，都要一眼看得到，否则用户报"开机不自启"只能靠猜。
    /// </summary>
    public static void LogSnapshot(string tag)
    {
        try
        {
            AppendLog(
                $"snapshot[{tag}]: elevated={IsElevated()} user={Environment.UserName} sid={ResolveUserId()} " +
                $"exe={Environment.ProcessPath} 64bit={Environment.Is64BitProcess}");

            AppendLog(
                $"snapshot[{tag}]: run-key={ReadRunValue() ?? "(无)"} approved={ReadStartupApproved()}");

            AppendLog($"snapshot[{tag}]: task={ReadTaskSummary()}");

            AppendLog($"snapshot[{tag}]: power={ReadPowerStatus()}");
        }
        catch (Exception ex)
        {
            AppendLog($"snapshot[{tag}]: 记录失败 {ex.Message}");
        }
    }

    private static string? ReadRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);

            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex)
        {
            return $"(读取失败 {ex.Message})";
        }
    }

    /// <summary>
    /// 读"启动应用"里的启用/禁用标记。前 4 字节：2 = 启用，3 = 被用户/优化软件禁用 ——
    /// 被禁时 Run 键还在，但登录时 Windows 会跳过它（而且在任务管理器里看起来仍是"开"）。
    /// </summary>
    private static string ReadStartupApproved()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath);

            if (key?.GetValue(ValueName) is not byte[] bytes || bytes.Length < 4) return "(无记录)";

            var head = BitConverter.ToUInt32(bytes, 0);
            var state = head switch
            {
                2 => "启用",
                3 => "被禁用（任务管理器→启动里关过，Run 键不会生效）",
                6 => "启用",
                _ => $"未知({head})",
            };

            return $"{state} raw={BitConverter.ToString(bytes)}";
        }
        catch (Exception ex)
        {
            return $"(读取失败 {ex.Message})";
        }
    }

    private static string ReadTaskSummary()
    {
        var xml = ReadTaskXml();

        if (xml is null) return "不存在或查询失败";

        return $"存在 command={ReadNodeText(xml, "Command")} " +
               $"batteries={ReadNodeText(xml, "DisallowStartIfOnBatteries")}/{ReadNodeText(xml, "StopIfGoingOnBatteries")} " +
               $"limit={ReadNodeText(xml, "ExecutionTimeLimit")} runLevel={ReadNodeText(xml, "RunLevel")} " +
               $"delay={ReadNodeText(xml, "Delay")}";
    }

    private static string ReadPowerStatus()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status)) return "(读不到)";

            var ac = status.ACLineStatus switch
            {
                0 => "电池",
                1 => "市电",
                _ => "未知",
            };

            var battery = status.BatteryFlag == 128 ? "无电池" : $"0x{status.BatteryFlag:X2}";

            return $"ac={ac} battery={battery} life={status.BatteryLifePercent}%";
        }
        catch (Exception ex)
        {
            return $"(读不到 {ex.Message})";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();

            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>跑一次 schtasks 并拿回退出码与输出（输出完整，日志侧再截断 —— 校验要靠完整 XML）。</summary>
    private static (int Code, string Output) RunSchtasks(params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var arg in args) start.ArgumentList.Add(arg);

            using var process = Process.Start(start);
            if (process is null) return (-1, "(进程没起来)");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            return (process.HasExited ? process.ExitCode : -1, (stdout + Environment.NewLine + stderr).Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static string Shorten(string text, int max = 300)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();

        while (flat.Contains("  ", StringComparison.Ordinal)) flat = flat.Replace("  ", " ");

        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    /// <summary>自启相关结果写进启动日志。</summary>
    private static void AppendLog(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "ExplorerDock.startup.log");

            var info = new FileInfo(path);
            if (info.Exists && info.Length > 200_000) File.Delete(path);

            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败无所谓
        }
    }
}
