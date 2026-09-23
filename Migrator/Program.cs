using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClipboardX.Migrator;

/// <summary>
/// 迁移版 launcher 入口。
///
/// 这个 exe 会被老更新器原地覆盖到 <c>%LocalAppData%\Programs\ClipboardX\ClipboardX.exe</c>，
/// 随后由老版自己 <c>Start-Process</c> 拉起 —— 用户看到的就是本向导。
///
/// 支持的开关（给自机演练与排障用）：
///   --check         只打印现状（clipx 是否已装、老版是否在跑、自启项还在不在）后退出
///   --migrate       不弹窗，直接跑完整迁移流程（无交互，供脚本演练）
///   --yes           与 --migrate 同义（兼容习惯写法）
///   --demo-busy     以「迁移中」形态打开向导（进度条 + 日志可见），供截图自检
///   --demo-scale N  按 N% 的 DPI 缩放模拟排版（如 150），用于在 100% 屏上重现高分屏
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 必须先初始化 WinForms 运行时（含进程级 DPI 感知声明）。
        //
        // 放在所有分支之前：--check 是 headless 的、不建窗口，但它的 DPI 诊断要靠这个 ——
        // 不初始化的话进程停留在默认感知级别，Graphics.FromHwnd(0) 会把真实 DPI
        // 虚拟化成 96，高分屏用户报障时会给出「主屏 DPI 96（100%）」这种错误现场数据。
        // （实测：同一台机器的向导窗口跑在 200% 的屏上，--check 却报 100%。）
        ApplicationConfiguration.Initialize();

        var check = args.Any(a => a.Equals("--check", StringComparison.OrdinalIgnoreCase));
        var headless = args.Any(a =>
            a.Equals("--migrate", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--yes", StringComparison.OrdinalIgnoreCase));
        var demoBusy = args.Any(a => a.Equals("--demo-busy", StringComparison.OrdinalIgnoreCase));
        var demoScale = ReadDemoScale(args);

        if (check)
        {
            PrintStatus();
            return 0;
        }

        if (headless)
        {
            return RunHeadless();
        }

        Application.Run(new WizardForm { DemoBusy = demoBusy, SimulatedScalePercent = demoScale });
        return 0;
    }

    /// <summary>读 <c>--demo-scale N</c>；缺失或非法时返回 0（= 用真实显示器 DPI）。</summary>
    private static int ReadDemoScale(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].Equals("--demo-scale", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(args[i + 1], out var pct) && pct is >= 100 and <= 400) return pct;
        }
        return 0;
    }

    /// <summary>取某块显示器的有效 DPI（shcore 的 GetDpiForMonitor，MDT_EFFECTIVE_DPI）。</summary>
    private static uint EffectiveDpi(IntPtr monitor)
    {
        try
        {
            if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0) return dpiX;
        }
        catch (DllNotFoundException)
        {
            // Windows 8.1 之前没有 shcore，退回系统 DPI。
        }
        catch (EntryPointNotFoundException)
        {
        }
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return (uint)g.DpiX;
    }

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    /// <summary>只读现状打印（不修改任何状态）。</summary>
    private static void PrintStatus()
    {
        Console.WriteLine("=== ClipboardX 迁移版 launcher 现状 ===");
        Console.WriteLine($"launcher 版本      : {MigratePaths.MigratorVersion}");

        // 显示相关：高 DPI 下的显示问题全靠这几个数定位。
        // 逐显示器列出 —— 多屏不同缩放（例如笔记本 100% + 外接 200%）是这类问题的高发场景，
        // 只报主屏不足以判断向导会落在哪块屏上、按哪个缩放排版。
        Console.WriteLine("显示器（各屏实测 DPI，launcher 声明 PerMonitorV2，这里是真值）：");
        foreach (var screen in Screen.AllScreens)
        {
            var b = screen.Bounds;
            var monitor = MonitorFromPoint(
                new POINT { X = b.X + b.Width / 2, Y = b.Y + b.Height / 2 },
                MONITOR_DEFAULTTONEAREST);
            var dpi = EffectiveDpi(monitor);
            Console.WriteLine(
                $"  {b.Width}x{b.Height} @({b.X},{b.Y})  DPI={dpi}（{dpi / 96.0 * 100:F0}%）" +
                (screen.Primary ? "  主屏" : ""));
        }
        Console.WriteLine($"自身目录（老版）   : {MigratePaths.SelfDir}");
        Console.WriteLine($"老版数据目录       : {MigratePaths.LegacyDataDir}" +
                          (Directory.Exists(MigratePaths.LegacyDataDir) ? "（存在）" : "（不存在）"));
        Console.WriteLine($"老版是否在运行     : {LegacyCleanup.IsLegacyRunning()}");
        Console.WriteLine($"clipx 安装目录     : {MigratePaths.ClipxInstallDir}");
        Console.WriteLine($"clipx 是否已安装   : {ClipxInstaller.IsClipxInstalled()}");

        var run = new List<string>();
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(MigratePaths.RunSubKey))
        {
            if (key is not null)
            {
                foreach (var n in MigratePaths.RunValueNames)
                {
                    if (key.GetValue(n) is not null) run.Add(n);
                }
            }
        }
        Console.WriteLine($"剩余 Run 值        : [{(run.Count == 0 ? "" : string.Join(", ", run))}]");

        var tasks = new List<string>();
        foreach (var t in MigratePaths.ScheduledTasks)
        {
            var psi = new ProcessStartInfo("schtasks")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/Query");
            psi.ArgumentList.Add("/TN");
            psi.ArgumentList.Add(t);
            using var p = Process.Start(psi);
            p?.WaitForExit();
            if (p?.ExitCode == 0) tasks.Add(t);
        }
        Console.WriteLine($"剩余计划任务       : [{(tasks.Count == 0 ? "" : string.Join(", ", tasks))}]");
    }

    /// <summary>无交互跑完整迁移（供脚本演练；真实用户走向导）。</summary>
    private static int RunHeadless()
    {
        Console.WriteLine("[migrate] 开始（无交互模式）");

        if (!ClipxInstaller.IsClipxInstalled())
        {
            Console.WriteLine("[migrate] 查询 clipx 安装包…");
            var asset = ClipxInstaller.FindSetupAsync().GetAwaiter().GetResult();
            if (asset is null)
            {
                Console.WriteLine("[migrate] × 未找到安装包");
                return 2;
            }
            Console.WriteLine($"[migrate] 下载 {asset.Name}…");
            var progress = new Progress<(long Got, long? Total)>(p =>
            {
                if (p.Total is long t and > 0 && p.Got == t)
                    Console.WriteLine($"[migrate] 下载完成 {p.Got / 1048576.0:F1} MB");
            });
            var setup = ClipxInstaller.DownloadAsync(asset, progress).GetAwaiter().GetResult();
            Console.WriteLine($"[migrate] 静默安装 {setup}");
            var code = ClipxInstaller.RunSilentInstall(setup);
            Console.WriteLine($"[migrate] 安装器退出码 = {code}");
            if (!ClipxInstaller.IsClipxInstalled())
            {
                Console.WriteLine("[migrate] × 安装后仍未找到 clipx.exe");
                return 3;
            }
        }
        else
        {
            Console.WriteLine("[migrate] clipx 已安装，跳过");
        }

        var outcome = LegacyCleanup.DisableAutostart();
        Console.WriteLine($"[migrate] Run 值已删 = [{string.Join(", ", outcome.RunRemoved)}]");
        Console.WriteLine($"[migrate] 任务已删   = [{string.Join(", ", outcome.TasksRemoved)}]");
        Console.WriteLine($"[migrate] 任务失败   = [{string.Join(", ", outcome.TasksFailed)}]");
        foreach (var e in outcome.Errors) Console.WriteLine("[migrate] ! " + e);

        ClipxInstaller.LaunchClipx();
        Console.WriteLine("[migrate] 已启动 clipx");

        // 退出码语义（供脚本/CI 判成败）：
        //   0 = 干净完成（要么清干净了，要么本来就没自启项）
        //   4 = 自启项没清干净（多半是提权任务删不掉，需以管理员身份再跑一次）
        if (outcome.TasksFailed.Count > 0) return 4;
        return 0;
    }
}
