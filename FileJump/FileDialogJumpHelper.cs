using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace ClipboardManager;

/// <summary>
/// 识别文件对话框类型（对齐 QuickSwitch 的 SysListView / DirectUI 启发式）、读取与跳转路径。
/// </summary>
internal enum FileDialogKind
{
    None,
    /// <summary>#32770 + Shell 视图，或未检出子类型时的通用处理（地址栏）。</summary>
    ShellDefViewOrGeneral,
    /// <summary>DirectUIHWND + ToolbarWindow32 + Edit（较新通用对话框）。</summary>
    GeneralDirectUi,
    /// <summary>SysListView32 + ToolbarWindow32 + Edit（如部分旧式宿主）。</summary>
    SysListView,
    /// <summary>WPS 办公组件（wps/et/wpp 等）自带的「打开文件 / 另存为」等非 #32770 对话框。</summary>
    WpsCustom,
}

internal static class FileDialogJumpHelper
{
    public static FileDialogKind ClassifyFileDialog(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return FileDialogKind.None;

        // WPS 不使用系统公共 #32770 对话框，须在类名判断之前识别。
        if (IsWpsSuiteFileDialog(hwnd))
            return FileDialogKind.WpsCustom;

        if (!Win32.GetWindowClassName(hwnd).Equals("#32770", StringComparison.Ordinal))
            return FileDialogKind.None;

        // Internet Download Manager 主界面为 #32770 + Explorer 风格子控件，易误判为公共对话框，
        // 且会触发下方整树 Class 收集导致明显卡顿。无 owner 的顶层且标题不像打开/保存时视为其主壳，不参与跳转。
        if (TryGetExeBaseNameLower(hwnd, out var idmExe) && idmExe == "idman"
            && Win32.GetWindow(hwnd, Win32.GW_OWNER) == IntPtr.Zero
            && !IsFileDialogTitle(Win32.GetWindowText(hwnd)))
            return FileDialogKind.None;

        var classes = CollectDescendantClassNames(hwnd);
        var hasDirect = classes.Any(c => c.Contains("DirectUIHWND", StringComparison.Ordinal));
        var hasList = classes.Contains("SysListView32", StringComparer.OrdinalIgnoreCase);
        var hasTb = classes.Contains("ToolbarWindow32", StringComparer.OrdinalIgnoreCase);
        var hasEdit = classes.Contains("Edit", StringComparer.OrdinalIgnoreCase);

        if (hasDirect && hasTb && hasEdit) return FileDialogKind.GeneralDirectUi;
        if (hasList && hasTb && hasEdit) return FileDialogKind.SysListView;
        if (ContainsShellDefView(hwnd)) return FileDialogKind.ShellDefViewOrGeneral;
        if (IsFileDialogTitle(Win32.GetWindowText(hwnd))) return FileDialogKind.ShellDefViewOrGeneral;
        return FileDialogKind.None;
    }

    public static bool IsLikelyFileDialog(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return false;
        if (ClassifyFileDialog(hwnd) != FileDialogKind.None) return true;
        return CustomFileDialogStore.FindMatchingRule(hwnd) != null;
    }

