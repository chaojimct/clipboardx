using System.IO;
using System.Text.Json;

namespace ClipboardManager;

public class QuickPasteEntry
{
    public string Phrase { get; set; } = "";
    public string Content { get; set; } = "";
}

public class AppSettings
{
    public int MaxItems { get; set; } = 2000;

    /// <summary>图片条目专项上限。图片占用远大于文本/文件，独立限制避免内存失控。默认 150 张。</summary>
    public int MaxImageItems { get; set; } = 150;

    /// <summary>单张图片最大字节数（PNG 编码后）。超过则跳过不入库，避免单张超大图片吃掉大量内存。默认 15MB。</summary>
    public long MaxImageSizeBytes { get; set; } = 15 * 1024 * 1024;
    public uint HotkeyModifiers { get; set; } = Win32.MOD_CONTROL;
    public uint HotkeyKey { get; set; } = Win32.VK_OEM_3;

    /// <summary>「打开/保存」对话框中跳转到文件夹的全局快捷键（默认 Ctrl+G）。</summary>
    public uint FileJumpHotkeyModifiers { get; set; } = Win32.MOD_CONTROL;
    public uint FileJumpHotkeyKey { get; set; } = Win32.VK_G;

    /// <summary>多候选时跳转列表弹出前的延时（毫秒）；0 表示立即弹出。自动弹出时仍会合并极短防抖（约一帧），见 PopupWindow。</summary>
    public int FileJumpPickerShowDelayMs { get; set; } = 0;

    /// <summary>Mouse：跳转列表跟随鼠标附近；Dialog：紧贴文件对话框并随窗口移动。</summary>
    public string FileJumpPickerFollowMode { get; set; } = FileJumpPickerFollowModes.Dialog;

    /// <summary>
    /// 历史字段：是否弹出跳转列表。当前版本与 <see cref="FileJumpPickerOpenWhenDialogForeground"/> 同义，
    /// 保存时强制与之相等；读取旧配置若不一致则取 OR。保留以兼容旧 settings.json。
    /// </summary>
    public bool FileJumpPickerAutoPopup { get; set; } = true;

    /// <summary>
    /// 「对话框打开时自动弹出列表」：检测到文件对话框成为前台时（含焦点首次进框兜底），自动采集候选并弹出跳转列表。
    /// 开启时跳转列表（含 Ctrl+G 弹出）始终贴对话框；关闭时按 <see cref="FileJumpPickerFollowMode"/>。
    /// 同一对话框 root 仅自动弹一次；手动 Ctrl+G 不受影响。与 <see cref="FileJumpAutoOnFirstClick"/> 同时开
    /// 时为 A 方案：先直跳最佳路径再弹列表，用户可在列表内再换。
    /// </summary>
    public bool FileJumpPickerOpenWhenDialogForeground { get; set; } = true;

    /// <summary>
    /// 从资源管理器/TC 切回已打开的文件对话框时，重新采集候选列表；
    /// 若最新外部文件夹与对话框当前路径不同，则自动跳转（可关闭）。
    /// 与 <see cref="FileJumpPickerOpenWhenDialogForeground"/> 的区别：后者只在「首次」到前台时触发一次，
    /// 此选项在「每次」切回时重新采集并比较。
    /// </summary>
    public bool FileJumpAutoSyncOnReturn { get; set; } = true;

    /// <summary>
    /// 系统公共文件对话框内跳转时，是否尝试将 Shell 导航 DLL 注入宿主进程（IShellBrowser::BrowseObject）。
    /// 关闭后仅走地址栏/键入模拟，兼容部分杀软或宿主拦截注入的环境；WPS 等自定义对话框始终不注入。
    /// </summary>
    public bool EnableShellNavigateInject { get; set; } = true;

    /// <summary>
    /// 「自动跳转到最佳路径」：对话框成为前台时直接跳到候选首条（不依赖快捷键、不依赖点击）。
    /// 同一对话框 root 仅成功一次。配合内部低级鼠标钩兜底：部分宿主（如微信）不发前台事件时，
    /// 会在对话框内首次左键时触发等价直跳；钩子仅在本开关开启时武装。
    /// 与 <see cref="FileJumpPickerOpenWhenDialogForeground"/> 同时开时为 A 方案：先直跳再弹列表。
    /// 字段名沿用历史 (First Click)，语义已升级为"自动跳转"，保留名以兼容旧 settings.json。
    /// </summary>
    public bool FileJumpAutoOnFirstClick { get; set; } = false;

    public string Theme { get; set; } = "System";
    public string PopupPosition { get; set; } = "Caret";
    public double PopupOpacity { get; set; } = 1.0;
    public bool HideOnSameAppClick { get; set; } = true;

