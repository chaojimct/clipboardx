using System.IO;
using System.Reflection;

namespace ClipboardManager;

/// <summary>
/// 统一管理应用的所有数据路径。
/// 安装模式：DataRoot = %LocalAppData%\ClipboardX
/// 便携模式：DataRoot = exe 同级 Data\
/// 必须在 App.OnStartup 最早期调用 <see cref="Initialize"/>。
/// </summary>
internal static class AppPaths
{
    private const string PortableSentinel = "ClipboardX.portable";

#if CLIPX_FULL
    private const string ProductDirName = "ClipboardX";
    public const string MutexName = "ClipboardX_F7A2E9B0";
#elif CLIPX_CLIPBOARD
    private const string ProductDirName = "ClipboardX-clipboard";
    public const string MutexName = "ClipboardX_Clipboard_A1B2C3D4";
#elif CLIPX_FILEJUMP
    private const string ProductDirName = "ClipboardX-filejump";
    public const string MutexName = "ClipboardX_FileJump_E5F6G7H8";
#else
    private const string ProductDirName = "ClipboardX";
    public const string MutexName = "ClipboardX_F7A2E9B0";
#endif

    private static string? _dataRoot;
    private static bool _isPortable;

    /// <summary>是否处于便携模式（exe 同级存在 ClipboardX.portable 文件）。</summary>
    public static bool IsPortable => _isPortable;

    /// <summary>所有配置、数据库、日志的根目录。</summary>
    public static string DataRoot => _dataRoot ?? throw new InvalidOperationException("AppPaths.Initialize() has not been called.");

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string CustomDialogsFile => Path.Combine(DataRoot, "custom_file_dialogs.json");
    public static string SqliteDbFile => Path.Combine(DataRoot, "clipboard_history.db");
    public static string ShellNavigateLogFile => Path.Combine(DataRoot, "shell_navigate.log");
    public static string ClipboardDiagnosticsLogFile => Path.Combine(DataRoot, "clipboard_diagnostics.log");
    public static string ExplorerQuickFindLogFile => Path.Combine(DataRoot, "explorer_quickfind.log");

    /// <summary>
    /// 旧版 settings.json 所在目录（%AppData%\ClipboardX），迁移用。
    /// </summary>
    public static string LegacyRoamingDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardX");

    /// <summary>
    /// 更早期的旧版目录（%AppData%\ClipboardManager），迁移用。
    /// </summary>
    public static string LegacyClipboardManagerDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipboardManager");

    /// <summary>获取 exe 所在目录。</summary>
    private static string GetExeDirectory()
    {
        var loc = typeof(AppPaths).Assembly.Location;
        if (!string.IsNullOrEmpty(loc))
        {
            var d = Path.GetDirectoryName(loc);
            if (!string.IsNullOrEmpty(d)) return d;
        }
        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// 在 App.OnStartup 最早期调用。检测便携模式，确定 DataRoot，执行旧路径迁移。
    /// </summary>
    public static void Initialize()
    {
        var exeDir = GetExeDirectory();
        var sentinel = Path.Combine(exeDir, PortableSentinel);

        if (File.Exists(sentinel))
        {
            _isPortable = true;
            _dataRoot = Path.Combine(exeDir, "Data");
        }
        else
        {
            _isPortable = false;
            _dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductDirName);
        }

        Directory.CreateDirectory(_dataRoot);

        if (!_isPortable)
            MigrateLegacyPaths();
    }

    /// <summary>
    /// 将旧版散布在 Roaming/Local 的文件迁移到统一 DataRoot。
    /// 迁移策略：仅在目标不存在时复制，写 migrated.flag 标记已完成。
    /// </summary>
    private static void MigrateLegacyPaths()
    {
        var flag = Path.Combine(DataRoot, "migrated.flag");
        if (File.Exists(flag)) return;

        try
        {
            // 从 Roaming\ClipboardX 迁移 settings.json 和 custom_file_dialogs.json
            TryCopyIfMissing(
                Path.Combine(LegacyRoamingDir, "settings.json"),
                SettingsFile);
            TryCopyIfMissing(
                Path.Combine(LegacyRoamingDir, "custom_file_dialogs.json"),
                CustomDialogsFile);

            // 从更老的 Roaming\ClipboardManager 迁移
            TryCopyIfMissing(
                Path.Combine(LegacyClipboardManagerDir, "settings.json"),
                SettingsFile);

            // Local\ClipboardX 下的文件已经在 DataRoot 中（因为安装模式 DataRoot = %LocalAppData%\ClipboardX），无需迁移

            File.WriteAllText(flag, $"Migrated at {DateTime.Now:O}");
        }
        catch
        {
            // 迁移失败不影响主流程
        }
    }

    private static void TryCopyIfMissing(string source, string dest)
    {
        try
        {
            if (File.Exists(source) && !File.Exists(dest))
            {
                var dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.Copy(source, dest, overwrite: false);
            }
        }
        catch
        {
            // 单文件迁移失败不阻断后续
        }
    }
}