    /// <summary>进程主模块基名（小写，无扩展名），用于识别 WPS 套件。</summary>
    private static bool TryGetExeBaseNameLower(IntPtr hwnd, out string name)
    {
        name = "";
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return false;
        var h = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            var sb = new StringBuilder(1024);
            if (Win32.GetModuleFileNameEx(h, IntPtr.Zero, sb, sb.Capacity) == 0)
                return false;
            name = Path.GetFileNameWithoutExtension(sb.ToString()).ToLowerInvariant();
            return name.Length > 0;
        }
        finally
        {
            Win32.CloseHandle(h);
        }
    }

    private static bool IsWpsSuiteExe(string exeBaseLower) =>
        exeBaseLower is "wps" or "et" or "wpp" or "pdf" or "ksolaunch";

    /// <summary>WPS 自定义打开/保存窗口标题（需与套件进程同时匹配，避免误伤其他「打开」对话框）。</summary>
    private static bool IsWpsFileDialogTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        if (title.Contains("打开文件", StringComparison.Ordinal)) return true;
        if (title.Contains("打开文档", StringComparison.Ordinal)) return true;
        if (title.Contains("打开工作簿", StringComparison.Ordinal)) return true;
        if (title.Contains("打开演示", StringComparison.Ordinal)) return true;
        if (title.Contains("另存文件", StringComparison.Ordinal)) return true;
        if (title.Contains("保存文件", StringComparison.Ordinal)) return true;
        if (title.Contains("选择文件", StringComparison.Ordinal)) return true;
        if (title.Contains("选取文件", StringComparison.Ordinal)) return true;
        if (title.Contains("浏览文件夹", StringComparison.Ordinal)) return true;
        // 简短标题（部分语言包 / 新版本）
        if (title.Equals("另存为", StringComparison.Ordinal)) return true;
        if (title.Equals("保存", StringComparison.Ordinal)) return true;
        if (title.Equals("打开", StringComparison.Ordinal)) return true;
        if (title.StartsWith("打开(", StringComparison.Ordinal)) return true;
        if (title.StartsWith("另存为(", StringComparison.Ordinal)) return true;
        var t = title.ToLowerInvariant();
        if (t.Contains("save as")) return true;
        if (t.Contains("open file")) return true;
        if (t.Contains("browse")) return true;
        if (t.Contains("select file")) return true;
        if (t is "open" or "save") return true;
        return false;
    }

    /// <summary>Qt 壳 + 极短本地化标题时补充匹配（仍要求 WPS 进程）。</summary>
    private static bool IsWpsQtLikeWindowClass(string className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        return className.Contains("Qt", StringComparison.Ordinal)
               || className.Contains("QWindow", StringComparison.Ordinal);
    }

    private static bool IsWpsSuiteFileDialog(IntPtr hwnd)
    {
        if (!TryGetExeBaseNameLower(hwnd, out var exe) || !IsWpsSuiteExe(exe))
            return false;

        var className = Win32.GetWindowClassName(hwnd);

        // #32770 也归入 WpsCustom（后备方法更丰富），注入由 TryNavigateToFolder 按类名单独处理
        if (className.Equals("#32770", StringComparison.Ordinal))
            return true;

        var title = Win32.GetWindowText(hwnd);
        if (IsWpsFileDialogTitle(title))
            return true;

        if (!IsWpsQtLikeWindowClass(className))
            return false;

        if (!string.IsNullOrEmpty(title)
            && (title.Contains("打开", StringComparison.Ordinal)
                || title.Contains("另存", StringComparison.Ordinal)
                || title.Contains("保存", StringComparison.Ordinal)))
            return true;

        // WPS Qt5 自绘对话框：GetWindowText 为空且 UIA 不可用。
        // WPS 主窗口/首页/新建页同样是空标题 Qt 窗口，需排除：
        // 文件对话框由主窗口弹出，有 owner；主窗口/首页无 owner。
        if (string.IsNullOrEmpty(title)
            && Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero)
            return true;

        return false;
    }

    private static HashSet<string> CollectDescendantClassNames(IntPtr root)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(IntPtr h)
        {
            set.Add(Win32.GetWindowClassName(h));
            Win32.EnumChildWindows(h, (c, _) =>
            {
                Walk(c);
                return true;
            }, IntPtr.Zero);
        }
        Walk(root);
        return set;
    }

    private static bool ContainsShellDefView(IntPtr root)
    {
        var stack = new Stack<IntPtr>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var h = stack.Pop();
            if (string.Equals(Win32.GetWindowClassName(h), "SHELLDLL_DefView", StringComparison.Ordinal))
                return true;
            var children = new List<IntPtr>();
            Win32.EnumChildWindows(h, (ch, _) =>
            {
                children.Add(ch);
                return true;
            }, IntPtr.Zero);
            foreach (var c in children) stack.Push(c);
        }
        return false;
    }

    private static bool IsFileDialogTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        var t = title.ToLowerInvariant();
        return title.Contains("打开", StringComparison.Ordinal)
               || title.Contains("另存", StringComparison.Ordinal)
               || title.Contains("保存", StringComparison.Ordinal)
               || t.Contains("open", StringComparison.Ordinal)
               || t.Contains("save", StringComparison.Ordinal)
               || t.Contains("browse", StringComparison.Ordinal);
    }

    public static bool TryReadCurrentFolder(IntPtr hwnd, out string folder) =>
        TryReadCurrentFolder(hwnd, out folder, relaxed: false);

    /// <param name="relaxed">为 true 时扩大 UIA 扫描并尝试面包屑解析，便于自定义对话框探测。</param>
    public static bool TryReadCurrentFolder(IntPtr hwnd, out string folder, bool relaxed)
    {
        folder = "";
        var kind = ClassifyFileDialog(hwnd);
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return false;

            var best = "";
            var q = new Queue<AutomationElement>();
            q.Enqueue(root);
            var seen = 0;
            var maxNodes = relaxed
                ? 500
                : kind == FileDialogKind.WpsCustom ? 450 : 150;
            var allowBreadcrumbInLoop = relaxed || kind == FileDialogKind.WpsCustom;
            while (q.Count > 0 && seen < maxNodes)
            {
                var el = q.Dequeue();
                seen++;
                try
                {
                    foreach (AutomationElement c in el.FindAll(TreeScope.Children, Condition.TrueCondition))
                        q.Enqueue(c);
                }
                catch { /* ignore */ }

                try
                {
                    if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj))
                    {
                        var v = ((ValuePattern)vpObj).Current.Value;
                        if (TryNormalizeToExistingDirectory(v, out var norm) && norm.Length > best.Length)
                            best = norm;
                    }
                }
                catch { }

                // 勿在 best 非空时跳过：Win11 资源管理器等场景下 BFS 可能先命中「D:\」类 Value，
                // 地址栏面包屑 Name（此电脑 > … > gn）在后序结点，仍需参与取更长、更具体的路径。
                if (!allowBreadcrumbInLoop) continue;

                try
                {
                    var name = el.Current.Name;
                    if (TryWpsBreadcrumbTextToFolder(name, out var bc) && bc.Length > best.Length)
                        best = bc;
                }
                catch { }

                try
                {
                    var aid = el.Current.AutomationId;
                    if (!string.IsNullOrEmpty(aid)
                        && TryWpsBreadcrumbTextToFolder(aid, out var bc2) && bc2.Length > best.Length)
                        best = bc2;
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(best))
            {
                folder = best;
                return true;
            }
        }
        catch { }

        if ((relaxed || kind == FileDialogKind.WpsCustom) && TryReadWpsBreadcrumbOnly(hwnd, out var wpsFolder))
        {
            folder = wpsFolder;
            return true;
        }

        return false;
    }

    /// <summary>专用于 ValuePattern 未给出完整路径时，仅从名称/AutomationId 中的面包屑推断目录。</summary>
    private static bool TryReadWpsBreadcrumbOnly(IntPtr hwnd, out string folder)
    {
        folder = "";
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return false;
            var best = "";
            var q = new Queue<AutomationElement>();
            q.Enqueue(root);
            for (var seen = 0; q.Count > 0 && seen < 500; seen++)
            {
                var el = q.Dequeue();
                try
                {
                    foreach (AutomationElement c in el.FindAll(TreeScope.Children, Condition.TrueCondition))
                        q.Enqueue(c);
                }
                catch { /* ignore */ }

                try
                {
                    var name = el.Current.Name;
                    if (TryWpsBreadcrumbTextToFolder(name, out var p) && p.Length > best.Length)
                        best = p;
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(best))
            {
                folder = best;
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>将地址栏/名称中的「此电脑 &gt; …」或「C: &gt; …」等面包屑文本还原为已存在目录（Q-Dir、WPS 等共用）。</summary>
    internal static bool TryWpsBreadcrumbTextToFolder(string? text, out string folder)
    {
        folder = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Replace('＞', '>').Replace('›', '>').Trim();
        if (TryNormalizeToExistingDirectory(text.Replace(" > ", "\\"), out folder))
            return true;

        if (!text.Contains('>'))
            return false;

        var parts = text.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        string? volume = null;
        foreach (var partRaw in parts)
        {
            var part = partRaw.Trim();
            if (part.Length == 0) continue;

            if (part.Contains("此电脑", StringComparison.Ordinal)
                || part.Equals("This PC", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Computer", StringComparison.OrdinalIgnoreCase))
            {
                volume = "";
                continue;
            }

            if (TryDriveRootFromBreadcrumbSegment(part, out var driveRoot))
            {
                volume = driveRoot;
                continue;
            }

            if (WpsKnownFolderToPath(part) is { } known)
            {
                volume = known;
                continue;
            }

            if (volume == null)
                continue;
            // 「此电脑」之后 volume 为空串：尚无盘符/根路径时不应拼相对路径。
            if (volume.Length == 0)
                continue;

            try
            {
                var next = Path.Combine(volume, part);
                volume = Path.GetFullPath(next);
            }
            catch
            {
                return false;
            }
        }

        if (string.IsNullOrEmpty(volume)) return false;
        try
        {
            if (!Directory.Exists(volume)) return false;
            folder = Path.GetFullPath(volume);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDriveRootFromBreadcrumbSegment(string segment, out string driveRoot)
    {
        driveRoot = "";
        if (TryNormalizeToExistingDirectory(segment, out var direct))
        {
            try
            {
                if (Directory.Exists(direct))
                {
                    driveRoot = Path.GetFullPath(direct);
                    return true;
                }
            }
            catch
            {
                /* ignore */
            }
        }

        var i = segment.LastIndexOf('(');
        if (i >= 0)
        {
            var j = segment.IndexOf(')', i);
            if (j > i)
            {
                var inner = segment.AsSpan(i + 1, j - i - 1).Trim();
                if (inner.Length >= 2 && inner[^1] == ':' && char.IsLetter(inner[0]))
                {
                    driveRoot = char.ToUpperInvariant(inner[0]) + @":\";
                    return true;
                }
                if (inner.Length == 1 && char.IsLetter(inner[0]))
                {
                    driveRoot = char.ToUpperInvariant(inner[0]) + @":\";
                    return true;
                }
            }
        }

        var m = Regex.Match(segment, @"^([A-Za-z]):$");
        if (m.Success)
        {
            driveRoot = char.ToUpperInvariant(m.Groups[1].Value[0]) + @":\";
            return true;
        }

        return false;
    }

    private static string? WpsKnownFolderToPath(string display)
    {
        try
        {
            if (display.Contains("桌面", StringComparison.Ordinal))
                return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (display.Contains("文档", StringComparison.Ordinal))
                return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (display.Contains("下载", StringComparison.Ordinal))
            {
                var down = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                return Directory.Exists(down) ? down : null;
            }
            if (display.Contains("图片", StringComparison.Ordinal))
                return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (display.Contains("音乐", StringComparison.Ordinal))
                return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (display.Contains("视频", StringComparison.Ordinal))
                return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

            if (display.Equals("Desktop", StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (display.Equals("Documents", StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (display.Equals("Downloads", StringComparison.OrdinalIgnoreCase))
            {
                var down = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                return Directory.Exists(down) ? down : null;
            }
        }
        catch
        {
            /* ignore */
        }

        return null;
    }

    /// <summary>供路径采集等模块复用：从 UI 文本还原为已存在的目录路径。</summary>
    /// <remarks>
    /// <para>禁止无名相对路径（如单独的 <c>tools</c>）走 <see cref="Directory.Exists"/>，否则会相对
    /// <see cref="Environment.CurrentDirectory"/> 解析；<c>dotnet run</c> 时 cwd 常为仓库根，易误判为 <c>…\\clipboardx\\tools</c>。</para>
    /// <para>但 <c>D:</c> 在 .NET 里不是 <see cref="Path.IsPathFullyQualified"/>，而地址栏常见，故对单盘符先规范为 <c>X:\\</c>。</para>
    /// </remarks>
    internal static bool TryNormalizeToExistingDirectory(string? raw, out string norm)
    {
        norm = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var v = raw.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1].Trim();

        try
        {
            // 单独「D:」等：与 IsPathFullyQualified 兼容的资源管理器地址栏形态
            if (v.Length == 2 && v[1] == ':' && char.IsLetter(v[0]))
            {
                var root = char.ToUpperInvariant(v[0]) + @":\";
                if (Directory.Exists(root))
                {
                    norm = Path.GetFullPath(root);
                    return true;
                }
            }

            if (!Path.IsPathFullyQualified(v))
                return false;

            if (Directory.Exists(v))
            {
                norm = Path.GetFullPath(v);
                return true;
            }

            var dir = Path.GetDirectoryName(v);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                norm = Path.GetFullPath(dir);
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// “保存的目标”等实际目录：将联接点/符号链接解析为物理路径，减轻 BrowseObject 与地址栏在 Shell/云目录上的失败率。
    /// </summary>
    private static string NormalizeFolderPathForNavigation(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return folderPath;
        try
        {
            var full = Path.GetFullPath(folderPath.Trim());
            if (!Directory.Exists(full)) return full;
            var di = new DirectoryInfo(full);
            DirectoryInfo? concrete = null;
            try
            {
                if (di.LinkTarget != null || (di.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var resolved = di.ResolveLinkTarget(returnFinalTarget: true);
                    if (resolved is DirectoryInfo d && d.Exists)
                        concrete = d;
                }
            }
            catch { /* 非链接或无权解析则沿用 full */ }

            if (concrete != null && concrete.Exists)
                return concrete.FullName.TrimEnd('\\', '/');
            return full.TrimEnd('\\', '/');
        }
        catch
        {
            return folderPath;
        }
    }

    /// <param name="allowShellInject">为 false 时不注入宿主进程，仅走 UIA/地址栏等模拟（兼容拦截注入的环境）。</param>
    public static bool TryNavigateToFolder(IntPtr dialogHwnd, string folderPath, bool allowShellInject = true)
    {
        var path = NormalizeFolderPathForNavigation(folderPath);
        if (!Directory.Exists(path)) return false;

        var kind = ClassifyFileDialog(dialogHwnd);
        var customRule = kind == FileDialogKind.None
            ? CustomFileDialogStore.FindMatchingRule(dialogHwnd)
            : null;

        if (kind == FileDialogKind.None && customRule != null)
            return TryNavigateCustomRule(dialogHwnd, path, allowShellInject, customRule);

        if (kind == FileDialogKind.None) return false;

        // #32770 是标准 Shell 对话框，始终支持注入（包括 WPS 进程内弹出的浏览对话框）
        if (allowShellInject
            && Win32.GetWindowClassName(dialogHwnd).Equals("#32770", StringComparison.Ordinal)
            && ShellDialogDeepNavigate.TryBrowseObjectInject(dialogHwnd, path))
            return true;

        if (kind == FileDialogKind.SysListView)
            return TryNavigateSysListViewStyle(dialogHwnd, path);
        if (kind == FileDialogKind.WpsCustom)
            return TryNavigateWpsCustom(dialogHwnd, path);
        return TryNavigateAddressBarStyle(dialogHwnd, path);
    }

    /// <summary>
    /// 在对话框当前不处于 <paramref name="folderPath"/> 时，依次尝试策略并用宽松 UIA 读取校验；
    /// 成功则将 <paramref name="rule"/>.<see cref="CustomFileDialogRule.PinnedStrategy"/> 设为命中项。
    /// </summary>
    public static bool TryProbeCustomStrategies(
        IntPtr dialogHwnd,
        string folderPath,
        bool allowShellInject,
        CustomFileDialogRule rule)
    {
        if (dialogHwnd == IntPtr.Zero || !Win32.IsWindow(dialogHwnd)) return false;
        var norm = NormalizeFolderPathForNavigation(folderPath);
        if (!Directory.Exists(norm)) return false;

        if (TryReadCurrentFolder(dialogHwnd, out var before, relaxed: true)
            && PathsLooselyEqual(before, norm))
        {
            ShellNavigateLog.Write("custom_fd",
                "probe skipped: dialog already at target path (请切换到其他文件夹后再探测)");
            return false;
        }

        var order = rule.StrategyOrder is { Count: > 0 }
            ? rule.StrategyOrder.Where(s => !string.IsNullOrEmpty(s)).ToList()
            : CustomFileDialogStore.DefaultStrategyOrder.ToList();

        foreach (var s in order)
        {
            TryApplyCustomDialogStrategy(s, dialogHwnd, norm, allowShellInject);
            Thread.Sleep(480);
            if (TryReadCurrentFolder(dialogHwnd, out var after, relaxed: true)
                && PathsLooselyEqual(after, norm))
            {
                rule.PinnedStrategy = s;
                ShellNavigateLog.Write("custom_fd", $"probe pinned strategy={s}");
                return true;
            }
        }

        rule.PinnedStrategy = null;
        return false;
    }

    private static bool PathsLooselyEqual(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try
        {
            var na = Path.GetFullPath(a).TrimEnd('\\', '/');
            var nb = Path.GetFullPath(b).TrimEnd('\\', '/');
            return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static List<string> BuildCustomStrategyOrder(CustomFileDialogRule rule)
    {
        var source = rule.StrategyOrder is { Count: > 0 }
            ? rule.StrategyOrder
            : CustomFileDialogStore.DefaultStrategyOrder.ToList();
        var order = new List<string>();
        if (!string.IsNullOrEmpty(rule.PinnedStrategy)
            && source.Contains(rule.PinnedStrategy, StringComparer.OrdinalIgnoreCase))
            order.Add(rule.PinnedStrategy);
        foreach (var s in source)
        {
            if (string.IsNullOrEmpty(s)) continue;
            if (order.Contains(s, StringComparer.OrdinalIgnoreCase)) continue;
            order.Add(s);
        }

        return order;
    }

    private static bool TryNavigateCustomRule(
        IntPtr dialogHwnd,
        string path,
        bool allowShellInject,
        CustomFileDialogRule rule)
    {
        foreach (var s in BuildCustomStrategyOrder(rule))
        {
            if (TryApplyCustomDialogStrategy(s, dialogHwnd, path, allowShellInject))
                return true;
        }

        return false;
    }

    private static bool TryApplyCustomDialogStrategy(
        string strategyId,
        IntPtr dialogHwnd,
        string normalizedExistingDir,
        bool allowShellInject)
    {
        var id = strategyId.Trim().ToLowerInvariant();
        try
        {
            switch (id)
            {
                case "shell_inject":
                    if (!allowShellInject) return false;
                    if (!Win32.GetWindowClassName(dialogHwnd).Equals("#32770", StringComparison.Ordinal))
                        return false;
                    return ShellDialogDeepNavigate.TryBrowseObjectInject(dialogHwnd, normalizedExistingDir);
                case "sys_listview":
                    return TryNavigateSysListViewStyle(dialogHwnd, normalizedExistingDir);
                case "address_bar":
                    return TryNavigateAddressBarStyle(dialogHwnd, normalizedExistingDir);
                case "wps_chain":
                    return TryNavigateWpsCustom(dialogHwnd, normalizedExistingDir);
                case "qt_alt_n":
                {
                    var folderWithSlash = Path.GetFullPath(normalizedExistingDir).TrimEnd('\\', '/') + "\\";
                    ActivateDialog(dialogHwnd);
                    Thread.Sleep(50);
                    TryNavigateQtFileDialog(dialogHwnd, folderWithSlash);
                    return true;
                }
                case "alt_d_value_enter":
                    ActivateDialog(dialogHwnd);
                    Thread.Sleep(100);
                    SendAltD();
                    Thread.Sleep(160);
                    if (TrySetFocusedAddressValue(Path.GetFullPath(normalizedExistingDir)))
                    {
                        Thread.Sleep(50);
                        SendEnter();
                        return true;
                    }

                    return false;
                case "ctrl_l_type_enter":
                    ActivateDialog(dialogHwnd);
                    Thread.Sleep(60);
                    SendCtrlL();
                    Thread.Sleep(120);
                    SendUnicodeString(Path.GetFullPath(normalizedExistingDir));
                    Thread.Sleep(50);
                    SendEnter();
                    return true;
                default:
                    ShellNavigateLog.Write("custom_fd", $"unknown strategy id={strategyId}");
                    return false;
            }
        }
        catch (Exception ex)
        {
            ShellNavigateLog.Write("custom_fd", $"strategy {strategyId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// WPS：无 IShellBrowser；含 ComboBoxEx/Edit、ReBar+F4 地址栏（与逍遥 QuickJump ChangePath 同类）、UIA、Alt+D、Ctrl+L。
    /// Qt5 窗口无 Win32 子控件，通过文件名输入框键入路径跳转。
    /// </summary>
    private static bool TryNavigateWpsCustom(IntPtr dialogHwnd, string folderPath)
    {
        if (!Directory.Exists(folderPath)) return false;
        var norm = Path.GetFullPath(folderPath);
        var folderWithSlash = norm.TrimEnd('\\', '/') + "\\";

        // 提前判断 Qt5：无 Win32 子控件时跳过所有 Win32/UIA 遍历（耗时会导致焦点丢失）
        if (!HasAnyWin32ChildWindow(dialogHwnd))
        {
            ShellNavigateLog.Write("wps",
                $"Qt5 dialog detected; class={Win32.GetWindowClassName(dialogHwnd)}");
            return TryNavigateQtFileDialog(dialogHwnd, folderWithSlash);
        }

        ActivateDialog(dialogHwnd);
        Thread.Sleep(100);

        if (TryWpsSetPathViaValuePattern(dialogHwnd, norm))
        {
            Thread.Sleep(60);
            SendEnter();
            Thread.Sleep(120);
            return true;
        }

        var comboEdit = FindComboBoxExEmbeddedEdit(dialogHwnd);
        if (comboEdit != IntPtr.Zero && TryWpsFillEditWithFolderAndEnter(comboEdit, folderWithSlash))
            return true;

        var bottomInput = FindBottomMostPathInputHwnd(dialogHwnd);
        if (bottomInput != IntPtr.Zero && TryWpsFillEditWithFolderAndEnter(bottomInput, folderWithSlash))
            return true;

        if (TryNavigateReBarF4AddressEdit(dialogHwnd, folderWithSlash, bottomInput))
            return true;

        SendAltD();
        Thread.Sleep(160);
        try
        {
            if (TrySetFocusedAddressValue(norm))
            {
                Thread.Sleep(50);
                SendEnter();
                Thread.Sleep(120);
                return true;
            }
        }
        catch { /* ignore */ }

        ShellNavigateLog.Write("wps",
            $"TryNavigateWpsCustom 回退 Ctrl+L；class={Win32.GetWindowClassName(dialogHwnd)} title={Win32.GetWindowText(dialogHwnd)}");
        SendCtrlL();
        Thread.Sleep(120);
        SendUnicodeString(norm);
        Thread.Sleep(50);
        SendEnter();
        Thread.Sleep(120);
        return true;
    }

    private static void ActivateDialog(IntPtr dialogHwnd)
    {
        var dialogTid = Win32.GetWindowThreadProcessId(dialogHwnd, out _);
        var curTid = Win32.GetCurrentThreadId();
        Win32.AttachThreadInput(curTid, dialogTid, true);
        try { Win32.SetForegroundWindow(dialogHwnd); }
        finally { Win32.AttachThreadInput(curTid, dialogTid, false); }
    }

    private static bool HasAnyWin32ChildWindow(IntPtr hwnd)
    {
        var found = false;
        Win32.EnumChildWindows(hwnd, (_, _) => { found = true; return false; }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// WPS Qt5 文件对话框跳转：先确保前台焦点，然后依次尝试 Alt+N / 直接输入。
    /// </summary>
    private static bool TryNavigateQtFileDialog(IntPtr dialogHwnd, string folderWithSlash)
    {
        ActivateDialog(dialogHwnd);
        Thread.Sleep(50);
        SendAltN();
        Thread.Sleep(30);
        SendCtrlA();
        Thread.Sleep(10);
        SendUnicodeString(folderWithSlash);
        Thread.Sleep(10);
        SendEnter();
        return true;
    }

    /// <summary>
    /// 逍遥 QuickJump ChangePath：ReBarWindow32 取焦点后 F4，待键盘焦点落入地址类 Edit 再填路径回车。
    /// 部分带经典壳的打开/保存窗（含个别 WPS 混合界面）适用。
    /// </summary>
    private static bool TryNavigateReBarF4AddressEdit(IntPtr dialogHwnd, string folderWithSlash, IntPtr skipEditHwnd)
    {
        var rebar = FindFirstHwndByClass(dialogHwnd, "ReBarWindow32");
        if (rebar == IntPtr.Zero) return false;

        uint dialogPid;
        var dialogTid = Win32.GetWindowThreadProcessId(dialogHwnd, out dialogPid);
        var curTid = Win32.GetCurrentThreadId();

        Win32.AttachThreadInput(curTid, dialogTid, true);
        try
        {
            Win32.SetForegroundWindow(dialogHwnd);
            Thread.Sleep(50);
            Win32.SetFocus(rebar);
            Thread.Sleep(40);
            SendF4();
            Thread.Sleep(100);

            for (var i = 0; i < 45; i++)
            {
                var h = Win32.GetFocus();
                if (h != IntPtr.Zero && h != skipEditHwnd && IsWin32PathInputClass(Win32.GetWindowClassName(h)))
                {
                    if (TryWpsFillEditWithFolderAndEnter(h, folderWithSlash))
                        return true;
                }
                Thread.Sleep(15);
            }
        }
        finally
        {
            Win32.AttachThreadInput(curTid, dialogTid, false);
        }

        return false;
    }

    private static IntPtr FindFirstHwndByClass(IntPtr root, string className)
    {
        IntPtr found = IntPtr.Zero;
        void Walk(IntPtr h)
        {
            if (found != IntPtr.Zero) return;
            if (string.Equals(Win32.GetWindowClassName(h), className, StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return;
            }
            Win32.EnumChildWindows(h, (c, _) =>
            {
                Walk(c);
                return true;
            }, IntPtr.Zero);
        }
        Walk(root);
        return found;
    }

    private static void SendF4()
    {
        const ushort vkF4 = 0x73;
        var inputs = new Win32.INPUT[2];
        inputs[0].type = Win32.INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = vkF4;
        inputs[1].type = Win32.INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = vkF4;
        inputs[1].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        Win32.SendInput(2, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    /// <summary>commctrl ComboBoxEx32 内嵌编辑框，常见于地址栏。</summary>
    private static IntPtr FindComboBoxExEmbeddedEdit(IntPtr root)
    {
        IntPtr found = IntPtr.Zero;
        const uint cbemGetEditControl = 0x0400 + 102;
        void Walk(IntPtr h)
        {
            if (found != IntPtr.Zero) return;
            if (string.Equals(Win32.GetWindowClassName(h), "ComboBoxEx32", StringComparison.OrdinalIgnoreCase))
            {
                var edit = Win32.SendMessage(h, cbemGetEditControl, IntPtr.Zero, IntPtr.Zero);
                if (edit != IntPtr.Zero)
                    found = edit;
                return;
            }
            Win32.EnumChildWindows(h, (c, _) =>
            {
                Walk(c);
                return true;
            }, IntPtr.Zero);
        }
        Walk(root);
        return found;
    }

    private static bool TryWpsFillEditWithFolderAndEnter(IntPtr hwnd, string folderWithSlash)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            var cap = (int)(long)Win32.SendMessage(hwnd, Win32.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
            if (cap < 0) cap = 0;
            cap = Math.Min(cap + 64, 32767);
            var oldSb = new StringBuilder(Math.Max(cap, 256));
            Win32.SendMessage(hwnd, Win32.WM_GETTEXT, (IntPtr)oldSb.Capacity, oldSb);
            var oldText = oldSb.ToString();

            Win32.SendMessage(hwnd, Win32.WM_SETTEXT, IntPtr.Zero, folderWithSlash);
            Win32.SetFocus(hwnd);
            Thread.Sleep(40);
            SendEnter();
            Thread.Sleep(120);

            Win32.SendMessage(hwnd, Win32.WM_SETTEXT, IntPtr.Zero, oldText);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryWpsSetPathViaValuePattern(IntPtr dialogHwnd, string fullPath)
    {
        try
        {
            var root = AutomationElement.FromHandle(dialogHwnd);
            if (root == null) return false;
            var candidates = new List<(int score, AutomationElement el)>();
            var q = new Queue<AutomationElement>();
            q.Enqueue(root);
            for (var seen = 0; q.Count > 0 && seen < 450; seen++)
            {
                var el = q.Dequeue();
                try
                {
                    foreach (AutomationElement c in el.FindAll(TreeScope.Children, Condition.TrueCondition))
                        q.Enqueue(c);
                }
                catch { /* ignore */ }

                try
                {
                    if (!el.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj))
                        continue;
                    var vp = (ValuePattern)vpObj;
                    var score = WpsValuePatternCandidateScore(el, vp);
                    if (score < 0)
                        continue;
                    candidates.Add((score, el));
                }
                catch { /* ignore */ }
            }

            foreach (var (_, el) in candidates.OrderByDescending(t => t.score))
            {
                try
                {
                    if (!el.TryGetCurrentPattern(ValuePattern.Pattern, out var useVp))
                        continue;
                    var v = (ValuePattern)useVp;
                    if (v.Current.IsReadOnly)
                        continue;
                    v.SetValue(fullPath);
                    return true;
                }
                catch { /* ignore */ }
            }

            // 部分 Qt/自定义提供程序误报 IsReadOnly，第二轮不区分只读位一律尝试
            foreach (var (_, el) in candidates.OrderByDescending(t => t.score))
            {
                try
                {
                    if (!el.TryGetCurrentPattern(ValuePattern.Pattern, out var useVp))
                        continue;
                    ((ValuePattern)useVp).SetValue(fullPath);
                    return true;
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        return false;
    }

    /// <summary>分数越高越可能是地址/路径框；返回 -1 表示不参与。</summary>
    private static int WpsValuePatternCandidateScore(AutomationElement el, ValuePattern vp)
    {
        var score = 0;
        try
        {
            if (!vp.Current.IsReadOnly)
                score += 2;
            var curVal = vp.Current.Value ?? "";
            if (curVal.Contains(':', StringComparison.Ordinal) && (curVal.Contains('\\') || curVal.Contains('/')))
                score += 3;
        }
        catch { /* ignore */ }

        try
        {
            var n = el.Current.Name ?? "";
            if (n.Contains("地址", StringComparison.Ordinal)
                || n.Contains("路径", StringComparison.Ordinal)
                || n.Contains("位置", StringComparison.Ordinal))
                score += 6;
            if (n.Contains("文件夹", StringComparison.Ordinal))
                score += 4;
            if (n.Contains("文件", StringComparison.Ordinal) && n.Contains("名", StringComparison.Ordinal))
                score += 1;
        }
        catch { /* ignore */ }

        return score > 0 ? score : -1;
    }

    /// <summary>取屏幕上最靠下的路径类输入控件（Edit / RichEdit），通常为「文件名」或底部路径框。</summary>
    private static IntPtr FindBottomMostPathInputHwnd(IntPtr root)
    {
        IntPtr best = IntPtr.Zero;
        var bestBottom = int.MinValue;
        void Walk(IntPtr h)
        {
            var cls = Win32.GetWindowClassName(h);
            if (IsWin32PathInputClass(cls))
            {
                if (Win32.GetWindowRect(h, out var r) && r.Bottom >= bestBottom)
                {
                    bestBottom = r.Bottom;
                    best = h;
                }
            }
            Win32.EnumChildWindows(h, (c, _) =>
            {
                Walk(c);
                return true;
            }, IntPtr.Zero);
        }
        Walk(root);
        return best;
    }

    private static bool IsWin32PathInputClass(string cls)
    {
        if (string.Equals(cls, "Edit", StringComparison.OrdinalIgnoreCase)) return true;
        if (cls.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(cls, "RICHEDIT50W", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>QuickSwitch FeedDialogSYSLISTVIEW：改 Edit1 为文件夹路径并回车，再恢复原文件名。</summary>
    private static bool TryNavigateSysListViewStyle(IntPtr dialogHwnd, string folderPath)
    {
        if (!Directory.Exists(folderPath)) return false;
        var norm = Path.GetFullPath(folderPath);
        var folderWithSlash = norm.TrimEnd('\\') + "\\";

        var edit1 = FindFirstEditControl(dialogHwnd);
        if (edit1 == IntPtr.Zero) return false;

        uint dialogPid;
        var dialogTid = Win32.GetWindowThreadProcessId(dialogHwnd, out dialogPid);
        var curTid = Win32.GetCurrentThreadId();
        Win32.AttachThreadInput(curTid, dialogTid, true);
        try { Win32.SetForegroundWindow(dialogHwnd); }
        finally { Win32.AttachThreadInput(curTid, dialogTid, false); }

        Thread.Sleep(40);

        var cap = (int)(long)Win32.SendMessage(edit1, Win32.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
        if (cap < 0) cap = 0;
        cap = Math.Min(cap + 64, 32767);
        var oldSb = new StringBuilder(Math.Max(cap, 256));
        Win32.SendMessage(edit1, Win32.WM_GETTEXT, (IntPtr)oldSb.Capacity, oldSb);
        var oldText = oldSb.ToString();

        Win32.SendMessage(edit1, Win32.WM_SETTEXT, IntPtr.Zero, folderWithSlash);
        Win32.SetFocus(edit1);
        Thread.Sleep(30);
        SendEnter();
        Thread.Sleep(80);

        Win32.SendMessage(edit1, Win32.WM_SETTEXT, IntPtr.Zero, oldText);
        return true;
    }

    private static IntPtr FindFirstEditControl(IntPtr root)
    {
        IntPtr found = IntPtr.Zero;
        void Walk(IntPtr h)
        {
            if (found != IntPtr.Zero) return;
            if (string.Equals(Win32.GetWindowClassName(h), "Edit", StringComparison.Ordinal))
            {
                found = h;
                return;
            }
            Win32.EnumChildWindows(h, (c, _) =>
            {
                Walk(c);
                return true;
            }, IntPtr.Zero);
        }
        Walk(root);
        return found;
    }

    private static bool TryNavigateAddressBarStyle(IntPtr dialogHwnd, string folderPath)
    {
        if (!Directory.Exists(folderPath)) return false;
        var norm = Path.GetFullPath(folderPath);

        uint dialogPid;
        var dialogTid = Win32.GetWindowThreadProcessId(dialogHwnd, out dialogPid);
        var curTid = Win32.GetCurrentThreadId();

        Win32.AttachThreadInput(curTid, dialogTid, true);
        try { Win32.SetForegroundWindow(dialogHwnd); }
        finally { Win32.AttachThreadInput(curTid, dialogTid, false); }

        Thread.Sleep(60);

        var folderWithSlash = norm.TrimEnd('\\', '/') + "\\";
        var bottomEdit = FindBottomMostPathInputHwnd(dialogHwnd);
        if (TryNavigateReBarF4AddressEdit(dialogHwnd, folderWithSlash, bottomEdit))
            return true;

        SendCtrlL();
        Thread.Sleep(140);

        if (TrySetFocusedAddressValue(norm))
        {
            Thread.Sleep(40);
            SendEnter();
            return true;
        }

        SendCtrlA();
        Thread.Sleep(30);
        SendUnicodeString(norm);
        Thread.Sleep(30);
        SendEnter();
        return true;
    }

    private static bool TrySetFocusedAddressValue(string path)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null) return false;
            if (focused.TryGetCurrentPattern(ValuePattern.Pattern, out var p))
            {
                ((ValuePattern)p).SetValue(path);
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Alt+N 聚焦 WPS Qt 对话框"文件名称(N)"输入框。</summary>
    private static void SendAltN()
    {
        const ushort vkMenu = 0x12;
        const ushort vkN = 0x4E;
        var inputs = new Win32.INPUT[4];
        inputs[0].type = Win32.INPUT_KEYBOARD; inputs[0].u.ki.wVk = vkMenu;
        inputs[1].type = Win32.INPUT_KEYBOARD; inputs[1].u.ki.wVk = vkN;
        inputs[2].type = Win32.INPUT_KEYBOARD; inputs[2].u.ki.wVk = vkN; inputs[2].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        inputs[3].type = Win32.INPUT_KEYBOARD; inputs[3].u.ki.wVk = vkMenu; inputs[3].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        Win32.SendInput(4, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    /// <summary>与资源管理器一致：Alt+D 聚焦地址栏（部分宿主自带的打开对话框也支持）。</summary>
    private static void SendAltD()
    {
        var inputs = new Win32.INPUT[4];
        inputs[0].type = Win32.INPUT_KEYBOARD; inputs[0].u.ki.wVk = 0x12; // VK_MENU
        inputs[1].type = Win32.INPUT_KEYBOARD; inputs[1].u.ki.wVk = 0x44; // VK_D
        inputs[2].type = Win32.INPUT_KEYBOARD; inputs[2].u.ki.wVk = 0x44; inputs[2].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        inputs[3].type = Win32.INPUT_KEYBOARD; inputs[3].u.ki.wVk = 0x12; inputs[3].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        Win32.SendInput(4, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    private static void SendCtrlL()
    {
        var inputs = new Win32.INPUT[4];
        inputs[0].type = Win32.INPUT_KEYBOARD; inputs[0].u.ki.wVk = Win32.VK_CONTROL;
        inputs[1].type = Win32.INPUT_KEYBOARD; inputs[1].u.ki.wVk = Win32.VK_L;
        inputs[2].type = Win32.INPUT_KEYBOARD; inputs[2].u.ki.wVk = Win32.VK_L; inputs[2].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        inputs[3].type = Win32.INPUT_KEYBOARD; inputs[3].u.ki.wVk = Win32.VK_CONTROL; inputs[3].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        Win32.SendInput(4, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    private static void SendCtrlA()
    {
        var inputs = new Win32.INPUT[4];
        inputs[0].type = Win32.INPUT_KEYBOARD; inputs[0].u.ki.wVk = Win32.VK_CONTROL;
        inputs[1].type = Win32.INPUT_KEYBOARD; inputs[1].u.ki.wVk = Win32.VK_A;
        inputs[2].type = Win32.INPUT_KEYBOARD; inputs[2].u.ki.wVk = Win32.VK_A; inputs[2].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        inputs[3].type = Win32.INPUT_KEYBOARD; inputs[3].u.ki.wVk = Win32.VK_CONTROL; inputs[3].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        Win32.SendInput(4, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    private static void SendEnter()
    {
        var inputs = new Win32.INPUT[2];
        inputs[0].type = Win32.INPUT_KEYBOARD; inputs[0].u.ki.wVk = (ushort)Win32.VK_RETURN;
        inputs[1].type = Win32.INPUT_KEYBOARD; inputs[1].u.ki.wVk = (ushort)Win32.VK_RETURN; inputs[1].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        Win32.SendInput(2, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    private static void SendUnicodeString(string s)
    {
        var list = new List<Win32.INPUT>(s.Length * 2);
        foreach (var c in s)
        {
            var u = (ushort)c;
            list.Add(new Win32.INPUT
            {
                type = Win32.INPUT_KEYBOARD,
                u = new Win32.INPUTUNION
                {
                    ki = new Win32.KEYBDINPUT
                    {
                        wVk = 0, wScan = u,
                        dwFlags = Win32.KEYEVENTF_UNICODE, time = 0, dwExtraInfo = IntPtr.Zero
                    }
                }
            });
            list.Add(new Win32.INPUT
            {
                type = Win32.INPUT_KEYBOARD,
                u = new Win32.INPUTUNION
                {
                    ki = new Win32.KEYBDINPUT
                    {
                        wVk = 0, wScan = u,
                        dwFlags = Win32.KEYEVENTF_UNICODE | Win32.KEYEVENTF_KEYUP,
                        time = 0, dwExtraInfo = IntPtr.Zero
                    }
                }
            });
        }
        var arr = list.ToArray();
        Win32.SendInput((uint)arr.Length, arr, Marshal.SizeOf<Win32.INPUT>());
    }
}
