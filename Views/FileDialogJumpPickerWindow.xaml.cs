using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClipboardManager.Models;
using Media = System.Windows.Media;
using Orientation = System.Windows.Controls.Orientation;

namespace ClipboardManager;

/// <summary>文件对话框跳转：检索、方向键、主键+数字、收藏（设置持久化），交互对齐主剪贴板面板。</summary>
public partial class FileDialogJumpPickerWindow : Window
{
    private const int PageSize = 8;

    /// <summary>低级钩子回调须用静态委托持有，避免 Unhook 后晚到回调撞上已被回收的实例委托（见 <see cref="PopupWindow"/> 说明）。</summary>
    private static readonly Win32.LowLevelKeyboardProc s_jumpPickerKbThunk = StaticJumpPickerKeyboardHook;
    private static readonly Win32.LowLevelMouseProc s_jumpPickerMouseThunk = StaticJumpPickerMouseHook;
    private static readonly Win32.WinEventDelegate s_jumpPickerWinEventThunk = StaticJumpPickerWinEventProc;
    private static readonly Win32.WinEventDelegate s_jumpPickerDockWinEventThunk = StaticJumpPickerDockWinEventProc;
    private static readonly Win32.WinEventDelegate s_jumpPickerOwnerDestroyThunk = StaticJumpPickerOwnerDestroyProc;
    private static IntPtr s_jumpPickerKbHookForNext;
    private static FileDialogJumpPickerWindow? s_jumpPickerKbOwner;
    private static IntPtr s_jumpPickerMouseHookForNext;
    private static FileDialogJumpPickerWindow? s_jumpPickerMouseOwner;
    private static FileDialogJumpPickerWindow? s_jumpPickerWinEventOwner;
    private static FileDialogJumpPickerWindow? s_jumpPickerDockWinEventOwner;
    private static FileDialogJumpPickerWindow? s_jumpPickerOwnerDestroyOwner;

    private readonly IntPtr _fileDialogOwnerHwnd;
    private readonly int _mouseScreenX;
    private readonly int _mouseScreenY;
    private readonly AppSettings _settings;
    private readonly bool _dockBesideDialog;
    /// <summary>由「对话框成为前台」自动打开：贴靠、不因点文件窗/失焦而关、单击条目只导航不关闭。</summary>
    private readonly bool _autoForegroundStickyMode;
    private readonly List<FileJumpCandidate> _collectorSnapshot;

    private IntPtr _jumpKeyboardHook;
    private bool _suppressJumpHook;

    private IntPtr _hwnd;
    private bool _isOurSetWindowPosForPicker;
    /// <summary>对齐 <see cref="PopupWindow"/>：首屏阻止 WPF 改坐标，避免先顶部/角落后跳到鼠标处。</summary>
    private bool _lockJumpPickerNomove;

    /// <summary>对齐剪切板弹窗：<see cref="AppSettings.HideOnSameAppClick"/> 时点击跳转窗外关闭。</summary>
    private IntPtr _jumpPickerMouseHook;
    private bool _clickReceivedByJumpPicker;
    private bool _suppressDismissForSubDialog;

    private IntPtr _jumpPickerWinEventHook;
    private IntPtr _ownerDestroyHook;

    private DispatcherTimer? _dockFollowTimer;
    private IntPtr _dockOwnerMoveSizeHook;
    private IntPtr _dockOwnerLocationHook;
    private bool _dockOwnerMoveActive;
    private List<FileJumpCandidate>? _deferredExternalRefresh;
    private string? _deferredExternalPreferredPath;
    private DispatcherTimer? _deferredExternalRefreshTimer;
    private int _dockPopupPhysWidth;
    private int _dockPopupPhysHeight;
    private int _lastDockOwnerLeft = int.MinValue;
    private int _lastDockOwnerTop = int.MinValue;
    private int _lastDockOwnerRight = int.MinValue;
    private int _lastDockOwnerBottom = int.MinValue;
    private int _lastDockActualWidth = int.MinValue;
    private int _lastDockActualHeight = int.MinValue;
    private int _lastAppliedPhysX = int.MinValue;
    private int _lastAppliedPhysY = int.MinValue;
    private DispatcherTimer? _focusRetryTimer;
    private DispatcherTimer? _searchRefreshTimer;
    private int _focusRetryCount;
    private int _perfDockFollowSlowLogCount;
    private int _perfFocusSlowLogCount;

    private int _pendingPhysX;
    private int _pendingPhysY;
    private bool _snappedPhysicalOnce;
    private string _searchText = "";
    /// <summary>_searchText 是否非空；用 volatile 保证钩子线程可安全读取。</summary>
    private volatile bool _hasSearchText;
    private bool _userHasResized;
    private bool _isResizing;

    /// <summary>列表内搜索高亮绑定用：当前检索词（Trim）。</summary>
    public static readonly DependencyProperty HighlightSearchQueryProperty = DependencyProperty.Register(
        nameof(HighlightSearchQuery),
        typeof(string),
        typeof(FileDialogJumpPickerWindow),
        new PropertyMetadata(""));

    public string HighlightSearchQuery
    {
        get => (string)GetValue(HighlightSearchQueryProperty);
        set => SetValue(HighlightSearchQueryProperty, value ?? "");
    }

    private bool _favoritesOnly;
    private int _firstVisibleIndex;

    /// <summary>粘性模式下点击导航后异步采集；新一次点击递增，过时回调丢弃。</summary>
    private int _commitNavigateKeepOpenGen;
    private string _commitNavigateKeepOpenPath = "";
    private long _commitNavigateKeepOpenUntilTick;

    private readonly List<FileJumpPickerRow> _masterRows = new();
    private readonly BulkObservableCollection<FileJumpPickerRow> _displayRows = new();

    private readonly List<string> _everythingFolderPaths = new();
    private string _everythingPathsValidForQuery = "";
    private int _everythingQueryGen;
    private CancellationTokenSource? _everythingQueryCts;

    public string? SelectedPath { get; private set; }
    public IntPtr OwnerDialogHwnd => _fileDialogOwnerHwnd;
    public bool IsAutoForegroundStickyMode => _autoForegroundStickyMode;

    private static void PerfLog(string eventName, long elapsedMs, long thresholdMs, string detail = "")
    {
        if (elapsedMs < thresholdMs) return;
        ClipboardDiagnosticsLog.Write(
            string.IsNullOrEmpty(detail)
                ? $"filejump.perf {eventName} elapsedMs={elapsedMs}"
                : $"filejump.perf {eventName} elapsedMs={elapsedMs} {detail}");
    }

    public FileDialogJumpPickerWindow(
        IReadOnlyList<FileJumpCandidate> collectorItems,
        int preferSelectedIndex,
        int mouseScreenX,
        int mouseScreenY,
        AppSettings settings,
        IntPtr fileDialogOwnerHwnd,
        bool autoForegroundStickyMode = false)
    {
        _fileDialogOwnerHwnd = fileDialogOwnerHwnd;
        _mouseScreenX = mouseScreenX;
        _mouseScreenY = mouseScreenY;
        _settings = settings;
        _autoForegroundStickyMode = autoForegroundStickyMode;
        // 「对话框打开时自动弹出列表」开启时所有跳转列表（含 Ctrl+G）强制贴对话框；
        // 关闭时按用户「跟随对话框/鼠标」。autoForegroundStickyMode 自身就是自动弹出，必须贴。
        _dockBesideDialog = fileDialogOwnerHwnd != IntPtr.Zero
                            && (autoForegroundStickyMode
                                || settings.FileJumpPickerOpenWhenDialogForeground
                                || FileJumpPickerFollowModes.IsDialog(settings.FileJumpPickerFollowMode));
        _collectorSnapshot = collectorItems.ToList();

        InitializeComponent();
        Width = settings.FileJumpPickerWidth;
        MaxHeight = settings.FileJumpPickerMaxHeight;
        if (settings.FileJumpPickerHeight > 0)
        {
            _userHasResized = true;
            SizeToContent = SizeToContent.Manual;
            Height = settings.FileJumpPickerHeight;
        }
        Opacity = 0;
        ItemsList.ItemsSource = _displayRows;
        FileJumpHintText.Text = autoForegroundStickyMode
            ? $"列表紧贴文件对话框并随窗口移动。单击切换目录；右键收藏/移除；Esc 关闭。快捷键 {_settings.FileJumpHotkeyDisplayName} 与平常相同。"
            : $"右键可收藏/移除；再按 {_settings.FileJumpHotkeyDisplayName} 同主面板逻辑。";

        string? preferPath = null;
        if (preferSelectedIndex >= 0 && preferSelectedIndex < _collectorSnapshot.Count)
            preferPath = _collectorSnapshot[preferSelectedIndex].Path;

        BuildMasterList();
        RefreshFilter();
        if (!string.IsNullOrEmpty(preferPath))
        {
            var idx = _displayRows.ToList().FindIndex(r =>
                string.Equals(r.Path, preferPath, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                ItemsList.SelectedIndex = idx;
                ItemsList.ScrollIntoView(ItemsList.SelectedItem);
            }
        }

        Closed += FileDialogJumpPickerWindow_Closed;
        Activated += FileDialogJumpPickerWindow_Activated;
        SizeChanged += FileDialogJumpPickerWindow_SizeChanged;

        SourceInitialized += FileDialogJumpPickerWindow_SourceInitialized;
        ContentRendered += FileDialogJumpPickerWindow_ContentRendered;
        UpdateSearchChrome();
        UpdateFooterHints();
    }

    private void FileDialogJumpPickerWindow_Closed(object? sender, EventArgs e)
    {
        if (_settings != null)
        {
            _settings.FileJumpPickerWidth = Width;
            _settings.FileJumpPickerMaxHeight = MaxHeight;
            if (_userHasResized && ActualHeight > 0)
                _settings.FileJumpPickerHeight = ActualHeight;
            _settings.Save();
        }
        _everythingQueryCts?.Cancel();
        _everythingQueryCts = null;
        _dockFollowTimer?.Stop();
        _dockFollowTimer = null;
        _focusRetryTimer?.Stop();
        _focusRetryTimer = null;
        _searchRefreshTimer?.Stop();
        _searchRefreshTimer = null;
        _deferredExternalRefreshTimer?.Stop();
        _deferredExternalRefreshTimer = null;
        UninstallDockOwnerFollowHooks();
        UninstallKeyboardHook();
        UninstallJumpPickerOutsideHooks();
        UninstallOwnerDestroyHook();
        if (_hwnd != IntPtr.Zero)
        {
            try
            {
                HwndSource.FromHwnd(_hwnd)?.RemoveHook(JumpPickerWndProc);
            }
            catch { /* ignore */ }
        }
    }

    private void FileDialogJumpPickerWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateDockPopupPhysicalSizeCache();
    }

