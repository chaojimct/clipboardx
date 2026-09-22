using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClipboardX.Migrator;

/// <summary>原生调用（目前只有取 OEM 代码页）。</summary>
internal static partial class NativeMethods
{
    /// <summary>取系统 OEM 代码页（简中 = 936）。</summary>
    [DllImport("kernel32.dll")]
    public static extern uint GetOEMCP();
}

/// <summary>单项停用结果。</summary>
internal sealed record CleanupOutcome(
    List<string> RunRemoved,
    List<string> TasksRemoved,
    List<string> TasksFailed,
    List<string> Errors)
{
    /// <summary>是否有任何一项真的被清掉了。</summary>
    public bool Changed => RunRemoved.Count > 0 || TasksRemoved.Count > 0;

    /// <summary>是否清得干净（没剩失败项）。</summary>
    public bool FullyCleared => Changed && TasksFailed.Count == 0 && Errors.Count == 0;
}

/// <summary>
/// 老版自启项清理。
///
/// <b>铁律：只删自启项，绝不碰老版的程序目录与数据目录。</b>
/// 历史库在 %LocalAppData%\ClipboardX，用户确认迁移无误后自己走老版卸载器
/// （卸载向导选「是」会递归删掉它 —— 所以卸载这件事不由本工具代劳）。
/// </summary>
internal static class LegacyCleanup
{
    /// <summary>老版是否仍在运行（按互斥体判定，覆盖三个 flavor）。</summary>
    public static bool IsLegacyRunning()
    {
        // FULL flavor 互斥体存在即认为在跑；另两个 flavor 的名字不同，
        // 这里只判最常见的那个（launcher 场景下用户基本都装 FULL）。
        try
        {
            using var m = Mutex.OpenExisting(MigratePaths.LegacyMutex);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // 存在但当前无权打开 —— 也算在跑。
            return true;
        }
    }

    /// <summary>清 HKCU Run 值。</summary>
    private static List<string> RemoveRunValues()
    {
        var removed = new List<string>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(MigratePaths.RunSubKey, writable: true);
            if (key is null) return removed;

            foreach (var name in MigratePaths.RunValueNames)
            {
                if (key.GetValue(name) is not null)
                {
                    key.DeleteValue(name, throwOnMissingValue: false);
                    removed.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            // 不吞异常语义：由调用方决定怎么呈现。这里返回已删部分。
            Console.Error.WriteLine($"清理自启项失败：{ex.Message}");
        }
        return removed;
    }

    /// <summary>某个计划任务是否存在。</summary>
    private static bool TaskExists(string name) =>
        RunSchtasks(["/Query", "/TN", name]).ExitCode == 0;

    /// <summary>删除计划任务。</summary>
    private static bool DeleteTask(string name) =>
        RunSchtasks(["/Delete", "/F", "/TN", name]).ExitCode == 0;

    private static (int ExitCode, string StdOut, string StdErr) RunSchtasks(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // schtasks 输出是系统 OEM 代码页（简中=GBK）。显式按 OEM 解码，
            // 否则中文错误信息会变成 ??????。取不到 OEM 码页时退回系统默认。
            try
            {
                var oem = System.Text.Encoding.GetEncoding(
                    (int)NativeMethods.GetOEMCP());
                psi.StandardOutputEncoding = oem;
                psi.StandardErrorEncoding = oem;
            }
            catch
            {
                // 取码页失败不影响主流程，只是错误文本可能不完美。
            }

            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return (-1, "", "无法启动 schtasks");
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, so, se);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    /// <summary>
    /// 停用老版全部自启项。返回逐项结果（供向导如实展示，别只说「已完成」）。
    /// </summary>
    public static CleanupOutcome DisableAutostart()
    {
        var runRemoved = RemoveRunValues();
        var tasksRemoved = new List<string>();
        var tasksFailed = new List<string>();
        var errors = new List<string>();

        foreach (var task in MigratePaths.ScheduledTasks)
        {
            if (!TaskExists(task))
            {
                continue; // 本来就没有，不算失败也不算成功
            }
            if (DeleteTask(task))
            {
                tasksRemoved.Add(task);
            }
            else
            {
                tasksFailed.Add(task);
                errors.Add($"计划任务 {task} 删除失败（多半需要管理员权限）");
            }
        }

        return new CleanupOutcome(runRemoved, tasksRemoved, tasksFailed, errors);
    }
}
