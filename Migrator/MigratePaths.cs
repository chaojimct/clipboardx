using System.IO;

namespace ClipboardX.Migrator;

/// <summary>
/// 迁移流程涉及的全部路径 / 注册表位置 / 常量（单一真源）。
///
/// 这些值都有出处，改之前请对照 docs/MIGRATION.md 的「事实依据」一节：
/// 老版的路径与自启方式在老仓库 Services/AppPaths.cs 与 StartupRegistration.cs。
/// </summary>
internal static class MigratePaths
{
    /// <summary>clipx 安装目录（Inno 安装器 DefaultDirName={localappdata}\clipx）。</summary>
    public static string ClipxInstallDir =>
        Path.Combine(LocalAppData, "clipx");

    /// <summary>clipx 主程序。</summary>
    public static string ClipxExe =>
        Path.Combine(ClipxInstallDir, "clipx.exe");

    /// <summary>
    /// 本 launcher 所在的真实目录 = 老版安装目录（老更新器原地覆盖的就是它）。
    ///
    /// ⚠️ 必须用 <see cref="Environment.ProcessPath"/>，**不能用 AppContext.BaseDirectory**：
    /// 本 exe 以 PublishSingleFile + IncludeAllContentForSelfExtract 发布，
    /// BaseDirectory 会指向 <c>%Temp%\.net\ClipboardX\&lt;hash&gt;\</c> 这个自解压临时目录，
    /// 拿它当安装目录会做错事（实测踩过）。
    /// </summary>
    public static string SelfDir
    {
        get
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                return Path.GetDirectoryName(exe)!;
            }
            // 兜底（正常发布形态下不会走到）
            return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        }
    }

    /// <summary>老版数据根（历史库与 settings.json，<b>本工具绝不删除</b>）。</summary>
    public static string LegacyDataDir =>
        Path.Combine(LocalAppData, "ClipboardX");

    public static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>HKCU Run 子键。</summary>
    public const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>老版 Run 值名（现行）与更早版本的值名。</summary>
    public static readonly string[] RunValueNames = ["ClipboardX", "ClipboardManager"];

    /// <summary>老版管理员模式自启计划任务（含 Dev 变体）。</summary>
    public static readonly string[] ScheduledTasks =
        ["ClipboardX_AutoStart", "ClipboardX_AutoStart_Dev"];

    /// <summary>老版单实例互斥体（FULL flavor）。</summary>
    public const string LegacyMutex = "ClipboardX_F7A2E9B0";

    /// <summary>clipx 安装来源仓库。</summary>
    public const string ClipxRepo = "chaojimct/clipx";

    /// <summary>
    /// 本迁移版自身的版本（显示用）。
    ///
    /// 单一真源 = <c>Migrator.csproj</c> 的 <c>&lt;Version&gt;</c>（CI 用 <c>-p:Version=</c> 覆盖同一个值），
    /// 从程序集读回来，避免像 v1.9.10 那样版本号写死在两处、改一处漏一处。
    /// </summary>
    public static string MigratorVersion { get; } =
        typeof(MigratePaths).Assembly.GetName().Version is { } v ? v.ToString(3) : "0.0.0";
}