    /// <summary>开启时：单击列表仅选中、不粘贴，双击才粘贴；关闭时：单击即粘贴（默认）。</summary>
    public bool PasteRequiresDoubleClick { get; set; } = false;
    /// <summary>登录 Windows 时自动启动本程序（默认开启）。</summary>
    public bool RunAtStartup { get; set; } = true;

    /// <summary>为 true 时，每次手动启动或开机自启均请求 UAC 以管理员身份运行（用户取消则退回普通权限）。</summary>
    public bool RunAsAdministrator { get; set; } = true;

    /// <summary>向目标窗口模拟粘贴：<see cref="PasteSimulationModes.CtrlV"/>（Ctrl+V）或 <see cref="PasteSimulationModes.ShiftInsert"/>。</summary>
    public string PasteSimulationMode { get; set; } = PasteSimulationModes.CtrlV;

    /// <summary>启动后静默访问 GitHub Releases，若有新版本则在托盘气泡提示（不弹阻断窗）。</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>用 ClipboardX 替换系统 Win+V 快捷键（拦截系统剪贴板历史，触发 ClipboardX 弹窗）。</summary>
    public bool ReplaceSystemWinV { get; set; } = false;

    /// <summary>退出时自动清空剪贴板历史记录（保留快捷短语）。</summary>
    public bool ClearHistoryOnExit { get; set; } = false;

    /// <summary>复制图片后使用 Windows 内置 OCR 识别文字，便于搜索与列表预览。</summary>
    public bool ImageOcrEnabled { get; set; } = true;

    /// <summary>启动检测已提示过的发行 tag（如 v1.2.0），避免同一版本重复气泡；升级或已最新时会清空。</summary>
    public string? LastStartupUpdateNotifiedTag { get; set; }
    public int PreviewMaxLines { get; set; } = 2;

    /// <summary>剪贴板弹窗宽度（DIP），默认与内置 XAML 一致。</summary>
    public double PopupPanelWidth { get; set; } = 420;

    /// <summary>剪贴板弹窗最大高度（DIP，列表区域随内容增高直至该上限）。</summary>
    public double PopupPanelMaxHeight { get; set; } = 560;

    /// <summary>剪贴板弹窗实际高度（DIP），0 表示未手动调整过，使用 SizeToContent。</summary>
    public double PopupPanelHeight { get; set; }

    /// <summary>文件跳转弹窗宽度（DIP），默认 500。</summary>
    public double FileJumpPickerWidth { get; set; } = 500;

    /// <summary>文件跳转弹窗最大高度（DIP），默认 620。</summary>
    public double FileJumpPickerMaxHeight { get; set; } = 620;

    /// <summary>文件跳转弹窗实际高度（DIP），0 表示未手动调整过。</summary>
    public double FileJumpPickerHeight { get; set; }

    /// <summary>资源管理器快速筛选弹窗宽度（DIP），默认 540。</summary>
    public double ExplorerQuickFindWidth { get; set; } = 540;

    /// <summary>资源管理器快速筛选弹窗最大高度（DIP），默认 520。</summary>
    public double ExplorerQuickFindMaxHeight { get; set; } = 520;

    /// <summary>资源管理器快速筛选弹窗实际高度（DIP），0 表示未手动调整过。</summary>
    public double ExplorerQuickFindHeight { get; set; }

    /// <summary>列表每次翻过的条目数（PgUp/Dn、←→ 及翻页快捷键共用，原固定为 8）。</summary>
    public int PopupPageItems { get; set; } = 8;

    /// <summary>列表向上翻页组合键（须含修饰键，默认 Ctrl+-）。</summary>
    public uint PanelPageScrollUpModifiers { get; set; } = Win32.MOD_CONTROL;

    public uint PanelPageScrollUpKey { get; set; } = 0xBD;

    /// <summary>列表向下翻页组合键（须含修饰键，默认 Ctrl+=）。</summary>
    public uint PanelPageScrollDownModifiers { get; set; } = Win32.MOD_CONTROL;

    public uint PanelPageScrollDownKey { get; set; } = 0xBB;

    public string PanelModifierKey { get; set; } = "Ctrl";

    /// <summary>批量粘贴：Off / Fifo / Lifo（与 <see cref="BatchPasteQueueMode"/> 枚举名一致）。</summary>
    public string BatchPasteMode { get; set; } = nameof(BatchPasteQueueMode.Off);

    /// <summary>面板未打开时也可用的「批量模式」循环切换快捷键（默认 Alt+/）。须与其它全局热键不同。</summary>
    public uint BatchModeCycleHotkeyModifiers { get; set; } = Win32.MOD_ALT;

