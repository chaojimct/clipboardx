using System.Diagnostics;
using System.IO;
using System.Security;
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

    /// <summary>
    /// 任务计划程序中用于管理员自启的任务名。
    /// DEBUG 构建加 _Dev 后缀：开发版与正式安装版各自独立注册自启任务，
    /// 避免 /F 强制覆盖把正式版的开机任务改指向开发版 exe。
    /// </summary>
    private const string ScheduledTaskName =
#if DEBUG
        "ClipboardX_AutoStart_Dev";
#else
        "ClipboardX_AutoStart";
#endif

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
    /// 通过 schtasks /Create /XML 注册一个“登录时触发、以最高权限运行”的任务。
    /// 比 HKCU Run + PowerShell RunAs 更可靠：登录会话阶段 UAC 无法稳定显示同意弹窗，
    /// RunAs 方案会被静默拦截；任务计划程序的 HighestLevel 自带提权、不弹 UAC。
    /// </summary>
    /// <remarks>
    /// 不用 <c>/TR "\"exe路径\""</c> 方式：schtasks 会把引号原样写进任务的 Command 字段，
    /// 任务计划程序运行带引号的 Command 时改由 cmd.exe 解释执行，登录时会闪现 conhost
    /// 控制台窗口。改用标准 XML（Command 为不带引号的纯路径）导入注册，行为可控。
    /// </remarks>
    private static void RegisterScheduledTaskForElevatedStartup(string exePath)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), $"clipboardx_task_{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildTaskXml(exePath), Encoding.Unicode);
            if (RunSchtasks($"/Create /F /TN \"{ScheduledTaskName}\" /XML \"{xmlPath}\"", out _) == 0)
                return;
        }
        catch
        {
            // 落盘失败则尝试回退方案
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* ignore */ }
        }

        // 回退：带引号 /TR（与 v1.9.7 相同——路径含空格也能启动，仅登录时闪一次控制台）。
        // 不能用不带引号的 /TR：空格路径会被任务计划程序拆断，注册出完全无法启动的任务。
        RunSchtasks(
            $"/Create /F /TN \"{ScheduledTaskName}\" /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL HIGHEST",
            out _);
    }

    /// <summary>生成最高权限登录自启任务的标准 XML（Command 不带引号，电池模式允许启动，无执行时限）。</summary>
    private static string BuildTaskXml(string exePath)
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var escapedPath = SecurityElement.Escape(exePath);
        var escapedUser = SecurityElement.Escape(user);

        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>ClipboardX logon auto start (elevated)</Description>
  </RegistrationInfo>
  <Principals>
    <Principal id=""Author"">
      <UserId>{escapedUser}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <DisallowDemandStart>false</DisallowDemandStart>
  </Settings>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{escapedUser}</UserId>
    </LogonTrigger>
  </Triggers>
  <Actions Context=""Author"">
    <Exec>
      <Command>{escapedPath}</Command>
    </Exec>
  </Actions>
</Task>";
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
