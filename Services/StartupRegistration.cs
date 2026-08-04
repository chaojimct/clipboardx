using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace ClipboardManager;

/// <summary>
/// 当前用户登录时自启动。支持两种方式：
/// <list type="bullet">
/// <item>普通权限：写入 HKCU Run 键，直接指向 exe。</item>
/// <item>管理员权限：注册“任务计划程序”登录触发、最高权限运行的任务，避免 Run 键 + RunAs 在登录阶段被 UAC 阻塞。</item>
/// </list>
/// </summary>
public static class StartupRegistration
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "ClipboardManager";
    private const string ValueName = "ClipboardX";

    /// <summary>任务计划程序中用于管理员自启的任务名。</summary>
    private const string ScheduledTaskName = "ClipboardX_AutoStart";

    /// <summary>
    /// 解析要写入 Run 键 / 任务计划程序的可执行路径；<c>dotnet run</c> 等场景返回 null，避免误注册 dotnet.exe。
    /// </summary>
    private static string? ResolveExecutablePathForStartup()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath)) return null;

        if (processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return null;

        if (File.Exists(processPath) &&
            processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return processPath;

        return null;
    }

    /// <param name="runAtStartup">是否启用登录自启动。</param>
    /// <param name="runAsAdministrator">为 true 时使用任务计划程序以最高权限运行（登录触发，无 UAC 弹窗）；为 false 时写 HKCU Run。</param>
    public static void Apply(bool runAtStartup, bool runAsAdministrator)
    {
        // 始终先清理另一种方式，避免残留
        if (!runAtStartup || runAsAdministrator)
        {
            RemoveRunKeyEntry();
        }

        if (!runAtStartup || !runAsAdministrator)
        {
            RemoveScheduledTask();
        }

        if (!runAtStartup) return;

        var exePath = ResolveExecutablePathForStartup();
        if (string.IsNullOrEmpty(exePath)) return;

        if (runAsAdministrator)
        {
            RegisterScheduledTaskForElevatedStartup(exePath);
        }
        else
        {
            RegisterRunKeyEntry(exePath);
        }
    }

    /// <summary>判断当前是否已注册自启（任一方式）。</summary>
    public static bool IsRegistered()
    {
        return IsRunKeyRegistered() || IsScheduledTaskRegistered();
    }

    private static bool IsRunKeyRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: false);
            return key?.GetValue(ValueName) != null || key?.GetValue(LegacyValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsScheduledTaskRegistered()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{ScheduledTaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void RegisterRunKeyEntry(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: true);
            if (key == null) return;
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        catch
        {
            // 权限或策略失败时静默跳过
        }
    }

    private static void RemoveRunKeyEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: true);
            if (key == null) return;
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// 通过 schtasks /Create 注册一个“登录时触发、以最高权限运行”的任务。
    /// 比 HKCU Run + PowerShell RunAs 更可靠：登录会话阶段 UAC 无法稳定显示同意弹窗，
    /// RunAs 方案会被静默拦截；任务计划程序的 HighestLevel 自带提权、不弹 UAC。
    /// </summary>
    private static void RegisterScheduledTaskForElevatedStartup(string exePath)
    {
        // /RU "%USERNAME%" 指定当前用户，/RL HIGHEST 请求最高权限，/SC ONLOGON 登录时触发
        // /F 强制覆盖已存在的同名任务（用于重新启用）
        var args = new StringBuilder()
            .Append("/Create /F ")
            .Append("/TN \"").Append(ScheduledTaskName).Append("\" ")
            .Append("/TR \"\\\"").Append(exePath).Append("\\\"\" ")
            .Append("/SC ONLOGON ")
            .Append("/RL HIGHEST ")
            .Append("/RU \"").Append(Environment.UserDomainName).Append("\\").Append(Environment.UserName).Append("\"");

        if (RunSchtasks(args.ToString(), out var _) == 0) return;

        // 回退：不指定 /RU，由 schtasks 使用当前调用方用户
        var argsFallback = new StringBuilder()
            .Append("/Create /F ")
            .Append("/TN \"").Append(ScheduledTaskName).Append("\" ")
            .Append("/TR \"\\\"").Append(exePath).Append("\\\"\" ")
            .Append("/SC ONLOGON ")
            .Append("/RL HIGHEST");

        RunSchtasks(argsFallback.ToString(), out _);
    }

    private static void RemoveScheduledTask()
    {
        // /F 静默删除，避免任务不存在时报错
        RunSchtasks($"/Delete /F /TN \"{ScheduledTaskName}\"", out _);
    }

    private static int RunSchtasks(string arguments, out string stderr)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null)
            {
                stderr = "";
                return -1;
            }
            // schtasks 偶有卡顿，给足时间但不无限等待
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(); } catch { /* ignore */ }
            }
            stderr = p.StandardError.ReadToEnd();
            return p.ExitCode;
        }
        catch
        {
            stderr = "";
            return -1;
        }
    }
}