    private void FileDialogJumpPickerWindow_Activated(object? sender, EventArgs e)
    {
        if (IsLoaded)
            Dispatcher.BeginInvoke(TryStealFocusForPicker, DispatcherPriority.Input);
    }

    private void TryStealFocusForPicker()
    {
        if (_dockOwnerMoveActive || IsPrimaryMouseButtonDown())
        {
            ScheduleFocusRetry();
            return;
        }
        _focusRetryTimer?.Stop();
        _focusRetryTimer = null;
        _focusRetryCount = 0;
        var swTotal = Stopwatch.StartNew();
        try
        {
            var hwnd = _hwnd != IntPtr.Zero ? _hwnd : new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            if (Win32.GetForegroundWindow() != hwnd)
            {
                Activate();
                Win32.SetForegroundWindowAggressive(hwnd);
                if (Win32.GetForegroundWindow() != hwnd)
                {
                    ScheduleFocusRetry();
                    return;
                }
            }

            ItemsList.Focusable = true;
            _ = ItemsList.Focus();
            Keyboard.Focus(ItemsList);
            ItemsList.Focus();
        }
        catch { /* ignore */ }
        finally
        {
            swTotal.Stop();
            if (swTotal.ElapsedMilliseconds >= 25 && _perfFocusSlowLogCount < 20)
            {
                _perfFocusSlowLogCount++;
                ClipboardDiagnosticsLog.Write(
                    $"filejump.perf focus elapsedMs={swTotal.ElapsedMilliseconds} moveActive={_dockOwnerMoveActive}");
            }
        }
    }

    private void ScheduleFocusRetry()
    {
        if (_focusRetryCount >= 12) return;
        _focusRetryTimer?.Stop();
        _focusRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _focusRetryTimer.Tick += (_, _) =>
        {
            _focusRetryTimer?.Stop();
            _focusRetryTimer = null;
            _focusRetryCount++;
            if (!IsLoaded) return;
            TryStealFocusForPicker();
        };
        _focusRetryTimer.Start();
    }

    private static bool IsPrimaryMouseButtonDown()
        => (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0;

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        _clickReceivedByJumpPicker = true;
        base.OnPreviewMouseDown(e);
    }

