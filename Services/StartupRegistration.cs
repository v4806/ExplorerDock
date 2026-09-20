using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ExplorerDock.Services;

/// <summary>
/// 开机自启的注册。两个去处：
///   ① 普通权限启动 → 写 <c>HKCU\...\Run</c>，登录时拉起本程序；
///   ② 勾了「以管理员身份运行」→ 改用计划任务（<c>/RL HIGHEST</c>），登录时以管理员身份静默启动、不弹 UAC。
///
/// 为什么抽成一个独立类：创建 <c>/RL HIGHEST</c> 的计划任务**需要管理员权限**，而界面进程从拆成
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
/// 下面每个结果都写进 <c>%TEMP%\ExplorerDock.startup.log</c>，用户报"开机不自启"时一眼能看出坏在哪一步。
/// </summary>
internal static class StartupRegistration
{
    private const string ValueName = "ExplorerDock";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>界面进程调用：按当前设置把 Run 键与计划任务同步成该有的样子。</summary>
    public static void Apply(bool runAtStartup, bool runElevated, string? exe)
    {
        bool valid = !string.IsNullOrWhiteSpace(exe);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

            if (key is not null)
            {
                if (runAtStartup && !runElevated && valid)
                {
                    key.SetValue(ValueName, $"\"{exe}\"");
                }
                else
                {
                    // 走计划任务时必须把 Run 键删掉，否则登录时会启动两次（一次普通权限、一次提权）
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"run-key: 写入失败 {ex.Message}");
        }

        if (runAtStartup && runElevated && valid)
        {
            // 界面进程多半没权限建这个任务（预期之内），提权的 --host 启动时会再补一次
            EnsureElevatedTask(exe);
        }
        else
        {
            DeleteTask();
        }
    }

    /// <summary>
    /// 保证「登录时以最高权限运行」的计划任务存在、且命令行是完整的。
    /// 已经正确就直接返回，不做多余动作。
    /// </summary>
    public static void EnsureElevatedTask(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return;

        if (TaskIsCorrect(exe)) return;

        var (rc, output) = RunSchtasks(
            "/Create", "/TN", ValueName,
            "/TR", $"\"{exe}\"",   // 关键：这层引号必须自己带，见类注释 ①
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",
            "/F");

        AppendLog($"startup-task: create rc={rc} out={Shorten(output)} pid={Environment.ProcessId}");

        if (rc != 0) return;

        var (verifyRc, xml) = RunSchtasks("/Query", "/TN", ValueName, "/XML");

        bool ok = verifyRc == 0 && CommandMatches(xml, exe);

        AppendLog(ok
            ? "startup-task: verify ok（<Command> 是完整路径）"
            : $"startup-task: verify MISMATCH exe={exe} xml={Shorten(xml, 600)}");
    }

    private static bool TaskIsCorrect(string exe)
    {
        var (rc, xml) = RunSchtasks("/Query", "/TN", ValueName, "/XML");

        return rc == 0 && CommandMatches(xml, exe);
    }

    /// <summary>
    /// 计划任务里的 &lt;Command&gt; 是不是这个 exe。
    ///
    /// 注意：schtasks 存进去的值**自带引号**，读回来长这样 ——
    ///   &lt;Command&gt;"D:\Program Files\ExplorerDock\ExplorerDock.exe"&lt;/Command&gt;
    /// 所以不能直接拿裸路径去 Contains（会把正确任务误报成 MISMATCH）。这里去掉首尾引号再比。
    /// </summary>
    private static bool CommandMatches(string xml, string exe)
    {
        const string open = "<Command>";
        const string close = "</Command>";

        int start = xml.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return false;

        start += open.Length;

        int end = xml.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return false;

        var command = xml[start..end].Trim().Trim('"');

        return string.Equals(command, exe, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteTask()
    {
        var (rc, output) = RunSchtasks("/Delete", "/TN", ValueName, "/F");

        AppendLog($"startup-task: delete rc={rc} out={Shorten(output)}");
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
