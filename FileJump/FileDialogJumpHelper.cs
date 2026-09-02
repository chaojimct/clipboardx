using System.Diagnostics;
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
        if (IsKnownNonFileDialogTitle(Win32.GetWindowText(hwnd))) return FileDialogKind.None;

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
        if (IsKnownNonFileDialogTitle(Win32.GetWindowText(hwnd))) return false;
        if (ClassifyFileDialog(hwnd) != FileDialogKind.None) return true;
        return CustomFileDialogStore.FindMatchingRule(hwnd) != null;
    }

    /// <summary>
    /// 部分宿主（如微信）将键盘焦点落在对话框内子控件上时，<see cref="Win32.GetForegroundWindow"/> 可能返回子 HWND，
    /// 而 <see cref="ClassifyFileDialog"/> 只对 #32770 等顶层成立。沿 GetParent 链上溯直至找到可对 <see cref="IsLikelyFileDialog"/> 成立的窗口。
    /// </summary>
    public static IntPtr ResolveFileDialogHwndFromWindowOrAncestor(IntPtr start)
    {
        if (start == IntPtr.Zero || !Win32.IsWindow(start)) return IntPtr.Zero;
        var h = start;
        for (var i = 0; i < 64 && h != IntPtr.Zero; i++)
        {
            if (IsLikelyFileDialog(h))
                return h;
            h = Win32.GetParent(h);
        }

        var root = Win32.GetAncestor(start, Win32.GA_ROOT);
        if (root != IntPtr.Zero)
        {
            // 微信等：前台事件里的 HWND 常仍是主窗，模态「打开文件」在 GetLastActivePopup(主窗) 上。
            Span<IntPtr> owners = root != start
                ? stackalloc IntPtr[] { start, root }
                : stackalloc IntPtr[] { start };
            foreach (var owner in owners)
            {
                if (owner == IntPtr.Zero) continue;
                var popup = Win32.GetLastActivePopup(owner);
                if (popup != IntPtr.Zero
                    && popup != owner
                    && Win32.IsWindow(popup)
                    && IsLikelyFileDialog(popup))
                    return popup;
            }

            if (root != start && IsLikelyFileDialog(root))
                return root;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 仅做轻量判断（沿父链比类名、看 <see cref="Win32.GetLastActivePopup"/>），供高频焦点钩过滤；
    /// 不做 <see cref="ClassifyFileDialog"/>，避免在全局焦点事件上整树枚举子控件。
    /// </summary>
    public static bool QuickMayBeUnderFileDialog(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return false;
        var h = hwnd;
        for (var i = 0; i < 14 && h != IntPtr.Zero; i++)
        {
            if (Win32.GetWindowClassName(h).Equals("#32770", StringComparison.Ordinal))
                return true;
            h = Win32.GetParent(h);
        }

        var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        if (root == IntPtr.Zero) return false;
        var pop = Win32.GetLastActivePopup(root);
        return pop != IntPtr.Zero
               && pop != root
               && Win32.IsWindow(pop)
               && Win32.GetWindowClassName(pop).Equals("#32770", StringComparison.Ordinal);
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

        // WPS 进程内的 #32770 也可能是原生消息框（保存确认、错误提示等）。
        // 此前无条件归入 WpsCustom 会导致自动跳转向消息框发送按键（Enter 直接点中「保存/确定」）。
        // 现在要求标题像文件对话框，或子控件形态像文件对话框（消息框只有 Static+Button）。
        // 注入不受影响：TryNavigateToFolder 仍按 #32770 类名单独尝试注入。
        if (className.Equals("#32770", StringComparison.Ordinal))
        {
            if (IsWpsFileDialogTitle(Win32.GetWindowText(hwnd))) return true;
            return HasFileDialogishChildControls(hwnd);
        }

        var title = Win32.GetWindowText(hwnd);
        if (IsWpsFileDialogTitle(title))
            return true;

        if (!IsWpsQtLikeWindowClass(className))
            return false;

        // Qt 窗口先做形态校验，排除加载框/气泡/进度/确认框等小型或辅助窗口
        //（Qt5QWindowToolSaveBits 为 tooltip/辅助窗口类）。
        if (!LooksLikeWpsQtFileDialogWindow(hwnd, className))
            return false;

        if (!string.IsNullOrEmpty(title)
            && (title.Contains("打开", StringComparison.Ordinal)
                || title.Contains("另存", StringComparison.Ordinal)))
            return true;

        // WPS Qt5 自绘对话框：GetWindowText 为空且 UIA 不可用。
        // WPS 主窗口/首页/新建页同样是空标题 Qt 窗口，需排除：
        // 文件对话框由主窗口弹出，有 owner；主窗口/首页无 owner。
        return string.IsNullOrEmpty(title)
            && Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero;
    }

    /// <summary>#32770 是否带文件对话框特征子控件（地址栏/文件名输入/Shell 视图）；纯 Static+Button 的消息框返回 false。</summary>
    private static bool HasFileDialogishChildControls(IntPtr hwnd)
    {
        var found = false;
        Win32.EnumChildWindows(hwnd, (child, _) =>
        {
            var cls = Win32.GetWindowClassName(child);
            if (cls.Contains("Edit", StringComparison.OrdinalIgnoreCase)
                || cls.Contains("ComboBox", StringComparison.OrdinalIgnoreCase)
                || cls.Contains("SysListView32", StringComparison.OrdinalIgnoreCase)
                || cls.Contains("DirectUIHWND", StringComparison.OrdinalIgnoreCase)
                || cls.Contains("SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase)
                || cls.Contains("ToolbarWindow32", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>WPS Qt 窗口形态是否像文件对话框：可见、已启用、非辅助窗口类、尺寸达到对话框级别。</summary>
    private static bool LooksLikeWpsQtFileDialogWindow(IntPtr hwnd, string className)
    {
        if (className.Contains("ToolSaveBits", StringComparison.Ordinal)) return false;
        if (!Win32.IsWindowVisible(hwnd) || !Win32.IsWindowEnabled(hwnd)) return false;
        if (!Win32.GetWindowRect(hwnd, out var r)) return false;
        return r.Right - r.Left >= 360 && r.Bottom - r.Top >= 240;
    }

    private static HashSet<string> CollectDescendantClassNames(IntPtr root)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int nodeCount = 0;
        const int maxNodes = 500;
        void Walk(IntPtr h)
        {
            if (++nodeCount > maxNodes) return;
            var cls = Win32.GetWindowClassName(h);
            set.Add(cls);
            // 提前终止：4 个目标类名全部找到后无需继续遍历
            if (set.Count >= 4
                && set.Any(c => c.Contains("DirectUIHWND", StringComparison.Ordinal))
                && set.Contains("SysListView32")
                && set.Contains("ToolbarWindow32")
                && set.Contains("Edit"))
                return;
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
        if (IsKnownNonFileDialogTitle(title)) return false;
        var t = title.ToLowerInvariant();
        return title.Contains("打开", StringComparison.Ordinal)
               || title.Contains("另存", StringComparison.Ordinal)
               || title.Contains("保存", StringComparison.Ordinal)
               || t.Contains("open file", StringComparison.Ordinal)
               || t.Contains("open folder", StringComparison.Ordinal)
               || t.Equals("open", StringComparison.Ordinal)
               || t.Contains("save as", StringComparison.Ordinal)
               || t.Equals("save", StringComparison.Ordinal)
               || t.Contains("browse", StringComparison.Ordinal);
    }

    private static bool IsKnownNonFileDialogTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var t = title.Trim().ToLowerInvariant();

        // Sublime Text 等编辑器的保存确认框是 #32770，标题含 save，但不是文件对话框。
        if (t.Contains("save changes", StringComparison.Ordinal)) return true;
        if (t.Contains("unsaved changes", StringComparison.Ordinal)) return true;
        if (t.Contains("do you want to save", StringComparison.Ordinal)) return true;
        if (t.Contains("confirm save", StringComparison.Ordinal)) return true;

        if (title.Contains("保存更改", StringComparison.Ordinal)) return true;
        if (title.Contains("是否保存", StringComparison.Ordinal)) return true;
        if (title.Contains("保存修改", StringComparison.Ordinal)) return true;
        if (title.Contains("未保存", StringComparison.Ordinal)) return true;
        return false;
    }

    public static bool TryReadCurrentFolder(IntPtr hwnd, out string folder) =>
        TryReadCurrentFolder(hwnd, out folder, relaxed: false);

    /// <param name="relaxed">为 true 时扩大 UIA 扫描并尝试面包屑解析，便于自定义对话框探测。</param>
    public static bool TryReadCurrentFolder(IntPtr hwnd, out string folder, bool relaxed)
    {
        folder = "";
        var kind = ClassifyFileDialog(hwnd);

        // DLL 注入仅适用于文件对话框（#32770），不适用于 Explorer 窗口
        var className = Win32.GetWindowClassName(hwnd);
        var isExplorer = className is "CabinetWClass" or "ExploreWClass";
        if (!isExplorer)
        {
            // 最可靠方式：DLL 注入读取 IShellBrowser（适用于标准对话框）
            if (ShellDialogDeepNavigate.TryReadCurrentFolderInject(hwnd, out var injectFolder))
            {
                folder = injectFolder;
                return true;
            }
        }

        // UIA 回退（用于非标准对话框、Explorer 窗口、或注入失败时）
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
                        if (TryNormalizeToExistingDirectory(v, out var norm) && best.Length == 0)
                            best = norm;
                    }
                }
                catch { }

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

    /// <summary>尝试对对话框的子窗口发送 CDM_GETFOLDERPATH。</summary>
    private static bool TryReadFolderViaCdmChildren(IntPtr parent, out string folder)
    {
        folder = "";
        try
        {
            var child = Win32.GetTopWindow(parent);
            if (child == IntPtr.Zero) return false;
            var queue = new Queue<IntPtr>();
            queue.Enqueue(child);
            var count = 0;
            while (queue.Count > 0 && count < 30)
            {
                var h = queue.Dequeue();
                count++;
                if (TryReadFolderViaCdmMessage(h, out var path))
                {
                    folder = path;
                    return true;
                }
                // 枚举子窗口
                var sub = Win32.GetTopWindow(h);
                while (sub != IntPtr.Zero)
                {
                    queue.Enqueue(sub);
                    sub = Win32.GetWindow(sub, 2 /* GW_HWNDNEXT */);
                }
            }
        }
        catch { }
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

    /// <summary>
    /// 通过向对话框发送 CDM_GETFOLDERPATH（WM_USER + 0x44C）获取当前文件夹路径。
    /// 这是 Win32 共用对话框的原生 API，适用于标准 GetOpenFileName / GetSaveFileName 对话框，
    /// 在 UIA ValuePattern 不可用时作为回退。
    /// </summary>
    private static bool TryReadFolderViaCdmMessage(IntPtr hwnd, out string folder)
    {
        folder = "";
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return false;
        try
        {
            const int bufChars = 1024;
            var hBuf = Marshal.AllocHGlobal(bufChars * 2);
            try
            {
                var result = Win32.SendMessageTimeout(
                    hwnd, Win32.CDM_GETFOLDERPATH,
                    new IntPtr(bufChars), hBuf,
                    Win32.SMTO_ABORTIFHUNG, 800, out _);
                if (result.ToInt64() > 0)
                {
                    var path = Marshal.PtrToStringUni(hBuf);
                    if (!string.IsNullOrEmpty(path) && TryNormalizeToExistingDirectory(path, out var norm))
                    {
                        folder = norm;
                        return true;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(hBuf); }
        }
        catch { }
        return false;
    }
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
    /// <param name="allowKeyboardFallback">为 false（自动同步等无人值守场景）时仅允许注入导航，
    /// 跳过一切模拟输入回退：焦点不在地址栏时 Enter 会误触「打开/保存」直接关掉用户正在操作的对话框。</param>
    public static bool TryNavigateToFolder(IntPtr dialogHwnd, string folderPath, bool allowShellInject = true,
        bool allowKeyboardFallback = true)
    {
        var path = NormalizeFolderPathForNavigation(folderPath);
        if (!Directory.Exists(path)) return false;

        if (TryReadCurrentFolder(dialogHwnd, out var currentFolder, relaxed: true)
            && PathsLooselyEqual(currentFolder, path))
        {
            ShellNavigateLog.Write("filejump",
                $"skip navigate already at target path=\"{path}\" hwnd=0x{dialogHwnd.ToInt64():X}");
            return true;
        }

        var kind = ClassifyFileDialog(dialogHwnd);
        var customRule = kind == FileDialogKind.None
            ? CustomFileDialogStore.FindMatchingRule(dialogHwnd)
            : null;

        if (kind == FileDialogKind.None && customRule != null)
            return TryNavigateCustomRule(dialogHwnd, path, allowShellInject, allowKeyboardFallback, customRule);

        if (kind == FileDialogKind.None) return false;

        // #32770 是标准 Shell 对话框，始终支持注入（包括 WPS 进程内弹出的浏览对话框）
        if (allowShellInject
            && Win32.GetWindowClassName(dialogHwnd).Equals("#32770", StringComparison.Ordinal))
        {
            if (ShellDialogDeepNavigate.TryBrowseObjectInject(dialogHwnd, path))
                return true;

            Thread.Sleep(80);
            if (TryReadCurrentFolder(dialogHwnd, out var afterInject, relaxed: true)
                && PathsLooselyEqual(afterInject, path))
            {
                ShellNavigateLog.Write("filejump",
                    $"shell inject reached target despite failure status path=\"{path}\" hwnd=0x{dialogHwnd.ToInt64():X}");
                return true;
            }
        }

        if (!allowKeyboardFallback)
        {
            ShellNavigateLog.Write("filejump",
                $"injection-only navigation gave up (simulated-input fallback disabled) path=\"{path}\" hwnd=0x{dialogHwnd.ToInt64():X}");
            return false;
        }

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
        bool allowKeyboardFallback,
        CustomFileDialogRule rule)
    {
        foreach (var s in BuildCustomStrategyOrder(rule))
        {
            if (TryApplyCustomDialogStrategy(s, dialogHwnd, path, allowShellInject, allowKeyboardFallback))
                return true;
        }

        return false;
    }

    private static bool TryApplyCustomDialogStrategy(
        string strategyId,
        IntPtr dialogHwnd,
        string normalizedExistingDir,
        bool allowShellInject,
        bool allowKeyboardFallback = true)
    {
        var id = strategyId.Trim().ToLowerInvariant();
        // 无人值守（自动同步）仅允许注入策略，其余策略都含模拟输入，可能误触对话框按钮。
        if (!allowKeyboardFallback && id != "shell_inject")
            return false;
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

        // 焦点安全闸：Ctrl+L 不一定把焦点送到地址栏（部分宿主忽略该快捷键）。
        // 焦点仍在文件列表/文件名框时，后续盲打 Ctrl+A+路径+Enter 会把 Enter 送进
        // 文件列表，直接点中「打开/保存」关掉用户正在操作的对话框。
        if (!IsFocusSuitableForAddressTyping(dialogHwnd, dialogTid))
        {
            ShellNavigateLog.Write("filejump",
                $"address-bar fallback aborted: focus not on address edit hwnd=0x{dialogHwnd.ToInt64():X}");
            return false;
        }

        // 优先 WM_SETTEXT 静默填充：逐字键入（SendUnicodeString）会触发地址栏自动完成
        // 下拉列表弹出（微信等宿主表现明显），消息填充则无任何可见的键入过程。
        if (TryFillFocusedAddressEditViaMessage(dialogTid, norm))
        {
            Thread.Sleep(40);
            SendEnter();
            return true;
        }

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

    /// <summary>焦点在真实 Win32 Edit 上时直接 WM_SETTEXT 填入路径并回读校验，全程无键入、无下拉。</summary>
    private static bool TryFillFocusedAddressEditViaMessage(uint dialogTid, string path)
    {
        try
        {
            var gti = new Win32.GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf<Win32.GUITHREADINFO>();
            if (!Win32.GetGUIThreadInfo(dialogTid, ref gti) || gti.hwndFocus == IntPtr.Zero)
                return false;

            var focus = gti.hwndFocus;
            if (!Win32.GetWindowClassName(focus).Equals("Edit", StringComparison.OrdinalIgnoreCase))
                return false;

            Win32.SendMessage(focus, Win32.WM_SETTEXT, IntPtr.Zero, path);

            var sb = new StringBuilder(1024);
            Win32.SendMessage(focus, Win32.WM_GETTEXT, (IntPtr)sb.Capacity, sb);
            return string.Equals(sb.ToString(), path, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判断对话框线程当前焦点是否位于可安全输入路径的地址栏：
    /// 焦点控件须属于该对话框、类名为可编辑控件（Edit/ComboBox/DirectUI 面包屑），
    /// 且位于对话框上半部——文件名输入框在底部，文件列表 DirectUI 在中部，
    /// 两者盲打 Enter 都会误触「打开/保存」。
    /// </summary>
    private static bool IsFocusSuitableForAddressTyping(IntPtr dialogHwnd, uint dialogTid)
    {
        try
        {
            var gti = new Win32.GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf<Win32.GUITHREADINFO>();
            if (!Win32.GetGUIThreadInfo(dialogTid, ref gti) || gti.hwndFocus == IntPtr.Zero)
                return false;

            var focus = gti.hwndFocus;
            if (Win32.GetAncestor(focus, Win32.GA_ROOT) != dialogHwnd)
                return false;

            var cls = Win32.GetWindowClassName(focus);
            var editable = cls.Contains("Edit", StringComparison.OrdinalIgnoreCase)
                || cls.Contains("ComboBox", StringComparison.OrdinalIgnoreCase)
                || cls.Contains("DirectUIHWND", StringComparison.Ordinal);
            if (!editable)
                return false;

            if (!Win32.GetWindowRect(focus, out var fr) || !Win32.GetWindowRect(dialogHwnd, out var dr))
                return false;
            var dlgHeight = dr.Bottom - dr.Top;
            return dlgHeight > 0 && fr.Top - dr.Top < dlgHeight * 0.55;
        }
        catch
        {
            return false;
        }
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
