using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        {
            // 弹窗自身获得前台时，仍需拦截 Escape 关闭会话
            if (_sessionActive && kb.vkCode == Win32.VK_ESCAPE)
            {
                _dispatcher.BeginInvoke(EndSession);
                return (IntPtr)1;
            }
            return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        _hookNCode = nCode;
        _hookWParam = wParam;
        _hookLParam = lParam;

        // F2（重命名）按下后记录时间与前台：Explorer 把焦点移到重命名编辑框有延迟，
        // 用户立即输入首字符时焦点可能仍在文件列表，误判为“输入即搜索”启动会话后
        // 会吞掉全部按键，重命名无法进行。短时间内不启动会话。
        if (kb.vkCode == 0x71)
        {
            _lastRenameF2Tick = Environment.TickCount;
            _lastRenameF2Foreground = fg;
        }

        if (_sessionActive)
            return HandleSessionKeyInHook(fg, kb);

        return TryStartSessionInHook(fg, kb);
    }

    private int _lastRenameF2Tick = int.MinValue;
    private IntPtr _lastRenameF2Foreground;

    /// <summary>已在会话中：快速决策是否吞键。会话期间全面接管键盘，防止 Explorer 处理任何按键。</summary>
    private IntPtr HandleSessionKeyInHook(IntPtr fg, Win32.KBDLLHOOKSTRUCT kb)
    {
        if (!IsStillTargetExplorer(fg))
        {
            _dispatcher.BeginInvoke(EndSession);
            return PassThrough();
        }

        // 焦点已落入可编辑控件（F2 重命名框迟到就绪、或用户点击了地址栏/搜索框）：
        // 立即结束会话并把按键还给 Explorer，避免吞掉重命名/地址栏输入。
        if (!QuickCheckFocusNotEditBox(_sessionExplorerFrame))
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

        var cls = Win32.GetWindowClassName(fg);
        var isDesktop = cls.Equals("Progman", StringComparison.OrdinalIgnoreCase)
                     || cls.Equals("WorkerW", StringComparison.OrdinalIgnoreCase);

        IntPtr frame;
        if (isDesktop)
        {
            frame = fg;
            // 桌面重命名（F2）同样会弹 Edit 编辑框，此前桌面路径未做焦点检查，
            // 导致重命名输入被吞、误触发全盘搜索。
            if (!QuickCheckFocusNotEditBox(frame))
                return PassThrough();
        }
        else
        {
            frame = FileManagerPathCollector.TryFindExplorerCabinetFrame(fg);
            if (frame == IntPtr.Zero)
                return PassThrough();

            if (!QuickCheckFocusNotEditBox(frame))
                return PassThrough();
        }

        // F2 刚在同一个资源管理器/桌面窗口按下（重命名框可能尚未获得焦点），
        // 抑制会话启动，把字符留给重命名编辑框。
        var sinceF2 = unchecked(Environment.TickCount - _lastRenameF2Tick);
        if (sinceF2 >= 0 && sinceF2 < 1500
            && (_lastRenameF2Foreground == fg || _lastRenameF2Foreground == frame))
            return PassThrough();

        CaptureKeyState(kb, out var keyState);
        if (!TryGetChar(kb.vkCode, kb.scanCode, keyState, out var ch) || ch < ' ')
            return PassThrough();

        // 通过快速检测：吞键，异步启动会话
        _sessionActive = true;
        _sessionExplorerFrame = frame;
        _dispatcher.BeginInvoke(() => BeginSessionAsync(frame, ch, isDesktop));
        return (IntPtr)1;
    }

    private static bool IsModifierKey(uint vk) => vk is
        0x10 or 0x11 or 0x12 or 0x14           // Shift, Ctrl, Alt, CapsLock
        or 0xA0 or 0xA1 or 0xA2 or 0xA3        // L/R Shift, L/R Ctrl
        or 0xA4 or 0xA5                          // L/R Alt
        or 0x5B or 0x5C;                         // L/R Win

    // ===================== Dispatcher 线程处理 =====================

    /// <summary>异步启动会话：获取文件夹路径（可能耗时），失败则回退。</summary>
    private async void BeginSessionAsync(IntPtr frame, char firstChar, bool isDesktop = false)
    {
        string? folder = null;

        if (isDesktop)
        {
            folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            LogDiag($"桌面场景，使用桌面目录 folder={folder}");
        }
        else
        {
            try
            {
                folder = await Task.Run(() => FileManagerPathCollector.TryGetExplorerFolderIfForeground(frame));
            }
            catch { /* ignore */ }
        }

        _session = true;
        _sessionExplorerFrame = frame;
        if (string.IsNullOrEmpty(folder))
        {
            _sessionFolderPath = "";
            _sessionFolderDisplay = "全盘";
            LogDiag($"非常规文件夹，全盘搜索 frame=0x{frame:X}");
        }
        else
        {
            try { _sessionFolderPath = Path.GetFullPath(folder.Trim()); }
            catch { _sessionFolderPath = folder; }
            _sessionFolderDisplay = _sessionFolderPath;
        }
        _typing = firstChar.ToString();

        // 消费异步初始化期间缓冲的字符
        if (_pendingChars.Count > 0)
        {
            foreach (var c in _pendingChars)
                _typing += c;
            _pendingChars.Clear();
        }

        EnsureWindow();
        _window!.SetQueryText(_sessionFolderDisplay, _typing, TypingHighlightNeedle(_typing));
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
                    _ = Task.Run(() => NavigateAndSelect(frame, path!, _settings.ExplorerQuickFindOpenMode));
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
                    _window?.SetQueryText(_sessionFolderDisplay, _typing, TypingHighlightNeedle(_typing));
                    ScheduleQuery();
                }
                else
                {
                    EndSession();
                }
                return;
        }
    }

    private static string? TypingHighlightNeedle(string? typing) =>
        string.IsNullOrWhiteSpace(typing) ? null : typing.Trim();

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
        _window?.SetQueryText(_sessionFolderDisplay, _typing, TypingHighlightNeedle(_typing));
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
        _window.ApplySettings(_settings);
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
            _ = Task.Run(() => NavigateAndSelect(frame, fullPath, _settings.ExplorerQuickFindOpenMode));
    }

    private void QuickSelectAndActivate(int index)
    {
        if (!_session || _window == null) return;
        var path = _window.GetFullPathByIndex(index);
        if (string.IsNullOrEmpty(path)) return;
        var frame = _sessionExplorerFrame;
        EndSession();
        _ = Task.Run(() => NavigateAndSelect(frame, path!, _settings.ExplorerQuickFindOpenMode));
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
        var typingTrim = typing.Trim();
        var hasTyping = typingTrim.Length > 0;

        // 早期 debounce 写到 120ms，叠加上 LL 钩 → BeginInvoke 的自然延迟，每次按键都要等
        // 100ms+ 才出结果，连按时表现为「打了字面板像没反应」。
        // Everything IPC 单次查询通常 <5ms，把节流压到 30ms 已足够合并连按，又不会感知卡顿。
        _ = Task.Run(() =>
        {
            if (tok.WaitHandle.WaitOne(30)) return;
            if (gen != _queryGen) return;

            void PostUi(List<QuickFindResultItem> items, string? status, string? countLine = null)
            {
                if (tok.IsCancellationRequested || gen != _queryGen) return;
                var hl = hasTyping ? typingTrim : null;
                _dispatcher.BeginInvoke(() =>
                {
                    if (gen != _queryGen || !_session || _window == null) return;
                    _window.SetResults(items, status, countLine, hl);
                    _window.SetQueryText(_sessionFolderDisplay, _typing, hl);
                }, DispatcherPriority.Background);
            }

            // ---------- 阶段 1：parent: 仅当前文件夹一层 + 关键词，立刻回显 ----------
            var parentSearch = BuildEverythingParentScopedSearch(folder, typing);
            var parentPaths = new List<string>();
            var okParent = false;
            var errParent = 0;
            EverythingIpc.InvokeExclusive(() =>
            {
                okParent = EverythingIpc.TryQueryFullPathsCore(parentSearch, maxResults, parentPaths, out errParent);
            });

            if (tok.IsCancellationRequested || gen != _queryGen) return;

            if (!hasTyping)
            {
                if (!okParent)
                {
                    PostUi([], FormatError(errParent));
                    return;
                }

                var listOnly = QuickFindResultItem.FromFullPaths(parentPaths, folder);
                PostUi(listOnly, listOnly.Count == 0 ? "无匹配项" : null);
                return;
            }

            if (!okParent)
                PostUi([], $"{FormatError(errParent)} · 正在检索当前路径树下…");
            else
            {
                var parentOnly = QuickFindResultItem.FromFullPaths(parentPaths, folder);
                var hint1 = parentOnly.Count == 0
                    ? "当前文件夹无直接匹配 · 正在检索当前路径树下…"
                    : "正在检索当前路径树下…";
                var count1 = parentOnly.Count > 0 ? $"{parentOnly.Count} 项（当前文件夹）" : "";
                PostUi(parentOnly, hint1, string.IsNullOrEmpty(count1) ? null : count1);
            }

            if (tok.IsCancellationRequested || gen != _queryGen) return;

            // ---------- 阶段 2：path: 当前路径树下任意深度 + 关键词，合并后回显 ----------
            var pathSearch = BuildEverythingPathSubtreeScopedSearch(folder, typing);
            var pathPaths = new List<string>();
            var okPath = false;
            var errPath = 0;
            EverythingIpc.InvokeExclusive(() =>
            {
                okPath = EverythingIpc.TryQueryFullPathsCore(pathSearch, maxResults, pathPaths, out errPath);
            });

            if (tok.IsCancellationRequested || gen != _queryGen) return;

            var localMerged = MergePathListsPreferFirst(parentPaths, pathPaths, maxResults);
            var okLocal = okParent || okPath;
            var localItems = okLocal
                ? QuickFindResultItem.FromFullPaths(localMerged, folder)
                : [];

            if (!okLocal)
            {
                PostUi([], $"{FormatError(errParent)} · {FormatError(errPath)} · 正在检索全盘…");
            }
            else
            {
                var hint2 = localItems.Count == 0
                    ? "当前路径树下无匹配 · 正在检索全盘…"
                    : "正在补充全盘结果…";
                var count2 = localItems.Count > 0 ? $"{localItems.Count} 项（当前路径）" : "";
                PostUi(localItems, hint2, string.IsNullOrEmpty(count2) ? null : count2);
            }

            if (tok.IsCancellationRequested || gen != _queryGen) return;

            // ---------- 阶段 3：全盘关键词，与本地合并 ----------
            var globalPaths = new List<string>();
            var okGlobal = false;
            var errGlobal = 0;
            EverythingIpc.InvokeExclusive(() =>
            {
                okGlobal = EverythingIpc.TryQueryFullPathsCore(typingTrim, maxResults, globalPaths, out errGlobal);
            });

            if (tok.IsCancellationRequested || gen != _queryGen) return;

            List<QuickFindResultItem> items;
            string? status = null;
            string? countLine = null;

            if (!okLocal && !okGlobal)
            {
                items = [];
                status = $"parent: {FormatError(errParent)} · path: {FormatError(errPath)}";
            }
            else if (!okLocal && okGlobal)
            {
                items = QuickFindResultItem.FromScopedAndGlobalLists(Array.Empty<string>(), globalPaths, folder, maxResults);
                status = "当前路径检索失败，仅显示全盘";
                countLine = items.Count > 0 ? $"{items.Count} 项（全盘）" : "";
            }
            else if (okLocal && !okGlobal)
            {
                items = QuickFindResultItem.FromFullPaths(localMerged, folder);
                if (items.Count == 0)
                    status = "无匹配项";
                else
                    status = $"全盘检索不可用（{FormatError(errGlobal)}）";
            }
            else
            {
                items = QuickFindResultItem.FromScopedAndGlobalLists(localMerged, globalPaths, folder, maxResults);
                if (items.Count == 0)
                    status = "无匹配项";
                else
                {
                    var nGlob = items.Count(x => x.IsGlobalMatch);
                    var nScoped = items.Count - nGlob;
                    countLine = nGlob > 0
                        ? $"共 {items.Count} 项（当前路径 {nScoped} · 全盘 {nGlob}）"
                        : $"{items.Count} 项";
                }
            }

            PostUi(items, status, countLine);
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

    /// <summary>
    /// 规范化当前文件夹路径，供 Everything 的 <c>parent:</c> / <c>path:</c> 使用。
    ///
    /// 历史坑 1：在 <see cref="Path.GetFullPath"/> **之前**把 <c>C:\</c> 收成 <c>C:</c>，
    /// Windows 上 <c>GetFullPath("C:")</c> 会变成「该盘当前工作目录」而不是根目录，
    /// <c>parent:C:</c> 与 Everything 里正确的 <c>parent:C:\</c> 不一致 → 根下搜索 0 条。
    ///
    /// 历史坑 2：引号内路径末尾反斜杠 <c>\"</c> 会被 Everything 当成转义引号；非根路径
    /// 仍去掉末尾分隔符；盘符根则固定为 <c>X:\</c>（无引号、无歧义）。
    /// </summary>
    private static string NormalizeFolderForEverything(string folder)
    {
        var t = folder.Trim();
        if (t.Length == 0) return "";
        try
        {
            var full = Path.GetFullPath(t);
            if (full.Length >= 3 && full[1] == ':' && (full[2] == '\\' || full[2] == '/'))
            {
                var rest = full.Length > 3 ? full.Substring(3).TrimStart('\\', '/') : "";
                if (rest.Length == 0)
                    return $"{char.ToUpperInvariant(full[0])}:\\";
            }

            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return t;
        }
    }

    /// <summary>当前文件夹<strong>一层</strong>子项 + 可选关键词。<c>parent:</c></summary>
    private static string BuildEverythingParentScopedSearch(string folder, string typing)
    {
        var f = NormalizeFolderForEverything(folder);
        if (f.Length == 0) return typing.Trim();
        var token = "parent:" + QuotePathForEverythingToken(f);
        if (string.IsNullOrWhiteSpace(typing)) return token;
        return token + " " + typing.Trim();
    }

    /// <summary>当前路径<strong>树下</strong>任意深度 + 可选关键词。<c>path:</c> 匹配完整路径前缀。</summary>
    private static string BuildEverythingPathSubtreeScopedSearch(string folder, string typing)
    {
        var f = NormalizeFolderForEverything(folder);
        if (f.Length == 0) return typing.Trim();
        var token = "path:" + QuotePathForEverythingToken(f);
        if (string.IsNullOrWhiteSpace(typing)) return token;
        return token + " " + typing.Trim();
    }

    /// <summary>路径含空格时用双引号包住；末尾不应有反斜杠（避免 \" 被解释为转义引号）。</summary>
    private static string QuotePathForEverythingToken(string path)
    {
        if (path.IndexOf(' ', StringComparison.Ordinal) >= 0)
            return "\"" + path + "\"";
        return path;
    }

    /// <summary>先保留 <paramref name="first"/> 顺序，再追加 <paramref name="second"/> 中未出现的路径，最多 <paramref name="maxCount"/> 条。</summary>
    private static List<string> MergePathListsPreferFirst(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second,
        int maxCount)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>(Math.Min(maxCount, first.Count + second.Count));
        foreach (var p in first)
        {
            if (seen.Add(p)) merged.Add(p);
            if (merged.Count >= maxCount) return merged;
        }

        foreach (var p in second)
        {
            if (seen.Add(p)) merged.Add(p);
            if (merged.Count >= maxCount) return merged;
        }

        return merged;
    }

    // ===================== 就地导航 + 选中 =====================

    /// <summary>
    /// 在资源管理器中就地导航到目标文件所在文件夹并选中该文件。
    /// Shell COM late-binding → fallback SHOpenFolderAndSelectItems。
    /// </summary>
    private static void NavigateAndSelect(IntPtr explorerFrame, string targetFullPath, string openMode)
    {
        // DirectOpen 模式：找到前台文件对话框并导航到目标文件所在目录
        if (openMode == "DirectOpen")
        {
            try
            {
                var fg = Win32.GetForegroundWindow();
                if (fg != IntPtr.Zero && FileDialogJumpHelper.IsLikelyFileDialog(fg))
                {
                    var targetDir = Path.GetDirectoryName(targetFullPath);
                    if (!string.IsNullOrEmpty(targetDir))
                    {
                        FileDialogJumpHelper.TryNavigateToFolder(fg, targetDir);
                        return;
                    }
                }
            }
            catch { /* fallback to Explorer mode */ }
        }

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
                    // 轮询等待 Explorer 完成导航（SSD 通常 < 100ms），比固定 Sleep(400) 更快
                    for (int poll = 0; poll < 10; poll++)
                    {
                        Thread.Sleep(50);
                        try
                        {
                            var afterPath = ReadShellWindowPath(matchedWin);
                            if (string.Equals(NormPath(afterPath), NormPath(targetDir), StringComparison.OrdinalIgnoreCase))
                                break;
                        }
                        catch { break; }
                    }
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

    private static string NormPath(string? p) => NormalizeFolderForEverything(p ?? "");

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
        if (focus == IntPtr.Zero)
        {
            // Win11 DirectUI 下 focus 偶发为 0：焦点为 0 但该线程存在文本 caret
            // （hwndCaret 非空或 rcCaret 非空）说明正在编辑文本（重命名/地址栏），不启动会话。
            if (gti.hwndCaret != IntPtr.Zero
                || (gti.rcCaret.Right > gti.rcCaret.Left && gti.rcCaret.Bottom > gti.rcCaret.Top))
                return false;
            return true;
        }

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