    public uint BatchModeCycleHotkeyKey { get; set; } = Win32.VK_OEM_2;

    /// <summary>多选且条目全部为文本时，是否拼成一段一次写入剪贴板并粘贴（关则逐条粘贴，便于多步撤销）。</summary>
    public bool BatchPasteMergeText { get; set; } = true;

    /// <summary>
    /// FIFO / LIFO 下队列已贴完后，在<strong>下一次</strong>他处 Ctrl+V / Shift+Insert 时自动切回普通模式（避免长期停留在队列模式）。
    /// </summary>
    public bool BatchQueueAutoSwitchToNormalAfterQueueDone { get; set; } = true;

    public List<QuickPasteEntry> QuickPastes { get; set; } = new();

    /// <summary>Ctrl+G 跳转列表顶部展示的收藏目录（Phrase 为关键词/别名，供检索）。</summary>
    public List<FolderFavoriteEntry> FolderFavorites { get; set; } = new();

    /// <summary>最近一次在「打开/保存」对话框中记录到的文件夹（与 <see cref="RecentFileDialogFolders"/> 首项同步），供兼容旧逻辑。</summary>
    public string LastFileDialogFolder { get; set; } = "";

    /// <summary>最近通过「确定/打开/保存」等确认操作记录的路径，最多 <see cref="RecentFolderMaxCount"/> 条（新的在前）。</summary>
    public List<string> RecentFileDialogFolders { get; set; } = new();

    /// <summary>常用路径最大显示数量（默认 5）。</summary>
    public int RecentFolderMaxCount { get; set; } = 5;

    /// <summary>自动加入常用路径的最小确认次数阈值（默认 1）。</summary>
    public int RecentFolderAutoAddMinCount { get; set; } = 1;

    /// <summary>排除应用列表：前台属于这些进程时不触发 ClipboardX 全局快捷键（进程名不含 .exe 后缀，不区分大小写）。</summary>
    public List<string> ExclusionApps { get; set; } = new();

    /// <summary>面板打开时将指定按键交给 AutoHotkey 等外部低级钩子（见设置「实验性功能」）。</summary>
    public bool KeyPassthroughEnabled { get; set; }

    /// <summary>按住所列修饰键时穿透非面板必要键（<see cref="Win32.MOD_*"/> 与 <see cref="Win32.MOD_CAPS"/>）。</summary>
    public uint KeyPassthroughModifierMask { get; set; } = Win32.MOD_CAPS;

    /// <summary>穿透时仍由面板处理 Esc、方向键、Enter 等导航键。</summary>
    public bool KeyPassthroughKeepPanelKeys { get; set; } = true;

    /// <summary>精确穿透组合键；Key 为 0 表示该修饰键下任意键穿透。</summary>
    public List<KeyPassthroughRule> KeyPassthroughRules { get; set; } = new();

    /// <summary>各文件夹的确认次数统计（key=归一化路径，value=累计次数）。未达阈值时仅计数，达阈值后才写入 <see cref="RecentFileDialogFolders"/>。</summary>
    public Dictionary<string, int> FolderConfirmCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>在公共对话框内确认当前目录时调用：递增该路径计数，达到阈值后才写入常用列表。</summary>
    public void RecordFolderConfirmation(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        string normalized;
        try
        {
            normalized = Path.GetFullPath(folder.Trim());
        }
        catch
        {
            return;
        }

        try
        {
            if (!Directory.Exists(normalized)) return;
        }
        catch
        {
            return;
        }

        if (IsApplicationInstallDirectory(normalized)) return;

        FolderConfirmCounts ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        FolderConfirmCounts.TryGetValue(normalized, out var count);
        count++;
        FolderConfirmCounts[normalized] = count;

        if (count < RecentFolderAutoAddMinCount)
        {
            Save();
            return;
        }

        PushToRecentFileDialogFolders(normalized);
    }

    /// <summary>直接将路径写入常用列表（跳过阈值检查），供手动添加或迁移等场景使用。</summary>
    public void PushRecentFileDialogFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        string normalized;
        try
        {
            normalized = Path.GetFullPath(folder.Trim());
        }
        catch
        {
            return;
        }

        try
        {
            if (!Directory.Exists(normalized)) return;
        }
        catch
        {
            return;
        }

        if (IsApplicationInstallDirectory(normalized)) return;