    private void InstallJumpPickerOutsideHooks()
    {
        if (_jumpPickerMouseHook != IntPtr.Zero) return;
        s_jumpPickerMouseOwner = this;
        _jumpPickerMouseHook = Win32.SetWindowsHookEx(
            Win32.WH_MOUSE_LL, s_jumpPickerMouseThunk, Win32.GetModuleHandle(null), 0);
        s_jumpPickerMouseHookForNext = _jumpPickerMouseHook;

        if (_jumpPickerWinEventHook == IntPtr.Zero)
        {
            s_jumpPickerWinEventOwner = this;
            _jumpPickerWinEventHook = Win32.SetWinEventHook(
                Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, s_jumpPickerWinEventThunk, 0, 0,
                Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
        }
    }

    private void UninstallJumpPickerOutsideHooks()
    {
        if (_jumpPickerMouseHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_jumpPickerMouseHook);
            _jumpPickerMouseHook = IntPtr.Zero;
        }
        if (s_jumpPickerMouseOwner == this)
        {
            s_jumpPickerMouseOwner = null;
            s_jumpPickerMouseHookForNext = IntPtr.Zero;
        }

        if (_jumpPickerWinEventHook != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_jumpPickerWinEventHook);
            _jumpPickerWinEventHook = IntPtr.Zero;
        }
        if (s_jumpPickerWinEventOwner == this)
            s_jumpPickerWinEventOwner = null;
    }

    private static IntPtr StaticJumpPickerKeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var owner = s_jumpPickerKbOwner;
        var hhk = s_jumpPickerKbHookForNext;
        if (owner != null && hhk != IntPtr.Zero)
            return owner.JumpKeyboardHookProc(nCode, wParam, lParam);
        return Win32.CallNextHookEx(hhk, nCode, wParam, lParam);
    }

    private static IntPtr StaticJumpPickerMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var owner = s_jumpPickerMouseOwner;
        var hhk = s_jumpPickerMouseHookForNext;
        if (owner != null && hhk != IntPtr.Zero)
            return owner.JumpPickerMouseHookProc(nCode, wParam, lParam);
        return Win32.CallNextHookEx(hhk, nCode, wParam, lParam);
    }

    private static void StaticJumpPickerWinEventProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        var owner = s_jumpPickerWinEventOwner;
        if (owner != null)
            owner.JumpPickerForegroundCallback(hWinEventHook, eventType, hwnd, idObject, idChild, dwEventThread, dwmsEventTime);
    }

    private static void StaticJumpPickerDockWinEventProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        var owner = s_jumpPickerDockWinEventOwner;
        if (owner != null)
            owner.JumpPickerDockMoveSizeCallback(eventType, hwnd, idObject, idChild);
    }

    private static void StaticJumpPickerOwnerDestroyProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        var owner = s_jumpPickerOwnerDestroyOwner;
        if (owner == null) return;
        if (owner._suppressDismissForSubDialog) return;
        if (owner._fileDialogOwnerHwnd == IntPtr.Zero)
        {
            owner._ownerDestroyHook = IntPtr.Zero;
            owner.Dispatcher.BeginInvoke(() =>
            {
                try { owner.Close(); } catch { }
            });
            return;
        }
        if (hwnd == owner._fileDialogOwnerHwnd || !Win32.IsWindow(owner._fileDialogOwnerHwnd))
        {
            owner._ownerDestroyHook = IntPtr.Zero;
            owner.Dispatcher.BeginInvoke(() =>
            {
                try { owner.Close(); } catch { }
            });
        }
    }

    private void InstallOwnerDestroyHook()
    {
        if (_fileDialogOwnerHwnd == IntPtr.Zero) return;
        s_jumpPickerOwnerDestroyOwner = this;
        _ownerDestroyHook = Win32.SetWinEventHook(
            Win32.EVENT_OBJECT_DESTROY, Win32.EVENT_OBJECT_DESTROY,
            IntPtr.Zero, s_jumpPickerOwnerDestroyThunk,
            0, 0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
    }

    private void UninstallOwnerDestroyHook()
    {
        s_jumpPickerOwnerDestroyOwner = null;
        if (_ownerDestroyHook != IntPtr.Zero)
        {
            try { Win32.UnhookWinEvent(_ownerDestroyHook); } catch { }
            _ownerDestroyHook = IntPtr.Zero;
        }
    }

    private IntPtr JumpPickerMouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsLoaded && Opacity > 0)
        {
            var msg = wParam.ToInt32();
            if (msg is Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN)
            {
                if (_settings.HideOnSameAppClick)
                {
                    _clickReceivedByJumpPicker = false;
                    Dispatcher.BeginInvoke(
                        DispatcherPriority.Background,
                        (Action)(() => TryDismissJumpPickerFromOutsideMouse()));
                }
            }
        }
        return Win32.CallNextHookEx(_jumpPickerMouseHook, nCode, wParam, lParam);
    }

    private void JumpPickerForegroundCallback(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        Dispatcher.BeginInvoke(() => TryDismissJumpPickerFromForegroundChange(hwnd));
    }

    private void JumpPickerDockMoveSizeCallback(uint eventType, IntPtr hwnd, int idObject, int idChild)
    {
        if (idObject != Win32.OBJID_WINDOW || idChild != 0) return;
        if (!DockEventBelongsToOwner(hwnd)) return;

        if (eventType == Win32.EVENT_OBJECT_LOCATIONCHANGE)
        {
            TryRealtimeDockFollow();
            return;
        }

        if (eventType == Win32.EVENT_SYSTEM_MOVESIZESTART)
        {
            _dockOwnerMoveActive = true;
            return;
        }

        if (eventType == Win32.EVENT_SYSTEM_MOVESIZEEND)
        {
            _dockOwnerMoveActive = false;
            TryRealtimeDockFollow(force: true);
            FlushDeferredExternalRefresh();
        }
    }

    private void TryDismissJumpPickerFromOutsideMouse()
    {
        if (!IsLoaded || Opacity <= 0) return;
        if (_clickReceivedByJumpPicker) return;
        if (JumpRowContextMenu.IsOpen) return;
        if (_suppressDismissForSubDialog) return;
        // 跳转窗是 modeless 窗口，关闭只负责收起 UI，导航由 CommitNavigateKeepOpen 独立完成。
        Close();
    }

    private void TryDismissJumpPickerFromForegroundChange(IntPtr newForeground)
    {
        if (!IsLoaded || Opacity <= 0) return;
        if (newForeground == _hwnd) return;
        Win32.GetCursorPos(out var cursor);
        if (Win32.WindowFromPoint(cursor) == _hwnd) return;
        if (JumpRowContextMenu.IsOpen) return;
        if (_suppressDismissForSubDialog) return;
        Close();
    }

    private void InstallKeyboardHook()
    {
        if (_jumpKeyboardHook != IntPtr.Zero) return;
        s_jumpPickerKbOwner = this;
        _jumpKeyboardHook = Win32.SetWindowsHookEx(
            Win32.WH_KEYBOARD_LL, s_jumpPickerKbThunk, Win32.GetModuleHandle(null), 0);
        s_jumpPickerKbHookForNext = _jumpKeyboardHook;
    }

    private void UninstallKeyboardHook()
    {
        if (_jumpKeyboardHook == IntPtr.Zero) return;
        Win32.UnhookWindowsHookEx(_jumpKeyboardHook);
        _jumpKeyboardHook = IntPtr.Zero;
        if (s_jumpPickerKbOwner == this)
        {
            s_jumpPickerKbOwner = null;
            s_jumpPickerKbHookForNext = IntPtr.Zero;
        }
    }

    private IntPtr JumpKeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return Win32.CallNextHookEx(_jumpKeyboardHook, nCode, wParam, lParam);
        if (wParam != (IntPtr)Win32.WM_KEYDOWN)
            return Win32.CallNextHookEx(_jumpKeyboardHook, nCode, wParam, lParam);
        if (_suppressJumpHook)
            return Win32.CallNextHookEx(_jumpKeyboardHook, nCode, wParam, lParam);
        if (!KeyboardHookShouldObserveForeground())
            return Win32.CallNextHookEx(_jumpKeyboardHook, nCode, wParam, lParam);
        if (KeyboardFocusIsExternalEditable())
            return Win32.CallNextHookEx(_jumpKeyboardHook, nCode, wParam, lParam);

        var kb = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);

        try
        {
            // 纯逻辑判断是否拦截，不访问 UI 状态，不阻塞钩子线程
            if (ShouldInterceptKey(kb.vkCode, kb.scanCode))
            {
                Dispatcher.BeginInvoke(() => ApplyKeyDown(kb.vkCode, kb.scanCode),
                    System.Windows.Threading.DispatcherPriority.Send);
                return (IntPtr)1;
            }
        }
        catch
        {
            // fall through to CallNextHookEx
        }

        return Win32.CallNextHookEx(_jumpKeyboardHook, nCode, wParam, lParam);
    }

    private bool KeyboardHookShouldObserveForeground()
    {
        if (_hwnd == IntPtr.Zero) return false;

        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (fg == _hwnd) return true;

        var ownerRoot = _fileDialogOwnerHwnd != IntPtr.Zero && Win32.IsWindow(_fileDialogOwnerHwnd)
            ? Win32.GetAncestor(_fileDialogOwnerHwnd, Win32.GA_ROOT)
            : IntPtr.Zero;
        if (ownerRoot == IntPtr.Zero) return false;

        var fgDialog = FileDialogJumpHelper.ResolveFileDialogHwndFromWindowOrAncestor(fg);
        var fgRoot = fgDialog != IntPtr.Zero
            ? Win32.GetAncestor(fgDialog, Win32.GA_ROOT)
            : Win32.GetAncestor(fg, Win32.GA_ROOT);

        return fgRoot != IntPtr.Zero && fgRoot == ownerRoot;
    }

    /// <summary>
    /// 前台线程焦点在「另存为」等系统对话框的文件名编辑框时，不把按键留给跳转列表，
    /// 便于在保持跳转面板打开的同时修改文件名。
    /// </summary>
    private bool KeyboardFocusIsExternalEditable()
    {
        if (_hwnd == IntPtr.Zero) return false;
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        uint tid = Win32.GetWindowThreadProcessId(fg, out _);
        var gti = new Win32.GUITHREADINFO { cbSize = Marshal.SizeOf<Win32.GUITHREADINFO>() };
        if (!Win32.GetGUIThreadInfo(tid, ref gti) || gti.hwndFocus == IntPtr.Zero)
            return false;
        if (IsFocusWithinJumpPicker(gti.hwndFocus))
            return false;
        return IsEditableTextHwnd(gti.hwndFocus);
    }

    private bool IsFocusWithinJumpPicker(IntPtr hwndFocus)
    {
        for (IntPtr h = hwndFocus; h != IntPtr.Zero; h = Win32.GetParent(h))
        {
            if (h == _hwnd) return true;
        }
        return false;
    }

    private static bool IsEditableTextHwnd(IntPtr hwnd)
    {
        string cls = Win32.GetWindowClassName(hwnd);
        if (string.IsNullOrEmpty(cls)) return false;
        if (cls.Equals("Edit", StringComparison.OrdinalIgnoreCase))
        {
            uint style = unchecked((uint)Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64());
            return (style & Win32.ES_READONLY) == 0;
        }
        return cls.Contains("RichEdit", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>统一键盘逻辑：低级钩子同步调用；返回 true 表示已消费按键。</summary>
    private bool ApplyKeyDown(uint vk, uint scan)
    {
        if (IsPanelModifierMatch())
        {
            if (vk is >= 0x31 and <= 0x39)
            {
                JumpByQuickIndex((int)(vk - 0x30));
                return true;
            }
            if (vk is >= 0x61 and <= 0x69)
            {
                JumpByQuickIndex((int)(vk - 0x60));
                return true;
            }
            if (vk == 0x09)
            {
                ToggleFavoritesFilter();
                return true;
            }
            if (vk is 0xBB or 0x6B)
            {
                ScrollPage(1);
                return true;
            }
            if (vk is 0xBD or 0x6D)
            {
                ScrollPage(-1);
                return true;
            }
        }

        bool ctrl = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool alt = (Win32.GetAsyncKeyState(0x12) & 0x8000) != 0;
        if (ctrl || alt)
            return false;

        switch (vk)
        {
            case Win32.VK_UP:
                FlushPendingSearchRefresh();
                MoveSelection(-1);
                return true;
            case Win32.VK_DOWN:
                FlushPendingSearchRefresh();
                MoveSelection(1);
                return true;
            case Win32.VK_LEFT:
                FlushPendingSearchRefresh();
                ScrollPage(-1);
                return true;
            case Win32.VK_RIGHT:
                FlushPendingSearchRefresh();
                ScrollPage(1);
                return true;
            case Win32.VK_RETURN:
                FlushPendingSearchRefresh();
                if (ItemsList.SelectedItem is FileJumpPickerRow r)
                    CommitSelection(r.Path);
                return true;
            case Win32.VK_ESCAPE:
                if (_searchText.Length > 0)
                {
                    _searchText = "";
                    _hasSearchText = false;
                    ScheduleSearchRefresh();
                }
                else
                {
                    Close();
                }
                return true;
            case Win32.VK_BACK:
                if (_searchText.Length > 0)
                {
                    _searchText = _searchText[..^1];
                    _hasSearchText = _searchText.Length > 0;
                    ScheduleSearchRefresh();
                }
                return true;
            case 0x09:
                ToggleFavoritesFilter();
                return true;
        }

        var ch = VkToChar(vk, scan);
        if (ch.HasValue && !char.IsControl(ch.Value))
        {
            _searchText += ch.Value;
            _hasSearchText = true;
            ScheduleSearchRefresh();
            return true;
        }

        return false;
    }

    private void ScheduleSearchRefresh()
    {
        _searchRefreshTimer?.Stop();
        _searchRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _searchRefreshTimer.Tick += (_, _) =>
        {
            _searchRefreshTimer?.Stop();
            _searchRefreshTimer = null;
            if (!IsLoaded) return;
            RefreshFilter();
        };
        _searchRefreshTimer.Start();
    }

    private void FlushPendingSearchRefresh()
    {
        if (_searchRefreshTimer == null) return;
        _searchRefreshTimer.Stop();
        _searchRefreshTimer = null;
        RefreshFilter();
    }

    private static char? VkToChar(uint vkCode, uint scanCode)
    {
        var keyState = new byte[256];
        if ((Win32.GetAsyncKeyState(0x10) & 0x8000) != 0) { keyState[0x10] = 0x80; keyState[0xA0] = 0x80; }
        if ((Win32.GetKeyState(0x14) & 0x0001) != 0) keyState[0x14] = 0x01;

        var sb = new StringBuilder(4);
        int result = Win32.ToUnicode(vkCode, scanCode, keyState, sb, sb.Capacity, 0);
        if (result < 0) Win32.ToUnicode(vkCode, scanCode, keyState, sb, sb.Capacity, 0);
        if (result == 1 && !char.IsControl(sb[0])) return sb[0];
        return null;
    }

    /// <summary>
    /// 钩子线程调用：纯逻辑判断按键是否应被跳转窗拦截（不访问任何 UI 状态）。
    /// 返回 true 表示应拦截并 BeginInvoke ApplyKeyDown；返回 false 表示放行。
    /// </summary>
    private bool ShouldInterceptKey(uint vk, uint scan)
    {
        if (IsPanelModifierMatch())
        {
            if (vk is >= 0x31 and <= 0x39) return true;   // 1-9 快速索引
            if (vk is >= 0x61 and <= 0x69) return true;   // Numpad 1-9
            if (vk == 0x09) return true;                   // Tab 切换收藏
            if (vk is 0xBB or 0x6B) return true;           // +/= 翻页
            if (vk is 0xBD or 0x6D) return true;           // -/_ 翻页
        }

        bool ctrl = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool alt = (Win32.GetAsyncKeyState(0x12) & 0x8000) != 0;
        if (ctrl || alt) return false;

        switch (vk)
        {
            case Win32.VK_UP:
            case Win32.VK_DOWN:
            case Win32.VK_LEFT:
            case Win32.VK_RIGHT:
            case Win32.VK_RETURN:
                return true;
            case Win32.VK_ESCAPE:
                return true; // 有搜索文本则清空，无则关闭窗，均应拦截
            case Win32.VK_BACK:
                return _hasSearchText; // 有搜索文本时拦截删除，无搜索文本时放行给宿主对话框
            case 0x09: // Tab
                return true;
        }

        // 可打印字符：无论搜索框是否有内容，新字符都应进入搜索框
        var ch = VkToChar(vk, scan);
        if (ch.HasValue && !char.IsControl(ch.Value))
            return true;

        return false;
    }

    private void BuildMasterList()
    {
        var sw = Stopwatch.StartNew();
        var favCount = _settings.FolderFavorites.Count;
        var snapshotCount = _collectorSnapshot.Count;
        _masterRows.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fav in _settings.FolderFavorites)
        {
            if (string.IsNullOrWhiteSpace(fav.Path)) continue;
            try
            {
                var full = Path.GetFullPath(fav.Path.Trim());
                if (!Directory.Exists(full)) continue;
                if (!seen.Add(full)) continue;
                _masterRows.Add(new FileJumpPickerRow("收藏", full, true, fav.Phrase));
            }
            catch { /* ignore */ }
        }

        foreach (var c in _collectorSnapshot)
        {
            if (seen.Contains(c.Path)) continue;
            try
            {
                if (!Directory.Exists(c.Path)) continue;
            }
            catch { continue; }

            seen.Add(c.Path);
            _masterRows.Add(new FileJumpPickerRow(c.Label, c.Path, false));
        }

        sw.Stop();
        PerfLog("build_master_list", sw.ElapsedMilliseconds, 25,
            $"fav={favCount} snapshot={snapshotCount} rows={_masterRows.Count}");
    }

    private void RefreshFilter(int? preferListIndex = null, string? preferPath = null, bool scrollSelection = true)
    {
        var sw = Stopwatch.StartNew();
        var keepPath = preferPath ?? (ItemsList.SelectedItem as FileJumpPickerRow)?.Path;
        _firstVisibleIndex = 0;

        var query = _searchText.Trim();
        if (string.IsNullOrEmpty(query) || !_settings.FileJumpPickerEverythingFolderSearch)
        {
            _everythingFolderPaths.Clear();
            _everythingPathsValidForQuery = "";
            _everythingQueryCts?.Cancel();
        }
        else if (!string.Equals(query, _everythingPathsValidForQuery, StringComparison.OrdinalIgnoreCase))
        {
            _everythingFolderPaths.Clear();
            _everythingPathsValidForQuery = "";
        }

        using (var _ = _displayRows.BeginBulkUpdate())
        {
            _displayRows.Clear();

            IEnumerable<FileJumpPickerRow> seq = _masterRows;
            if (_favoritesOnly)
                seq = seq.Where(r => r.IsFavorite);

            if (!string.IsNullOrEmpty(query))
                seq = seq.Where(r => r.MatchesSearch(query));

            var sorted = seq
                .OrderByDescending(r => r.IsFavorite && !string.IsNullOrEmpty(query))
                .ThenByDescending(r => r.IsFavorite)
                .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase);

            foreach (var r in sorted)
                _displayRows.Add(r);

            if (!string.IsNullOrEmpty(query)
                && _settings.FileJumpPickerEverythingFolderSearch
                && string.Equals(query, _everythingPathsValidForQuery, StringComparison.OrdinalIgnoreCase)
                && _everythingFolderPaths.Count > 0)
            {
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in _displayRows)
                    seenPaths.Add(r.Path);

                foreach (var p in _everythingFolderPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (!seenPaths.Add(p)) continue;
                    _displayRows.Add(new FileJumpPickerRow("everything", p, false));
                }
            }
        }

        _prevQuickIndexFirstVisible = -1; // 列表重建，重置索引缓存
        AssignVisibleQuickIndices(0);

        UpdateSearchChrome();
        if (_displayRows.Count > 0)
        {
            int sel = 0;
            if (!string.IsNullOrEmpty(keepPath))
            {
                var i = _displayRows.ToList().FindIndex(r =>
                    string.Equals(r.Path, keepPath, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) sel = i;
                else if (preferListIndex.HasValue)
                    sel = Math.Clamp(preferListIndex.Value, 0, _displayRows.Count - 1);
            }
            else if (preferListIndex.HasValue)
                sel = Math.Clamp(preferListIndex.Value, 0, _displayRows.Count - 1);

            ItemsList.SelectedIndex = sel;
            if (scrollSelection)
                ItemsList.ScrollIntoView(ItemsList.SelectedItem);
        }

        if (!string.IsNullOrEmpty(query) && _settings.FileJumpPickerEverythingFolderSearch)
            ScheduleEverythingFolderQuery(query);

        sw.Stop();
        PerfLog("refresh_filter", sw.ElapsedMilliseconds, 25,
            $"queryLen={query.Length} master={_masterRows.Count} display={_displayRows.Count}");
    }

    private void ScheduleEverythingFolderQuery(string queryForSchedule)
    {
        if (!_settings.FileJumpPickerEverythingFolderSearch || string.IsNullOrEmpty(queryForSchedule))
        {
            _everythingQueryCts?.Cancel();
            return;
        }

        if (string.Equals(queryForSchedule, _everythingPathsValidForQuery, StringComparison.OrdinalIgnoreCase))
            return;

        _everythingQueryGen++;
        var gen = _everythingQueryGen;
        _everythingQueryCts?.Cancel();
        _everythingQueryCts = new CancellationTokenSource();
        var tok = _everythingQueryCts.Token;
        var maxResults = Math.Clamp(_settings.ExplorerEverythingQuickFindMaxResults, 1, 2000);

        // 早期 debounce 写到 140ms，对「换一段输入再按 Tab/字母」的交互而言体感很重。
        // Everything IPC 文件夹检索单次开销通常 <10ms，节流 40ms 足以合并连按又不感知卡顿。
        _ = Task.Run(() =>
        {
            try
            {
                if (tok.WaitHandle.WaitOne(40)) return;
                if (gen != _everythingQueryGen) return;

                var list = new List<string>();
                var ok = EverythingIpc.TryQueryFolderPaths(queryForSchedule, maxResults, list, out _);

                Dispatcher.BeginInvoke(() =>
                {
                    if (gen != _everythingQueryGen) return;
                    if (!string.Equals(_searchText.Trim(), queryForSchedule, StringComparison.Ordinal)) return;

                    _everythingFolderPaths.Clear();
                    if (ok)
                        _everythingFolderPaths.AddRange(list);
                    _everythingPathsValidForQuery = queryForSchedule;

                    var pathKeep = (ItemsList.SelectedItem as FileJumpPickerRow)?.Path;
                    RefreshFilter(preferPath: pathKeep);
                }, DispatcherPriority.Background);
            }
            catch
            {
                /* ignore */
            }
        });
    }

    private int _prevQuickIndexFirstVisible = -1;

    private void AssignVisibleQuickIndices(int firstVisible)
    {
        var count = _displayRows.Count;
        if (count == 0) { _prevQuickIndexFirstVisible = firstVisible; return; }

        // 只更新 old/new 可见窗口的并集（最多 18 行），而非全部遍历
        var oldFirst = _prevQuickIndexFirstVisible;
        _prevQuickIndexFirstVisible = firstVisible;

        int newLast = Math.Min(firstVisible + 8, count - 1);
        int oldLast = oldFirst >= 0 ? Math.Min(oldFirst + 8, count - 1) : -1;

        int rangeStart = Math.Max(0, Math.Min(firstVisible, oldFirst >= 0 ? oldFirst : firstVisible));
        int rangeEnd = Math.Max(newLast, oldLast);

        for (int i = rangeStart; i <= rangeEnd; i++)
        {
            int rel = i - firstVisible + 1;
            _displayRows[i].DisplayIndex = rel is >= 1 and <= 9 ? rel : 0;
        }
    }

    private void UpdateSearchChrome()
    {
        var has = _searchText.Length > 0;
        SearchBarPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        SetCurrentValue(HighlightSearchQueryProperty, _searchText.Trim());
        if (has)
        {
            var primary = TryFindResource("PrimaryText") as Media.Brush ?? Media.Brushes.White;
            var accent = TryFindResource("AccentBg") as Media.Brush ?? Media.Brushes.Teal;
            SearchTextBlock.Inlines.Clear();
            SearchHighlightInlines.Append(
                SearchTextBlock.Inlines,
                _searchText,
                _searchText.Trim(),
                primary,
                accent,
                13,
                FontWeights.Normal);
        }
        else
            SearchTextBlock.Inlines.Clear();

        SearchCountText.Text = has ? $"{_displayRows.Count} 条结果" : "";
    }

    private void UpdateFooterHints()
    {
        var m = PanelModifierDisplayName(_settings.PanelModifierKey);
        FooterHintsText.Text =
            $"{m}+1~9 跳转 · ↑↓ 选择 · ←→ 翻页 · Tab · 字母 everything · 右键收藏 · Esc 取消/清除";
    }

    private static string PanelModifierDisplayName(string key) => key switch
    {
        "Alt" => "Alt",
        "Win" => "Win",
        "CapsLock" => "CapsLk",
        _ => "Ctrl",
    };

    private bool IsPanelModifierMatch()
    {
        bool ctrl = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool alt = (Win32.GetAsyncKeyState(0x12) & 0x8000) != 0;
        bool win = ((Win32.GetAsyncKeyState(0x5B) | Win32.GetAsyncKeyState(0x5C)) & 0x8000) != 0;
        bool caps = (Win32.GetAsyncKeyState(0x14) & 0x8000) != 0;

        return _settings.PanelModifierKey switch
        {
            "Alt" => alt && !ctrl,
            "Win" => win && !ctrl && !alt,
            "CapsLock" => caps && !ctrl && !alt,
            _ => ctrl && !alt,
        };
    }

    private void ToggleFavoritesFilter()
    {
        _favoritesOnly = !_favoritesOnly;
        _searchText = "";
        _hasSearchText = false;
        var keepPath = (ItemsList.SelectedItem as FileJumpPickerRow)?.Path;
        RefreshFilter();
        if (!string.IsNullOrEmpty(keepPath))
        {
            var i = _displayRows.ToList().FindIndex(r => string.Equals(r.Path, keepPath, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) ItemsList.SelectedIndex = i;
        }
    }

    private void FileDialogJumpPickerWindow_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var helper = new WindowInteropHelper(this);
            helper.EnsureHandle();
            _hwnd = helper.Handle;
            // 粘性贴靠模式若设为文件对话框的 Owned 窗口，系统常把前台/键盘留在宿主对话框，SetForeground 难以生效；本窗已 Topmost。
            if (_fileDialogOwnerHwnd != IntPtr.Zero && !_autoForegroundStickyMode)
                helper.Owner = _fileDialogOwnerHwnd;

            HwndSource.FromHwnd(_hwnd)?.AddHook(JumpPickerWndProc);
        }
        catch { /* ignore */ }

        _lockJumpPickerNomove = true;
        try
        {
            ComputePhysicalPosition(useActualSize: false);
            ApplyPendingPhysicalAsWpfLeftTop();
            ApplyPendingPhysicalSetWindowPos();
            ApplyPendingPhysicalAsWpfLeftTop();
        }
        catch { /* ignore */ }

        InstallKeyboardHook();
    }

    private IntPtr JumpPickerWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_NCHITTEST:
                var htResult = WindowResizeHelper.HandleNcHitTest(_hwnd, lParam, 16, ref handled);
                if (handled) return htResult;
                break;

            case Win32.WM_SIZING:
                if (WindowResizeHelper.HandleWmSizing(_hwnd, wParam, lParam,
                    MinWidth > 0 ? MinWidth : 280, 1200,
                    MinHeight > 0 ? MinHeight : 200, MaxHeight > 0 ? MaxHeight : 900,
                    _userHasResized))
                {
                    _userHasResized = true;
                    Dispatcher.BeginInvoke(() => SizeToContent = SizeToContent.Manual);
                }
                _isResizing = true;
                var sizingRc = Marshal.PtrToStructure<Win32.RECT>(lParam);
                Dispatcher.BeginInvoke(() =>
                {
                    var src = HwndSource.FromHwnd(_hwnd);
                    double sx = src?.CompositionTarget != null ? src.CompositionTarget.TransformFromDevice.M11 : 1;
                    double sy = src?.CompositionTarget != null ? src.CompositionTarget.TransformFromDevice.M22 : 1;
                    double w = (sizingRc.Right - sizingRc.Left) * sx;
                    double h = (sizingRc.Bottom - sizingRc.Top) * sy;
                    if (w > 0 && h > 0)
                    {
                        Width = w;
                        Height = h;
                        MaxHeight = Math.Max(MaxHeight, h);
                        UpdateDockPopupPhysicalSizeCache();
                    }
                });
                handled = true;
                return IntPtr.Zero;

            case Win32.WM_ENTERSIZEMOVE:
                _isResizing = true;
                break;

            case Win32.WM_EXITSIZEMOVE:
                _isResizing = false;
                if (_settings != null)
                {
                    _settings.FileJumpPickerWidth = Width;
                    _settings.FileJumpPickerMaxHeight = MaxHeight;
                    if (_userHasResized && ActualHeight > 0)
                        _settings.FileJumpPickerHeight = ActualHeight;
                    _settings.Save();
                }
                break;

            case Win32.WM_DPICHANGED:
                _isOurSetWindowPosForPicker = false;
                break;
            case Win32.WM_WINDOWPOSCHANGING:
                if (!_isOurSetWindowPosForPicker && !_isResizing && _lockJumpPickerNomove)
                {
                    var pos = Marshal.PtrToStructure<Win32.WINDOWPOS>(lParam);
                    pos.flags |= Win32.SWP_NOMOVE;
                    Marshal.StructureToPtr(pos, lParam, false);
                }
                break;
        }
        return IntPtr.Zero;
    }

    private void ApplyPendingPhysicalSetWindowPos(bool noActivate = true)
    {
        if (_hwnd == IntPtr.Zero) return;
        _isOurSetWindowPosForPicker = true;
        try
        {
            var flags = Win32.SWP_NOSIZE | Win32.SWP_NOZORDER;
            if (noActivate) flags |= Win32.SWP_NOACTIVATE;
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, _pendingPhysX, _pendingPhysY, 0, 0, flags);
        }
        finally
        {
            _isOurSetWindowPosForPicker = false;
        }
    }

    private void FileDialogJumpPickerWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (_snappedPhysicalOnce) return;
        _snappedPhysicalOnce = true;

        InstallOwnerDestroyHook();

        var swTotal = Stopwatch.StartNew();

        try
        {
            var sw = Stopwatch.StartNew();
            UpdateLayout();
            PerfLog("content_rendered_update_layout", sw.ElapsedMilliseconds, 16);
            sw.Restart();
            UpdateDockPopupPhysicalSizeCache();
            PerfLog("content_rendered_size_cache", sw.ElapsedMilliseconds, 8);
            sw.Restart();
            ComputePhysicalPosition(useActualSize: true);
            PerfLog("content_rendered_compute_position", sw.ElapsedMilliseconds, 8);
            sw.Restart();
            // 首次布局完成：允许 SetWindowPos 顺带激活，避免 SWP_NOACTIVATE 与「Owned 子窗」叠加导致永远无法抢前台。
            ApplyPendingPhysicalSetWindowPos(noActivate: false);
            PerfLog("content_rendered_set_window_pos", sw.ElapsedMilliseconds, 8);
            _lastAppliedPhysX = _pendingPhysX;
            _lastAppliedPhysY = _pendingPhysY;
            sw.Restart();
            ApplyPendingPhysicalAsWpfLeftTop();
            PerfLog("content_rendered_apply_wpf_left_top", sw.ElapsedMilliseconds, 8);
        }
        catch { /* ignore */ }
        finally
        {
            _lockJumpPickerNomove = false;
        }

        Opacity = 1.0;
        if (!_autoForegroundStickyMode)
            InstallJumpPickerOutsideHooks();
        if (_dockBesideDialog)
        {
            InstallDockOwnerFollowHooks();
            // WinEvent 提供实时跟随；timer 只兜底处理个别宿主不发 LOCATIONCHANGE 的场景。
            _dockFollowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _dockFollowTimer.Tick += (_, _) => DockFollowTick();
            _dockFollowTimer.Start();
        }
        // 弹出后稍后再抢焦点，避免刚开窗立即拖动文件对话框时与系统拖动循环抢前台。
        ScheduleInitialFocusSteal();
        swTotal.Stop();
        PerfLog("content_rendered_total", swTotal.ElapsedMilliseconds, 30,
            $"dock={_dockBesideDialog} sticky={_autoForegroundStickyMode}");
    }

    private void ScheduleInitialFocusSteal()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!IsLoaded || _dockOwnerMoveActive) return;
            TryStealFocusForPicker();
        };
        timer.Start();
    }

    private void DockFollowTick(bool force = false)
    {
        var sw = Stopwatch.StartNew();
        if (!_dockBesideDialog || _hwnd == IntPtr.Zero) return;
        if (!TryReadDockOwnerRect(out var ownerRect))
        {
            Close();
            return;
        }

        UpdateDockPopupPhysicalSizeCache();
        var actualWidth = _dockPopupPhysWidth;
        var actualHeight = _dockPopupPhysHeight;
        if (!force
            && ownerRect.Left == _lastDockOwnerLeft
            && ownerRect.Top == _lastDockOwnerTop
            && ownerRect.Right == _lastDockOwnerRight
            && ownerRect.Bottom == _lastDockOwnerBottom
            && actualWidth == _lastDockActualWidth
            && actualHeight == _lastDockActualHeight)
            return;

        try
        {
            TryRealtimeDockFollow(force);
            RememberDockSnapshot(ownerRect, actualWidth, actualHeight);
        }
        catch { /* ignore */ }
        finally
        {
            sw.Stop();
            if (sw.ElapsedMilliseconds >= 8 && _perfDockFollowSlowLogCount < 60)
            {
                _perfDockFollowSlowLogCount++;
                ClipboardDiagnosticsLog.Write(
                    $"filejump.perf dock_follow_tick elapsedMs={sw.ElapsedMilliseconds} force={force}");
            }
        }
    }

    private void TryRealtimeDockFollow(bool force = false)
    {
        var sw = Stopwatch.StartNew();
        var hwnd = _hwnd;
        if (!_dockBesideDialog || hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return;
        if (!TryReadDockOwnerRect(out var ownerRect)) return;

        var popupW = _dockPopupPhysWidth;
        var popupH = _dockPopupPhysHeight;
        if (popupW <= 0 || popupH <= 0) return;
        if (!FileJumpPickerDockPlacement.TryComputePosition(ownerRect, popupW, popupH, out var x, out var y))
            return;

        if (!force && x == _lastAppliedPhysX && y == _lastAppliedPhysY)
            return;

        _isOurSetWindowPosForPicker = true;
        try
        {
            Win32.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOSENDCHANGING);
            _pendingPhysX = x;
            _pendingPhysY = y;
            _lastAppliedPhysX = x;
            _lastAppliedPhysY = y;
            RememberDockSnapshot(ownerRect, popupW, popupH);
        }
        finally
        {
            _isOurSetWindowPosForPicker = false;
            sw.Stop();
            if (sw.ElapsedMilliseconds >= 8 && _perfDockFollowSlowLogCount < 60)
            {
                _perfDockFollowSlowLogCount++;
                ClipboardDiagnosticsLog.Write(
                    $"filejump.perf realtime_dock_follow elapsedMs={sw.ElapsedMilliseconds} force={force} x={x} y={y}");
            }
        }
    }

    private void UpdateDockPopupPhysicalSizeCache()
    {
        if (!_dockBesideDialog) return;

        try
        {
            var wpfW = ActualWidth > 1 ? ActualWidth : Width;
            var wpfH = ActualHeight > 1 ? ActualHeight : MaxHeight > 1 && MaxHeight < 900 ? MaxHeight : Height;
            var dpiPoint = new Win32.POINT { X = _mouseScreenX, Y = _mouseScreenY };
            if (TryReadDockOwnerRect(out var ownerRect))
            {
                dpiPoint.X = (ownerRect.Left + ownerRect.Right) / 2;
                dpiPoint.Y = (ownerRect.Top + ownerRect.Bottom) / 2;
            }

            var hMon = Win32.MonitorFromPoint(dpiPoint, Win32.MONITOR_DEFAULTTONEAREST);
            Win32.GetDpiForMonitor(hMon, 0, out uint monDpiX, out uint monDpiY);
            _dockPopupPhysWidth = Math.Max(1, (int)Math.Ceiling(wpfW * (monDpiX / 96.0)));
            _dockPopupPhysHeight = Math.Max(1, (int)Math.Ceiling(wpfH * (monDpiY / 96.0)));
        }
        catch
        {
            // ignore
        }
    }

    private bool TryReadDockOwnerRect(out Win32.RECT ownerRect)
    {
        ownerRect = default;
        return _fileDialogOwnerHwnd != IntPtr.Zero
               && Win32.IsWindow(_fileDialogOwnerHwnd)
               && Win32.IsWindowVisible(_fileDialogOwnerHwnd)
               && Win32.GetWindowRect(_fileDialogOwnerHwnd, out ownerRect);
    }

    private void RememberDockSnapshot(Win32.RECT ownerRect, int actualWidth, int actualHeight)
    {
        _lastDockOwnerLeft = ownerRect.Left;
        _lastDockOwnerTop = ownerRect.Top;
        _lastDockOwnerRight = ownerRect.Right;
        _lastDockOwnerBottom = ownerRect.Bottom;
        _lastDockActualWidth = actualWidth;
        _lastDockActualHeight = actualHeight;
    }

    private void ResetDockSnapshot()
    {
        _lastDockOwnerLeft = int.MinValue;
        _lastDockOwnerTop = int.MinValue;
        _lastDockOwnerRight = int.MinValue;
        _lastDockOwnerBottom = int.MinValue;
        _lastDockActualWidth = int.MinValue;
        _lastDockActualHeight = int.MinValue;
    }

    private void InstallDockOwnerFollowHooks()
    {
        s_jumpPickerDockWinEventOwner = this;

        if (_dockOwnerMoveSizeHook == IntPtr.Zero)
        {
            _dockOwnerMoveSizeHook = Win32.SetWinEventHook(
                Win32.EVENT_SYSTEM_MOVESIZESTART,
                Win32.EVENT_SYSTEM_MOVESIZEEND,
                IntPtr.Zero,
                s_jumpPickerDockWinEventThunk,
                0,
                0,
                Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
        }

        if (_dockOwnerLocationHook == IntPtr.Zero)
        {
            _dockOwnerLocationHook = Win32.SetWinEventHook(
                Win32.EVENT_OBJECT_LOCATIONCHANGE,
                Win32.EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero,
                s_jumpPickerDockWinEventThunk,
                0,
                0,
                Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
        }
    }

    private void UninstallDockOwnerFollowHooks()
    {
        if (_dockOwnerMoveSizeHook != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_dockOwnerMoveSizeHook);
            _dockOwnerMoveSizeHook = IntPtr.Zero;
        }

        if (_dockOwnerLocationHook != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_dockOwnerLocationHook);
            _dockOwnerLocationHook = IntPtr.Zero;
        }

        if (s_jumpPickerDockWinEventOwner == this)
            s_jumpPickerDockWinEventOwner = null;
    }

    private bool DockEventBelongsToOwner(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _fileDialogOwnerHwnd == IntPtr.Zero) return false;
        if (!Win32.IsWindow(_fileDialogOwnerHwnd)) return false;

        var ownerRoot = Win32.GetAncestor(_fileDialogOwnerHwnd, Win32.GA_ROOT);
        if (ownerRoot == IntPtr.Zero) return false;

        var eventRoot = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        if (eventRoot == IntPtr.Zero || eventRoot != ownerRoot) return false;
        return hwnd == ownerRoot || hwnd == _fileDialogOwnerHwnd;
    }

    private void ComputePhysicalPosition(bool useActualSize)
    {
        if (_dockBesideDialog
            && _fileDialogOwnerHwnd != IntPtr.Zero
            && Win32.IsWindow(_fileDialogOwnerHwnd)
            && TryApplyDockedPhysical(useActualSize))
            return;

        const int marginX = 14;
        const int marginY = 10;

        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(_mouseScreenX, _mouseScreenY));
        var work = screen.WorkingArea;

        var hMon = Win32.MonitorFromPoint(
            new Win32.POINT { X = _mouseScreenX, Y = _mouseScreenY },
            Win32.MONITOR_DEFAULTTONEAREST);
        Win32.GetDpiForMonitor(hMon, 0, out uint monDpiX, out uint monDpiY);
        double scaleX = monDpiX / 96.0;
        double scaleY = monDpiY / 96.0;

        double wpfW = useActualSize && ActualWidth > 1 ? ActualWidth : Width;
        double wpfH;
        if (useActualSize && ActualHeight > 1)
            wpfH = ActualHeight;
        else
            wpfH = MaxHeight > 1 && MaxHeight < 900 ? MaxHeight : 400;

        int popupW = (int)Math.Ceiling(wpfW * scaleX);
        int popupH = (int)Math.Ceiling(wpfH * scaleY);

        int x = _mouseScreenX + marginX;
        int y = _mouseScreenY + marginY;

        if (x + popupW > work.Right) x = work.Right - popupW;
        if (y + popupH > work.Bottom) y = _mouseScreenY - popupH - marginY;
        if (x < work.Left) x = work.Left;
        if (y < work.Top) y = work.Top;

        _pendingPhysX = x;
        _pendingPhysY = y;
    }

    /// <summary>按文件对话框计算紧贴物理坐标；失败则返回 false，由调用方回退到鼠标定位。</summary>
    private bool TryApplyDockedPhysical(bool useActualSize)
    {
        Win32.POINT dpiPt = new() { X = _mouseScreenX, Y = _mouseScreenY };
        if (Win32.GetWindowRect(_fileDialogOwnerHwnd, out var drDlg))
        {
            dpiPt.X = (drDlg.Left + drDlg.Right) / 2;
            dpiPt.Y = (drDlg.Top + drDlg.Bottom) / 2;
        }
        var hMon = Win32.MonitorFromPoint(dpiPt, Win32.MONITOR_DEFAULTTONEAREST);
        Win32.GetDpiForMonitor(hMon, 0, out uint monDpiX, out uint monDpiY);
        double scaleX = monDpiX / 96.0;
        double scaleY = monDpiY / 96.0;

        double wpfW = useActualSize && ActualWidth > 1 ? ActualWidth : Width;
        double wpfH;
        if (useActualSize && ActualHeight > 1)
            wpfH = ActualHeight;
        else
            wpfH = MaxHeight > 1 && MaxHeight < 900 ? MaxHeight : 400;

        int popupW = (int)Math.Ceiling(wpfW * scaleX);
        int popupH = (int)Math.Ceiling(wpfH * scaleY);

        if (!FileJumpPickerDockPlacement.TryComputePosition(_fileDialogOwnerHwnd, popupW, popupH, out var px, out var py))
            return false;

        _pendingPhysX = px;
        _pendingPhysY = py;
        return true;
    }

    private void ApplyPendingPhysicalAsWpfLeftTop()
    {
        try
        {
            var helper = new WindowInteropHelper(this);
            helper.EnsureHandle();
            var src = HwndSource.FromHwnd(helper.Handle);
            if (src?.CompositionTarget == null) return;

            var dip = src.CompositionTarget.TransformFromDevice.Transform(
                new System.Windows.Point(_pendingPhysX, _pendingPhysY));
            Left = dip.X;
            Top = dip.Y;
        }
        catch { /* ignore */ }
    }

    private void ItemsList_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (FindAncestorListBoxItem(e.OriginalSource as DependencyObject) is { } lbi
            && lbi.DataContext is FileJumpPickerRow row)
        {
            ItemsList.SelectedItem = row;
            CommitSelection(row.Path);
        }
    }

    private void ItemsList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestorListBoxItem(e.OriginalSource as DependencyObject) is { } lbi
            && lbi.DataContext is FileJumpPickerRow row)
            ItemsList.SelectedItem = row;
    }

    /// <summary>
    /// 沿可视/逻辑树向上找最近的 <see cref="ListBoxItem"/>。
    /// 注意：原 <c>OriginalSource</c> 可能是 <see cref="System.Windows.Documents.Run"/> 等
    /// <see cref="ContentElement"/>（来自高亮 TextBlock 的 Inlines），它不是 Visual，
    /// 直接调用 <see cref="VisualTreeHelper.GetParent"/> 会抛
    /// "System.Windows.Documents.Run 不是 Visual 或 Visual3D"。
    /// 因此需要先用 <see cref="LogicalTreeHelper"/> 走到 Visual 节点再切回视觉树。
    /// </summary>
    private static ListBoxItem? FindAncestorListBoxItem(DependencyObject? start)
    {
        var el = start;
        while (el != null && el is not ListBoxItem)
        {
            DependencyObject? parent = null;
            if (el is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                try { parent = VisualTreeHelper.GetParent(el); }
                catch { parent = null; }
            }

            parent ??= LogicalTreeHelper.GetParent(el);
            if (parent == null) return null;
            el = parent;
        }

        return el as ListBoxItem;
    }

    private void JumpRowContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var row = ItemsList.SelectedItem as FileJumpPickerRow;
        CtxAddFavorite.Visibility = row is { IsFavorite: false } ? Visibility.Visible : Visibility.Collapsed;
        CtxRemoveFavorite.Visibility = row is { IsFavorite: true } ? Visibility.Visible : Visibility.Collapsed;
        CtxEditPhrase.Visibility = row is { IsFavorite: true } ? Visibility.Visible : Visibility.Collapsed;
        CtxRemoveRecentFolder.Visibility = row is { IsFavorite: false } && row.SourceLabel.StartsWith("常用路径") ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CtxAddFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not FileJumpPickerRow row || row.IsFavorite) return;
        var def = GuessPhraseFromPath(row.Path);
        var phrase = PromptSimpleText("收藏关键词（用于 everything 筛选）", def);
        if (phrase == null) return;
        phrase = phrase.Trim();
        if (string.IsNullOrEmpty(phrase)) phrase = def;

        _settings.FolderFavorites.RemoveAll(f =>
            string.Equals(f.Path, row.Path, StringComparison.OrdinalIgnoreCase));
        _settings.FolderFavorites.Add(new FolderFavoriteEntry { Phrase = phrase, Path = row.Path });
        _settings.Save();
        BuildMasterList();
        RefreshFilter();
        var i = _displayRows.ToList().FindIndex(r => string.Equals(r.Path, row.Path, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) ItemsList.SelectedIndex = i;
    }

    private void BtnAddCurrentToFavorites_Click(object sender, RoutedEventArgs e)
    {
        ClipboardDiagnosticsLog.Write($"filejump.fav btn_click hwnd=0x{_fileDialogOwnerHwnd.ToInt64():X} isWindow={Win32.IsWindow(_fileDialogOwnerHwnd)} class={Win32.GetWindowClassName(_fileDialogOwnerHwnd)} title={Win32.GetWindowText(_fileDialogOwnerHwnd)}");
        if (_fileDialogOwnerHwnd == IntPtr.Zero || !Win32.IsWindow(_fileDialogOwnerHwnd)) return;

        var ok = FileDialogJumpHelper.TryReadCurrentFolder(_fileDialogOwnerHwnd, out var currentPath, relaxed: true);
        ClipboardDiagnosticsLog.Write($"filejump.fav tryRead={ok} path=\"{currentPath ?? ""}\" dirExists={Directory.Exists(currentPath ?? "")}");

        if (string.IsNullOrEmpty(currentPath) || !Directory.Exists(currentPath)) return;

        if (_settings.FolderFavorites.Any(f =>
            string.Equals(f.Path, currentPath, StringComparison.OrdinalIgnoreCase)))
        {
            var idx = _displayRows.ToList().FindIndex(r =>
                string.Equals(r.Path, currentPath, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) ItemsList.SelectedIndex = idx;
            return;
        }

        var def = GuessPhraseFromPath(currentPath);
        var phrase = PromptSimpleText("收藏关键词（用于 everything 筛选）", def);
        if (phrase == null) return;
        phrase = phrase.Trim();
        if (string.IsNullOrEmpty(phrase)) phrase = def;

        _settings.FolderFavorites.RemoveAll(f =>
            string.Equals(f.Path, currentPath, StringComparison.OrdinalIgnoreCase));
        _settings.FolderFavorites.Add(new FolderFavoriteEntry { Phrase = phrase, Path = currentPath });
        _settings.Save();
        BuildMasterList();
        RefreshFilter();
        var i = _displayRows.ToList().FindIndex(r =>
            string.Equals(r.Path, currentPath, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) ItemsList.SelectedIndex = i;
    }


    private void CtxRemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not FileJumpPickerRow row || !row.IsFavorite) return;
        _settings.FolderFavorites.RemoveAll(f =>
            string.Equals(f.Path, row.Path, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        _collectorSnapshot.RemoveAll(c =>
            string.Equals(c.Path, row.Path, StringComparison.OrdinalIgnoreCase));
        BuildMasterList();
        RefreshFilter();
    }

    private void CtxEditPhrase_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not FileJumpPickerRow row || !row.IsFavorite) return;
        var phrase = PromptSimpleText("修改关键词", row.Phrase);
        if (phrase == null) return;
        phrase = phrase.Trim();
        var fav = _settings.FolderFavorites.FirstOrDefault(f =>
            string.Equals(f.Path, row.Path, StringComparison.OrdinalIgnoreCase));
        if (fav == null) return;
        fav.Phrase = phrase;
        _settings.Save();
        BuildMasterList();
        RefreshFilter();
        var i = _displayRows.ToList().FindIndex(r => string.Equals(r.Path, row.Path, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) ItemsList.SelectedIndex = i;
    }

    private void CtxRemoveRecentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not FileJumpPickerRow row || row.IsFavorite) return;
        _settings.RemoveRecentFileDialogFolder(row.Path);
        _collectorSnapshot.RemoveAll(c =>
            string.Equals(c.Path, row.Path, StringComparison.OrdinalIgnoreCase));
        BuildMasterList();
        RefreshFilter();
    }

    private static string GuessPhraseFromPath(string path)
    {
        try
        {
            var t = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFileName(t);
        }
        catch { return "收藏"; }
    }

    private string? PromptSimpleText(string title, string initial)
    {
        var dlg = new Window
        {
            Title = title,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = TryFindResource("WindowBgBrush") as Media.Brush ?? System.Windows.SystemColors.WindowBrush,
        };
        var tb = new System.Windows.Controls.TextBox
        {
            Text = initial,
            Margin = new Thickness(14, 6, 14, 8),
            FontSize = 13,
            Padding = new Thickness(8, 6, 8, 6),
            Background = TryFindResource("SurfaceBrush") as Media.Brush,
            Foreground = TryFindResource("PrimaryText") as Media.Brush,
            BorderBrush = TryFindResource("ThemeBorder") as Media.Brush,
            CaretBrush = TryFindResource("PrimaryText") as Media.Brush,
        };
        string? result = null;
        var ok = new System.Windows.Controls.Button { Content = "确定", Width = 88, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new System.Windows.Controls.Button { Content = "取消", Width = 88, IsCancel = true };
        ok.Click += (_, _) => { result = tb.Text; dlg.DialogResult = true; };
        cancel.Click += (_, _) => { dlg.DialogResult = false; };
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(14, 4, 14, 14)
        };
        btnRow.Children.Add(ok);
        btnRow.Children.Add(cancel);
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = title,
            Margin = new Thickness(14, 14, 14, 4),
            FontSize = 13,
            Foreground = TryFindResource("PrimaryText") as Media.Brush,
        });
        sp.Children.Add(tb);
        sp.Children.Add(btnRow);
        dlg.Content = sp;
        _suppressJumpHook = true;
        _suppressDismissForSubDialog = true;
        try
        {
            return dlg.ShowDialog() == true ? result?.Trim() : null;
        }
        finally
        {
            _suppressDismissForSubDialog = false;
            _suppressJumpHook = false;
        }
    }

    private void JumpByQuickIndex(int index1To9)
    {
        var row = _displayRows.FirstOrDefault(r => r.DisplayIndex == index1To9);
        if (row != null)
            CommitSelection(row.Path);
    }

    private void MoveSelection(int delta)
    {
        if (_displayRows.Count == 0) return;
        var idx = ItemsList.SelectedIndex + delta;
        if (idx < 0) idx = 0;
        if (idx >= _displayRows.Count) idx = _displayRows.Count - 1;
        ItemsList.SelectedIndex = idx;
        ItemsList.ScrollIntoView(ItemsList.SelectedItem);
    }

    private void ScrollPage(int direction)
    {
        if (_displayRows.Count == 0) return;
        var sv = GetListScrollViewer();
        if (sv == null) return;

        double itemHeight = sv.ExtentHeight / _displayRows.Count;
        if (itemHeight <= 0) return;

        int oldFirstVisible = Math.Max(0, (int)(sv.VerticalOffset / itemHeight));
        int relSelection = Math.Max(0, ItemsList.SelectedIndex - oldFirstVisible);

        double newOffset = sv.VerticalOffset + direction * PageSize * itemHeight;
        newOffset = Math.Max(0, Math.Min(newOffset, sv.ScrollableHeight));
        sv.ScrollToVerticalOffset(newOffset);

        int newFirstVisible = Math.Max(0, (int)(newOffset / itemHeight));
        _firstVisibleIndex = newFirstVisible;
        int newSel = Math.Clamp(newFirstVisible + relSelection, 0, _displayRows.Count - 1);
        ItemsList.SelectedIndex = newSel;

        AssignVisibleQuickIndices(newFirstVisible);
    }

    private ScrollViewer? GetListScrollViewer()
    {
        if (VisualTreeHelper.GetChildrenCount(ItemsList) == 0) return null;
        var border = VisualTreeHelper.GetChild(ItemsList, 0) as Decorator;
        return border?.Child as ScrollViewer;
    }

    private void ItemsList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 || _displayRows.Count == 0) return;
        var sv = GetListScrollViewer();
        if (sv == null) return;

        double itemHeight = sv.ExtentHeight / _displayRows.Count;
        if (itemHeight <= 0) return;

        int newFirstVisible = Math.Max(0, (int)(sv.VerticalOffset / itemHeight));
        if (newFirstVisible == _firstVisibleIndex) return;

        int relSelection = Math.Max(0, ItemsList.SelectedIndex - _firstVisibleIndex);
        _firstVisibleIndex = newFirstVisible;

        int newSel = Math.Clamp(newFirstVisible + relSelection, 0, _displayRows.Count - 1);
        ItemsList.SelectedIndex = newSel;

        AssignVisibleQuickIndices(newFirstVisible);
    }

    private void CommitSelection(string path)
    {
        // 选中即跳转，但跳转列表不自动关闭：由 Esc / 点击列表外 / 文件对话框关闭 等显式动作收起。
        CommitNavigateKeepOpen(path);
    }

    /// <summary>
    /// 外部文件管理器路径变化后，刷新当前候选列表并尽量保持用户关注的路径选中。
    /// </summary>
    public void RefreshCandidatesFromExternal(IReadOnlyList<FileJumpCandidate> fresh, string? preferredPath = null)
    {
        var freshList = fresh as List<FileJumpCandidate> ?? fresh.ToList();
        if (string.IsNullOrEmpty(preferredPath) && CandidateListsEquivalent(_collectorSnapshot, freshList))
        {
            ClipboardDiagnosticsLog.Write(
                $"filejump.perf skip_external_refresh_same fresh={freshList.Count} moveActive={_dockOwnerMoveActive}");
            return;
        }

        _deferredExternalRefresh = freshList;
        _deferredExternalPreferredPath = preferredPath;
        var delayMs = _dockOwnerMoveActive ? 220 : 90;
        ClipboardDiagnosticsLog.Write(
            $"filejump.perf defer_external_refresh moveActive={_dockOwnerMoveActive} delayMs={delayMs} fresh={freshList.Count} preferred={(string.IsNullOrEmpty(preferredPath) ? 0 : 1)}");
        ScheduleDeferredExternalRefresh(delayMs);
    }

    private void ApplyExternalRefreshNow(List<FileJumpCandidate> fresh, string? preferredPath)
    {
        if (string.IsNullOrEmpty(preferredPath) && CandidateListsEquivalent(_collectorSnapshot, fresh))
        {
            ClipboardDiagnosticsLog.Write(
                $"filejump.perf skip_external_refresh_same fresh={fresh.Count} moveActive={_dockOwnerMoveActive}");
            return;
        }
        var sw = Stopwatch.StartNew();
        var selectedPath = preferredPath;
        if (string.IsNullOrEmpty(selectedPath) && ItemsList.SelectedItem is FileJumpPickerRow row)
            selectedPath = row.Path;
        ApplyNavigateKeepOpenListRefresh(selectedPath ?? "", fresh);
        sw.Stop();
        PerfLog("refresh_candidates_external", sw.ElapsedMilliseconds, 25,
            $"fresh={fresh.Count} preferred={(string.IsNullOrEmpty(preferredPath) ? 0 : 1)}");
    }

    private void FlushDeferredExternalRefresh()
    {
        if (_deferredExternalRefresh == null) return;
        ScheduleDeferredExternalRefresh(220);
    }

    private void ScheduleDeferredExternalRefresh(int delayMs)
    {
        _deferredExternalRefreshTimer?.Stop();
        _deferredExternalRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(delayMs)
        };
        _deferredExternalRefreshTimer.Tick += (_, _) =>
        {
            _deferredExternalRefreshTimer?.Stop();
            _deferredExternalRefreshTimer = null;
            if (!IsLoaded) return;
            if (_dockOwnerMoveActive)
            {
                ScheduleDeferredExternalRefresh(220);
                return;
            }
            if (_deferredExternalRefresh == null) return;

            var fresh = _deferredExternalRefresh;
            var preferredPath = _deferredExternalPreferredPath;
            _deferredExternalRefresh = null;
            _deferredExternalPreferredPath = null;
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsLoaded || _dockOwnerMoveActive)
                {
                    _deferredExternalRefresh = fresh;
                    _deferredExternalPreferredPath = preferredPath;
                    ScheduleDeferredExternalRefresh(220);
                    return;
                }

                ApplyExternalRefreshNow(fresh, preferredPath);
            }, DispatcherPriority.ContextIdle);
        };
        _deferredExternalRefreshTimer.Start();
    }

    private static bool CandidateListsEquivalent(IReadOnlyList<FileJumpCandidate> current, IReadOnlyList<FileJumpCandidate> fresh)
    {
        if (current.Count != fresh.Count) return false;
        for (int i = 0; i < current.Count; i++)
        {
            if (!string.Equals(current[i].Path, fresh[i].Path, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.Equals(current[i].Label, fresh[i].Label, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static string NormalizeCommitNavigatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            return Path.GetFullPath(path.Trim()).TrimEnd('\\', '/');
        }
        catch
        {
            return path.Trim().TrimEnd('\\', '/');
        }
    }

    /// <summary>
    /// 粘性模式下由外部触发：保持窗口打开，直接导航到目标路径并在完成后刷新列表。
    /// </summary>
    /// <param name="allowKeyboardFallback">自动同步等无人值守触发传 false：仅注入导航，禁止模拟输入。</param>
    public void NavigateKeepOpenToPath(string path, bool allowKeyboardFallback = true)
    {
        if (!_autoForegroundStickyMode) return;
        if (string.IsNullOrWhiteSpace(path)) return;
        SelectedPath = path;
        CommitNavigateKeepOpen(path, allowKeyboardFallback);
    }

    private void CommitAndClose(string path)
    {
        SelectedPath = path;
        Close();
    }

    /// <summary>粘性自动模式：只切换文件对话框目录并刷新列表，不关闭窗口。</summary>
    private void CommitNavigateKeepOpen(string path, bool allowKeyboardFallback = true)
    {
        var normalizedPath = NormalizeCommitNavigatePath(path);
        if (!string.IsNullOrEmpty(normalizedPath)
            && _commitNavigateKeepOpenUntilTick != 0
            && Environment.TickCount64 <= _commitNavigateKeepOpenUntilTick
            && string.Equals(_commitNavigateKeepOpenPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            return;

        _commitNavigateKeepOpenPath = normalizedPath;
        _commitNavigateKeepOpenUntilTick = Environment.TickCount64 + 1500;
        unchecked { _commitNavigateKeepOpenGen++; }
        var gen = _commitNavigateKeepOpenGen;
        var dlgHwnd = _fileDialogOwnerHwnd;

        // 全局模式（无文件对话框）：直接在资源管理器中打开文件夹
        if (dlgHwnd == IntPtr.Zero)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch { /* ignore */ }
            Close();
            return;
        }
        var allowInject = _settings.EnableShellNavigateInject;
        var memBefore = _settings.LastFileDialogFolder?.Trim();

        // 导航过程会通过 SendInput 向目标对话框发送键盘事件（Alt+N / Ctrl+A / 路径 / Enter）；
        // 低级键盘钩子仍在运行，会拦截 Enter 并再次 CommitSelection → 死循环。
        // 在导航+采集完成前抑制钩子，让按键直接传递到目标对话框。
        _suppressJumpHook = true;

        void StaWork()
        {
            try
            {
                if (!FileDialogJumpHelper.TryNavigateToFolder(dlgHwnd, path, allowInject, allowKeyboardFallback))
                    return;

                string? folderAfter = null;
                try
                {
                    if (FileDialogJumpHelper.TryReadCurrentFolder(dlgHwnd, out var folder)
                        && !string.IsNullOrEmpty(folder))
                        folderAfter = folder;
                }
                catch { /* ignore */ }

                var memForCollect = !string.IsNullOrEmpty(folderAfter?.Trim())
                    ? folderAfter.Trim()
                    : memBefore;

                List<FileJumpCandidate> fresh;
                try
                {
                    fresh = FileManagerPathCollector.CollectCandidates(dlgHwnd, memForCollect,
                        recentFolders: _settings.RecentFileDialogFolders);
                }
                catch
                {
                    // 与原先一致：采集失败则不刷新列表，但仍尽量写入当前目录记忆。
                    var folderOnly = folderAfter;
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (gen != _commitNavigateKeepOpenGen) return;
                        try
                        {
                            if (!string.IsNullOrEmpty(folderOnly))
                                _settings.PushRecentFileDialogFolder(folderOnly);
                        }
                        catch { /* ignore */ }
                    }, DispatcherPriority.Normal);
                    return;
                }

                var folderForSettings = folderAfter;
                Dispatcher.BeginInvoke(() =>
                {
                    if (gen != _commitNavigateKeepOpenGen) return;
                    try
                    {
                        if (!string.IsNullOrEmpty(folderForSettings))
                            _settings.PushRecentFileDialogFolder(folderForSettings);
                    }
                    catch { /* ignore */ }

                    ApplyNavigateKeepOpenListRefresh(path, fresh);
                }, DispatcherPriority.Normal);
            }
            finally
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (gen == _commitNavigateKeepOpenGen)
                        _suppressJumpHook = false;
                }, DispatcherPriority.Normal);
            }
        }

        var th = new Thread(StaWork)
        {
            IsBackground = true,
            Name = "ClipboardX-JumpPicker-NavigateRefresh",
        };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
    }

    private void ApplyNavigateKeepOpenListRefresh(string committedPath, List<FileJumpCandidate> fresh)
    {
        var swTotal = Stopwatch.StartNew();
        _collectorSnapshot.Clear();
        _collectorSnapshot.AddRange(fresh);

        BuildMasterList();
        RefreshFilter(scrollSelection: false);
        var i = _displayRows.ToList().FindIndex(r =>
            string.Equals(r.Path, committedPath, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            ItemsList.SelectedIndex = i;
            if (_displayRows.Count > PageSize)
                ItemsList.ScrollIntoView(ItemsList.SelectedItem);
        }
        else if (_displayRows.Count > 0)
        {
            ItemsList.SelectedIndex = 0;
            if (_displayRows.Count > PageSize)
                ItemsList.ScrollIntoView(ItemsList.SelectedItem);
        }

        if (_dockBesideDialog && _hwnd != IntPtr.Zero)
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    UpdateDockPopupPhysicalSizeCache();
                    TryRealtimeDockFollow(force: true);
                    PerfLog("navigate_refresh_realign", sw.ElapsedMilliseconds, 8);
                }
                catch { /* ignore */ }
            }, DispatcherPriority.Background);
        }
        swTotal.Stop();
        PerfLog("navigate_refresh_total", swTotal.ElapsedMilliseconds, 35,
            $"fresh={fresh.Count} display={_displayRows.Count}");
    }
}
