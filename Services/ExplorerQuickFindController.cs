using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ClipboardManager;

/// <summary>
/// 资源管理器内打字触发 Everything 当前文件夹上下文检索。
/// 架构约束：WH_KEYBOARD_LL 回调 < 1ms，所有 COM/UIA/UI 操作走 BeginInvoke。
/// </summary>
public sealed class ExplorerQuickFindController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly AppSettings _settings;
    private IntPtr _hook;
    private static readonly Win32.LowLevelKeyboardProc Thunk = StaticHookProc;
    private static ExplorerQuickFindController? s_owner;

    // ---- 会话状态（仅 Dispatcher 线程读写） ----
    private ExplorerQuickFindWindow? _window;
    private bool _session;
    private IntPtr _sessionExplorerFrame;
    private string _sessionFolderPath = "";
    private string _sessionFolderDisplay = "";
    private string _typing = "";
    private int _queryGen;
    private CancellationTokenSource? _queryCts;

    // ---- 钩回调线程快速判断用（原子读写） ----
    private volatile bool _sessionActive;

    // ---- 异步初始化期间缓冲（BeginSessionAsync 完成前到达的字符） ----
    private readonly List<char> _pendingChars = new();

    public ExplorerQuickFindController(Dispatcher dispatcher, AppSettings settings)
    {
        _dispatcher = dispatcher;
        _settings = settings;
    }

    public void Start()
    {
        if (!_settings.ExplorerEverythingQuickFindEnabled) return;
        if (_hook != IntPtr.Zero) return;
        s_owner = this;
        _hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, Thunk, Win32.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            LogDiag($"SetWindowsHookEx(WH_KEYBOARD_LL) 失败，Win32={err}");
            TryAppendLog($"键盘钩安装失败 Win32={err}");
            s_owner = null;
        }
        else
        {
            LogDiag("SetWindowsHookEx(WH_KEYBOARD_LL) 成功");
            TryAppendLog("键盘钩已安装");
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        if (s_owner == this) s_owner = null;

        _queryCts?.Cancel();

        void Cleanup()
        {
            ResetSessionState();
            if (_window != null)
            {
                var w = _window;
                _window = null;
                w.UserClosed -= OnWindowClosed;
                w.ItemActivated -= OnItemActivated;
                try { w.Close(); } catch { }
            }
        }

        if (_dispatcher.CheckAccess())
            Cleanup();
        else
            _dispatcher.Invoke(Cleanup);
    }

    // ===================== 键盘钩（快速路径） =====================

    private static IntPtr StaticHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var o = s_owner;
        if (o == null) return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        return o.HookProc(nCode, wParam, lParam);
    }

    // 用于在分支方法中传递原始钩参数给 CallNextHookEx
    private int _hookNCode;
    private IntPtr _hookWParam;
    private IntPtr _hookLParam;

    /// <summary>
    /// 低级键盘钩回调。设计为 &lt;1ms：仅读 Win32 缓存状态，不做 COM/UIA/UI。
    /// 吞键后通过 BeginInvoke 异步处理。
    /// </summary>
    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !_settings.ExplorerEverythingQuickFindEnabled)
            return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);

        if (wParam != (IntPtr)Win32.WM_KEYDOWN)
            return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);

        var kb = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);

        if ((kb.flags & 0x10) != 0) // LLKHF_INJECTED (0x10, not 0x01 which is LLKHF_EXTENDED)
            return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);

        var fg = Win32.GetForegroundWindow();
        Win32.GetWindowThreadProcessId(fg, out var fgPid);
        if ((int)fgPid == Environment.ProcessId)
            return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);

        _hookNCode = nCode;
        _hookWParam = wParam;
        _hookLParam = lParam;

        if (_sessionActive)
            return HandleSessionKeyInHook(fg, kb);

        return TryStartSessionInHook(fg, kb);
    }

    /// <summary>已在会话中：快速决策是否吞键。会话期间全面接管键盘，防止 Explorer 处理任何按键。</summary>
    private IntPtr HandleSessionKeyInHook(IntPtr fg, Win32.KBDLLHOOKSTRUCT kb)
    {
        if (!IsStillTargetExplorer(fg))
        {
            _dispatcher.BeginInvoke(EndSession);
            return PassThrough();
        }

        if (IsModifierKey(kb.vkCode))
            return (IntPtr)1;

        var ctrl = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0;
        var alt = (Win32.GetAsyncKeyState(0x12) & 0x8000) != 0;
        var win = (Win32.GetAsyncKeyState(0x5B) & 0x8000) != 0
               || (Win32.GetAsyncKeyState(0x5C) & 0x8000) != 0;

        if (ctrl && !alt && !win && kb.vkCode >= 0x31 && kb.vkCode <= 0x39)
        {
            int idx = (int)(kb.vkCode - 0x31);
            _dispatcher.BeginInvoke(() => QuickSelectAndActivate(idx));
            return (IntPtr)1;
        }

        if (ctrl || alt || win)
        {
            _dispatcher.BeginInvoke(EndSession);
            return PassThrough();
        }

        switch (kb.vkCode)
        {
            case Win32.VK_ESCAPE:
            case Win32.VK_RETURN:
            case Win32.VK_UP:
            case Win32.VK_DOWN:
            case Win32.VK_LEFT:
            case Win32.VK_RIGHT:
            case Win32.VK_BACK:
            case Win32.VK_DELETE:
            case 0x21: // Page Up
            case 0x22: // Page Down
            case 0x24: // Home
            case 0x23: // End
                _dispatcher.BeginInvoke(() => ProcessSessionKey(kb.vkCode));
                return (IntPtr)1;
        }

        CaptureKeyState(kb, out var keyState);
        if (TryGetChar(kb.vkCode, kb.scanCode, keyState, out var ch) && ch >= ' ')
        {
            _dispatcher.BeginInvoke(() => AppendChar(ch));
            return (IntPtr)1;
        }

        return (IntPtr)1;
    }

    private IntPtr PassThrough()
        => Win32.CallNextHookEx(_hook, _hookNCode, _hookWParam, _hookLParam);

    /// <summary>尝试开始新会话：快速判断是否在资源管理器文件列表上下文。</summary>
    private IntPtr TryStartSessionInHook(IntPtr fg, Win32.KBDLLHOOKSTRUCT kb)
    {
        // 修饰键、导航键、功能键等不触发会话
        if (kb.vkCode is Win32.VK_ESCAPE or Win32.VK_RETURN or Win32.VK_BACK
            or Win32.VK_UP or Win32.VK_DOWN or Win32.VK_LEFT or Win32.VK_RIGHT
            or Win32.VK_DELETE
            or >= 0x70 and <= 0x87   // F1-F24
            or 0x09                   // Tab
            or 0x91                   // Scroll Lock
            or 0x90                   // Num Lock
            or 0x2C)                  // Print Screen
            return PassThrough();

        if (IsModifierKey(kb.vkCode))
            return PassThrough();

        var ctrl = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0;
        var alt = (Win32.GetAsyncKeyState(0x12) & 0x8000) != 0;
        var win = (Win32.GetAsyncKeyState(0x5B) & 0x8000) != 0
               || (Win32.GetAsyncKeyState(0x5C) & 0x8000) != 0;
        if (ctrl || alt || win)
            return PassThrough();

        // 快速类名检测（< 0.1ms），不做 COM/UIA
        var frame = FileManagerPathCollector.TryFindExplorerCabinetFrame(fg);
        if (frame == IntPtr.Zero)
            return PassThrough();

        if (!QuickCheckFocusNotEditBox(frame))
            return PassThrough();

        CaptureKeyState(kb, out var keyState);
        if (!TryGetChar(kb.vkCode, kb.scanCode, keyState, out var ch) || ch < ' ')
            return PassThrough();

        // 通过快速检测：吞键，异步启动会话
        _sessionActive = true;
        _sessionExplorerFrame = frame;
        _dispatcher.BeginInvoke(() => BeginSessionAsync(frame, ch));
        return (IntPtr)1;
    }

    private static bool IsModifierKey(uint vk) => vk is
        0x10 or 0x11 or 0x12 or 0x14           // Shift, Ctrl, Alt, CapsLock
        or 0xA0 or 0xA1 or 0xA2 or 0xA3        // L/R Shift, L/R Ctrl
        or 0xA4 or 0xA5                          // L/R Alt
        or 0x5B or 0x5C;                         // L/R Win

    // ===================== Dispatcher 线程处理 =====================

    /// <summary>异步启动会话：获取文件夹路径（可能耗时），失败则回退。</summary>
    private async void BeginSessionAsync(IntPtr frame, char firstChar)
    {
        string? folder = null;
        try
        {
            folder = await Task.Run(() => FileManagerPathCollector.TryGetExplorerFolderIfForeground(frame));
        }
        catch { /* ignore */ }

        if (string.IsNullOrEmpty(folder))
        {
            LogDiag($"异步取文件夹路径失败 frame=0x{frame:X}");
            _sessionActive = false;
            _sessionExplorerFrame = IntPtr.Zero;
            _pendingChars.Clear();
            return;
        }

        _session = true;
        _sessionExplorerFrame = frame;
        try { _sessionFolderPath = Path.GetFullPath(folder.Trim()); }
        catch { _sessionFolderPath = folder; }
        _sessionFolderDisplay = _sessionFolderPath;
        _typing = firstChar.ToString();

        // 消费异步初始化期间缓冲的字符
        if (_pendingChars.Count > 0)
        {
            foreach (var c in _pendingChars)
                _typing += c;
            _pendingChars.Clear();
        }

        EnsureWindow();
        _window!.SetQueryText(_sessionFolderDisplay, _typing);
        _window!.PositionNearExplorer(_sessionExplorerFrame);
        ScheduleQuery();
        LogDiag($"会话已启动 folder={_sessionFolderPath} typing={_typing}");
    }

    private void ProcessSessionKey(uint vk)
    {
        if (!_session)
        {
            if (_sessionActive && vk is Win32.VK_ESCAPE or Win32.VK_BACK)
            {
                _sessionActive = false;
                _sessionExplorerFrame = IntPtr.Zero;
                _pendingChars.Clear();
            }
            return;
        }

        switch (vk)
        {
            case Win32.VK_ESCAPE:
                EndSession();
                return;

            case Win32.VK_RETURN:
                var path = _window?.GetSelectedFullPath();
                var frame = _sessionExplorerFrame;
                EndSession();
                if (!string.IsNullOrEmpty(path))
                    _ = Task.Run(() => NavigateAndSelect(frame, path!));
                return;

            case Win32.VK_UP:
                _window?.MoveSelection(-1);
                return;

            case Win32.VK_DOWN:
                _window?.MoveSelection(1);
                return;

            case Win32.VK_LEFT:
                _window?.MoveSelectionPage(-1);
                return;

            case Win32.VK_RIGHT:
                _window?.MoveSelectionPage(1);
                return;

            case 0x21: // Page Up
                _window?.MoveSelectionPage(-1);
                return;

            case 0x22: // Page Down
                _window?.MoveSelectionPage(1);
                return;

            case 0x24: // Home
                _window?.MoveSelectionToEnd(false);
                return;

            case 0x23: // End
                _window?.MoveSelectionToEnd(true);
                return;

            case Win32.VK_DELETE:
                return;

            case Win32.VK_BACK:
                if (_typing.Length > 0)
                {
                    _typing = _typing[..^1];
                    if (_typing.Length == 0)
                    {
                        EndSession();
                        return;
                    }
                    _window?.SetQueryText(_sessionFolderDisplay, _typing);
                    ScheduleQuery();
                }
                else
                {
                    EndSession();
                }
                return;
        }
    }

    private void AppendChar(char ch)
    {
        if (!_session)
        {
            // 异步初始化期间到达的字符先缓冲
            if (_sessionActive)
                _pendingChars.Add(ch);
            return;
        }
        _typing += ch;
        _window?.SetQueryText(_sessionFolderDisplay, _typing);
        ScheduleQuery();
    }

    // ===================== 会话管理 =====================

    private void EnsureWindow()
    {
        if (_window != null)
        {
            if (!_window.IsVisible)
                _window.Show();
            return;
        }
        _window = new ExplorerQuickFindWindow();
        _window.UserClosed += OnWindowClosed;
        _window.ItemActivated += OnItemActivated;
        _window.Show();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is ExplorerQuickFindWindow w)
        {
            w.UserClosed -= OnWindowClosed;
            w.ItemActivated -= OnItemActivated;
            if (_window == w)
                _window = null;
        }
        ResetSessionState();
    }

    private void OnItemActivated(string fullPath)
    {
        var frame = _sessionExplorerFrame;
        EndSession();
        if (!string.IsNullOrEmpty(fullPath))
            _ = Task.Run(() => NavigateAndSelect(frame, fullPath));
    }

    private void QuickSelectAndActivate(int index)
    {
        if (!_session || _window == null) return;
        var path = _window.GetFullPathByIndex(index);
        if (string.IsNullOrEmpty(path)) return;
        var frame = _sessionExplorerFrame;
        EndSession();
        _ = Task.Run(() => NavigateAndSelect(frame, path!));
    }

    private void EndSession()
    {
        ResetSessionState();
        _window?.Hide();
    }

    private void ResetSessionState()
    {
        _session = false;
        _sessionActive = false;
        _sessionExplorerFrame = IntPtr.Zero;
        _sessionFolderPath = "";
        _sessionFolderDisplay = "";
        _typing = "";
        _pendingChars.Clear();
        _queryGen++;
        _queryCts?.Cancel();
        _queryCts = null;
    }

    // ===================== Everything 查询 =====================

    private void ScheduleQuery()
    {
        _queryGen++;
        var gen = _queryGen;
        _queryCts?.Cancel();
        _queryCts = new CancellationTokenSource();
        var tok = _queryCts.Token;
        var folder = _sessionFolderPath;
        var typing = _typing;
        var maxResults = _settings.ExplorerEverythingQuickFindMaxResults;

        _ = Task.Run(() =>
        {
            if (tok.WaitHandle.WaitOne(120)) return;
            if (gen != _queryGen) return;

            List<QuickFindResultItem>? items = null;
            string? status = null;

            var search = BuildEverythingSearch(folder, typing);
            var rawPaths = new List<string>();
            var ok = EverythingIpc.TryQueryFullPaths(search, maxResults, rawPaths, out var err);
            if (tok.IsCancellationRequested || gen != _queryGen) return;

            if (!ok)
                status = FormatError(err);
            else if (rawPaths.Count == 0)
                status = "无匹配项";
            else
                items = QuickFindResultItem.FromFullPaths(rawPaths, folder);

            if (tok.IsCancellationRequested || gen != _queryGen) return;

            _dispatcher.BeginInvoke(() =>
            {
                if (gen != _queryGen || !_session || _window == null) return;
                _window.SetResults(items ?? new List<QuickFindResultItem>(), status);
                _window.SetQueryText(_sessionFolderDisplay, _typing);
            }, DispatcherPriority.Background);
        }, tok);
    }

    private static string TryMakeRelative(string fullPath, string basePath)
    {
        if (string.IsNullOrEmpty(basePath)) return fullPath;
        var b = basePath.TrimEnd('\\', '/');
        if (fullPath.StartsWith(b, StringComparison.OrdinalIgnoreCase) && fullPath.Length > b.Length)
            return fullPath[(b.Length + 1)..];
        return fullPath;
    }

    private static string BuildEverythingSearch(string folder, string typing)
    {
        var f = folder.Trim().TrimEnd('\\');
        if (f.Length == 0) return typing;
        try { f = Path.GetFullPath(f); } catch { /* keep as-is */ }
        if (!f.EndsWith('\\')) f += "\\";

        var pathToken = f.Contains(' ') ? "\"" + f + "\"" : f;
        return string.IsNullOrWhiteSpace(typing) ? f.TrimEnd('\\') : pathToken + " " + typing.Trim();
    }

    // ===================== 就地导航 + 选中 =====================

    /// <summary>
    /// 在资源管理器中就地导航到目标文件所在文件夹并选中该文件。
    /// Shell COM late-binding → fallback SHOpenFolderAndSelectItems。
    /// </summary>
    private static void NavigateAndSelect(IntPtr explorerFrame, string targetFullPath)
    {
        try
        {
            if (TryNavigateViaShellCom(explorerFrame, targetFullPath))
                return;
        }
        catch { /* fallback */ }

        try
        {
            RevealViaShellApi(targetFullPath);
        }
        catch { /* ignore */ }
    }

    /// <summary>通过 Shell.Application.Windows 后期绑定 COM 就地导航并选中。</summary>
    private static bool TryNavigateViaShellCom(IntPtr explorerFrame, string targetFullPath)
    {
        var targetDir = Path.GetDirectoryName(targetFullPath);
        var targetName = Path.GetFileName(targetFullPath);
        if (string.IsNullOrEmpty(targetDir) || string.IsNullOrEmpty(targetName))
            return false;

        Type? t = Type.GetTypeFromProgID("Shell.Application");
        if (t == null) return false;

        object? shell = null;
        object? windows = null;
        try
        {
            shell = Activator.CreateInstance(t);
            if (shell == null) return false;

            windows = shell.GetType().InvokeMember("Windows",
                BindingFlags.InvokeMethod, null, shell, null);
            if (windows == null)
            {
                try
                {
                    windows = shell.GetType().InvokeMember("Windows",
                        BindingFlags.GetProperty, null, shell, null);
                }
                catch { return false; }
            }
            if (windows == null) return false;

            var wt = windows.GetType();
            var count = Convert.ToInt32(
                wt.InvokeMember("Count", BindingFlags.GetProperty, null, windows, null));

            object? matchedWin = null;
            try
            {
                for (var i = 0; i < count; i++)
                {
                    object? win = null;
                    try
                    {
                        win = wt.InvokeMember("Item", BindingFlags.InvokeMethod, null, windows, new object[] { i });
                        if (win == null) continue;

                        var hwndObj = TryGetComHwnd(win);
                        if (!ExplorerFrameMatches(explorerFrame, hwndObj))
                        {
                            Marshal.ReleaseComObject(win);
                            continue;
                        }

                        matchedWin = win;
                        break;
                    }
                    catch
                    {
                        if (win != null) Marshal.ReleaseComObject(win);
                    }
                }

                if (matchedWin == null) return false;

                var currentPath = ReadShellWindowPath(matchedWin);
                var needNavigate = !string.Equals(
                    NormPath(currentPath), NormPath(targetDir), StringComparison.OrdinalIgnoreCase);

                if (needNavigate)
                {
                    matchedWin.GetType().InvokeMember("Navigate",
                        BindingFlags.InvokeMethod, null, matchedWin, new object[] { targetDir });
                    Thread.Sleep(400);
                }

                TrySelectItem(matchedWin, targetName);
                return true;
            }
            finally
            {
                if (matchedWin != null) Marshal.ReleaseComObject(matchedWin);
            }
        }
        catch { return false; }
        finally
        {
            if (windows != null) Marshal.ReleaseComObject(windows);
            if (shell != null) Marshal.ReleaseComObject(shell);
        }
    }

    private static void TrySelectItem(object shellWindow, string fileName)
    {
        const int SVSI_SELECT = 1;
        const int SVSI_DESELECTOTHERS = 4;
        const int SVSI_ENSUREVISIBLE = 8;
        const int SVSI_FOCUSED = 16;
        const int flags = SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_ENSUREVISIBLE | SVSI_FOCUSED;

        object? doc = null;
        object? folder = null;
        object? item = null;
        try
        {
            doc = shellWindow.GetType().InvokeMember("Document",
                BindingFlags.GetProperty, null, shellWindow, null);
            if (doc == null) return;

            folder = doc.GetType().InvokeMember("Folder",
                BindingFlags.GetProperty, null, doc, null);
            if (folder == null) return;

            item = folder.GetType().InvokeMember("ParseName",
                BindingFlags.InvokeMethod, null, folder, new object[] { fileName });
            if (item == null) return;

            doc.GetType().InvokeMember("SelectItem",
                BindingFlags.InvokeMethod, null, doc, new object[] { item, flags });
        }
        catch { /* ignore */ }
        finally
        {
            if (item != null) try { Marshal.ReleaseComObject(item); } catch { }
            if (folder != null) try { Marshal.ReleaseComObject(folder); } catch { }
            if (doc != null) try { Marshal.ReleaseComObject(doc); } catch { }
        }
    }

    private static IntPtr TryGetComHwnd(object win)
    {
        foreach (var name in new[] { "HWND", "Hwnd" })
        {
            try
            {
                var v = win.GetType().InvokeMember(name,
                    BindingFlags.GetProperty | BindingFlags.IgnoreCase, null, win, null);
                if (v == null) continue;
                if (v is IntPtr p) return p;
                return new IntPtr(Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture));
            }
            catch { /* ignore */ }
        }
        return IntPtr.Zero;
    }

    private static bool ExplorerFrameMatches(IntPtr explorerFrame, IntPtr comHwnd)
    {
        if (explorerFrame == IntPtr.Zero || comHwnd == IntPtr.Zero) return false;
        if (explorerFrame == comHwnd) return true;
        if (Win32.IsChild(explorerFrame, comHwnd)) return true;
        if (Win32.GetAncestor(comHwnd, Win32.GA_ROOT) == explorerFrame) return true;
        for (var w = comHwnd; w != IntPtr.Zero; w = Win32.GetParent(w))
            if (w == explorerFrame) return true;
        return false;
    }

    private static string? ReadShellWindowPath(object win)
    {
        object? doc = null, folder = null, self = null;
        try
        {
            doc = win.GetType().InvokeMember("Document", BindingFlags.GetProperty, null, win, null);
            if (doc == null) return null;
            folder = doc.GetType().InvokeMember("Folder", BindingFlags.GetProperty, null, doc, null);
            if (folder == null) return null;
            self = folder.GetType().InvokeMember("Self", BindingFlags.GetProperty, null, folder, null);
            if (self == null) return null;
            return self.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, self, null)?.ToString();
        }
        catch { return null; }
        finally
        {
            if (self != null) Marshal.ReleaseComObject(self);
            if (folder != null) Marshal.ReleaseComObject(folder);
            if (doc != null) Marshal.ReleaseComObject(doc);
        }
    }

    private static string NormPath(string? p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        try { return Path.GetFullPath(p.Trim().TrimEnd('\\', '/')); }
        catch { return p.Trim().TrimEnd('\\', '/'); }
    }

    /// <summary>SHOpenFolderAndSelectItems fallback：可复用已开窗口。</summary>
    private static void RevealViaShellApi(string fullPath)
    {
        var pidl = Win32.ILCreateFromPathW(fullPath);
        if (pidl == IntPtr.Zero) return;
        try
        {
            Win32.SHOpenFolderAndSelectItems(pidl, 0, null, 0);
        }
        finally
        {
            Win32.ILFree(pidl);
        }
    }

    // ===================== 辅助：快速上下文检测 =====================

    /// <summary>检测焦点是否在 Edit/ComboBox 上（地址栏/搜索框/重命名）。仅用 Win32 API，&lt;0.1ms。</summary>
    private static bool QuickCheckFocusNotEditBox(IntPtr explorerFrame)
    {
        var tid = Win32.GetWindowThreadProcessId(explorerFrame, out _);
        if (tid == 0) return false;

        var gti = new Win32.GUITHREADINFO { cbSize = Marshal.SizeOf<Win32.GUITHREADINFO>() };
        if (!Win32.GetGUIThreadInfo(tid, ref gti))
            return true; // 取不到就放行，让后续异步检测做最终判断

        var focus = gti.hwndFocus;
        if (focus == IntPtr.Zero) return true; // Win11 DirectUI 下偶发

        var cls = Win32.GetWindowClassName(focus);
        if (cls.Equals("Edit", StringComparison.Ordinal)) return false;
        if (cls.Contains("ComboBox", StringComparison.Ordinal)) return false;
        if (cls.Contains("RichEdit", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>会话中快速判断前台是否仍是目标资源管理器。</summary>
    private bool IsStillTargetExplorer(IntPtr fg)
    {
        if (_sessionExplorerFrame == IntPtr.Zero) return false;
        if (fg == _sessionExplorerFrame) return true;
        var frame = FileManagerPathCollector.TryFindExplorerCabinetFrame(fg);
        return frame == _sessionExplorerFrame;
    }

    // ===================== 键盘状态工具 =====================

    private static void CaptureKeyState(Win32.KBDLLHOOKSTRUCT kb, out byte[] keyState)
    {
        keyState = new byte[256];
        Win32.GetKeyboardState(keyState);
    }

    private static bool TryGetChar(uint vk, uint scan, byte[] keyState, out char ch)
    {
        ch = '\0';
        var sb = new StringBuilder(8);
        var n = Win32.ToUnicode(vk, scan, keyState, sb, sb.Capacity, 0);
        if (n != 1 || sb.Length <= 0) return false;
        ch = sb[0];
        return !char.IsControl(ch);
    }

    // ===================== 错误格式化 =====================

    private static string FormatError(int err)
    {
        if (err == EverythingIpc.LastErrorDllNotFound)
            return "未找到 Everything64.dll。请把 Everything 安装目录下的 DLL 复制到 ClipboardX 同目录，或将该目录加入 PATH。";
        if (err == EverythingIpc.LastErrorInterop)
            return "调用 Everything 接口失败（体系结构或版本不匹配）。";
        return $"Everything 查询失败（错误码 {err}）。请确认 Everything 已运行且权限一致。";
    }

    // ===================== 诊断日志 =====================

    private static bool DiagEnabled =>
#if DEBUG
        true
#else
        string.Equals(Environment.GetEnvironmentVariable("CLIPBOARDX_DEBUG_EXPLORER_QF"), "1", StringComparison.Ordinal)
#endif
        ;

    private static void LogDiag(string message)
    {
        if (!DiagEnabled) return;
        var line = "[ExplorerQF] " + message;
        try
        {
            System.Diagnostics.Trace.WriteLine(line);
#if DEBUG
            System.Diagnostics.Debug.WriteLine(line);
#endif
            Win32.OutputDebugString(line + "\n");
        }
        catch { /* ignore */ }
    }

    private static void TryAppendLog(string detail)
    {
        try
        {
            File.AppendAllText(AppPaths.ExplorerQuickFindLogFile,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {detail}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }
}