        PushToRecentFileDialogFolders(normalized);
    }

    private void PushToRecentFileDialogFolders(string normalized)
    {
        RecentFileDialogFolders ??= new List<string>();
        RecentFileDialogFolders.RemoveAll(p =>
        {
            if (string.IsNullOrWhiteSpace(p)) return true;
            try
            {
                return string.Equals(Path.GetFullPath(p.Trim()), normalized, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        });
        RecentFileDialogFolders.Insert(0, normalized);
        while (RecentFileDialogFolders.Count > RecentFolderMaxCount)
            RecentFileDialogFolders.RemoveAt(RecentFileDialogFolders.Count - 1);

        LastFileDialogFolder = RecentFileDialogFolders.Count > 0 ? RecentFileDialogFolders[0] : "";
        Save();
    }

    private static bool IsApplicationInstallDirectory(string path)
    {
        try
        {
            var programDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "ClipboardX");
            var programDirFull = Path.GetFullPath(programDir);
            var pathFull = Path.GetFullPath(path);
            return string.Equals(pathFull, programDirFull, StringComparison.OrdinalIgnoreCase) ||
                   pathFull.StartsWith(programDirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从常用路径列表中移除指定路径并保存。</summary>
    public void RemoveRecentFileDialogFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        string normalized;
        try
        {
            normalized = Path.GetFullPath(folder.Trim());
        }
        catch
        {
            return;
        }

        RecentFileDialogFolders ??= new List<string>();
        RecentFileDialogFolders.RemoveAll(p =>
        {
            if (string.IsNullOrWhiteSpace(p)) return true;
            try
            {
                return string.Equals(Path.GetFullPath(p.Trim()), normalized, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        });

        LastFileDialogFolder = RecentFileDialogFolders.Count > 0 ? RecentFileDialogFolders[0] : "";
        Save();
    }

    /// <summary>资源管理器非地址栏焦点时直接键入：用 Everything 限定当前文件夹检索并弹出结果（需本机运行 Everything）。默认开启，见设置「实验性功能」。</summary>
    public bool ExplorerEverythingQuickFindEnabled { get; set; } = true;

    /// <summary>Everything IPC 单次最大返回条数。</summary>
    public int ExplorerEverythingQuickFindMaxResults { get; set; } = 150;

    /// <summary>文件对话框「跳转到文件夹」列表内检索时，用 Everything 补充匹配文件夹（需本机运行 Everything）。</summary>
    public bool FileJumpPickerEverythingFolderSearch { get; set; } = true;

    /// <summary>已保留字段：配置仍可反序列化；当前版本始终走 Everything，保存设置时会写回 false。</summary>
    public bool UseFindXSearch { get; set; } = false;

    /// <summary>资源管理器 Everything 筛选后选中操作模式："Explorer"（默认，在资源管理器内就地导航并选中文件）或 "DirectOpen"（将前台文件对话框导航到目标路径）。</summary>
    public string ExplorerQuickFindOpenMode { get; set; } = "Explorer";

    public string HotkeyDisplayName => FormatHotkey(HotkeyModifiers, HotkeyKey);

    public string FileJumpHotkeyDisplayName => FormatHotkey(FileJumpHotkeyModifiers, FileJumpHotkeyKey);

    public string BatchModeCycleHotkeyDisplayName => FormatHotkey(BatchModeCycleHotkeyModifiers, BatchModeCycleHotkeyKey);

    /// <summary>用于设置中单键展示（无修饰键）。</summary>
    public static string FormatSingleVk(uint vk) => VkToName(vk);

    public static string FormatHotkey(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & Win32.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & Win32.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & Win32.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & Win32.MOD_WIN) != 0) parts.Add("Win");
        if ((modifiers & Win32.MOD_CAPS) != 0) parts.Add("CapsLock");
        parts.Add(VkToName(key));
        return string.Join("+", parts);
    }

    private static string VkToName(uint vk) => vk switch
    {
        0xC0 => "`", 0xBD => "-", 0xBB => "=", 0xDB => "[", 0xDD => "]",
        0xDC => "\\", 0xBA => ";", 0xDE => "'", 0xBC => ",", 0xBE => ".",
        0xBF => "/", 0x20 => "Space",
        0x6B => "Num +", 0x6D => "Num -",
        >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        _ => $"0x{vk:X2}"
    };

    public static void NormalizePopupPanelSettings(AppSettings s)
    {
        if (s.PopupPanelWidth < 280 || s.PopupPanelWidth > 1200 || double.IsNaN(s.PopupPanelWidth))
            s.PopupPanelWidth = 420;
        if (s.PopupPanelMaxHeight < 200 || s.PopupPanelMaxHeight > 900 || double.IsNaN(s.PopupPanelMaxHeight))
            s.PopupPanelMaxHeight = 560;
        if (s.PopupPanelHeight < 0 || double.IsNaN(s.PopupPanelHeight))
            s.PopupPanelHeight = 0;
        if (s.FileJumpPickerWidth < 280 || s.FileJumpPickerWidth > 1200 || double.IsNaN(s.FileJumpPickerWidth))
            s.FileJumpPickerWidth = 520;
        if (s.FileJumpPickerMaxHeight < 200 || s.FileJumpPickerMaxHeight > 900 || double.IsNaN(s.FileJumpPickerMaxHeight))
            s.FileJumpPickerMaxHeight = 560;
        if (s.FileJumpPickerHeight < 0 || double.IsNaN(s.FileJumpPickerHeight))
            s.FileJumpPickerHeight = 0;
        if (s.ExplorerQuickFindWidth < 280 || s.ExplorerQuickFindWidth > 1200 || double.IsNaN(s.ExplorerQuickFindWidth))
            s.ExplorerQuickFindWidth = 540;
        if (s.ExplorerQuickFindMaxHeight < 200 || s.ExplorerQuickFindMaxHeight > 900 || double.IsNaN(s.ExplorerQuickFindMaxHeight))
            s.ExplorerQuickFindMaxHeight = 520;
        if (s.ExplorerQuickFindHeight < 0 || double.IsNaN(s.ExplorerQuickFindHeight))
            s.ExplorerQuickFindHeight = 0;
        if (s.PopupPageItems < 1 || s.PopupPageItems > 50)
            s.PopupPageItems = 8;
        if (s.PanelPageScrollUpModifiers == 0)
            s.PanelPageScrollUpModifiers = Win32.MOD_CONTROL;
        if (s.PanelPageScrollDownModifiers == 0)
            s.PanelPageScrollDownModifiers = Win32.MOD_CONTROL;
        if (s.PanelPageScrollUpKey == 0)
            s.PanelPageScrollUpKey = 0xBD;
        if (s.PanelPageScrollDownKey == 0)
            s.PanelPageScrollDownKey = 0xBB;
        if (s.PanelPageScrollUpModifiers == s.PanelPageScrollDownModifiers
            && s.PanelPageScrollUpKey == s.PanelPageScrollDownKey)
        {
            s.PanelPageScrollUpKey = 0xBD;
            s.PanelPageScrollDownKey = 0xBB;
        }
    }

    private static readonly string LegacySettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClipboardManager");

    private static string SettingsDir => Path.GetDirectoryName(AppPaths.SettingsFile)!;

    private static string SettingsFile => AppPaths.SettingsFile;
    private static readonly string LegacySettingsFile = Path.Combine(LegacySettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                using (var doc = JsonDocument.Parse(json))
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
                    // 旧版 settings.json 无此字段时 Json 反序列化为 false，产品默认应为开启
                    if (!doc.RootElement.TryGetProperty(nameof(RunAtStartup), out _))
                        settings.RunAtStartup = true;
                    if (!doc.RootElement.TryGetProperty(nameof(RunAsAdministrator), out _))
                        settings.RunAsAdministrator = true;
                    if (!doc.RootElement.TryGetProperty(nameof(EnableShellNavigateInject), out _))
                        settings.EnableShellNavigateInject = true;
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpAutoOnFirstClick), out _))
                        settings.FileJumpAutoOnFirstClick = false;
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpPickerFollowMode), out _))
                    {
                        if (doc.RootElement.TryGetProperty("FileJumpPickerDockBesideDialog", out var dockEl)
                            && dockEl.ValueKind == JsonValueKind.False)
                            settings.FileJumpPickerFollowMode = FileJumpPickerFollowModes.Mouse;
                        else
                            settings.FileJumpPickerFollowMode = FileJumpPickerFollowModes.Dialog;
                    }
                    settings.FileJumpPickerFollowMode = FileJumpPickerFollowModes.Normalize(settings.FileJumpPickerFollowMode);
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpPickerAutoPopup), out _))
                        settings.FileJumpPickerAutoPopup = true;
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpPickerOpenWhenDialogForeground), out _))
                        settings.FileJumpPickerOpenWhenDialogForeground = true;
                    NormalizeFileJumpDialogAutoOpen(settings);
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpAutoSyncOnReturn), out _))
                        settings.FileJumpAutoSyncOnReturn = true;
                    if (!doc.RootElement.TryGetProperty(nameof(CheckUpdatesOnStartup), out _))
                        settings.CheckUpdatesOnStartup = true;
                    if (!doc.RootElement.TryGetProperty(nameof(BatchPasteMergeText), out _))
                        settings.BatchPasteMergeText = true;
                    if (!doc.RootElement.TryGetProperty(nameof(BatchQueueAutoSwitchToNormalAfterQueueDone), out _))
                    {
                        if (doc.RootElement.TryGetProperty("FifoAutoSwitchToNormalAfterQueueDone", out var legacyFifo))
                            settings.BatchQueueAutoSwitchToNormalAfterQueueDone = legacyFifo.ValueKind == JsonValueKind.True;
                        else
                            settings.BatchQueueAutoSwitchToNormalAfterQueueDone = true;
                    }
                    if (!doc.RootElement.TryGetProperty(nameof(ExplorerEverythingQuickFindMaxResults), out _))
                        settings.ExplorerEverythingQuickFindMaxResults = 150;
                    if (!doc.RootElement.TryGetProperty(nameof(ExplorerEverythingQuickFindEnabled), out _))
                        settings.ExplorerEverythingQuickFindEnabled = true;
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpPickerEverythingFolderSearch), out _))
                        settings.FileJumpPickerEverythingFolderSearch = true;
                    if (!doc.RootElement.TryGetProperty(nameof(RecentFolderMaxCount), out _))
                        settings.RecentFolderMaxCount = 5;
                    if (!doc.RootElement.TryGetProperty(nameof(RecentFolderAutoAddMinCount), out _))
                        settings.RecentFolderAutoAddMinCount = 1;
                    if (settings.FolderFavorites == null)
                        settings.FolderFavorites = new List<FolderFavoriteEntry>();
                    if (settings.FolderConfirmCounts == null)
                        settings.FolderConfirmCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    MigrateRecentFileDialogFolders(settings);
                    settings.PasteSimulationMode = PasteSimulationModes.Normalize(settings.PasteSimulationMode);
                    NormalizePopupPanelSettings(settings);
                    return settings;
                }
            }

            if (File.Exists(LegacySettingsFile))
            {
                var json = File.ReadAllText(LegacySettingsFile);
                using (var doc = JsonDocument.Parse(json))
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
                    if (!doc.RootElement.TryGetProperty(nameof(RunAtStartup), out _))
                        settings.RunAtStartup = true;
                    if (!doc.RootElement.TryGetProperty(nameof(RunAsAdministrator), out _))
                        settings.RunAsAdministrator = true;
                    if (!doc.RootElement.TryGetProperty(nameof(EnableShellNavigateInject), out _))
                        settings.EnableShellNavigateInject = true;
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpAutoOnFirstClick), out _))
                        settings.FileJumpAutoOnFirstClick = false;
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpPickerFollowMode), out _))
                    {
                        if (doc.RootElement.TryGetProperty("FileJumpPickerDockBesideDialog", out var dockEl)
                            && dockEl.ValueKind == JsonValueKind.False)
                            settings.FileJumpPickerFollowMode = FileJumpPickerFollowModes.Mouse;
                        else
                            settings.FileJumpPickerFollowMode = FileJumpPickerFollowModes.Dialog;
                    }
                    settings.FileJumpPickerFollowMode = FileJumpPickerFollowModes.Normalize(settings.FileJumpPickerFollowMode);
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpPickerAutoPopup), out _))
                        settings.FileJumpPickerAutoPopup = true;
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpPickerOpenWhenDialogForeground), out _))
                        settings.FileJumpPickerOpenWhenDialogForeground = true;
                    NormalizeFileJumpDialogAutoOpen(settings);
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpAutoSyncOnReturn), out _))
                        settings.FileJumpAutoSyncOnReturn = true;
                    if (!doc.RootElement.TryGetProperty(nameof(CheckUpdatesOnStartup), out _))
                        settings.CheckUpdatesOnStartup = true;
                    if (!doc.RootElement.TryGetProperty(nameof(BatchPasteMergeText), out _))
                        settings.BatchPasteMergeText = true;
                    if (!doc.RootElement.TryGetProperty(nameof(BatchQueueAutoSwitchToNormalAfterQueueDone), out _))
                    {
                        if (doc.RootElement.TryGetProperty("FifoAutoSwitchToNormalAfterQueueDone", out var legacyFifo))
                            settings.BatchQueueAutoSwitchToNormalAfterQueueDone = legacyFifo.ValueKind == JsonValueKind.True;
                        else
                            settings.BatchQueueAutoSwitchToNormalAfterQueueDone = true;
                    }
                    if (!doc.RootElement.TryGetProperty(nameof(ExplorerEverythingQuickFindMaxResults), out _))
                        settings.ExplorerEverythingQuickFindMaxResults = 150;
                    if (!doc.RootElement.TryGetProperty(nameof(ExplorerEverythingQuickFindEnabled), out _))
                        settings.ExplorerEverythingQuickFindEnabled = true;
                    if (!doc.RootElement.TryGetProperty(nameof(FileJumpPickerEverythingFolderSearch), out _))
                        settings.FileJumpPickerEverythingFolderSearch = true;
                    if (!doc.RootElement.TryGetProperty(nameof(RecentFolderMaxCount), out _))
                        settings.RecentFolderMaxCount = 5;
                    if (!doc.RootElement.TryGetProperty(nameof(RecentFolderAutoAddMinCount), out _))
                        settings.RecentFolderAutoAddMinCount = 1;
                    if (settings.FolderFavorites == null)
                        settings.FolderFavorites = new List<FolderFavoriteEntry>();
                    if (settings.FolderConfirmCounts == null)
                        settings.FolderConfirmCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    MigrateRecentFileDialogFolders(settings);
                    settings.PasteSimulationMode = PasteSimulationModes.Normalize(settings.PasteSimulationMode);
                    NormalizePopupPanelSettings(settings);
                    settings.SaveSync();
                    return settings;
                }
            }
        }
        catch { }
        return new();
    }

    /// <summary>
    /// 将历史字段 <see cref="FileJumpPickerAutoPopup"/> 与 <see cref="FileJumpPickerOpenWhenDialogForeground"/> 拉齐：
    /// 当前版本两者同义（均代表"对话框打开时自动弹出列表"），任一为 true 即两者都置 true，避免历史 JSON 留下不一致。
    /// </summary>
    private static void NormalizeFileJumpDialogAutoOpen(AppSettings settings)
    {
        var openList = settings.FileJumpPickerOpenWhenDialogForeground || settings.FileJumpPickerAutoPopup;
        settings.FileJumpPickerOpenWhenDialogForeground = openList;
        settings.FileJumpPickerAutoPopup = openList;
    }

    private static void MigrateRecentFileDialogFolders(AppSettings settings)
    {
        settings.RecentFileDialogFolders ??= new List<string>();
        settings.ExclusionApps ??= new List<string>();
        settings.KeyPassthroughRules ??= new List<KeyPassthroughRule>();
        settings.RecentFileDialogFolders.RemoveAll(string.IsNullOrWhiteSpace);
        if (settings.RecentFileDialogFolders.Count == 0 && !string.IsNullOrWhiteSpace(settings.LastFileDialogFolder))
        {
            try
            {
                var n = Path.GetFullPath(settings.LastFileDialogFolder.Trim());
                if (Directory.Exists(n))
                    settings.RecentFileDialogFolders.Add(n);
            }
            catch { /* ignore */ }
        }

        if (settings.RecentFileDialogFolders.Count > 0)
            settings.LastFileDialogFolder = settings.RecentFileDialogFolders[0].Trim();
    }

    private static readonly object s_saveLock = new();
    private static CancellationTokenSource? s_pendingSaveCts;
    private static AppSettings? s_pendingSaveSnapshot;

    public void Save()
    {
        try
        {
            var snapshot = ShallowCopy();
            lock (s_saveLock)
            {
                s_pendingSaveSnapshot = snapshot;
                s_pendingSaveCts?.Cancel();
                s_pendingSaveCts = new CancellationTokenSource();
                var token = s_pendingSaveCts.Token;
                var settingsDir = SettingsDir;
                var settingsFile = SettingsFile;
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(80, token).ConfigureAwait(false);
                        AppSettings toWrite;
                        lock (s_saveLock)
                        {
                            if (token.IsCancellationRequested) return;
                            toWrite = s_pendingSaveSnapshot;
                            s_pendingSaveSnapshot = null;
                        }
                        if (toWrite != null)
                        {
                            Directory.CreateDirectory(settingsDir);
                            var json = JsonSerializer.Serialize(toWrite, new JsonSerializerOptions { WriteIndented = true });
                            File.WriteAllText(settingsFile, json);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { }
                });
            }
        }
        catch { }
    }

    public void SaveSync()
    {
        try
        {
            lock (s_saveLock)
            {
                s_pendingSaveCts?.Cancel();
                s_pendingSaveSnapshot = null;
            }
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch { }
    }

    public static void FlushPendingSave()
    {
        try
        {
            AppSettings? toWrite;
            lock (s_saveLock)
            {
                s_pendingSaveCts?.Cancel();
                toWrite = s_pendingSaveSnapshot;
                s_pendingSaveSnapshot = null;
            }
            if (toWrite != null)
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(toWrite, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFile, json);
            }
        }
        catch { }
    }

    public AppSettings ShallowCopy() => new()
    {
        MaxItems = MaxItems,
        MaxImageItems = MaxImageItems,
        MaxImageSizeBytes = MaxImageSizeBytes,
        HotkeyModifiers = HotkeyModifiers,
        HotkeyKey = HotkeyKey,
        FileJumpHotkeyModifiers = FileJumpHotkeyModifiers,
        FileJumpHotkeyKey = FileJumpHotkeyKey,
        FileJumpPickerShowDelayMs = FileJumpPickerShowDelayMs,
        FileJumpPickerFollowMode = FileJumpPickerFollowModes.Normalize(FileJumpPickerFollowMode),
        FileJumpPickerAutoPopup = FileJumpPickerAutoPopup,
        FileJumpPickerOpenWhenDialogForeground = FileJumpPickerOpenWhenDialogForeground,
        FileJumpAutoSyncOnReturn = FileJumpAutoSyncOnReturn,
        EnableShellNavigateInject = EnableShellNavigateInject,
        FileJumpAutoOnFirstClick = FileJumpAutoOnFirstClick,
        Theme = Theme,
        PopupPosition = PopupPosition,
        PopupOpacity = PopupOpacity,
        HideOnSameAppClick = HideOnSameAppClick,
        PasteRequiresDoubleClick = PasteRequiresDoubleClick,
        RunAtStartup = RunAtStartup,
        RunAsAdministrator = RunAsAdministrator,
        PasteSimulationMode = PasteSimulationMode,
        CheckUpdatesOnStartup = CheckUpdatesOnStartup,
        ReplaceSystemWinV = ReplaceSystemWinV,
        ClearHistoryOnExit = ClearHistoryOnExit,
        ImageOcrEnabled = ImageOcrEnabled,
        LastStartupUpdateNotifiedTag = LastStartupUpdateNotifiedTag,
        PreviewMaxLines = PreviewMaxLines,
        PopupPanelWidth = PopupPanelWidth,
        PopupPanelMaxHeight = PopupPanelMaxHeight,
        PopupPanelHeight = PopupPanelHeight,
        FileJumpPickerWidth = FileJumpPickerWidth,
        FileJumpPickerMaxHeight = FileJumpPickerMaxHeight,
        FileJumpPickerHeight = FileJumpPickerHeight,
        ExplorerQuickFindWidth = ExplorerQuickFindWidth,
        ExplorerQuickFindMaxHeight = ExplorerQuickFindMaxHeight,
        ExplorerQuickFindHeight = ExplorerQuickFindHeight,
        PopupPageItems = PopupPageItems,
        PanelPageScrollUpModifiers = PanelPageScrollUpModifiers,
        PanelPageScrollUpKey = PanelPageScrollUpKey,
        PanelPageScrollDownModifiers = PanelPageScrollDownModifiers,
        PanelPageScrollDownKey = PanelPageScrollDownKey,
        PanelModifierKey = PanelModifierKey,
        BatchPasteMode = BatchPasteMode,
        BatchModeCycleHotkeyModifiers = BatchModeCycleHotkeyModifiers,
        BatchModeCycleHotkeyKey = BatchModeCycleHotkeyKey,
        BatchPasteMergeText = BatchPasteMergeText,
        BatchQueueAutoSwitchToNormalAfterQueueDone = BatchQueueAutoSwitchToNormalAfterQueueDone,
        QuickPastes = QuickPastes.Select(q => new QuickPasteEntry { Phrase = q.Phrase, Content = q.Content }).ToList(),
        FolderFavorites = FolderFavorites.Select(f => new FolderFavoriteEntry { Phrase = f.Phrase, Path = f.Path }).ToList(),
        LastFileDialogFolder = LastFileDialogFolder,
        RecentFileDialogFolders = RecentFileDialogFolders.ToList(),
        RecentFolderMaxCount = RecentFolderMaxCount,
        RecentFolderAutoAddMinCount = RecentFolderAutoAddMinCount,
        FolderConfirmCounts = FolderConfirmCounts != null
            ? new Dictionary<string, int>(FolderConfirmCounts, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        ExplorerEverythingQuickFindEnabled = ExplorerEverythingQuickFindEnabled,
        ExplorerEverythingQuickFindMaxResults = ExplorerEverythingQuickFindMaxResults,
        FileJumpPickerEverythingFolderSearch = FileJumpPickerEverythingFolderSearch,
        UseFindXSearch = UseFindXSearch,
        ExplorerQuickFindOpenMode = ExplorerQuickFindOpenMode,
        ExclusionApps = ExclusionApps.ToList(),
        KeyPassthroughEnabled = KeyPassthroughEnabled,
        KeyPassthroughModifierMask = KeyPassthroughModifierMask,
        KeyPassthroughKeepPanelKeys = KeyPassthroughKeepPanelKeys,
        KeyPassthroughRules = KeyPassthroughRules
            .Select(r => new KeyPassthroughRule { Modifiers = r.Modifiers, Key = r.Key })
            .ToList()
    };
}
