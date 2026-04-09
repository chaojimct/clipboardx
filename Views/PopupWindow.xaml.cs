using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace ClipboardManager;

public partial class PopupWindow : Window
{
    private const int HotkeyId = 9001;
    private const int HotkeyJumpLastFolderId = 9002;

    private readonly List<ClipboardEntry> _allItems = new();
    private readonly ObservableCollection<ClipboardEntry> _displayItems = new();

    /// <summary>FIFO/LIFO 下：多选 Enter 入队、新复制可自动入队；出队后条目不占批量角标，回到底部列表排序。</summary>
    private readonly List<ClipboardEntry> _batchQueue = new();
#if CLIPX_CLIPBOARD
    /// <summary>全局 Ctrl+V / Shift+Insert 松键出队防抖（毫秒，TickCount64）。</summary>
    private long _lastGlobalPasteQueueAdvanceTick;
    /// <summary>序列化「队列队首 → 剪贴板」写入，避免与监控钩交错或多路 TryPush 争用 OpenClipboard。</summary>
    private readonly SemaphoreSlim _queueClipboardPushLock = new(1, 1);
#endif
    /// <summary>Shift+↑↓ 区间选择的固定锚点（_displayItems 索引，-1 表示未激活）。</summary>
    private int _selectionRangeAnchor = -1;
    /// <summary>Shift+↑↓ 区间选择的移动端（_displayItems 索引）。</summary>
    private int _selectionCursorEnd = -1;
    /// <summary>鼠标 Shift+点击划范围的锚点（最后一次非 Shift 左击行索引，-1 表示未建立）。</summary>
    private int _mouseShiftAnchorIndex = -1;
    private readonly List<(Border Row, Action Activate)> _batchMenuNav = new();
    private int _batchNavIndex;

    private IntPtr _hwnd;
    private IntPtr _targetWindow;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private IntPtr _winEventHook;
    private IntPtr _winEventHookFocus;
    /// <summary>最后一次弹窗关闭时刻（毫秒，TickCount64），用于拦截 Alt 快捷键释放后的单独 Alt 按键。</summary>
    private long _lastPopupHideTick;

    /// <summary>
    /// WH_KEYBOARD_LL / WH_MOUSE_LL 回调由 user32 保存函数指针；Unhook 后仍可能再触发一两次。
    /// 若此时实例字段上的委托已被置 null，CLR 可能已回收闭环委托，导致「callback on garbage collected delegate」崩溃。
    /// 使用进程内长期存活的静态委托 + 当前拥有者指针，避免 lpfn 被回收。
    /// </summary>
    private static readonly Win32.LowLevelKeyboardProc s_popupKeyboardHookThunk = StaticKeyboardHookProc;
    private static readonly Win32.LowLevelMouseProc s_popupMouseHookThunk = StaticMouseHookProc;
    private static IntPtr s_popupKeyboardHookForNext;
    private static PopupWindow? s_popupKeyboardHookOwner;
    private static IntPtr s_popupMouseHookForNext;
    private static PopupWindow? s_popupMouseHookOwner;

    /// <summary>「点击对话框自动跳转首条路径」专用鼠标钩，与剪贴板弹窗钩分离以便弹窗关闭后仍可监听。</summary>
    private static readonly Win32.LowLevelMouseProc s_fileJumpAutoMouseThunk = StaticFileJumpAutoMouseProc;
    private static IntPtr s_fileJumpAutoMouseHookForNext;
    private static PopupWindow? s_fileJumpAutoMouseOwner;

    /// <summary>SetWinEventHook 同样会把托管委托交给系统；须用静态委托避免 Unhook 后晚到回调撞上已回收的实例委托。</summary>
    private static readonly Win32.WinEventDelegate s_popupWinEventThunk = StaticWinEventProc;
    private static PopupWindow? s_popupWinEventOwner;

    private bool _isSettingClipboard;
    /// <summary>从历史粘贴整段流程中：禁止监控线程读剪贴板，避免 Contains/Get 与即将执行的 Set 在同 UI 线程上交错 OpenClipboard。</summary>
    private bool _pasteInProgress;
    /// <summary>连续粘贴多段（批量/队列）时保持 true，整轮结束后才清除 <see cref="_pasteInProgress"/>，避免段间剪贴板回波或 FIFO 自动入队插队。</summary>
    private bool _sequentialPasteHold;
    private bool _isPopupVisible;
    private string _searchText = "";
    private EntryType? _typeFilter;
    private bool _quickPhraseOnly;
    private ClipboardEntry? _contextEntry;
    /// <summary>已按下 Alt，等待 KeyUp：无组合键则打开右键菜单。</summary>
    private bool _ctxAltAwaitRelease;
    private bool _ctxAltComboDuringRelease;
    /// <summary>右键菜单已打开时再次按下 Alt，松开时若无组合键则关闭菜单。</summary>
    private bool _ctxAltCloseMenuArmed;
    private readonly List<(Border Row, Action Activate)> _contextMenuNav = new();
    private int _contextNavIndex;
    /// <summary>当前标为「待二次 Del 删除」的条目，与 <see cref="ClipboardEntry.IsPendingDelete"/> 同步。</summary>
    private ClipboardEntry? _pendingDeleteEntry;

    private const int PageSize = 8;
    private int _firstVisibleIndex;

    private uint _hotkeyModifiers;
    private uint _hotkeyKey;
    private int _maxItems;
    private string _popupPosition = "Caret";
    private double _popupOpacity = 1.0;
    private bool _hideOnSameAppClick = true;
    private string _panelModifierKey = "Ctrl";
    private bool _isDragging;
    private Win32.POINT _dragLastPt;
    private bool _clickReceivedByPopup;
    private int _pendingPhysX, _pendingPhysY;
    private bool _isOurSetWindowPos;
    /// <summary>Show/UpdateLayout 过程中阻止 WPF 改写位置，避免先出现在 (0,0) 或顶边再跳到目标点。</summary>
    private bool _lockPopupWindowNomove;
    private List<QuickPasteEntry> _quickPastes = new();
    private readonly ClipboardHistoryStore _historyStore = new();
    private AppSettings? _appSettings;
    private IntPtr _lastForegroundForDialogTrack = IntPtr.Zero;

    /// <summary>文件对话框跳转：2 秒内第二次 Ctrl+G 直接跳默认项（与列表预选一致）。</summary>
    private const int FileJumpDoubleTapMs = 2000;

    private long _fileJumpLastHotkeyTick;
    private IntPtr _fileJumpLastDialogHwnd = IntPtr.Zero;
    private FileDialogJumpPickerWindow? _activeFileJumpPicker;
    /// <summary>
    /// <see cref="FileDialogJumpPickerWindow"/> 的 ShowDialog 会嵌套消息循环，同优先级的 BeginInvoke 仍可运行；
    /// 若没有此项，狂按跳转热键时会再次进入 ShowDialog，嵌套模态窗导致状态错乱甚至进程退出。
    /// </summary>
    private bool _fileJumpPickerOpenInProgress;
    private int _fileJumpPickerSession;
    private DispatcherTimer? _fileJumpOpenDelayTimer;
    private DispatcherTimer? _fileJumpAutoOpenDebounceTimer;
    private int _fileJumpDelaySession;
    /// <summary>「对话框到前台自动执行」路径采集异步化，避免与 UI 线程争抢；递增后过时结果丢弃。</summary>
    private int _fileJumpAutoForegroundCollectGen;
    /// <summary>手动跳转热键路径采集异步化；连按热键时仅最后一次结果生效（极端情况下或影响「双按直跳」窗口期）。</summary>
    private int _fileJumpHotkeyCollectGen;
    private uint _fileJumpHotkeyModifiers;
    private uint _fileJumpHotkeyKey;

    private IntPtr _fileJumpAutoMouseHook;
    /// <summary>待监听左键的文件对话框 HWND（与前台识别一致）。</summary>
    private IntPtr _fileJumpAutoArmedDialog;
    /// <summary>同上对话框的顶层 HWND，用于判断点击是否落在该对话框 UI 内。</summary>
    private IntPtr _fileJumpAutoArmedRoot;

    /// <summary>已对其实施过「点击后自动跳转」的对话框顶层 HWND；同一窗口存续期间仅跳转一次，避免失焦再切回后重复跳转。</summary>
    private IntPtr _fileJumpAutoFirstJumpDoneRoot;

    /// <summary>已因「对话框成为前台」自动弹出过跳转列表的顶层 HWND；关掉对话框再开才会再次自动弹。</summary>
    private IntPtr _fileJumpAutoOpenPickerDoneRoot;

    /// <summary>「切回对话框自动同步路径」采集代数，递增后过时结果丢弃。</summary>
    private int _fileJumpAutoSyncCollectGen;
    /// <summary>分层等待调度代数：新一次前台切换后旧定时器结果丢弃。</summary>
    private int _fileJumpAutoSyncScheduleGen;
    /// <summary>对话框路径快照分层等待调度代数。</summary>
    private int _dialogSnapshotScheduleGen;
    /// <summary>最近一次离开外部文件管理器时记录到的路径；用于切回文件对话框时优先同步。</summary>
    private string _lastExternalFolder = "";
    private IntPtr _lastExternalManagerRoot = IntPtr.Zero;

    public event Action? SettingsRequested;

    /// <summary>批量模式（普通 / LIFO / FIFO）切换后通知，用于托盘图标等。</summary>
    public event EventHandler? BatchPasteModeChanged;

    public PopupWindow()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _displayItems;
        ItemsList.SelectionChanged += ItemsList_SelectionChanged;
    }

    public void Initialize(AppSettings settings)
    {
        _appSettings = settings;
        _lastForegroundForDialogTrack = Win32.GetForegroundWindow();
        _maxItems = settings.MaxItems;
        _hotkeyModifiers = settings.HotkeyModifiers;
        _hotkeyKey = settings.HotkeyKey;
        _popupPosition = settings.PopupPosition;
        _popupOpacity = settings.PopupOpacity;
        _hideOnSameAppClick = settings.HideOnSameAppClick;
        _panelModifierKey = settings.PanelModifierKey;
        ClipboardEntry.PreviewMaxLines = settings.PreviewMaxLines;
        _quickPastes = settings.QuickPastes;
        _fileJumpHotkeyModifiers = settings.FileJumpHotkeyModifiers;
        _fileJumpHotkeyKey = settings.FileJumpHotkeyKey;

#if CLIPX_CLIPBOARD
        LoadHistoryFromStore();
        LoadQuickPastes();
#endif
        UpdateFooterHints();

        Opacity = _popupOpacity;

        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        _hwnd = helper.Handle;

        var exStyle = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE,
            new IntPtr(exStyle.ToInt64() | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW));

        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);

#if CLIPX_CLIPBOARD
        if (!Win32.RegisterHotKey(_hwnd, HotkeyId, _hotkeyModifiers | Win32.MOD_NOREPEAT, _hotkeyKey))
        {
            System.Windows.MessageBox.Show(
                $"热键 {settings.HotkeyDisplayName} 注册失败，可能被其他程序占用",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
#endif

#if CLIPX_FILEJUMP
        if (!Win32.RegisterHotKey(_hwnd, HotkeyJumpLastFolderId,
                _fileJumpHotkeyModifiers | Win32.MOD_NOREPEAT, _fileJumpHotkeyKey))
        {
            System.Windows.MessageBox.Show(
                $"快捷键 {settings.FileJumpHotkeyDisplayName}（文件对话框跳转）注册失败，可能与其他软件冲突",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
#endif

#if CLIPX_CLIPBOARD
        Win32.AddClipboardFormatListener(_hwnd);
#endif
#if CLIPX_FILEJUMP
        InstallForegroundWatcher();
#endif
        UpdateEmptyState();
        UpdateBatchHeaderUi();
    }

    public void Cleanup()
    {
#if CLIPX_FILEJUMP
        DisarmFileJumpClickToNavigate();
        _fileJumpAutoFirstJumpDoneRoot = IntPtr.Zero;
        _fileJumpAutoOpenDebounceTimer?.Stop();
        _fileJumpAutoOpenDebounceTimer = null;
#endif
        UninstallKeyboardHook();
        UninstallMouseHook();
#if CLIPX_FILEJUMP
        UninstallForegroundWatcher();
#endif
#if CLIPX_CLIPBOARD
        Win32.UnregisterHotKey(_hwnd, HotkeyId);
#endif
#if CLIPX_FILEJUMP
        Win32.UnregisterHotKey(_hwnd, HotkeyJumpLastFolderId);
#endif
#if CLIPX_CLIPBOARD
        Win32.RemoveClipboardFormatListener(_hwnd);
#endif
    }

#if CLIPX_CLIPBOARD
    public bool UpdateHotkey(uint modifiers, uint key)
    {
        Win32.UnregisterHotKey(_hwnd, HotkeyId);
        if (!Win32.RegisterHotKey(_hwnd, HotkeyId, modifiers | Win32.MOD_NOREPEAT, key))
        {
            Win32.RegisterHotKey(_hwnd, HotkeyId, _hotkeyModifiers | Win32.MOD_NOREPEAT, _hotkeyKey);
            return false;
        }
        _hotkeyModifiers = modifiers;
        _hotkeyKey = key;
        return true;
    }
#else
    public bool UpdateHotkey(uint modifiers, uint key) => true;
#endif

    public bool UpdateFileJumpHotkey(uint modifiers, uint key)
    {
        Win32.UnregisterHotKey(_hwnd, HotkeyJumpLastFolderId);
        if (!Win32.RegisterHotKey(_hwnd, HotkeyJumpLastFolderId, modifiers | Win32.MOD_NOREPEAT, key))
        {
            Win32.RegisterHotKey(_hwnd, HotkeyJumpLastFolderId,
                _fileJumpHotkeyModifiers | Win32.MOD_NOREPEAT, _fileJumpHotkeyKey);
            return false;
        }
        _fileJumpHotkeyModifiers = modifiers;
        _fileJumpHotkeyKey = key;
        return true;
    }

    public void ApplySettings(AppSettings settings)
    {
        _maxItems = settings.MaxItems;
        _popupPosition = settings.PopupPosition;
        _popupOpacity = settings.PopupOpacity;
        _hideOnSameAppClick = settings.HideOnSameAppClick;
        _panelModifierKey = settings.PanelModifierKey;
        ClipboardEntry.PreviewMaxLines = settings.PreviewMaxLines;
        Opacity = _popupOpacity;
        _quickPastes = settings.QuickPastes;
        TrimItems();
        UpdateBatchHeaderUi();

        if (!settings.FileJumpAutoOnFirstClick)
        {
            _fileJumpAutoFirstJumpDoneRoot = IntPtr.Zero;
            DisarmFileJumpClickToNavigate();
        }

#if CLIPX_CLIPBOARD
        if (settings.HotkeyModifiers != _hotkeyModifiers || settings.HotkeyKey != _hotkeyKey)
        {
            if (!UpdateHotkey(settings.HotkeyModifiers, settings.HotkeyKey))
            {
                System.Windows.MessageBox.Show(
                    $"热键 {settings.HotkeyDisplayName} 注册失败，已恢复原快捷键",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                settings.HotkeyModifiers = _hotkeyModifiers;
                settings.HotkeyKey = _hotkeyKey;
            }
        }
#endif

        if (settings.FileJumpHotkeyModifiers != _fileJumpHotkeyModifiers
            || settings.FileJumpHotkeyKey != _fileJumpHotkeyKey)
        {
            if (!UpdateFileJumpHotkey(settings.FileJumpHotkeyModifiers, settings.FileJumpHotkeyKey))
            {
                System.Windows.MessageBox.Show(
                    $"文件对话框跳转快捷键 {settings.FileJumpHotkeyDisplayName} 注册失败，已恢复原快捷键",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                settings.FileJumpHotkeyModifiers = _fileJumpHotkeyModifiers;
                settings.FileJumpHotkeyKey = _fileJumpHotkeyKey;
            }
        }

        UpdateFooterHints();
    }

    public void ClearHistory()
    {
#if CLIPX_CLIPBOARD
        _historyStore.DeleteAll();
        _allItems.RemoveAll(x => !x.IsQuickPaste);
        _batchQueue.Clear();
        UpdateBatchOrderProperties();
        RefreshFilter();
        SyncBatchPasteKeyboardHook();
#endif
    }

    #region Quick Paste Management

    private void LoadHistoryFromStore()
    {
        try
        {
            _historyStore.PruneExcess(_maxItems);
            var batch = _historyStore.LoadNewestFirst(_maxItems);
            for (int i = batch.Count - 1; i >= 0; i--)
                _allItems.Insert(0, batch[i]);
        }
        catch { /* ignore */ }
    }

    private void LoadQuickPastes()
    {
        _allItems.RemoveAll(x => x.IsQuickPaste);
        foreach (var qp in _quickPastes)
        {
            _allItems.Add(new ClipboardEntry
            {
                Type = EntryType.Text,
                TextContent = qp.Content,
                ShortcutPhrase = qp.Phrase,
                IsQuickPaste = true,
                CopiedAt = DateTime.MinValue
            });
        }
    }

    private ClipboardEntry? _phraseEditEntry;
    private string _phraseEditBuffer = "";
    private const int PhraseEditMaxLen = 200;

    private void RefreshPhraseEditDisplay()
    {
        if (string.IsNullOrEmpty(_phraseEditBuffer))
        {
            PhraseEditDisplay.Text = "在此输入…";
            if (TryFindResource("MutedText") is System.Windows.Media.Brush mb)
                PhraseEditDisplay.Foreground = mb;
        }
        else
        {
            PhraseEditDisplay.Text = _phraseEditBuffer;
            if (TryFindResource("PrimaryText") is System.Windows.Media.Brush pb)
                PhraseEditDisplay.Foreground = pb;
        }
    }

    private void AddQuickPaste(ClipboardEntry source)
    {
        if (source.Type != EntryType.Text || string.IsNullOrEmpty(source.TextContent))
        {
            System.Windows.MessageBox.Show("仅支持文本类型设为快捷短语", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _phraseEditEntry = source;
        var preview = source.TextContent!.Length > 60
            ? source.TextContent[..60] + "…"
            : source.TextContent;
        PhrasePreview.Text = preview;
        _phraseEditBuffer = source.ShortcutPhrase ?? "";
        RefreshPhraseEditDisplay();
        PhraseEditPopup.IsOpen = true;
    }

    private void CommitPhraseEdit()
    {
        var phrase = _phraseEditBuffer.Trim();
        if (string.IsNullOrWhiteSpace(phrase) || _phraseEditEntry == null)
        {
            PhraseEditPopup.IsOpen = false;
            _phraseEditEntry = null;
            _phraseEditBuffer = "";
            return;
        }

        var source = _phraseEditEntry;
        _quickPastes.RemoveAll(q => q.Content == source.TextContent);
        _quickPastes.Add(new QuickPasteEntry { Phrase = phrase, Content = source.TextContent! });

        if (source.IsQuickPaste)
        {
            source.ShortcutPhrase = phrase;
        }
        else
        {
            _allItems.RemoveAll(x => x.IsQuickPaste && x.TextContent == source.TextContent);
            _allItems.Add(new ClipboardEntry
            {
                Type = EntryType.Text,
                TextContent = source.TextContent,
                ShortcutPhrase = phrase,
                IsQuickPaste = true,
                CopiedAt = DateTime.MinValue
            });
        }

        SaveQuickPastes();
        RefreshFilter();

        PhraseEditPopup.IsOpen = false;
        _phraseEditEntry = null;
        _phraseEditBuffer = "";
    }

    private void SaveQuickPastes()
    {
        var settings = AppSettings.Load();
        settings.QuickPastes = _quickPastes;
        settings.Save();
    }

    private void PhraseConfirm_Click(object sender, MouseButtonEventArgs e) => CommitPhraseEdit();

    private void PhraseCancel_Click(object sender, MouseButtonEventArgs e)
    {
        PhraseEditPopup.IsOpen = false;
        _phraseEditEntry = null;
        _phraseEditBuffer = "";
    }

    #endregion

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        _clickReceivedByPopup = true;
        base.OnPreviewMouseDown(e);
    }

    #region Window Message Hook

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(Win32.MA_NOACTIVATE);

            case Win32.WM_DPICHANGED:
                _isOurSetWindowPos = false;
                break;

            case Win32.WM_WINDOWPOSCHANGING:
                if (!_isOurSetWindowPos && (_isPopupVisible || _lockPopupWindowNomove))
                {
                    var pos = Marshal.PtrToStructure<Win32.WINDOWPOS>(lParam);
                    pos.flags |= Win32.SWP_NOMOVE;
                    Marshal.StructureToPtr(pos, lParam, false);
                }
                break;

#if CLIPX_CLIPBOARD
            case Win32.WM_CLIPBOARDUPDATE:
                OnClipboardUpdate();
                handled = true;
                break;
#endif
            case Win32.WM_HOTKEY:
                switch (wParam.ToInt32())
                {
#if CLIPX_CLIPBOARD
                    case HotkeyId:
                        TogglePopup();
                        handled = true;
                        break;
#endif
                    case HotkeyJumpLastFolderId:
                        TryJumpFileDialogToLastFolder();
                        handled = true;
                        break;
                }
                break;
        }
        return IntPtr.Zero;
    }

    #endregion

    #region Clipboard Monitoring

    private const int ClipbrdECantOpenHResult = unchecked((int)0x800401D0);

    private static bool IsClipboardCantOpen(Exception ex) =>
        ex is COMException com && com.HResult == ClipbrdECantOpenHResult;

    /// <summary>
    /// 读剪贴板时其它进程常短时占用 → CLIPBRD_E_CANT_OPEN；与 TrySetClipboard 类似做短暂重试，避免 monitor outer catch 误判整次更新失败。
    /// </summary>
    private static bool TryReadClipboardBool(Func<bool> read, string tag, int maxRetries = 2, int delayMs = 18)
    {
        for (var i = 0; i < maxRetries; i++)
        {
            try { return read(); }
            catch (Exception ex)
            {
                if (IsClipboardCantOpen(ex))
                {
                    if (i == maxRetries - 1)
                        ClipboardDiagnosticsLog.Write(
                            $"monitor read {tag} gave_up retries={maxRetries} CLIPBRD_E_CANT_OPEN");
                    else
                        Thread.Sleep(delayMs);
                    continue;
                }
                ClipboardDiagnosticsLog.Write($"monitor read {tag} {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
        return false;
    }

    private static T? TryReadClipboard<T>(Func<T> read, string tag, int maxRetries = 2, int delayMs = 18) where T : class
    {
        for (var i = 0; i < maxRetries; i++)
        {
            try { return read(); }
            catch (Exception ex)
            {
                if (IsClipboardCantOpen(ex))
                {
                    if (i == maxRetries - 1)
                        ClipboardDiagnosticsLog.Write(
                            $"monitor read {tag} gave_up retries={maxRetries} CLIPBRD_E_CANT_OPEN");
                    else
                        Thread.Sleep(delayMs);
                    continue;
                }
                ClipboardDiagnosticsLog.Write($"monitor read {tag} {ex.GetType().Name}: {ex.Message}");
                return default;
            }
        }
        return default;
    }

    private void OnClipboardUpdate()
    {
        // 仅跳过：不得在 async Set 尚未收尾时清 _isSettingClipboard，否则下一条 WM_CLIPBOARDUPDATE 会当作用户复制 → AutoBatchEnqueue → TryPush 风暴与 CLIPBRD_E 重试卡顿。
        if (_isSettingClipboard)
        {
            ClipboardDiagnosticsLog.Write("monitor skip self_set");
            return;
        }
        if (ClipboardGate.IsActive) return;
        if (_pasteInProgress)
        {
            ClipboardDiagnosticsLog.Write("monitor skip pasteInProgress (post-paste echo suppressed)");
            return;
        }

        try
        {
            if (TryReadClipboardBool(() => System.Windows.Clipboard.ContainsFileDropList(), nameof(System.Windows.Clipboard.ContainsFileDropList)))
            {
                var files = TryReadClipboard(() => System.Windows.Clipboard.GetFileDropList(), nameof(System.Windows.Clipboard.GetFileDropList));
                if (files != null && files.Count > 0)
                {
                    var paths = files.Cast<string>().ToArray();
                    ClipboardDiagnosticsLog.Write($"monitor FILES in count={paths.Length} {SummarizeFileDropForLog(paths)}");
                    DeduplicateFiles(paths);
                    var fe = new ClipboardEntry { Type = EntryType.Files, FilePaths = paths };
                    _allItems.Insert(0, fe);
                    TrimItems();
                    _historyStore.TryInsert(fe);
                    AutoBatchEnqueueIfNeeded(fe);
                    RefreshFilter();
                    return;
                }
            }

            if (TryReadClipboardBool(() => System.Windows.Clipboard.ContainsText(), nameof(System.Windows.Clipboard.ContainsText)))
            {
                var text = TryReadClipboard<string>(() => System.Windows.Clipboard.GetText(), nameof(System.Windows.Clipboard.GetText));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    DeduplicateText(text);
                    var te = new ClipboardEntry { Type = EntryType.Text, TextContent = text };
                    _allItems.Insert(0, te);
                    TrimItems();
                    _historyStore.TryInsert(te);
                    AutoBatchEnqueueIfNeeded(te);
                    RefreshFilter();
                    return;
                }
            }

            if (TryReadClipboardBool(() => System.Windows.Clipboard.ContainsImage(), nameof(System.Windows.Clipboard.ContainsImage)))
            {
                var image = TryReadClipboard<BitmapSource>(() => System.Windows.Clipboard.GetImage(), nameof(System.Windows.Clipboard.GetImage));
                if (image != null)
                {
                    if (image.CanFreeze) image.Freeze();
                    int pw = image.PixelWidth, ph = image.PixelHeight;
                    // 必须在当前 UI 线程、且在剪贴板内容仍有效时立刻编码；丢到 Task.Run 会导致位图跨线程访问或未定义生命期 → 闪退
                    var sw = Stopwatch.StartNew();
                    byte[]? pngData = null;
                    ClipboardDiagnosticsLog.Write($"monitor IMAGE clipboard GetImage {pw}x{ph} → EncodeToPng(sync UI)");
                    try
                    {
                        pngData = ClipboardEntry.EncodeToPng(image);
                    }
                    catch (Exception ex)
                    {
                        ClipboardDiagnosticsLog.Write(
                            $"monitor EncodeToPng EX {pw}x{ph} elapsedMs={sw.ElapsedMilliseconds} {ex.GetType().Name}: {ex.Message}");
                    }
                    sw.Stop();
                    if (pngData == null)
                        ClipboardDiagnosticsLog.Write(
                            $"monitor EncodeToPng returned null {pw}x{ph} elapsedMs={sw.ElapsedMilliseconds}");
                    else
                    {
                        try
                        {
                            ClipboardDiagnosticsLog.Write(
                                $"monitor EncodeToPng OK {pw}x{ph} outBytes={pngData.Length} elapsedMs={sw.ElapsedMilliseconds}");
                            DeduplicateImageByMd5(pngData);
                            var ie = new ClipboardEntry
                            {
                                Type = EntryType.Image, ImageData = pngData,
                                ImageWidth = pw, ImageHeight = ph
                            };
                            _allItems.Insert(0, ie);
                            TrimItems();
                            _historyStore.TryInsert(ie);
                            AutoBatchEnqueueIfNeeded(ie);
                            RefreshFilter();
                            ClipboardDiagnosticsLog.Write($"monitor history inserted image outBytes={pngData.Length}");
                        }
                        catch (Exception ex)
                        {
                            ClipboardDiagnosticsLog.Write(
                                $"monitor history insert EX {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ClipboardDiagnosticsLog.Write($"monitor outer catch {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>日志用：路径数量、前若干项文件体积估算、首路径缩写（避免单行过长）。</summary>
    private static string SummarizeFileDropForLog(string[] paths, int maxStatFiles = 48)
    {
        if (paths.Length == 0) return "(empty)";
        long sum = 0;
        int n = Math.Min(paths.Length, maxStatFiles);
        for (int i = 0; i < n; i++)
        {
            try
            {
                var p = paths[i];
                if (File.Exists(p)) sum += new FileInfo(p).Length;
            }
            catch { /* ignore */ }
        }
        var first = paths[0];
        if (first.Length > 120) first = first[..117] + "...";
        return $"sampleFiles={n}/{paths.Length} sampleBytes≈{sum} first=\"{first}\"";
    }

    private void DeduplicateText(string text)
    {
        foreach (var x in _allItems.Where(x => x.Type == EntryType.Text && !x.IsQuickPaste && x.TextContent == text))
            _historyStore.TryDelete(x.PersistedId);
        _allItems.RemoveAll(x => x.Type == EntryType.Text && !x.IsQuickPaste && x.TextContent == text);
        // 一次复制常连发多条 WM_CLIPBOARDUPDATE；旧条已从列表移除，若不清理队列会叠多条同内容角标
        _batchQueue.RemoveAll(x => x.Type == EntryType.Text && !x.IsQuickPaste && x.TextContent == text);
    }

    private void DeduplicateFiles(string[] paths)
    {
        var key = string.Join("|", paths);
        foreach (var x in _allItems.Where(x => x.Type == EntryType.Files && string.Join("|", x.FilePaths ?? []) == key))
            _historyStore.TryDelete(x.PersistedId);
        _allItems.RemoveAll(x => x.Type == EntryType.Files && string.Join("|", x.FilePaths ?? []) == key);
        _batchQueue.RemoveAll(x => x.Type == EntryType.Files && string.Join("|", x.FilePaths ?? []) == key);
    }

    /// <summary>按 PNG 字节 MD5 去掉已有相同图片（含本程序粘贴触发的重复监控写入）。</summary>
    private void DeduplicateImageByMd5(byte[] pngData)
    {
        if (pngData == null || pngData.Length == 0) return;
        var hex = ClipboardEntry.ComputeImageBytesMd5Hex(pngData);
        if (hex.Length == 0) return;
        foreach (var x in _allItems.Where(x => x.Type == EntryType.Image && !x.IsQuickPaste && x.ImageContentMd5Hex == hex))
            _historyStore.TryDelete(x.PersistedId);
        _allItems.RemoveAll(x => x.Type == EntryType.Image && !x.IsQuickPaste && x.ImageContentMd5Hex == hex);
        _batchQueue.RemoveAll(x => x.Type == EntryType.Image && !x.IsQuickPaste && x.ImageContentMd5Hex == hex);
    }

    private void TrimItems()
    {
        var regular = _allItems.Where(x => !x.IsQuickPaste).ToList();
        while (regular.Count > _maxItems)
        {
            var last = regular[^1];
            _historyStore.TryDelete(last.PersistedId);
            _allItems.Remove(last);
            _batchQueue.Remove(last);
            regular.RemoveAt(regular.Count - 1);
        }
    }

    #endregion

    #region Search & Filter

    /// <param name="preferSelectListIndex">
    /// 刷新后希望选中的行（0-based，对应当前列表）。删除条目时传「被删项在原列表中的索引」，则选中同一位置（由下一项顶替），删最后一项则选中新末项。
    /// 其他场景省略或传 null，默认选中第 0 条。
    /// </param>
    private void RefreshFilter(int? preferSelectListIndex = null)
    {
        CloseEntryPreviewBubble();
        ClearPendingDelete();
        _displayItems.Clear();
        _firstVisibleIndex = 0;
        var query = _searchText.Trim();

        IEnumerable<ClipboardEntry> items = _allItems;

        if (_quickPhraseOnly)
            items = items.Where(i => i.IsQuickPaste);

        if (_typeFilter.HasValue)
            items = items.Where(i => i.Type == _typeFilter.Value);

        if (!string.IsNullOrEmpty(query))
            items = items.Where(i => i.MatchesSearch(query));

        var filtered = items.ToList();
        var filteredSet = filtered.ToHashSet();
        UpdateBatchOrderProperties();
        var queuePart = _batchQueue.Where(e => filteredSet.Contains(e)).ToList();
        var qset = new HashSet<ClipboardEntry>(_batchQueue);
        var rest = filtered
            .Where(e => !qset.Contains(e))
            .OrderByDescending(i => i.IsQuickPaste && !string.IsNullOrEmpty(_searchText))
            .ThenByDescending(i => i.CopiedAt);

        int idx = 1;
        foreach (var item in queuePart)
        {
            item.DisplayIndex = idx++;
            _displayItems.Add(item);
        }
        foreach (var item in rest)
        {
            item.DisplayIndex = idx++;
            _displayItems.Add(item);
        }

        UpdateEmptyState();
        if (_displayItems.Count > 0)
        {
            int sel = preferSelectListIndex.HasValue
                ? Math.Clamp(preferSelectListIndex.Value, 0, _displayItems.Count - 1)
                : 0;
            ItemsList.SelectedIndex = sel;
            _mouseShiftAnchorIndex = sel;
            ItemsList.ScrollIntoView(ItemsList.SelectedItem);
        }
    }

    private void UpdateSearchUI()
    {
        var hasSearch = _searchText.Length > 0;
        SearchBarPanel.Visibility = hasSearch ? Visibility.Visible : Visibility.Collapsed;
        SearchTextBlock.Text = _searchText;
        SearchCountText.Text = hasSearch ? $"{_displayItems.Count} 条结果" : "";
    }

    private void UpdateEmptyState()
    {
        UpdateSearchUI();
        var hasItems = _displayItems.Count > 0;
        EmptyHint.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        ItemsList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;

        if (_searchText.Length > 0 && !hasItems)
        {
            EmptyIcon.Text = "🔍"; EmptyText.Text = "无匹配结果";
            EmptySubText.Text = "可试拼音全拼或首字母，如「nihao」「nh」";
        }
        else if (!hasItems)
        {
            EmptyIcon.Text = "📭"; EmptyText.Text = "暂无剪切板记录"; EmptySubText.Text = "复制一些文本即可开始";
        }

        var regularCount = _allItems.Count(x => !x.IsQuickPaste);
        ItemCountText.Text = regularCount > 0 ? $"({regularCount})" : "";
    }

    #endregion

    #region Multi-paste batch queue

    private BatchPasteQueueMode GetBatchMode()
    {
        if (_appSettings == null) return BatchPasteQueueMode.Off;
        return Enum.TryParse<BatchPasteQueueMode>(_appSettings.BatchPasteMode, true, out var m)
            ? m
            : BatchPasteQueueMode.Off;
    }

    private void SetBatchPasteMode(BatchPasteQueueMode mode)
    {
        if (_appSettings == null) return;
        var prev = GetBatchMode();
        _appSettings.BatchPasteMode = mode.ToString();
        if (mode == BatchPasteQueueMode.Off)
            _batchQueue.Clear();
        _appSettings.Save();
        UpdateBatchHeaderUi();
        UpdateBatchOrderProperties();
        RefreshFilter();
#if CLIPX_CLIPBOARD
        SyncBatchPasteKeyboardHook();
#endif
        if (prev != mode)
            BatchPasteModeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateBatchHeaderUi()
    {
        ApplyBatchModeChromeResources();
        if (BatchModeHeaderText == null) return;
        BatchModeHeaderText.Text = GetBatchMode() switch
        {
            BatchPasteQueueMode.Fifo => "FIFO",
            BatchPasteQueueMode.Lifo => "LIFO",
            _ => "普通"
        };
    }

    /// <summary>顶栏模式 Tag、列表队列角标、列表选中/悬停与托盘图标共用 <see cref="TrayIconSvg"/> 模式主色。</summary>
    private void ApplyBatchModeChromeResources()
    {
        var mode = GetBatchMode();
        var fill = HexToFrozenBrush(TrayIconSvg.GetModeMainHex(mode));
        Resources["BatchModeTagFill"] = fill;
        Resources["BatchModeBadgeFill"] = fill;
        Resources["BatchModeTagFg"] = System.Windows.Media.Brushes.White;
        Resources["BatchModeBadgeFg"] = System.Windows.Media.Brushes.White;
        ApplyListSelectionBrushesForMode(mode);
    }

    private void ApplyListSelectionBrushesForMode(BatchPasteQueueMode mode)
    {
        var (mr, mg, mb) = ParseHexRgb(TrayIconSvg.GetModeMainHex(mode));
        if (IsDarkThemeEffective())
        {
            Resources["HoverBrush"] = MixRgbOnDarkEditor(mr, mg, mb, 7, 18);
            Resources["SelectedBrush"] = MixRgbOnDarkEditor(mr, mg, mb, 12, 13);
        }
        else
        {
            Resources["HoverBrush"] = MixRgbOnLightWindow(mr, mg, mb, 5, 20);
            Resources["SelectedBrush"] = MixRgbOnLightWindow(mr, mg, mb, 10, 15);
        }
    }

    private bool IsDarkThemeEffective()
    {
        if (_appSettings == null) return ThemeManager.IsSystemDark();
        return _appSettings.Theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => ThemeManager.IsSystemDark()
        };
    }

    private static (byte R, byte G, byte B) ParseHexRgb(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return (0x13, 0x94, 0x93);
        return (
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
    }

    /// <summary>与 <see cref="ThemeManager"/> 暗色列表混合比例一致，仅前景换为当前模式主色。</summary>
    private static SolidColorBrush MixRgbOnDarkEditor(byte r, byte g, byte b, int wFg, int wBg)
    {
        const byte bg = 0x1E;
        return MixRgbOnSolid(r, g, b, bg, bg, bg, wFg, wBg);
    }

    /// <summary>亮色窗口底 <see cref="ThemeManager"/> 浅灰混模式主色。</summary>
    private static SolidColorBrush MixRgbOnLightWindow(byte r, byte g, byte b, int wFg, int wBg)
    {
        return MixRgbOnSolid(r, g, b, 0xEF, 0xF1, 0xF5, wFg, wBg);
    }

    private static SolidColorBrush MixRgbOnSolid(
        byte r, byte g, byte b,
        byte bgR, byte bgG, byte bgB,
        int wFg, int wBg)
    {
        var d = wFg + wBg;
        if (d <= 0)
            d = 1;
        byte M(byte f, byte bg) => (byte)((f * (long)wFg + bg * (long)wBg) / d);
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(M(r, bgR), M(g, bgG), M(b, bgB)));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush HexToFrozenBrush(string hex)
    {
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>顺序：普通（关队列）→ LIFO → FIFO → 普通。全局快捷键由 App 侧消息窗口注册，与顶栏左键相同。</summary>
    public void CycleBatchPasteMode()
    {
        var next = GetBatchMode() switch
        {
            BatchPasteQueueMode.Off => BatchPasteQueueMode.Lifo,
            BatchPasteQueueMode.Lifo => BatchPasteQueueMode.Fifo,
            _ => BatchPasteQueueMode.Off
        };
        SetBatchPasteMode(next);
    }

    private void UpdateBatchOrderProperties()
    {
        foreach (var e in _allItems)
            e.BatchOrder = 0;
        int o = 1;
        foreach (var e in _batchQueue)
            e.BatchOrder = o++;
    }

    /// <summary>队列整体移到 <see cref="_allItems"/> 最前（与列表顶栏展示顺序一致）。</summary>
    private void ReorderAllItemsQueueFirst()
    {
        if (_batchQueue.Count == 0) return;
        var qset = new HashSet<ClipboardEntry>(_batchQueue);
        var tail = _allItems.Where(e => !qset.Contains(e)).ToList();
        _allItems.Clear();
        foreach (var e in _batchQueue)
            _allItems.Add(e);
        foreach (var e in tail)
            _allItems.Add(e);
    }

#if CLIPX_CLIPBOARD
    /// <summary>面板显示或 FIFO/LIFO 且队列非空时需 WH_KEYBOARD_LL（隐藏面板时仅靠此项收全局粘贴键）。</summary>
    private void SyncBatchPasteKeyboardHook()
    {
        var need = _isPopupVisible
            || (GetBatchMode() != BatchPasteQueueMode.Off && _batchQueue.Count > 0);
        if (need)
            InstallKeyboardHook();
        else
            UninstallKeyboardHook();
    }
#else
    private void SyncBatchPasteKeyboardHook() { }
#endif

#if CLIPX_CLIPBOARD
    /// <summary>FIFO/LIFO：多选 Enter 入队（不立即粘贴），顶栏序号；目标应用内每次 Ctrl+V / Shift+Insert 松键出队并推进剪贴板。</summary>
    private void EnqueueSelectedForBatchPasteMode()
    {
        var mode = GetBatchMode();
        if (mode == BatchPasteQueueMode.Off) return;

        var ordered = ItemsList.SelectedItems.Cast<ClipboardEntry>()
            .Where(e => _displayItems.Contains(e))
            .OrderBy(e => _displayItems.IndexOf(e))
            .ToList();
        if (ordered.Count == 0) return;

        foreach (var e in ordered)
        {
            _batchQueue.Remove(e);
            if (mode == BatchPasteQueueMode.Fifo)
                _batchQueue.Add(e);
            else
                _batchQueue.Insert(0, e);
        }
        UpdateBatchOrderProperties();
        ReorderAllItemsQueueFirst();
        RefreshFilter(0);
        SyncBatchPasteKeyboardHook();
        _ = TryPushClipboardQueueHeadAsync();
    }

    private void TryAdvancePasteQueueAfterGlobalPaste()
    {
        if (GetBatchMode() == BatchPasteQueueMode.Off || _batchQueue.Count == 0) return;

        var done = _batchQueue[0];
        _batchQueue.RemoveAt(0);
        if (!done.IsQuickPaste)
        {
            done.TouchCopiedTime();
            if (done.PersistedId is long pid)
                _historyStore.TryUpdateCopiedAt(pid, done.CopiedAt);
        }
        UpdateBatchOrderProperties();
        ReorderAllItemsQueueFirst();
        RefreshFilter(0);
        SyncBatchPasteKeyboardHook();
        _ = TryPushClipboardQueueHeadAsync();
    }

    /// <summary>仅写剪贴板为队首，不发按键；供入队后目标中 Ctrl+V / Shift+Insert 粘贴衔接。</summary>
    private async Task TryPushClipboardQueueHeadAsync()
    {
        await _queueClipboardPushLock.WaitAsync();
        try
        {
            if (_batchQueue.Count == 0) return;
            var item = _batchQueue[0];

            _isSettingClipboard = true;
            try
            {
                if (_hwnd != IntPtr.Zero)
                    Win32.TryEmptyClipboardAfterOpen(_hwnd);

                var ok = false;
                const int clipRetries = 8;
                const int clipRetryDelayMs = 55;
                try
                {
                    switch (item.Type)
                    {
                        case EntryType.Text:
                            ok = await TrySetClipboardAsync(
                                () => System.Windows.Clipboard.SetText(item.TextContent ?? ""),
                                $"queueHead SetText len={item.TextContent?.Length ?? 0}",
                                maxRetries: clipRetries,
                                delayMs: clipRetryDelayMs,
                                clipNudgeHwnd: _hwnd);
                            break;
                        case EntryType.Image:
                        {
                            BitmapImage? bi = null;
                            using (var ms = new MemoryStream(item.ImageData!))
                            {
                                bi = new BitmapImage();
                                bi.BeginInit();
                                bi.StreamSource = ms;
                                bi.CacheOption = BitmapCacheOption.OnLoad;
                                bi.EndInit();
                            }
                            if (bi != null && bi.CanFreeze) bi.Freeze();
                            if (bi != null)
                            {
                                ok = await TrySetClipboardAsync(
                                    () => System.Windows.Clipboard.SetImage(bi),
                                    $"queueHead SetImage {bi.PixelWidth}x{bi.PixelHeight}",
                                    maxRetries: clipRetries,
                                    delayMs: clipRetryDelayMs,
                                    clipNudgeHwnd: _hwnd);
                            }
                            if (!ok && item.ImageData is { Length: > 0 })
                            {
                                string? tmpPath = null;
                                try
                                {
                                    var dir = Path.Combine(Path.GetTempPath(), "ClipboardX");
                                    Directory.CreateDirectory(dir);
                                    tmpPath = Path.Combine(dir, $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}_fb.png");
                                    File.WriteAllBytes(tmpPath, item.ImageData);
                                    var flFb = new StringCollection();
                                    flFb.Add(tmpPath);
                                    ok = await TrySetClipboardAsync(
                                        () => System.Windows.Clipboard.SetFileDropList(flFb),
                                        "queueHead SetFileDropList imageFallback",
                                        maxRetries: clipRetries,
                                        delayMs: clipRetryDelayMs,
                                        clipNudgeHwnd: _hwnd);
                                    if (!ok && tmpPath != null) try { File.Delete(tmpPath); } catch { /* ignore */ }
                                }
                                catch (Exception ex)
                                {
                                    ClipboardDiagnosticsLog.Write($"queueHead image fallback EX {ex.GetType().Name}: {ex.Message}");
                                    if (tmpPath != null) try { File.Delete(tmpPath); } catch { /* ignore */ }
                                }
                            }
                            break;
                        }
                        case EntryType.Files:
                        {
                            var fl = new StringCollection();
                            fl.AddRange(item.FilePaths!);
                            ok = await TrySetClipboardAsync(
                                () => System.Windows.Clipboard.SetFileDropList(fl),
                                $"queueHead count={fl.Count}",
                                maxRetries: clipRetries,
                                delayMs: clipRetryDelayMs,
                                clipNudgeHwnd: _hwnd);
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    ClipboardDiagnosticsLog.Write($"queueHead unexpected {ex.GetType().Name}: {ex.Message}");
                }

                ClipboardDiagnosticsLog.Write($"queueHead ok={ok}");
                await Task.Delay(10);
            }
            finally
            {
                _isSettingClipboard = false;
            }
        }
        finally
        {
            _queueClipboardPushLock.Release();
        }
    }
#else
    private void EnqueueSelectedForBatchPasteMode() { }
    private void TryAdvancePasteQueueAfterGlobalPaste() { }
    private Task TryPushClipboardQueueHeadAsync() => Task.CompletedTask;
#endif

    private void AutoBatchEnqueueIfNeeded(ClipboardEntry entry)
    {
#if CLIPX_CLIPBOARD
        if (entry.IsQuickPaste) return;
        var mode = GetBatchMode();
        if (mode == BatchPasteQueueMode.Off) return;

        _batchQueue.Remove(entry);
        if (mode == BatchPasteQueueMode.Fifo)
            _batchQueue.Add(entry);
        else
            _batchQueue.Insert(0, entry);
        UpdateBatchOrderProperties();
        ReorderAllItemsQueueFirst();
        SyncBatchPasteKeyboardHook();
        _ = TryPushClipboardQueueHeadAsync();
#endif
    }

    private void BatchModeHeader_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CycleBatchPasteMode();
    }

    private void BatchModeHeader_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenBatchMenuPopup();
    }

    private void OpenBatchMenuPopup()
    {
        BatchMenuPopup.PlacementTarget = BatchModeHeaderBorder;
        BatchMenuPopup.Placement = PlacementMode.Bottom;
        RebuildBatchMenuNav();
        _batchNavIndex = 0;
        BatchMenuPopup.IsOpen = true;
        ApplyBatchMenuHighlight();
    }

    private void CloseBatchMenuNavUi()
    {
        foreach (var (row, _) in _batchMenuNav)
            row.ClearValue(Border.BackgroundProperty);
        _batchMenuNav.Clear();
        _batchNavIndex = 0;
    }

    private void RebuildBatchMenuNav()
    {
        _batchMenuNav.Clear();
        void Add(Border b, Action a) => _batchMenuNav.Add((b, a));
        Add(BatchRowPasteAll, ActivateBatchPasteAll);
    }

    private void ApplyBatchMenuHighlight()
    {
        var hi = FindResource("SelectedBrush") as Brush ?? System.Windows.Media.Brushes.LightGray;
        for (int i = 0; i < _batchMenuNav.Count; i++)
            _batchMenuNav[i].Row.Background = i == _batchNavIndex ? hi : System.Windows.Media.Brushes.Transparent;
    }

    private void MoveBatchMenuHighlight(int delta)
    {
        if (_batchMenuNav.Count == 0) return;
        _batchNavIndex = (_batchNavIndex + delta + _batchMenuNav.Count) % _batchMenuNav.Count;
        ApplyBatchMenuHighlight();
    }

    private void ActivateBatchMenuHighlight()
    {
        if (_batchMenuNav.Count == 0) return;
        if (_batchNavIndex < 0 || _batchNavIndex >= _batchMenuNav.Count) return;
        _batchMenuNav[_batchNavIndex].Activate();
    }

    private async void ActivateBatchPasteAll()
    {
        BatchMenuPopup.IsOpen = false;
        CloseBatchMenuNavUi();
        var list = _batchQueue.ToList();
        if (list.Count == 0) return;
        _batchQueue.Clear();
        UpdateBatchOrderProperties();
        RefreshFilter(0);
        SyncBatchPasteKeyboardHook();
        var mergeText = _appSettings?.BatchPasteMergeText ?? true;
        if (IsAllTextEntries(list) && mergeText)
            await RunAllTextBatchSingleClipboardAsync(list, newlineAfterEachTextWhenCtrlEnter: false);
        else if (mergeText)
            await RunOrderedPastesWithAdjacentTextMergeAsync(list, newlineAfterEachTextWhenCtrlEnter: false);
        else
            await RunSequentialPastesAsync(list);
    }

    private void BatchMenu_PasteAll_Click(object sender, MouseButtonEventArgs e) => ActivateBatchPasteAll();

    private void HandleMainEnterKey(bool ctrlHeldWithEnter = false)
    {
        if (ItemsList.SelectedItems.Count > 1)
        {
            if (GetBatchMode() != BatchPasteQueueMode.Off)
            {
                EnqueueSelectedForBatchPasteMode();
                return;
            }
            _ = PasteMultipleSelectedInOrderAsync(newlineAfterEachTextWhenCtrlEnter: ctrlHeldWithEnter);
            return;
        }
        if (GetBatchMode() != BatchPasteQueueMode.Off && _batchQueue.Count > 0)
        {
            _ = PasteBatchQueueHeadAsync();
            return;
        }
        PasteSelectedItem();
    }

    private async Task PasteBatchQueueHeadAsync()
    {
        if (_batchQueue.Count == 0) return;
        var item = _batchQueue[0];
        _batchQueue.RemoveAt(0);
        if (!item.IsQuickPaste)
        {
            item.TouchCopiedTime();
            if (item.PersistedId is long pid)
                _historyStore.TryUpdateCopiedAt(pid, item.CopiedAt);
        }
        UpdateBatchOrderProperties();
        ReorderAllItemsQueueFirst();
        RefreshFilter(0);
        try
        {
            await PasteEntryAsync(item, hidePopupAfter: true);
        }
        catch (Exception ex)
        {
            // 恢复队列以防粘贴失败
            _batchQueue.Insert(0, item);
            UpdateBatchOrderProperties();
            ReorderAllItemsQueueFirst();
            RefreshFilter(0);
            ClipboardDiagnosticsLog.Write($"PasteBatchQueueHeadAsync failed, restored queue: {ex.Message}");
            return;
        }
#if CLIPX_CLIPBOARD
        SyncBatchPasteKeyboardHook();
        if (_batchQueue.Count > 0)
            await TryPushClipboardQueueHeadAsync();
#endif
    }

    private async Task PasteMultipleSelectedInOrderAsync(bool newlineAfterEachTextWhenCtrlEnter = false)
    {
        var ordered = ItemsList.SelectedItems.Cast<ClipboardEntry>()
            .Where(e => _displayItems.Contains(e))
            .OrderBy(e => _displayItems.IndexOf(e))
            .ToList();
        if (ordered.Count == 0) return;
        if (ordered.Count == 1)
        {
            await PasteEntryAsync(ordered[0], hidePopupAfter: true);
            return;
        }

        var mergeText = _appSettings?.BatchPasteMergeText ?? true;
        if (IsAllTextEntries(ordered) && mergeText)
            await RunAllTextBatchSingleClipboardAsync(ordered, newlineAfterEachTextWhenCtrlEnter);
        else if (mergeText)
            await RunOrderedPastesWithAdjacentTextMergeAsync(ordered, newlineAfterEachTextWhenCtrlEnter);
        else
            await RunSequentialPastesAsync(ordered, newlineAfterEachTextWhenCtrlEnter);
    }

    private static bool IsAllTextEntries(IReadOnlyList<ClipboardEntry> items) =>
        items.Count > 0 && items.All(e => e.Type == EntryType.Text);

    /// <summary>保持顺序将列表切成连续纯文段与其它条目（每段 1 条非文本，或连续文本）。</summary>
    private static List<List<ClipboardEntry>> BuildAdjacentTextRuns(IReadOnlyList<ClipboardEntry> ordered)
    {
        var segments = new List<List<ClipboardEntry>>();
        var i = 0;
        while (i < ordered.Count)
        {
            if (ordered[i].Type == EntryType.Text)
            {
                var run = new List<ClipboardEntry>();
                while (i < ordered.Count && ordered[i].Type == EntryType.Text)
                {
                    run.Add(ordered[i]);
                    i++;
                }
                segments.Add(run);
            }
            else
            {
                segments.Add([ordered[i]]);
                i++;
            }
        }
        return segments;
    }

    /// <summary>开启合并粘贴且条目类型混合时：仅将相邻的纯文本合并成一段粘贴，图/文件等仍分段粘贴。</summary>
    private async Task RunOrderedPastesWithAdjacentTextMergeAsync(
        IReadOnlyList<ClipboardEntry> items,
        bool newlineAfterEachTextWhenCtrlEnter)
    {
        var segments = BuildAdjacentTextRuns(items);
        _sequentialPasteHold = true;
        try
        {
            var opIndex = 0;
            for (var s = 0; s < segments.Count; s++)
            {
                var seg = segments[s];
                var isLast = s == segments.Count - 1;
                if (seg.Count >= 2 && IsAllTextEntries(seg))
                {
                    await RunAllTextBatchSingleClipboardAsync(
                        seg,
                        newlineAfterEachTextWhenCtrlEnter,
                        hidePopupAfter: isLast,
                        applyHistoryReorder: false,
                        ownsGlobalPasteState: false);
                }
                else
                {
                    await PasteEntryAsync(
                        seg[0],
                        hidePopupAfter: isLast,
                        sequentialSegmentIndex: opIndex,
                        sendNewlineAfterTextWhenCtrlEnterBatch: newlineAfterEachTextWhenCtrlEnter);
                }
                opIndex++;
                if (!isLast)
                    await Task.Delay(SequentialInterSegmentDelayMs);
            }

            if (items.Count > 0)
                ApplyDeferredSequentialPasteHistoryOrder(items);

            if (items.Count > 0)
                await Task.Delay(SequentialTailSettleMs);
        }
        finally
        {
            _sequentialPasteHold = false;
            _pasteInProgress = false;
        }
    }

    /// <summary>纯文本多选：拼成一段一次 SetClipboard + 一次粘贴，等价于逐条贴接在一起，但无 N 次剪贴板轮询与段间等待。</summary>
    private async Task RunAllTextBatchSingleClipboardAsync(
        IReadOnlyList<ClipboardEntry> ordered,
        bool newlineAfterEachTextWhenCtrlEnter,
        bool hidePopupAfter = true,
        bool applyHistoryReorder = true,
        bool ownsGlobalPasteState = true)
    {
        int cap = 0;
        var nlLen = Environment.NewLine.Length;
        foreach (var e in ordered)
        {
            cap += (e.TextContent?.Length ?? 0);
            if (newlineAfterEachTextWhenCtrlEnter)
                cap += nlLen;
        }
        var sb = new StringBuilder(cap + 8);
        foreach (var e in ordered)
        {
            sb.Append(e.TextContent);
            if (newlineAfterEachTextWhenCtrlEnter)
                sb.Append(Environment.NewLine);
        }
        var combined = sb.ToString();

        if (ownsGlobalPasteState)
            _sequentialPasteHold = true;
        _pasteInProgress = true;
        try
        {
            ClearPendingDelete();
            if (_targetWindow != IntPtr.Zero && !Win32.IsWindow(_targetWindow))
                _targetWindow = IntPtr.Zero;

            ClipboardDiagnosticsLog.Write(
                $"paste BATCH_TEXT_ONE_SHOT count={ordered.Count} len={combined.Length} ctrlNl={newlineAfterEachTextWhenCtrlEnter} outerHold={ownsGlobalPasteState}");

            if (hidePopupAfter)
                HidePopup();
            if (_targetWindow != IntPtr.Zero)
                Win32.SetForegroundWindowAggressive(_targetWindow);

            await Task.Delay(26);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

            if (_hwnd != IntPtr.Zero)
                Win32.TryEmptyClipboardAfterOpen(_hwnd);

            _isSettingClipboard = true;
            var ok = await TrySetClipboardAsync(
                () => System.Windows.Clipboard.SetText(combined),
                $"SetText batchCombined len={combined.Length}",
                maxRetries: 10,
                delayMs: 45,
                clipNudgeHwnd: _hwnd);
            await Task.Delay(10);
            _isSettingClipboard = false;

            if (ok)
            {
                await Task.Delay(14);
                SendCtrlV();
            }

            if (applyHistoryReorder)
                ApplyDeferredSequentialPasteHistoryOrder(ordered);
            await Task.Delay(80);
        }
        finally
        {
            if (ownsGlobalPasteState)
            {
                _sequentialPasteHold = false;
                _pasteInProgress = false;
            }
        }
    }

    /// <summary>连续段之间极短让步，避免 CLIPBRD_E_CANT_OPEN（仅图/文件等必须分段时使用）。</summary>
    private const int SequentialInterSegmentDelayMs = 22;

    /// <summary>整轮结束后稍晚再解除「粘贴中」。</summary>
    private const int SequentialTailSettleMs = 85;

    /// <summary>多段粘贴共享外部「粘贴进行中」标志，避免每段之间剪贴板监听插队。</summary>
    private async Task RunSequentialPastesAsync(IReadOnlyList<ClipboardEntry> items, bool newlineAfterEachTextWhenCtrlEnter = false)
    {
        _sequentialPasteHold = true;
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                await PasteEntryAsync(
                    items[i],
                    hidePopupAfter: i == items.Count - 1,
                    sequentialSegmentIndex: i,
                    sendNewlineAfterTextWhenCtrlEnterBatch: newlineAfterEachTextWhenCtrlEnter);
                if (i < items.Count - 1)
                    await Task.Delay(SequentialInterSegmentDelayMs);
            }

            if (items.Count > 0)
                ApplyDeferredSequentialPasteHistoryOrder(items);

            if (items.Count > 0)
                await Task.Delay(SequentialTailSettleMs);
        }
        finally
        {
            _sequentialPasteHold = false;
            _pasteInProgress = false;
        }
    }

    /// <summary>连续粘贴结束后，按「后贴的在最上」依次插队置顶并补写库，只做一次列表刷新。</summary>
    private void ApplyDeferredSequentialPasteHistoryOrder(IReadOnlyList<ClipboardEntry> itemsInPasteOrder)
    {
        if (itemsInPasteOrder.Count == 0) return;
        for (int j = itemsInPasteOrder.Count - 1; j >= 0; j--)
        {
            var item = itemsInPasteOrder[j];
            if (item.IsQuickPaste) continue;
            var idx = _allItems.IndexOf(item);
            if (idx > 0) { _allItems.RemoveAt(idx); _allItems.Insert(0, item); }
            item.TouchCopiedTime();
            if (item.PersistedId is long pid)
                _historyStore.TryUpdateCopiedAt(pid, item.CopiedAt);
        }
        RefreshFilter(0);
    }

    private void TryOpenBatchOrContextMenuFromKeyboard()
    {
        if (_displayItems.Count == 0) return;
        if (ItemsList.SelectedItem is not ClipboardEntry) return;

        if (ShouldPreferBatchMenuOverItemContext())
        {
            if (ItemsList.SelectedItem is ClipboardEntry entry
                && ItemsList.ItemContainerGenerator.ContainerFromItem(entry) is ListBoxItem li)
            {
                BatchMenuPopup.PlacementTarget = li;
                BatchMenuPopup.Placement = PlacementMode.Right;
            }
            else
            {
                BatchMenuPopup.PlacementTarget = BatchModeHeaderBorder;
                BatchMenuPopup.Placement = PlacementMode.Bottom;
            }
            OpenBatchMenuPopup();
            return;
        }
        OpenContextMenuFromKeyboard();
    }

    private bool ShouldPreferBatchMenuOverItemContext() =>
        GetBatchMode() != BatchPasteQueueMode.Off || _batchQueue.Count > 0;

    #endregion

    #region Popup Show/Hide

    public void TogglePopup()
    {
        if (_isPopupVisible) HidePopup();
        else ShowPopup();
    }

    private void ShowPopup()
    {
        CleanupOldClipboardExports();
        _targetWindow = Win32.GetForegroundWindow();
        _searchText = "";
        _typeFilter = null;
        _quickPhraseOnly = false;
        _selectionRangeAnchor = -1;
        _selectionCursorEnd = -1;
        _mouseShiftAnchorIndex = -1;
        TypeFilterText.Text = "全部";

        RefreshFilter();

        Opacity = 0;

        _lockPopupWindowNomove = true;
        try
        {
            PositionPopup();
            TryApplyPendingPositionAsWpfLeftTop();
            ApplyPendingPositionSetWindowPos();
            TryApplyPendingPositionAsWpfLeftTop();

            Show();
            UpdateLayout();

            PositionPopup();

            _isPopupVisible = true;

            ApplyPendingPositionSetWindowPos();
            TryApplyPendingPositionAsWpfLeftTop();
        }
        finally
        {
            _lockPopupWindowNomove = false;
        }

        Opacity = _popupOpacity;

        if (_displayItems.Count > 0)
            ItemsList.SelectedIndex = 0;

#if CLIPX_CLIPBOARD
        SyncBatchPasteKeyboardHook();
#else
        InstallKeyboardHook();
#endif
        InstallMouseHook();
    }

    private void ApplyPendingPositionSetWindowPos()
    {
        _isOurSetWindowPos = true;
        try
        {
            Win32.SetWindowPos(_hwnd, IntPtr.Zero,
                _pendingPhysX, _pendingPhysY, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }
        finally
        {
            _isOurSetWindowPos = false;
        }
    }

    private void HidePopup()
    {
        _isPopupVisible = false;
        _lockPopupWindowNomove = false;
        _lastPopupHideTick = Environment.TickCount64;
        UninstallMouseHook();
        CloseEntryPreviewBubble();
        CloseContextMenuPopup();
        BatchMenuPopup.IsOpen = false;
        CloseBatchMenuNavUi();
#if CLIPX_CLIPBOARD
        if (GetBatchMode() == BatchPasteQueueMode.Off)
        {
            _batchQueue.Clear();
            UpdateBatchOrderProperties();
        }
#else
        _batchQueue.Clear();
        UpdateBatchOrderProperties();
#endif
        _selectionRangeAnchor = -1;
        _selectionCursorEnd = -1;
        _mouseShiftAnchorIndex = -1;
        ShortcutHelpPopup.IsOpen = false;
        PhraseEditPopup.IsOpen = false;
        _phraseEditEntry = null;
        _phraseEditBuffer = "";
        ClearPendingDelete();
#if CLIPX_CLIPBOARD
        SyncBatchPasteKeyboardHook();
#else
        UninstallKeyboardHook();
#endif
        Hide();
    }

    private void PositionPopup()
    {
        const int caretGap = 24;

        if (_popupPosition == "Mouse")
        {
            Win32.GetCursorPos(out var pt);
            SetPositionWithOffset(pt.X + 8, pt.Y + 20);
            return;
        }

        // 资源管理器驱动的桌面（壁纸/图标层）：无可靠文本光标，跟随鼠标更符合直觉
        if (IsExplorerDesktopForeground(_targetWindow))
        {
            Win32.GetCursorPos(out var deskPt);
            SetPositionWithOffset(deskPt.X + 8, deskPt.Y + 20);
            return;
        }

        var fgThread = Win32.GetWindowThreadProcessId(_targetWindow, out _);
        var gti = new Win32.GUITHREADINFO { cbSize = Marshal.SizeOf<Win32.GUITHREADINFO>() };

        if (Win32.GetGUIThreadInfo(fgThread, ref gti) && gti.hwndCaret != IntPtr.Zero)
        {
            var pt = new Win32.POINT { X = gti.rcCaret.Left, Y = gti.rcCaret.Bottom };
            Win32.ClientToScreen(gti.hwndCaret, ref pt);
            if (pt.X > 0 || pt.Y > 0)
            {
                SetPositionWithOffset(pt.X, pt.Y + caretGap);
                return;
            }
        }

        try
        {
            var myThread = Win32.GetCurrentThreadId();
            Win32.AttachThreadInput(myThread, fgThread, true);
            try
            {
                var focusWnd = Win32.GetFocus();
                if (focusWnd != IntPtr.Zero && Win32.GetCaretPos(out var caretPos))
                {
                    if (caretPos.X != 0 || caretPos.Y != 0)
                    {
                        Win32.ClientToScreen(focusWnd, ref caretPos);
                        SetPositionWithOffset(caretPos.X, caretPos.Y + caretGap);
                        return;
                    }
                }
            }
            finally { Win32.AttachThreadInput(myThread, fgThread, false); }
        }
        catch { }

        if (TryGetCaretByAutomation(out double uiaX, out double uiaY))
        {
            SetPositionWithOffset(uiaX, uiaY + caretGap);
            return;
        }

        Win32.GetCursorPos(out var cursor);
        SetPositionWithOffset(cursor.X + 8, cursor.Y + 20);
    }

    /// <summary>当前前台是否为 Windows 桌面（Progman/WorkerW），即点击壁纸或桌面图标时的焦点窗体。</summary>
    private static bool IsExplorerDesktopForeground(IntPtr foregroundHwnd)
    {
        if (foregroundHwnd == IntPtr.Zero) return false;
        var cls = Win32.GetWindowClassName(foregroundHwnd);
        return cls.Equals("Progman", StringComparison.OrdinalIgnoreCase)
               || cls.Equals("WorkerW", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetCaretByAutomation(out double x, out double y)
    {
        x = y = 0;
        try
        {
            var task = Task.Run<(bool ok, double x, double y)>(() =>
            {
                try
                {
                    var focused = System.Windows.Automation.AutomationElement.FocusedElement;
                    if (focused == null) return (false, 0, 0);

                    if (focused.TryGetCurrentPattern(
                            System.Windows.Automation.TextPattern.Pattern, out var p))
                    {
                        var sel = ((System.Windows.Automation.TextPattern)p).GetSelection();
                        if (sel.Length > 0)
                        {
                            var rects = sel[0].GetBoundingRectangles();
                            if (rects.Length > 0 && (rects[0].X > 0 || rects[0].Y > 0))
                                return (true, rects[0].X, rects[0].Bottom + 4);
                        }
                    }

                    var rect = focused.Current.BoundingRectangle;
                    if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
                        return (true, rect.X + 20, rect.Bottom + 4);

                    return (false, 0, 0);
                }
                catch { return (false, 0, 0); }
            });

            if (task.Wait(200))
            {
                var result = task.Result;
                if (result.ok) { x = result.x; y = result.y; return true; }
            }
        }
        catch { }
        return false;
    }

    private void SetPositionWithOffset(double physX, double physY)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)physX, (int)physY));
        var work = screen.WorkingArea;

        var hMon = Win32.MonitorFromPoint(
            new Win32.POINT { X = (int)physX, Y = (int)physY },
            Win32.MONITOR_DEFAULTTONEAREST);
        Win32.GetDpiForMonitor(hMon, 0, out uint monDpiX, out uint monDpiY);
        double scaleX = monDpiX / 96.0;
        double scaleY = monDpiY / 96.0;

        int popupW = (int)(Width * scaleX);
        double actualH = ActualHeight > 0 ? ActualHeight : MaxHeight;
        int popupH = (int)(actualH * scaleY);

        int x = (int)physX;
        int y = (int)physY;

        if (x + popupW > work.Right) x = work.Right - popupW;
        if (y + popupH > work.Bottom) y = (int)physY - popupH - 32;
        if (x < work.Left) x = work.Left;
        if (y < work.Top) y = work.Top;

        _pendingPhysX = x;
        _pendingPhysY = y;
    }

    /// <summary>
    /// 在 Show() 之前把即将使用的物理像素坐标写成 WPF Left/Top，避免窗口先在 (0,0) 露一帧再 SetWindowPos。
    /// </summary>
    private bool TryApplyPendingPositionAsWpfLeftTop()
    {
        try
        {
            if (_hwnd == IntPtr.Zero)
            {
                var helper = new WindowInteropHelper(this);
                helper.EnsureHandle();
                _hwnd = helper.Handle;
            }

            var src = HwndSource.FromHwnd(_hwnd);
            if (src?.CompositionTarget == null) return false;

            var dip = src.CompositionTarget.TransformFromDevice.Transform(
                new System.Windows.Point(_pendingPhysX, _pendingPhysY));
            Left = dip.X;
            Top = dip.Y;
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Keyboard Hook

    private static IntPtr StaticKeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var owner = s_popupKeyboardHookOwner;
        var hhk = s_popupKeyboardHookForNext;
        if (owner != null && hhk != IntPtr.Zero)
            return owner.KeyboardHookCallback(nCode, wParam, lParam);
        return Win32.CallNextHookEx(hhk, nCode, wParam, lParam);
    }

    private static IntPtr StaticMouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var owner = s_popupMouseHookOwner;
        var hhk = s_popupMouseHookForNext;
        if (owner != null && hhk != IntPtr.Zero)
            return owner.MouseHookCallback(nCode, wParam, lParam);
        return Win32.CallNextHookEx(hhk, nCode, wParam, lParam);
    }

    private static IntPtr StaticFileJumpAutoMouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var owner = s_fileJumpAutoMouseOwner;
        var hhk = s_fileJumpAutoMouseHookForNext;
        if (owner != null && hhk != IntPtr.Zero)
            return owner.FileJumpAutoMouseHookCallback(nCode, wParam, lParam);
        return Win32.CallNextHookEx(hhk, nCode, wParam, lParam);
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero) return;
        s_popupKeyboardHookOwner = this;
        _keyboardHook = Win32.SetWindowsHookEx(
            Win32.WH_KEYBOARD_LL, s_popupKeyboardHookThunk, Win32.GetModuleHandle(null), 0);
        s_popupKeyboardHookForNext = _keyboardHook;
    }

    private void UninstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        if (s_popupKeyboardHookOwner == this)
        {
            s_popupKeyboardHookOwner = null;
            s_popupKeyboardHookForNext = IntPtr.Zero;
        }
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        s_popupMouseHookOwner = this;
        _mouseHook = Win32.SetWindowsHookEx(
            Win32.WH_MOUSE_LL, s_popupMouseHookThunk, Win32.GetModuleHandle(null), 0);
        s_popupMouseHookForNext = _mouseHook;
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (s_popupMouseHookOwner == this)
        {
            s_popupMouseHookOwner = null;
            s_popupMouseHookForNext = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isPopupVisible)
        {
            var msg = wParam.ToInt32();
            var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);

            if (_isDragging)
            {
                if (msg == Win32.WM_LBUTTONUP)
                    _isDragging = false;
                return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            if (msg is Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN)
            {
                if (_hideOnSameAppClick)
                {
                    _clickReceivedByPopup = false;
                    Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background, () =>
                        {
                            if (_isPopupVisible && !_clickReceivedByPopup
                                && !ContextPopup.IsOpen && !PhraseEditPopup.IsOpen)
                                HidePopup();
                        });
                }
            }
        }
        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static void StaticWinEventProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        var owner = s_popupWinEventOwner;
        if (owner == null) return;
        if (eventType == Win32.EVENT_SYSTEM_FOREGROUND)
            owner.OnForegroundChanged(hWinEventHook, eventType, hwnd, idObject, idChild, dwEventThread, dwmsEventTime);
        else if (eventType == Win32.EVENT_OBJECT_FOCUS)
            owner.OnGlobalFocusMaybeFileDialog(hwnd);
    }

    private void InstallForegroundWatcher()
    {
        if (_winEventHook != IntPtr.Zero) return;
        s_popupWinEventOwner = this;
        _winEventHook = Win32.SetWinEventHook(
            Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, s_popupWinEventThunk, 0, 0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
        if (_winEventHook == IntPtr.Zero)
        {
            s_popupWinEventOwner = null;
            return;
        }

        _winEventHookFocus = Win32.SetWinEventHook(
            Win32.EVENT_OBJECT_FOCUS, Win32.EVENT_OBJECT_FOCUS,
            IntPtr.Zero, s_popupWinEventThunk, 0, 0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
        if (_winEventHookFocus == IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
            s_popupWinEventOwner = null;
        }
    }

    private void UninstallForegroundWatcher()
    {
        if (_winEventHookFocus != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_winEventHookFocus);
            _winEventHookFocus = IntPtr.Zero;
        }
        if (_winEventHook != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        if (s_popupWinEventOwner == this)
            s_popupWinEventOwner = null;
    }

    /// <summary>
    /// 微信等打开「选择文件」时可能不发前台切换事件，但焦点会进对话框；与 <see cref="OnForegroundChanged"/> 共用自动弹逻辑。
    /// </summary>
    private void OnGlobalFocusMaybeFileDialog(IntPtr hwnd)
    {
        if (_appSettings == null || !_appSettings.FileJumpPickerOpenWhenDialogForeground) return;
        if (hwnd == IntPtr.Zero) return;
        if (!FileDialogJumpHelper.QuickMayBeUnderFileDialog(hwnd)) return;

        var dlg = FileDialogJumpHelper.ResolveFileDialogHwndFromWindowOrAncestor(hwnd);
        if (dlg == IntPtr.Zero) return;

        ScheduleSnapshotFolderFromDialog(dlg);
        Dispatcher.BeginInvoke(() =>
        {
            TryAutoOpenFileJumpPickerWhenDialogForeground(dlg);
            UpdateFileJumpClickToNavigateArm(dlg);
        });
    }

    private void OnForegroundChanged(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        var prev = _lastForegroundForDialogTrack;
        _lastForegroundForDialogTrack = hwnd;

        Dispatcher.BeginInvoke(() => TryRememberFolderFromDialog(prev));
        Dispatcher.BeginInvoke(() => TryRememberExternalManagerFolder(prev));
        Dispatcher.BeginInvoke(() => TryRememberExternalManagerFolder(hwnd));

        var dialogForForeground = FileDialogJumpHelper.ResolveFileDialogHwndFromWindowOrAncestor(hwnd);
        if (dialogForForeground != IntPtr.Zero)
        {
            var prevForAutoSync = prev;
            ScheduleSnapshotFolderFromDialog(dialogForForeground);
            Dispatcher.BeginInvoke(() => TryAutoOpenFileJumpPickerWhenDialogForeground(dialogForForeground));
            Dispatcher.BeginInvoke(() => TryAutoSyncPathOnDialogReturn(hwnd, prevForAutoSync));
        }

        var armTarget = dialogForForeground != IntPtr.Zero
            ? dialogForForeground
            : FileDialogJumpHelper.ResolveFileDialogHwndFromWindowOrAncestor(hwnd);
        Dispatcher.BeginInvoke(() =>
            UpdateFileJumpClickToNavigateArm(armTarget != IntPtr.Zero ? armTarget : hwnd));

        if (!_isPopupVisible) return;
        if (hwnd == _hwnd || hwnd == _targetWindow) return;

        Win32.GetCursorPos(out var cursor);
        if (Win32.WindowFromPoint(cursor) == _hwnd) return;

        Dispatcher.BeginInvoke(HidePopup);
    }

    /// <summary>对话框成为前台后分层短等再读路径：先快试，读不到再逐段补等（总上限接近原先单次长等）。</summary>
    private void ScheduleSnapshotFolderFromDialog(IntPtr dialogHwnd)
    {
        if (_appSettings == null || dialogHwnd == IntPtr.Zero) return;
        unchecked { _dialogSnapshotScheduleGen++; }
        var scheduleGen = _dialogSnapshotScheduleGen;
        var target = dialogHwnd;
        Dispatcher.BeginInvoke(() =>
        {
            void SchedulePhase(int phase)
            {
                var delayMs = phase switch { 0 => 80, 1 => 120, 2 => 120, _ => 0 };
                if (delayMs == 0) return;
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(delayMs)
                };
                var p = phase;
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (scheduleGen != _dialogSnapshotScheduleGen) return;
                    if (_appSettings == null) return;
                    if (!Win32.IsWindow(target)) return;
                    var fgSnap = Win32.GetForegroundWindow();
                    if (FileDialogJumpHelper.ResolveFileDialogHwndFromWindowOrAncestor(fgSnap) != target) return;
                    if (FileDialogJumpHelper.TryReadCurrentFolder(target, out var folder)
                        && !string.IsNullOrEmpty(folder))
                    {
                        RememberLastDialogFolder(folder);
                        return;
                    }

                    if (p < 2)
                        SchedulePhase(p + 1);
                };
                timer.Start();
            }

            SchedulePhase(0);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>前台刚从「打开/保存」对话框切走时，尝试记下当时所在文件夹。</summary>
    private void TryRememberFolderFromDialog(IntPtr previousHwnd)
    {
        if (_appSettings == null || previousHwnd == IntPtr.Zero || previousHwnd == _hwnd) return;
        if (!Win32.IsWindow(previousHwnd)) return;
        var dlg = FileDialogJumpHelper.ResolveFileDialogHwndFromWindowOrAncestor(previousHwnd);
        if (dlg == IntPtr.Zero) return;
        if (!FileDialogJumpHelper.TryReadCurrentFolder(dlg, out var folder)
            || string.IsNullOrEmpty(folder)) return;

        RememberLastDialogFolder(folder);
    }

    private void RememberLastDialogFolder(string folder)
    {
        if (_appSettings == null || string.IsNullOrWhiteSpace(folder)) return;
        var normalized = folder.Trim();
        if (normalized == _appSettings.LastFileDialogFolder) return;
        _appSettings.LastFileDialogFolder = normalized;
        _appSettings.Save();
    }

    /// <summary>
    /// 记录最近一次活跃外部文件管理器的路径；切回文件对话框时优先将其作为同步目标。
    /// </summary>
    private void TryRememberExternalManagerFolder(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == _hwnd || !Win32.IsWindow(hwnd)) return;
        if (FileDialogJumpHelper.IsLikelyFileDialog(hwnd)) return;

        var folder = FileManagerPathCollector.TryGetFolderForWindow(hwnd);
        if (string.IsNullOrWhiteSpace(folder)) return;

        var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        var normalized = folder.Trim();
        if (string.Equals(_lastExternalFolder, normalized, StringComparison.OrdinalIgnoreCase)
            && _lastExternalManagerRoot == root)
            return;

        _lastExternalFolder = normalized;
        _lastExternalManagerRoot = root;
        ShellNavigateLog.Write("filejump", $"external folder updated root=0x{root.ToInt64():X} path=\"{normalized}\"");
    }

    private void TryJumpFileDialogToLastFolder()
    {
        if (_appSettings == null) return;

        try
        {
            TryJumpFileDialogToLastFolderCore();
        }
        catch (Exception ex)
        {
            ShellNavigateLog.Write("filejump", "TryJumpFileDialogToLastFolder: " + ex);
        }
    }

    private void TryJumpFileDialogToLastFolderCore()
    {
        if (_appSettings == null) return;

        var fgNow = Win32.GetForegroundWindow();
        var dialogHwnd = ResolveFileJumpTargetHwndInternal(fgNow);
        if (dialogHwnd == IntPtr.Zero) return;

        // 列表窗正在 new 的过程中尚未赋给 _activeFileJumpPicker：忽略重复热键，避免再次排队打开。
        if (_fileJumpPickerOpenInProgress && _activeFileJumpPicker == null)
            return;

        var mem = _appSettings.LastFileDialogFolder?.Trim();
        var allowShellInject = _appSettings.EnableShellNavigateInject;

        unchecked { _fileJumpHotkeyCollectGen++; }
        var gen = _fileJumpHotkeyCollectGen;
        var dialogHwndCapture = dialogHwnd;
        var memCapture = mem;
        var allowCapture = allowShellInject;

        void StaCollect()
        {
            List<FileJumpCandidate> candidates;
            try
            {
                candidates = FileManagerPathCollector.CollectCandidates(dialogHwndCapture, memCapture);
            }
            catch (Exception ex)
            {
                ShellNavigateLog.Write("filejump", "CollectCandidates (hotkey): " + ex);
                candidates = new List<FileJumpCandidate>();
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (gen != _fileJumpHotkeyCollectGen) return;
                TryJumpFileDialogToLastFolderContinueAfterCollect(dialogHwndCapture, candidates, allowCapture);
            }, DispatcherPriority.Normal);
        }

        var th = new Thread(StaCollect)
        {
            IsBackground = true,
            Name = "ClipboardX-FileJump-Hotkey-Collect",
        };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
    }

    private void TryJumpFileDialogToLastFolderContinueAfterCollect(
        IntPtr dialogHwnd,
        List<FileJumpCandidate> candidates,
        bool allowShellInject)
    {
        if (_appSettings == null) return;
        if (dialogHwnd == IntPtr.Zero || !Win32.IsWindow(dialogHwnd))
        {
            ClearFileJumpDoubleTapState();
            return;
        }

        if (candidates.Count == 0)
        {
            ClearFileJumpDoubleTapState();
            return;
        }

        if (candidates.Count == 1)
        {
            ClearFileJumpDoubleTapState();
            FileDialogJumpHelper.TryNavigateToFolder(dialogHwnd, candidates[0].Path, allowShellInject);
            return;
        }

        var prefer = PreferCandidateIndex(dialogHwnd, candidates);

        if (_activeFileJumpPicker != null)
        {
            _fileJumpPickerSession++;
            ClearFileJumpDoubleTapState();
            FileDialogJumpHelper.TryNavigateToFolder(dialogHwnd, candidates[prefer].Path, allowShellInject);
            Dispatcher.BeginInvoke(() => _activeFileJumpPicker?.Close(),
                System.Windows.Threading.DispatcherPriority.Normal);
            return;
        }

        var tick = Environment.TickCount64;
        var sameDialog = dialogHwnd == _fileJumpLastDialogHwnd;
        var withinDoubleTap = sameDialog && _fileJumpLastHotkeyTick != 0
                                        && tick - _fileJumpLastHotkeyTick >= 0
                                        && tick - _fileJumpLastHotkeyTick <= FileJumpDoubleTapMs;

        if (withinDoubleTap)
        {
            _fileJumpPickerSession++;
            ClearFileJumpDoubleTapState();
            var path = candidates[prefer].Path;
            FileDialogJumpHelper.TryNavigateToFolder(dialogHwnd, path, allowShellInject);
            Dispatcher.BeginInvoke(() => _activeFileJumpPicker?.Close(),
                System.Windows.Threading.DispatcherPriority.Normal);
            return;
        }

        if (!_appSettings.FileJumpPickerAutoPopup)
        {
            ClearFileJumpDoubleTapState();
            FileDialogJumpHelper.TryNavigateToFolder(dialogHwnd, candidates[prefer].Path, allowShellInject);
            return;
        }

        ScheduleFileJumpPickerOpen(dialogHwnd, candidates.ToList(), prefer, armHotkeyDoubleTap: true, allowShellInject,
            autoForegroundStickyMode: false);
    }

    /// <summary>
    /// 当前键盘焦点是否落在该文件对话框根窗口内（含微信主窗前台 + <see cref="Win32.GetLastActivePopup"/> 模态框等情形）。
    /// </summary>
    private static bool IsForegroundFocusOnFileDialogRoot(IntPtr dialogRoot)
    {
        if (dialogRoot == IntPtr.Zero) return false;
        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var dlg = FileDialogJumpHelper.ResolveFileDialogHwndFromWindowOrAncestor(fg);
        var fgRoot = dlg != IntPtr.Zero
            ? Win32.GetAncestor(dlg, Win32.GA_ROOT)
            : Win32.GetAncestor(fg, Win32.GA_ROOT);
        return fgRoot == dialogRoot;
    }

    /// <summary>
    /// 检测到打开/保存对话框成为前台时，延时后再尝试自动弹出（等对话框内路径可读）。
    /// </summary>
    private void TryAutoOpenFileJumpPickerWhenDialogForeground(IntPtr foregroundHwnd)
    {
        if (_appSettings == null || !_appSettings.FileJumpPickerOpenWhenDialogForeground) return;
        if (foregroundHwnd == IntPtr.Zero) return;

        _fileJumpAutoOpenDebounceTimer?.Stop();
        _fileJumpAutoOpenDebounceTimer = null;
        var hwndCapture = foregroundHwnd;
        _fileJumpAutoOpenDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _fileJumpAutoOpenDebounceTimer.Tick += (_, _) =>
        {
            _fileJumpAutoOpenDebounceTimer?.Stop();
            _fileJumpAutoOpenDebounceTimer = null;
            try
            {
                TryAutoOpenFileJumpPickerWhenDialogForegroundAfterDebounce(hwndCapture);
            }
            catch (Exception ex)
            {
                ShellNavigateLog.Write("filejump", "TryAutoOpenFileJumpPickerWhenDialogForegroundAfterDebounce: " + ex);
            }
        };
        _fileJumpAutoOpenDebounceTimer.Start();
    }

    /// <summary>
    /// 对话框成为前台并经过短延时后：自动弹出跳转列表（多候选）或直跳单一路径。
    /// </summary>
    private void TryAutoOpenFileJumpPickerWhenDialogForegroundAfterDebounce(IntPtr foregroundHwnd)
    {
        if (_appSettings == null || !_appSettings.FileJumpPickerOpenWhenDialogForeground) return;
        if (foregroundHwnd == IntPtr.Zero) return;
        if (_fileJumpPickerOpenInProgress && _activeFileJumpPicker == null) return;
        if (_activeFileJumpPicker != null) return;

        if (_fileJumpAutoOpenPickerDoneRoot != IntPtr.Zero
            && !Win32.IsWindow(_fileJumpAutoOpenPickerDoneRoot))
            _fileJumpAutoOpenPickerDoneRoot = IntPtr.Zero;

        var fgNow = Win32.GetForegroundWindow();
        var dialogHwnd = ResolveFileJumpTargetHwndInternal(fgNow);
        if (dialogHwnd == IntPtr.Zero) return;

        var dialogRoot = Win32.GetAncestor(dialogHwnd, Win32.GA_ROOT);
        if (dialogRoot == IntPtr.Zero) return;

        if (!IsForegroundFocusOnFileDialogRoot(dialogRoot))
            return;

        if (dialogRoot == _fileJumpAutoOpenPickerDoneRoot)
            return;

        var mem = _appSettings.LastFileDialogFolder?.Trim();
        var allowShellInject = _appSettings.EnableShellNavigateInject;

        unchecked { _fileJumpAutoForegroundCollectGen++; }
        var gen = _fileJumpAutoForegroundCollectGen;
        var dialogHwndCapture = dialogHwnd;
        var dialogRootCapture = dialogRoot;
        var memCapture = mem;
        var allowCapture = allowShellInject;

        void StaCollect()
        {
            List<FileJumpCandidate> candidates;
            try
            {
                candidates = FileManagerPathCollector.CollectCandidates(dialogHwndCapture, memCapture);
            }
            catch (Exception ex)
            {
                ShellNavigateLog.Write("filejump", "CollectCandidates (auto fg): " + ex);
                candidates = new List<FileJumpCandidate>();
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (gen != _fileJumpAutoForegroundCollectGen) return;
                TryAutoOpenFileJumpPickerAfterCollect(dialogHwndCapture, dialogRootCapture, candidates, allowCapture);
            }, DispatcherPriority.Normal);
        }

        var th = new Thread(StaCollect)
        {
            IsBackground = true,
            Name = "ClipboardX-FileJump-AutoFg-Collect",
        };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
    }

    private void TryAutoOpenFileJumpPickerAfterCollect(
        IntPtr dialogHwnd,
        IntPtr dialogRoot,
        List<FileJumpCandidate> candidates,
        bool allowShellInject)
    {
        if (_appSettings == null || !_appSettings.FileJumpPickerOpenWhenDialogForeground) return;
        if (dialogHwnd == IntPtr.Zero || !Win32.IsWindow(dialogHwnd)) return;

        var dialogRootNow = Win32.GetAncestor(dialogHwnd, Win32.GA_ROOT);
        if (dialogRootNow == IntPtr.Zero || dialogRootNow != dialogRoot) return;

        // 采集在线程里异步完成；列表窗可能已通过 BeginInvoke 打开：避免再次直跳/调度造成 WPS Qt 路径重复导航
        if (_activeFileJumpPicker != null) return;
        if (_fileJumpPickerOpenInProgress) return;

        if (!IsForegroundFocusOnFileDialogRoot(dialogRootNow)) return;

        if (dialogRootNow == _fileJumpAutoOpenPickerDoneRoot) return;

        if (candidates.Count == 0)
            return;

        if (candidates.Count == 1)
        {
            _fileJumpAutoOpenPickerDoneRoot = dialogRootNow;
            FileDialogJumpHelper.TryNavigateToFolder(dialogHwnd, candidates[0].Path, allowShellInject);
            return;
        }

        var prefer = PreferCandidateIndex(dialogHwnd, candidates);
        _fileJumpAutoOpenPickerDoneRoot = dialogRootNow;
        if (!_appSettings.FileJumpPickerAutoPopup)
        {
            FileDialogJumpHelper.TryNavigateToFolder(dialogHwnd, candidates[prefer].Path, allowShellInject);
            return;
        }

        ScheduleFileJumpPickerOpen(dialogHwnd, candidates.ToList(), prefer, armHotkeyDoubleTap: false, allowShellInject,
            autoForegroundStickyMode: true);
    }

    #region 切回对话框自动同步路径

    /// <summary>
    /// 对话框再次到前台时，重新采集候选路径；
    /// 若能确定最近一次外部文件管理器路径，且与对话框当前路径不同，则自动同步过去。
    /// </summary>
    /// <param name="previousForegroundHwnd">本次获得前台之前的窗口；仅当其为可解析目录的外部管理器时才用 <see cref="_lastExternalFolder"/> 驱动跳转，避免用户在对话框内改路径后到其它程序再切回被误拉到资源管理器旧目录。</param>
    private void TryAutoSyncPathOnDialogReturn(IntPtr foregroundHwnd, IntPtr previousForegroundHwnd)
    {
        if (_appSettings == null) return;
        if (foregroundHwnd == IntPtr.Zero) return;

        var dialogHwnd = ResolveFileJumpTargetHwndInternal(foregroundHwnd);
        if (dialogHwnd == IntPtr.Zero) return;
        var dialogRoot = Win32.GetAncestor(dialogHwnd, Win32.GA_ROOT);
        if (dialogRoot == IntPtr.Zero) return;

        var hasMatchingPicker = ActivePickerMatchesDialog(dialogRoot);
        if (!hasMatchingPicker && !_appSettings.FileJumpAutoSyncOnReturn) return;

        unchecked { _fileJumpAutoSyncScheduleGen++; }
        var scheduleGen = _fileJumpAutoSyncScheduleGen;
        var hwndCapture = dialogHwnd;
        var rootCapture = dialogRoot;
        var prevCapture = previousForegroundHwnd;

        void SchedulePrecheck(int phase)
        {
            var delayMs = phase switch { 0 => 50, 1 => 100, 2 => 100, _ => 0 };
            if (delayMs == 0) return;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            var p = phase;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (scheduleGen != _fileJumpAutoSyncScheduleGen) return;
                if (!TryAutoSyncForegroundStable(hwndCapture, rootCapture))
                {
                    if (p < 2)
                        SchedulePrecheck(p + 1);
                    return;
                }

                try
                {
                    TryAutoSyncPathOnDialogReturnCore(hwndCapture, rootCapture, prevCapture);
                }
                catch (Exception ex)
                {
                    ShellNavigateLog.Write("filejump", "TryAutoSyncPathOnDialogReturnCore: " + ex);
                }
            };
            timer.Start();
        }

        SchedulePrecheck(0);
    }

    /// <summary>仅当前台刚从「能采到文件夹路径、且不是文件对话框」的窗口切来时，才信任 <see cref="_lastExternalFolder"/> 作为自动同步目标。</summary>
    private bool ShouldPreferLastExternalFolderForAutoSync(IntPtr previousForegroundHwnd)
    {
        if (previousForegroundHwnd == IntPtr.Zero || previousForegroundHwnd == _hwnd || !Win32.IsWindow(previousForegroundHwnd))
            return false;
        if (FileDialogJumpHelper.IsLikelyFileDialog(previousForegroundHwnd)) return false;
        var folder = FileManagerPathCollector.TryGetFolderForWindow(previousForegroundHwnd);
        return !string.IsNullOrWhiteSpace(folder);
    }

    /// <summary>切回同步前：前台根窗口已与目标对话框一致（分层短等后再判）。</summary>
    private bool TryAutoSyncForegroundStable(IntPtr dialogHwnd, IntPtr dialogRoot)
    {
        if (_appSettings == null) return false;
        if (!Win32.IsWindow(dialogHwnd)) return false;
        return IsForegroundFocusOnFileDialogRoot(dialogRoot);
    }

    private void TryAutoSyncPathOnDialogReturnCore(IntPtr dialogHwnd, IntPtr dialogRoot, IntPtr previousForegroundHwnd)
    {
        if (_appSettings == null) return;
        if (!Win32.IsWindow(dialogHwnd)) return;

        if (!IsForegroundFocusOnFileDialogRoot(dialogRoot)) return;

        var allowShellInject = _appSettings.EnableShellNavigateInject;
        var preferLastExternal = ShouldPreferLastExternalFolderForAutoSync(previousForegroundHwnd);
        var preferredExternalFolder = preferLastExternal ? (_lastExternalFolder?.Trim() ?? "") : "";
        var mem = !string.IsNullOrEmpty(preferredExternalFolder)
            ? preferredExternalFolder
            : _appSettings.LastFileDialogFolder?.Trim();

        unchecked { _fileJumpAutoSyncCollectGen++; }
        var gen = _fileJumpAutoSyncCollectGen;
        var dialogCapture = dialogHwnd;
        var dialogRootCapture = dialogRoot;

        void StaCollect()
        {
            List<FileJumpCandidate> candidates;
            try
            {
                candidates = FileManagerPathCollector.CollectCandidates(dialogCapture, mem);
            }
            catch (Exception ex)
            {
                ShellNavigateLog.Write("filejump", "CollectCandidates (auto-sync): " + ex);
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (gen != _fileJumpAutoSyncCollectGen) return;
                if (candidates.Count == 0) return;

                var preferredPath = ResolvePreferredExternalFolder(candidates, preferredExternalFolder);

                RefreshActiveFileJumpPicker(dialogCapture, dialogRootCapture, candidates, preferredPath);

                if (!_appSettings.FileJumpAutoSyncOnReturn) return;
                if (string.IsNullOrEmpty(preferredPath)) return;

                string? currentFolder = null;

                // 比较对话框当前路径
                if (FileDialogJumpHelper.TryReadCurrentFolder(dialogCapture, out var currentFolderRead)
                    && !string.IsNullOrEmpty(currentFolderRead))
                {
                    currentFolder = currentFolderRead;
                    var norm1 = NormalizeFolderPathForCompare(currentFolderRead);
                    var norm2 = NormalizeFolderPathForCompare(preferredPath);
                    if (string.Equals(norm1, norm2, StringComparison.OrdinalIgnoreCase))
                        return;
                }

                if (TryNavigateViaActivePicker(dialogCapture, dialogRootCapture, preferredPath))
                    return;

                ShellNavigateLog.Write("filejump",
                    $"auto-sync navigating from \"{currentFolder ?? "(unreadable)"}\" to \"{preferredPath}\"");
                FileDialogJumpHelper.TryNavigateToFolder(dialogCapture, preferredPath, allowShellInject);
            }, DispatcherPriority.Normal);
        }

        var th = new Thread(StaCollect)
        {
            IsBackground = true,
            Name = "ClipboardX-FileJump-AutoSync-Collect",
        };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
    }

    private bool ActivePickerMatchesDialog(IntPtr dialogRoot)
    {
        if (_activeFileJumpPicker == null) return false;
        var pickerDialog = _activeFileJumpPicker.OwnerDialogHwnd;
        if (pickerDialog == IntPtr.Zero || !Win32.IsWindow(pickerDialog)) return false;
        var pickerRoot = Win32.GetAncestor(pickerDialog, Win32.GA_ROOT);
        return pickerRoot != IntPtr.Zero && pickerRoot == dialogRoot;
    }

    private void RefreshActiveFileJumpPicker(
        IntPtr dialogHwnd,
        IntPtr dialogRoot,
        List<FileJumpCandidate> candidates,
        string? preferredPath)
    {
        if (_activeFileJumpPicker == null) return;
        if (!ActivePickerMatchesDialog(dialogRoot)) return;
        _activeFileJumpPicker.RefreshCandidatesFromExternal(candidates, preferredPath);
    }

    private bool TryNavigateViaActivePicker(IntPtr dialogHwnd, IntPtr dialogRoot, string preferredPath)
    {
        if (_activeFileJumpPicker == null) return false;
        if (!ActivePickerMatchesDialog(dialogRoot)) return false;
        if (!_activeFileJumpPicker.IsAutoForegroundStickyMode) return false;
        _activeFileJumpPicker.NavigateKeepOpenToPath(preferredPath);
        return true;
    }

    private static string? ResolvePreferredExternalFolder(
        IReadOnlyList<FileJumpCandidate> candidates,
        string preferredExternalFolder)
    {
        if (!string.IsNullOrWhiteSpace(preferredExternalFolder))
        {
            var matched = candidates.FirstOrDefault(c =>
                string.Equals(
                    NormalizeFolderPathForCompare(c.Path),
                    NormalizeFolderPathForCompare(preferredExternalFolder),
                    StringComparison.OrdinalIgnoreCase));
            if (matched != null && !string.IsNullOrEmpty(matched.Path))
                return matched.Path;

            try
            {
                if (Directory.Exists(preferredExternalFolder))
                    return Path.GetFullPath(preferredExternalFolder);
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static string NormalizeFolderPathForCompare(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        return path.Trim().TrimEnd('\\', '/');
    }

    #endregion

    /// <summary>延时后打开跳转列表；与快捷键共用。</summary>
    /// <param name="armHotkeyDoubleTap">true 时参与「短时间内第二次快捷键直跳」探测。</param>
    /// <param name="autoForegroundStickyMode">true 时为自动前台模式：贴靠、不抢外部点击关窗、单击只导航。</param>
    private void ScheduleFileJumpPickerOpen(
        IntPtr dialogForPicker,
        List<FileJumpCandidate> capturedCandidates,
        int preferIdx,
        bool armHotkeyDoubleTap,
        bool allowShellInject,
        bool autoForegroundStickyMode)
    {
        if (_appSettings == null) return;

        var tick = Environment.TickCount64;
        if (armHotkeyDoubleTap)
        {
            _fileJumpLastHotkeyTick = tick;
            _fileJumpLastDialogHwnd = dialogForPicker;
        }
        else
        {
            _fileJumpLastDialogHwnd = dialogForPicker;
        }

        var session = unchecked(++_fileJumpPickerSession);
        Win32.GetCursorPos(out var jumpMouseScreen);
        var jumpX = jumpMouseScreen.X;
        var jumpY = jumpMouseScreen.Y;

        CancelFileJumpPickerDelay();
        var delaySession = _fileJumpDelaySession;

        var delayMs = Math.Clamp(_appSettings.FileJumpPickerShowDelayMs, 0, 10000);

        void QueueOpenFileJumpPicker()
        {
            Dispatcher.BeginInvoke(() =>
            {
                // 若未真正打开列表窗，必须撤销双按窗口期，否则 _fileJumpLastHotkeyTick 仍有效，
                // 短时间内再按会误走「二次快捷键直跳」，表现为列表从不弹出。
                if (session != _fileJumpPickerSession)
                {
                    _fileJumpLastHotkeyTick = 0;
                    return;
                }

                if (_activeFileJumpPicker != null || _fileJumpPickerOpenInProgress)
                {
                    _fileJumpLastHotkeyTick = 0;
                    return;
                }

                _fileJumpLastHotkeyTick = 0;
                _fileJumpPickerOpenInProgress = true;
                FileDialogJumpPickerWindow? picker = null;
                try
                {
                    picker = new FileDialogJumpPickerWindow(
                        capturedCandidates, preferIdx, jumpX, jumpY, _appSettings!, dialogForPicker,
                        autoForegroundStickyMode);
                    _activeFileJumpPicker = picker;
                    picker.Closed += (_, _) =>
                    {
                        if (ReferenceEquals(_activeFileJumpPicker, picker))
                            _activeFileJumpPicker = null;
                        ClearFileJumpDoubleTapState();
                    };
                    var accepted = picker.ShowDialog() == true;
                    if (!autoForegroundStickyMode && accepted && !string.IsNullOrEmpty(picker.SelectedPath))
                        FileDialogJumpHelper.TryNavigateToFolder(dialogForPicker, picker.SelectedPath, allowShellInject);
                }
                finally
                {
                    _fileJumpPickerOpenInProgress = false;
                }
            }, DispatcherPriority.Normal);
        }

        if (delayMs <= 0)
        {
            QueueOpenFileJumpPicker();
            return;
        }

        _fileJumpOpenDelayTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(delayMs),
        };
        _fileJumpOpenDelayTimer.Tick += (_, _) =>
        {
            _fileJumpOpenDelayTimer?.Stop();
            _fileJumpOpenDelayTimer = null;
            if (delaySession != _fileJumpDelaySession) return;
            QueueOpenFileJumpPicker();
        };
        _fileJumpOpenDelayTimer.Start();
    }

    private void CancelFileJumpPickerDelay()
    {
        if (_fileJumpOpenDelayTimer != null)
        {
            _fileJumpOpenDelayTimer.Stop();
            _fileJumpOpenDelayTimer = null;
        }
        unchecked { _fileJumpDelaySession++; }
    }

    /// <summary>前台可能是跳转列表窗；此时仍应对背后文件对话框取路径、导航。</summary>
    private IntPtr ResolveFileJumpTargetHwndInternal(IntPtr fgNow)
    {
        var resolved = FileDialogJumpHelper.ResolveFileDialogHwndFromWindowOrAncestor(fgNow);
        if (resolved != IntPtr.Zero)
            return resolved;

        if (_fileJumpLastDialogHwnd != IntPtr.Zero
            && Win32.IsWindow(_fileJumpLastDialogHwnd)
            && FileDialogJumpHelper.ClassifyFileDialog(_fileJumpLastDialogHwnd) != FileDialogKind.None)
            return _fileJumpLastDialogHwnd;

        if (_fileJumpLastDialogHwnd != IntPtr.Zero
            && Win32.IsWindow(_fileJumpLastDialogHwnd)
            && CustomFileDialogStore.FindMatchingRule(_fileJumpLastDialogHwnd) != null)
            return _fileJumpLastDialogHwnd;

        return IntPtr.Zero;
    }

    private static int PreferCandidateIndex(IntPtr dialogHwnd, List<FileJumpCandidate> candidates)
    {
        var zPath = FileManagerPathCollector.TryGetZOrderLinkedFolder(dialogHwnd, 2);
        if (string.IsNullOrEmpty(zPath)) return 0;
        var idx = candidates.FindIndex(c =>
            string.Equals(c.Path, zPath, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 ? idx : 0;
    }

    private void ClearFileJumpDoubleTapState()
    {
        CancelFileJumpPickerDelay();
        _fileJumpLastHotkeyTick = 0;
        _fileJumpLastDialogHwnd = IntPtr.Zero;
    }

    private void DisarmFileJumpClickToNavigate()
    {
        _fileJumpAutoArmedDialog = IntPtr.Zero;
        _fileJumpAutoArmedRoot = IntPtr.Zero;
        if (_fileJumpAutoMouseHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_fileJumpAutoMouseHook);
            _fileJumpAutoMouseHook = IntPtr.Zero;
        }
        if (s_fileJumpAutoMouseOwner == this)
        {
            s_fileJumpAutoMouseOwner = null;
            s_fileJumpAutoMouseHookForNext = IntPtr.Zero;
        }
    }

    private void InstallFileJumpAutoMouseHook()
    {
        if (_fileJumpAutoMouseHook != IntPtr.Zero) return;
        s_fileJumpAutoMouseOwner = this;
        _fileJumpAutoMouseHook = Win32.SetWindowsHookEx(
            Win32.WH_MOUSE_LL, s_fileJumpAutoMouseThunk, Win32.GetModuleHandle(null), 0);
        s_fileJumpAutoMouseHookForNext = _fileJumpAutoMouseHook;
    }

    private IntPtr FileJumpAutoMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0
            && _fileJumpAutoArmedRoot != IntPtr.Zero
            && _appSettings != null
            && _appSettings.FileJumpAutoOnFirstClick
            && wParam.ToInt32() == Win32.WM_LBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            var clickHwnd = Win32.WindowFromPoint(info.pt);
            if (clickHwnd != IntPtr.Zero
                && Win32.GetAncestor(clickHwnd, Win32.GA_ROOT) == _fileJumpAutoArmedRoot)
            {
                Dispatcher.BeginInvoke(new Action(TryFileJumpAutoNavigateAfterClick),
                    System.Windows.Threading.DispatcherPriority.Normal);
            }
        }
        return Win32.CallNextHookEx(_fileJumpAutoMouseHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 对话框成为前台后由 <see cref="OnForegroundChanged"/> 调度；在满足条件时挂接低级鼠标钩，等待首次落在对话框内的左键。
    /// 每个对话框顶层窗口在存活期内仅自动跳转成功一次（关闭后再开视为新窗口）。
    /// </summary>
    private void UpdateFileJumpClickToNavigateArm(IntPtr foregroundHwnd)
    {
        if (_appSettings == null || !_appSettings.FileJumpAutoOnFirstClick)
        {
            DisarmFileJumpClickToNavigate();
            return;
        }

        if (_fileJumpAutoFirstJumpDoneRoot != IntPtr.Zero
            && !Win32.IsWindow(_fileJumpAutoFirstJumpDoneRoot))
            _fileJumpAutoFirstJumpDoneRoot = IntPtr.Zero;

        if (_activeFileJumpPicker != null || _fileJumpPickerOpenInProgress)
        {
            DisarmFileJumpClickToNavigate();
            return;
        }

        if (foregroundHwnd == _hwnd)
        {
            DisarmFileJumpClickToNavigate();
            return;
        }

        var dialogHwnd = FileDialogJumpHelper.ResolveFileDialogHwndFromWindowOrAncestor(foregroundHwnd);
        if (dialogHwnd == IntPtr.Zero)
        {
            if (_fileJumpAutoArmedRoot != IntPtr.Zero && Win32.IsWindow(foregroundHwnd))
            {
                var fgRoot = Win32.GetAncestor(foregroundHwnd, Win32.GA_ROOT);
                if (fgRoot == _fileJumpAutoArmedRoot)
                    return;
            }
            DisarmFileJumpClickToNavigate();
            return;
        }
        var dialogRoot = Win32.GetAncestor(dialogHwnd, Win32.GA_ROOT);
        if (dialogRoot != IntPtr.Zero
            && dialogRoot == _fileJumpAutoFirstJumpDoneRoot
            && Win32.IsWindow(dialogRoot))
        {
            DisarmFileJumpClickToNavigate();
            return;
        }

        if (_fileJumpAutoArmedDialog == dialogHwnd && _fileJumpAutoMouseHook != IntPtr.Zero)
            return;

        _fileJumpAutoArmedDialog = dialogHwnd;
        _fileJumpAutoArmedRoot = dialogRoot;
        InstallFileJumpAutoMouseHook();
    }

    private void TryFileJumpAutoNavigateAfterClick()
    {
        try
        {
            if (_appSettings == null) return;
            var dlg = _fileJumpAutoArmedDialog;
            if (dlg == IntPtr.Zero || !Win32.IsWindow(dlg)) return;
            if (FileDialogJumpHelper.ClassifyFileDialog(dlg) == FileDialogKind.None
                && CustomFileDialogStore.FindMatchingRule(dlg) == null) return;

            var fg = Win32.GetForegroundWindow();
            var fgDlg = FileDialogJumpHelper.ResolveFileDialogHwndFromWindowOrAncestor(fg);
            if (fgDlg == IntPtr.Zero || Win32.GetAncestor(fgDlg, Win32.GA_ROOT) != _fileJumpAutoArmedRoot)
                return;

            var mem = _appSettings.LastFileDialogFolder?.Trim();
            var candidates = FileManagerPathCollector.CollectCandidates(dlg, mem);
            if (candidates.Count == 0) return;

            var doneRoot = Win32.GetAncestor(dlg, Win32.GA_ROOT);
            DisarmFileJumpClickToNavigate();
            if (FileDialogJumpHelper.TryNavigateToFolder(dlg, candidates[0].Path, _appSettings.EnableShellNavigateInject)
                && doneRoot != IntPtr.Zero)
                _fileJumpAutoFirstJumpDoneRoot = doneRoot;
        }
        catch (Exception ex)
        {
            ShellNavigateLog.Write("filejump", "TryFileJumpAutoNavigateAfterClick: " + ex);
            DisarmFileJumpClickToNavigate();
        }
    }

    private static bool IsMenuAltVk(uint vk) =>
        vk == 0x12 || vk == 0xA4 || vk == 0xA5;

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var msg = wParam.ToInt32();
        var kb = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
        var isKeyDown = msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
        var isKeyUp = msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;

        // 防止 Alt+快捷键 释放后的单独 Alt 激活前台窗口菜单：关闭弹窗后 200ms 内仍拦截 Alt 按键
        if (!_isPopupVisible && isKeyUp && IsMenuAltVk(kb.vkCode))
        {
            var timeSinceHide = Environment.TickCount64 - _lastPopupHideTick;
            if (timeSinceHide >= 0 && timeSinceHide < 200)
                return (IntPtr)1;
        }

#if CLIPX_CLIPBOARD
        // 非本窗口内 Ctrl+V 松 V、或 Shift+Insert 松 Insert：FIFO/LIFO 出队并写下一项到剪贴板；不拦截按键
        if (isKeyUp)
        {
            var injEarly = (kb.flags & (Win32.LLKHF_INJECTED | Win32.LLKHF_LOWER_IL_INJECTED)) != 0;
            if (!injEarly && !_isSettingClipboard && !_pasteInProgress && _activeFileJumpPicker == null)
            {
                bool ctrlNow = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0
                    || (Win32.GetAsyncKeyState(0xA2) & 0x8000) != 0
                    || (Win32.GetAsyncKeyState(0xA3) & 0x8000) != 0;
                bool shiftNow = (Win32.GetAsyncKeyState(0x10) & 0x8000) != 0
                    || (Win32.GetAsyncKeyState(0xA0) & 0x8000) != 0
                    || (Win32.GetAsyncKeyState(0xA1) & 0x8000) != 0;
                bool pasteChord =
                    (kb.vkCode == Win32.VK_V && ctrlNow)
                    || (kb.vkCode == Win32.VK_INSERT && shiftNow);
                if (pasteChord)
                {
                    var fg = Win32.GetForegroundWindow();
                    if (fg != IntPtr.Zero && fg != _hwnd
                        && GetBatchMode() != BatchPasteQueueMode.Off
                        && _batchQueue.Count > 0)
                    {
                        long tick = Environment.TickCount64;
                        if (tick - _lastGlobalPasteQueueAdvanceTick > 120)
                        {
                            _lastGlobalPasteQueueAdvanceTick = tick;
                            Dispatcher.BeginInvoke(new Action(TryAdvancePasteQueueAfterGlobalPaste));
                        }
                    }
                }
            }
        }
#endif

        if (!_isPopupVisible)
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        // 面板未关闭时钩子仍会吃掉未识别的键；多段粘贴中间几次不走 HidePopup，SendInput 的 Shift+Insert 必须放行。
        if ((kb.flags & (Win32.LLKHF_INJECTED | Win32.LLKHF_LOWER_IL_INJECTED)) != 0)
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        if (_activeFileJumpPicker != null)
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        if (isKeyUp && IsMenuAltVk(kb.vkCode))
        {
            if (PhraseEditPopup.IsOpen)
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            if (BatchMenuPopup.IsOpen)
            {
                if (_ctxAltCloseMenuArmed && !_ctxAltComboDuringRelease)
                    Dispatcher.BeginInvoke(() => { BatchMenuPopup.IsOpen = false; CloseBatchMenuNavUi(); });
                _ctxAltCloseMenuArmed = false;
                _ctxAltAwaitRelease = false;
                _ctxAltComboDuringRelease = false;
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }

            if (ContextPopup.IsOpen)
            {
                if (_ctxAltCloseMenuArmed && !_ctxAltComboDuringRelease)
                    Dispatcher.BeginInvoke(CloseContextMenuPopup);
                _ctxAltCloseMenuArmed = false;
                _ctxAltAwaitRelease = false;
                _ctxAltComboDuringRelease = false;
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }

            if (_ctxAltAwaitRelease && !_ctxAltComboDuringRelease)
                Dispatcher.BeginInvoke(TryOpenBatchOrContextMenuFromKeyboard);
            _ctxAltAwaitRelease = false;
            _ctxAltComboDuringRelease = false;
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        if (!isKeyDown)
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        if (PhraseEditPopup.IsOpen)
        {
            if (kb.vkCode == Win32.VK_ESCAPE)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    PhraseEditPopup.IsOpen = false;
                    _phraseEditEntry = null;
                    _phraseEditBuffer = "";
                });
                return (IntPtr)1;
            }
            if (kb.vkCode == Win32.VK_RETURN)
            {
                Dispatcher.BeginInvoke(CommitPhraseEdit);
                return (IntPtr)1;
            }

            if (kb.vkCode is 0x10 or 0x11 or 0x14
                or 0xA0 or 0xA1 or 0xA2 or 0xA3
                or 0x5B or 0x5C)
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            bool phraseCtrlDown = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0;
            bool phraseAltDown = ((Win32.GetAsyncKeyState(0x12) & 0x8000) != 0)
                || ((Win32.GetAsyncKeyState(0xA4) & 0x8000) != 0)
                || ((Win32.GetAsyncKeyState(0xA5) & 0x8000) != 0);
            bool phraseWinDown = ((Win32.GetAsyncKeyState(0x5B) & 0x8000) != 0)
                || ((Win32.GetAsyncKeyState(0x5C) & 0x8000) != 0);
            if (phraseCtrlDown || phraseAltDown || phraseWinDown)
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            if (kb.vkCode == Win32.VK_BACK)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_phraseEditBuffer.Length > 0)
                        _phraseEditBuffer = _phraseEditBuffer[..^1];
                    RefreshPhraseEditDisplay();
                });
                return (IntPtr)1;
            }

            if (kb.vkCode == 0x09)
                return (IntPtr)1;

            if (kb.vkCode == Win32.VK_SPACE)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_phraseEditBuffer.Length < PhraseEditMaxLen)
                        _phraseEditBuffer += " ";
                    RefreshPhraseEditDisplay();
                });
                return (IntPtr)1;
            }

            if (kb.vkCode is Win32.VK_UP or Win32.VK_DOWN or Win32.VK_LEFT or Win32.VK_RIGHT
                or Win32.VK_HOME or Win32.VK_END or Win32.VK_PRIOR or Win32.VK_NEXT
                or Win32.VK_DELETE)
                return (IntPtr)1;

            var pch = VkToChar(kb.vkCode, kb.scanCode);
            if (pch.HasValue && _phraseEditBuffer.Length < PhraseEditMaxLen)
                Dispatcher.BeginInvoke(() =>
                {
                    _phraseEditBuffer += pch.Value;
                    RefreshPhraseEditDisplay();
                });
            return (IntPtr)1;
        }

        if (BatchMenuPopup.IsOpen)
        {
            if (IsMenuAltVk(kb.vkCode))
            {
                _ctxAltCloseMenuArmed = true;
                _ctxAltComboDuringRelease = false;
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }

            bool altPhyB = ((Win32.GetAsyncKeyState(0x12) & 0x8000) != 0)
                || ((Win32.GetAsyncKeyState(0xA4) & 0x8000) != 0)
                || ((Win32.GetAsyncKeyState(0xA5) & 0x8000) != 0);
            if (_ctxAltCloseMenuArmed && altPhyB)
                _ctxAltComboDuringRelease = true;

            switch (kb.vkCode)
            {
                case Win32.VK_UP:
                    Dispatcher.BeginInvoke(() => MoveBatchMenuHighlight(-1));
                    return (IntPtr)1;
                case Win32.VK_DOWN:
                    Dispatcher.BeginInvoke(() => MoveBatchMenuHighlight(1));
                    return (IntPtr)1;
                case Win32.VK_RETURN:
                    Dispatcher.BeginInvoke(ActivateBatchMenuHighlight);
                    return (IntPtr)1;
                case Win32.VK_ESCAPE:
                    Dispatcher.BeginInvoke(() => { BatchMenuPopup.IsOpen = false; CloseBatchMenuNavUi(); });
                    return (IntPtr)1;
                default:
                    return (IntPtr)1;
            }
        }

        if (ContextPopup.IsOpen)
        {
            if (IsMenuAltVk(kb.vkCode))
            {
                _ctxAltCloseMenuArmed = true;
                _ctxAltComboDuringRelease = false;
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }

            bool altPhy = ((Win32.GetAsyncKeyState(0x12) & 0x8000) != 0)
                || ((Win32.GetAsyncKeyState(0xA4) & 0x8000) != 0)
                || ((Win32.GetAsyncKeyState(0xA5) & 0x8000) != 0);
            if (_ctxAltCloseMenuArmed && altPhy)
                _ctxAltComboDuringRelease = true;

            switch (kb.vkCode)
            {
                case Win32.VK_UP:
                    Dispatcher.BeginInvoke(() => MoveContextMenuHighlight(-1));
                    return (IntPtr)1;
                case Win32.VK_DOWN:
                    Dispatcher.BeginInvoke(() => MoveContextMenuHighlight(1));
                    return (IntPtr)1;
                case Win32.VK_RETURN:
                    Dispatcher.BeginInvoke(ActivateContextMenuHighlight);
                    return (IntPtr)1;
                case Win32.VK_ESCAPE:
                    Dispatcher.BeginInvoke(CloseContextMenuPopup);
                    return (IntPtr)1;
                default:
                    return (IntPtr)1;
            }
        }

        if (IsMenuAltVk(kb.vkCode) && !PhraseEditPopup.IsOpen && !ContextPopup.IsOpen && !BatchMenuPopup.IsOpen)
        {
            _ctxAltAwaitRelease = true;
            _ctxAltComboDuringRelease = false;
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        if (_ctxAltAwaitRelease && !IsMenuAltVk(kb.vkCode))
            _ctxAltComboDuringRelease = true;

        if (kb.vkCode is 0x10 or 0x11 or 0x14
            or 0xA0 or 0xA1 or 0xA2 or 0xA3
            or 0x5B or 0x5C)
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        bool ctrlHeld = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool altHeld = (Win32.GetAsyncKeyState(0x12) & 0x8000) != 0;

        if (IsPanelModifierDown())
        {
            if (kb.vkCode >= 0x31 && kb.vkCode <= 0x39)
            {
                int idx = (int)(kb.vkCode - 0x30);
                Dispatcher.BeginInvoke(() => PasteByIndex(idx));
                return (IntPtr)1;
            }
            if (kb.vkCode == 0x09)
            {
                Dispatcher.BeginInvoke(ToggleQuickPhraseFilter);
                return (IntPtr)1;
            }
            if (kb.vkCode == 0xBB)
            {
                Dispatcher.BeginInvoke(() => ScrollPage(1));
                return (IntPtr)1;
            }
            if (kb.vkCode == 0xBD)
            {
                Dispatcher.BeginInvoke(() => ScrollPage(-1));
                return (IntPtr)1;
            }
            if (kb.vkCode == Win32.VK_RETURN)
            {
                bool ctrlEnter = (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0;
                Dispatcher.BeginInvoke(() => HandleMainEnterKey(ctrlEnter));
                return (IntPtr)1;
            }
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        // Enter：不等同于「组合键放行」。Ctrl+点击多选后常仍按住 Ctrl 再按回车，必须在钩子内拦截。
        if (kb.vkCode == Win32.VK_RETURN)
        {
            bool ctrlEnter = (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0;
            Dispatcher.BeginInvoke(() => HandleMainEnterKey(ctrlEnter));
            return (IntPtr)1;
        }

        if (ctrlHeld || altHeld)
            return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        switch (kb.vkCode)
        {
            case Win32.VK_SPACE:
                Dispatcher.BeginInvoke(ToggleEntryPreviewBubble);
                return (IntPtr)1;
            case Win32.VK_UP:
                Dispatcher.BeginInvoke(() =>
                {
                    bool shift = (Win32.GetAsyncKeyState(0x10) & 0x8000) != 0
                        || (Win32.GetAsyncKeyState(0xA0) & 0x8000) != 0
                        || (Win32.GetAsyncKeyState(0xA1) & 0x8000) != 0;
                    if (shift)
                        MoveSelectionExtend(-1);
                    else
                        MoveSelection(-1);
                });
                return (IntPtr)1;
            case Win32.VK_DOWN:
                Dispatcher.BeginInvoke(() =>
                {
                    bool shift = (Win32.GetAsyncKeyState(0x10) & 0x8000) != 0
                        || (Win32.GetAsyncKeyState(0xA0) & 0x8000) != 0
                        || (Win32.GetAsyncKeyState(0xA1) & 0x8000) != 0;
                    if (shift)
                        MoveSelectionExtend(1);
                    else
                        MoveSelection(1);
                });
                return (IntPtr)1;
            case Win32.VK_HOME:
                Dispatcher.BeginInvoke(MoveSelectionToFirst);
                return (IntPtr)1;
            case Win32.VK_END:
                Dispatcher.BeginInvoke(MoveSelectionToLast);
                return (IntPtr)1;
            case Win32.VK_PRIOR:
                Dispatcher.BeginInvoke(() => ScrollPage(-1));
                return (IntPtr)1;
            case Win32.VK_NEXT:
                Dispatcher.BeginInvoke(() => ScrollPage(1));
                return (IntPtr)1;
            case Win32.VK_LEFT:
                Dispatcher.BeginInvoke(() => ScrollPage(-1));
                return (IntPtr)1;
            case Win32.VK_RIGHT:
                Dispatcher.BeginInvoke(() => ScrollPage(1));
                return (IntPtr)1;
            case Win32.VK_ESCAPE:
                Dispatcher.BeginInvoke(() =>
                {
                    if (BatchMenuPopup.IsOpen)
                    {
                        BatchMenuPopup.IsOpen = false;
                        CloseBatchMenuNavUi();
                        return;
                    }
                    if (ContextPopup.IsOpen)
                    {
                        CloseContextMenuPopup();
                        return;
                    }
                    if (ShortcutHelpPopup.IsOpen)
                    {
                        ShortcutHelpPopup.IsOpen = false;
                        return;
                    }
                    if (EntryPreviewPopup.IsOpen)
                    {
                        CloseEntryPreviewBubble();
                        return;
                    }
                    if (_pendingDeleteEntry != null)
                    {
                        ClearPendingDelete();
                        return;
                    }
                    if (_searchText.Length > 0) { _searchText = ""; RefreshFilter(); }
                    else HidePopup();
                });
                return (IntPtr)1;
            case Win32.VK_BACK:
                Dispatcher.BeginInvoke(() =>
                {
                    if (_searchText.Length > 0) { _searchText = _searchText[..^1]; RefreshFilter(); }
                });
                return (IntPtr)1;
            case 0x09:
                Dispatcher.BeginInvoke(CycleTypeFilter);
                return (IntPtr)1;
            case Win32.VK_DELETE:
                Dispatcher.BeginInvoke(DeleteSelectedItemWithConfirm);
                return (IntPtr)1;
        }

        var ch = VkToChar(kb.vkCode, kb.scanCode);
        if (ch.HasValue)
            Dispatcher.BeginInvoke(() => { _searchText += ch.Value; RefreshFilter(); });

        return (IntPtr)1;
    }

    private bool IsPanelModifierDown()
    {
        bool ctrl = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool alt = (Win32.GetAsyncKeyState(0x12) & 0x8000) != 0;
        bool win = ((Win32.GetAsyncKeyState(0x5B) | Win32.GetAsyncKeyState(0x5C)) & 0x8000) != 0;
        bool caps = (Win32.GetAsyncKeyState(0x14) & 0x8000) != 0;

        return _panelModifierKey switch
        {
            "Alt" => alt && !ctrl,
            "Win" => win && !ctrl && !alt,
            "CapsLock" => caps && !ctrl && !alt,
            _ => ctrl && !alt,
        };
    }

    private string PanelModifierDisplayName => _panelModifierKey switch
    {
        "Alt" => "Alt",
        "Win" => "Win",
        "CapsLock" => "CapsLk",
        _ => "Ctrl",
    };

    private void UpdateFooterHints()
    {
        string m = PanelModifierDisplayName;
        var hintBrush = FindResource("HintText") as System.Windows.Media.Brush;
        var titleBrush = FindResource("PrimaryText") as System.Windows.Media.Brush;
        var bodyBrush = FindResource("SecondaryText") as System.Windows.Media.Brush;

        var lines = new List<(string Key, string Label)>
        {
            ($"{m}+N", "快贴"),
            ("↑↓", "选择"),
            ("Home/End", "首尾"),
            ("PgUp/Dn", "翻页"),
            ("Enter", "单条粘贴；有队列时先贴队首；普通+多选为顺序连贴；FIFO/LIFO+多选为入队；其它窗口 Ctrl+V 或 Shift+Insert 各出队一条；Ctrl+Enter 多选时每条文本末换行（仅普通连贴）"),
            ($"{m}+Tab", "仅看快捷短语（再按切换）"),
            ("Tab", "循环类型筛选"),
            ("a-z", "拼音搜索"),
        };
#if CLIPX_CLIPBOARD
        lines.Add(("Space/中键", "预览当前条目"));
        lines.Add(("Shift+↑↓", "扩展多选；FIFO/LIFO 时新复制会按模式入队（角标、队列置顶）"));
        var batchCy = _appSettings?.BatchModeCycleHotkeyDisplayName ?? "Alt+/";
        lines.Add((batchCy, "循环批量模式（普通→LIFO→FIFO），与顶栏「批量」左键相同；可在设置里修改"));
#endif
        lines.Add(("Del×2", "连按两次删除当前条目"));
        lines.Add(("Alt", "批量一次性粘贴菜单（已开 FIFO/LIFO 或队列非空）否则条目右键菜单（↑↓ Enter）"));
        lines.Add(("Esc", "关闭菜单、预览、取消删除线、清空搜索或关闭面板"));

        FooterHints.Inlines.Clear();
        for (int i = 0; i < lines.Count; i++)
        {
            var (key, label) = lines[i];
            if (i > 0) FooterHints.Inlines.Add(new System.Windows.Documents.Run(" · "));
            FooterHints.Inlines.Add(new System.Windows.Documents.Run(key) { Foreground = hintBrush });
            FooterHints.Inlines.Add(new System.Windows.Documents.Run(CompactFooterLabel(label)));
        }

        ShortcutHelpFullText.Inlines.Clear();
        ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.Run("快捷键说明")
            { FontWeight = FontWeights.SemiBold, Foreground = titleBrush, FontSize = 13 });
        ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.LineBreak());
        ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.LineBreak());
        foreach (var (key, label) in lines)
        {
            ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.Run(key) { Foreground = hintBrush });
            ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.Run("　"));
            ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.Run(label) { Foreground = bodyBrush });
            ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.LineBreak());
        }

        ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.LineBreak());
        ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.Run(
            $"与 {m} 同时按下时（在设置中可更换面板修饰键）：")
            { Foreground = bodyBrush });
        ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.LineBreak());
        foreach (var (key, label) in new (string Key, string Label)[]
        {
            ("1～9", "粘贴列表第 1～9 条"),
            ("Tab", "仅看快捷短语开/关"),
            ("- / =", "列表向上 / 向下翻页"),
        })
        {
            ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.Run($"{m}+{key}") { Foreground = hintBrush });
            ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.Run("　"));
            ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.Run(label) { Foreground = bodyBrush });
            ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.LineBreak());
        }
    }

    /// <summary>底栏一行字数过多时用简短文案，完整说明见 more 气泡。</summary>
    private static string CompactFooterLabel(string full) => full switch
    {
        "仅看快捷短语（再按切换）" => "短语",
        "循环类型筛选" => "筛选",
        "拼音搜索" => "拼音",
        "预览当前条目" => "预览",
        "连按两次删除当前条目" => "删除",
        "键盘打开右键菜单（↑↓ Enter）" => "右键菜单",
        "关闭菜单、预览、取消删除线、清空搜索或关闭面板" => "取消/关闭",
        "单条粘贴；有批量队列时先贴队首；多选时按列表顺序连续贴；Ctrl+Enter 多选时每条文本末尾附带换行" => "粘贴",
        "扩展选中区间并写入批量队列（顶栏排序+角标）" => "批量选",
        "批量一次性粘贴菜单（已开 FIFO/LIFO 或队列非空）否则条目右键菜单（↑↓ Enter）" => "Alt菜单",
        "循环批量模式（普通→LIFO→FIFO），与顶栏「批量」左键相同；可在设置里修改" => "批量模式",
        _ => full
    };

    private void ShortcutHelpMore_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShortcutHelpPopup.IsOpen = !ShortcutHelpPopup.IsOpen;
    }

    #region Entry preview bubble (Space)

    private void CloseEntryPreviewBubble() => EntryPreviewPopup.IsOpen = false;

    private void ToggleEntryPreviewBubble()
    {
        if (_displayItems.Count == 0) return;
        if (ItemsList.SelectedItem is not ClipboardEntry) return;

        if (EntryPreviewPopup.IsOpen)
        {
            CloseEntryPreviewBubble();
            return;
        }

        ShowEntryPreviewBubble();
    }

    /// <summary>打开预览气泡（不关闭已打开的预览；用于中键切换条目时更新内容）。</summary>
    private void ShowEntryPreviewBubble()
    {
        if (_displayItems.Count == 0) return;
        if (ItemsList.SelectedItem is not ClipboardEntry entry) return;

        UpdateEntryPreviewBubbleContent(entry);
        EntryPreviewPopup.IsOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (EntryPreviewPopup.IsOpen)
                PositionEntryPreviewPopup();
        });
    }

    private void PositionEntryPreviewPopup()
    {
        EntryPreviewPopup.PlacementTarget = MainBorder;

        if (MainBorder.ActualWidth <= 0 || !IsVisible)
        {
            EntryPreviewPopup.Placement = PlacementMode.Right;
            EntryPreviewPopup.HorizontalOffset = 10;
            EntryPreviewPopup.VerticalOffset = 0;
            return;
        }

        const double gap = 10;
        const double previewNominalW = 548;
        const double previewNominalH = 420;

        try
        {
            EntryPreviewPopup.Child?.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        catch
        {
            /* ignore */
        }

        var desiredW = EntryPreviewPopup.Child is FrameworkElement fe && fe.DesiredSize.Width > 1
            ? fe.DesiredSize.Width
            : previewNominalW;

        var topLeft = MainBorder.PointToScreen(new System.Windows.Point(0, 0));
        double mbW = MainBorder.ActualWidth;
        double mbH = MainBorder.ActualHeight;
        double mainRightEdge = topLeft.X + mbW;
        double mainLeftEdge = topLeft.X;

        var wa = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point(
                (int)(topLeft.X + mbW / 2),
                (int)(topLeft.Y + mbH / 2))).WorkingArea;

        double spaceRight = wa.Right - mainRightEdge;
        double spaceLeft = mainLeftEdge - wa.Left;
        bool placeRight = spaceRight >= desiredW + gap || spaceRight >= spaceLeft;

        if (placeRight)
        {
            EntryPreviewPopup.Placement = PlacementMode.Right;
            EntryPreviewPopup.HorizontalOffset = gap;
        }
        else
        {
            EntryPreviewPopup.Placement = PlacementMode.Left;
            EntryPreviewPopup.HorizontalOffset = -gap;
        }

        double verticalOffset = 0;
        if (ItemsList.SelectedItem != null
            && ItemsList.ItemContainerGenerator.ContainerFromItem(ItemsList.SelectedItem) is ListBoxItem item
            && item.IsVisible)
        {
            var itemTop = item.PointToScreen(new System.Windows.Point(0, 0));
            verticalOffset = itemTop.Y - topLeft.Y;
        }

        double minOff = wa.Top + 8 - topLeft.Y;
        double maxOff = wa.Bottom - 8 - previewNominalH - topLeft.Y;
        if (maxOff < minOff) maxOff = minOff;
        verticalOffset = Math.Clamp(verticalOffset, minOff, maxOff);

        EntryPreviewPopup.VerticalOffset = verticalOffset;
    }

    private void UpdateEntryPreviewBubbleContent(ClipboardEntry? entry = null)
    {
        entry ??= ItemsList.SelectedItem as ClipboardEntry;

        EntryPreviewText.Visibility = Visibility.Collapsed;
        EntryPreviewImage.Visibility = Visibility.Collapsed;
        EntryPreviewImage.Source = null;
        EntryPreviewText.Text = "";

        if (entry == null) return;

        if (entry.Type == EntryType.Text)
        {
            EntryPreviewText.Text = string.IsNullOrEmpty(entry.TextContent)
                ? "（空文本）"
                : entry.TextContent;
            EntryPreviewText.Visibility = Visibility.Visible;
            return;
        }

        if (entry.Type == EntryType.Image || entry.IsImageFile)
        {
            var bmp = LoadEntryPreviewBitmap(entry);
            if (bmp != null)
            {
                EntryPreviewImage.Source = bmp;
                EntryPreviewImage.Visibility = Visibility.Visible;
                return;
            }

            EntryPreviewText.Text = entry.Type == EntryType.Image
                ? "（无法解码该图片）"
                : string.Join(Environment.NewLine, entry.FilePaths ?? Array.Empty<string>());
            EntryPreviewText.Visibility = Visibility.Visible;
            return;
        }

        EntryPreviewText.Text = entry.FilePaths is { Length: > 0 }
            ? string.Join(Environment.NewLine, entry.FilePaths)
            : "（无路径）";
        EntryPreviewText.Visibility = Visibility.Visible;
    }

    private static BitmapSource? LoadEntryPreviewBitmap(ClipboardEntry entry)
    {
        try
        {
            if (entry.Type == EntryType.Image && entry.ImageData is { Length: > 0 } bytes)
            {
                using var ms = new MemoryStream(bytes);
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.StreamSource = ms;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 520;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }

            if (entry.IsImageFile && entry.FilePaths![0] is { } p && File.Exists(p))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(Path.GetFullPath(p));
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 520;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
        }
        catch
        {
            /* ignore */
        }

        return null;
    }

    private void SyncEntryPreviewWithSelection()
    {
        if (!EntryPreviewPopup.IsOpen) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!EntryPreviewPopup.IsOpen) return;
            if (ItemsList.SelectedItem is ClipboardEntry e)
            {
                UpdateEntryPreviewBubbleContent(e);
                PositionEntryPreviewPopup();
            }
            else
                CloseEntryPreviewBubble();
        });
    }

    #endregion

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

    private void MoveSelection(int delta)
    {
        if (_displayItems.Count == 0) return;
        _selectionRangeAnchor = -1;
        _selectionCursorEnd = -1;
        var idx = ItemsList.SelectedIndex + delta;
        if (idx < 0) idx = 0;
        if (idx >= _displayItems.Count) idx = _displayItems.Count - 1;
        ItemsList.SelectedIndex = idx;
        _mouseShiftAnchorIndex = idx;
        ItemsList.ScrollIntoView(ItemsList.SelectedItem);
    }

    private void MoveSelectionExtend(int delta)
    {
        if (_displayItems.Count == 0) return;

        // 第一次按 Shift+↑↓：以当前选中行为锚点
        if (_selectionRangeAnchor < 0)
        {
            int cur = ItemsList.SelectedIndex >= 0 ? ItemsList.SelectedIndex : 0;
            _selectionRangeAnchor = cur;
            _selectionCursorEnd = cur;
        }

        // 移动"光标端"
        _selectionCursorEnd = Math.Clamp(_selectionCursorEnd + delta, 0, _displayItems.Count - 1);

        // 更新 WPF 多选区间（锚点↔光标端 之间所有行）
        int a = Math.Min(_selectionRangeAnchor, _selectionCursorEnd);
        int b = Math.Max(_selectionRangeAnchor, _selectionCursorEnd);
        ItemsList.SelectedItems.Clear();
        for (int i = a; i <= b; i++)
            ItemsList.SelectedItems.Add(_displayItems[i]);
        ItemsList.ScrollIntoView(_displayItems[_selectionCursorEnd]);
        _mouseShiftAnchorIndex = _selectionCursorEnd;
    }

    private void MoveSelectionToFirst()
    {
        if (_displayItems.Count == 0) return;
        _selectionRangeAnchor = -1;
        _selectionCursorEnd = -1;
        ItemsList.SelectedIndex = 0;
        _mouseShiftAnchorIndex = 0;
        ItemsList.ScrollIntoView(ItemsList.SelectedItem);
    }

    private void MoveSelectionToLast()
    {
        if (_displayItems.Count == 0) return;
        _selectionRangeAnchor = -1;
        _selectionCursorEnd = -1;
        ItemsList.SelectedIndex = _displayItems.Count - 1;
        _mouseShiftAnchorIndex = _displayItems.Count - 1;
        ItemsList.ScrollIntoView(ItemsList.SelectedItem);
    }

    private void ScrollPage(int direction)
    {
        if (_displayItems.Count == 0) return;
        _selectionRangeAnchor = -1;
        _selectionCursorEnd = -1;
        var sv = GetListScrollViewer();
        if (sv == null) return;

        double itemHeight = sv.ExtentHeight / _displayItems.Count;
        if (itemHeight <= 0) return;

        int oldFirstVisible = Math.Max(0, (int)(sv.VerticalOffset / itemHeight));
        int relSelection = Math.Max(0, ItemsList.SelectedIndex - oldFirstVisible);

        double newOffset = sv.VerticalOffset + direction * PageSize * itemHeight;
        newOffset = Math.Max(0, Math.Min(newOffset, sv.ScrollableHeight));
        sv.ScrollToVerticalOffset(newOffset);

        int newFirstVisible = Math.Max(0, (int)(newOffset / itemHeight));
        _firstVisibleIndex = newFirstVisible;
        int newSel = Math.Clamp(newFirstVisible + relSelection, 0, _displayItems.Count - 1);
        ItemsList.SelectedIndex = newSel;
        _mouseShiftAnchorIndex = newSel;

        UpdateVisibleIndices(newFirstVisible);
    }

    private void UpdateVisibleIndices(int firstVisible)
    {
        for (int i = 0; i < _displayItems.Count; i++)
        {
            int rel = i - firstVisible + 1;
            _displayItems[i].DisplayIndex = (rel >= 1 && rel <= 9) ? rel : 0;
        }
    }

    private ScrollViewer? GetListScrollViewer()
    {
        if (VisualTreeHelper.GetChildrenCount(ItemsList) == 0) return null;
        var border = VisualTreeHelper.GetChild(ItemsList, 0) as System.Windows.Controls.Decorator;
        return border?.Child as ScrollViewer;
    }

    #endregion

    #region Paste

    private static void CleanupOldClipboardExports()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "ClipboardX");
            if (!Directory.Exists(dir)) return;
            var threshold = DateTime.UtcNow.AddHours(-24);
            foreach (var f in Directory.GetFiles(dir, "clip_*.png"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < threshold)
                        File.Delete(f);
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// 使用 await Task.Delay 重试，避免 Thread.Sleep 卡死 UI；仅少量短重试，失败快速返回。
    /// </summary>
    private static async Task<bool> TrySetClipboardAsync(
        Action setAction,
        string logOp,
        int maxRetries = 2,
        int delayMs = 40,
        IntPtr clipNudgeHwnd = default)
    {
        Exception? last = null;
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                Win32.CloseClipboard();
                setAction();
                if (i > 0)
                    ClipboardDiagnosticsLog.Write($"TrySetClipboard OK after retry i={i} op={logOp}");
                return true;
            }
            catch (Exception ex)
            {
                last = ex;
                var hr = ex is COMException com ? $" hr=0x{(uint)com.HResult:X8}" : "";
                ClipboardDiagnosticsLog.Write($"TrySetClipboard fail attempt={i + 1}/{maxRetries} op={logOp} {ex.GetType().Name}: {ex.Message}{hr}");
                if (i >= maxRetries - 1) break;
                if (IsClipboardCantOpen(ex) && clipNudgeHwnd != IntPtr.Zero && Win32.TryEmptyClipboardAfterOpen(clipNudgeHwnd))
                    ClipboardDiagnosticsLog.Write($"TrySetClipboard reNudge op={logOp} afterAttempt={i + 1}");
                await Task.Delay(delayMs);
            }
        }
        ClipboardDiagnosticsLog.Write($"TrySetClipboard GAVE_UP op={logOp} last={last?.GetType().Name}: {last?.Message}");
        return false;
    }

    private async void PasteSelectedItem()
    {
        if (ItemsList.SelectedItem is not ClipboardEntry item) return;
        await PasteEntryAsync(item, hidePopupAfter: true);
    }

    /// <param name="sequentialSegmentIndex">≥0 表示连续粘贴中的第几段（无段间延时）；-1 表示单次粘贴（保留对焦/剪贴板/回波延时）。</param>
    /// <param name="sendNewlineAfterTextWhenCtrlEnterBatch">多选且由 Ctrl+Enter 触发时：每一小段为文本则写入剪贴板时在末尾追加系统换行（不再发键盘回车）。</param>
    private async Task PasteEntryAsync(
        ClipboardEntry item,
        bool hidePopupAfter,
        int sequentialSegmentIndex = -1,
        bool sendNewlineAfterTextWhenCtrlEnterBatch = false)
    {
        _pasteInProgress = true;
        try
        {
        ClearPendingDelete();

        // 连续粘贴时若每段都置顶+写库+通知 UI，列表会逐条「蹦」且加重卡顿；整轮结束后再统一排序（见 ApplyDeferredSequentialPasteHistoryOrder）。
        if (!item.IsQuickPaste && !_sequentialPasteHold)
        {
            var idx = _allItems.IndexOf(item);
            if (idx > 0) { _allItems.RemoveAt(idx); _allItems.Insert(0, item); }
            item.TouchCopiedTime();
            if (item.PersistedId is long pid)
                _historyStore.TryUpdateCopiedAt(pid, item.CopiedAt);
        }

        if (_targetWindow != IntPtr.Zero && !Win32.IsWindow(_targetWindow))
            _targetWindow = IntPtr.Zero;

        var tgt = _targetWindow.ToInt64();
        ClipboardDiagnosticsLog.Write(item.Type switch
        {
            EntryType.Text => $"paste BEGIN Text len={item.TextContent?.Length ?? 0} target=0x{tgt:X} gwFocus={Win32.GetForegroundWindow().ToInt64():X}",
            EntryType.Image => $"paste BEGIN Image pngBytes={item.ImageData?.Length ?? 0} target=0x{tgt:X}",
            EntryType.Files => $"paste BEGIN Files {SummarizeFileDropForLog(item.FilePaths ?? [])} target=0x{tgt:X}",
            _ => $"paste BEGIN type={item.Type} target=0x{tgt:X}"
        });

        // 在仍是弹窗前台时 OpenClipboard，常与目标进程（大段文本/富文本 OLE）争用 → 连续 CLIPBRD_E_CANT_OPEN。先 Hide + 强力切回目标并等待剪贴板释放。
        if (hidePopupAfter)
            HidePopup();
        if (_targetWindow != IntPtr.Zero)
            Win32.SetForegroundWindowAggressive(_targetWindow);
        var textLen = item.TextContent?.Length ?? 0;
        var imgBytes = item.ImageData?.Length ?? 0;
        var imgPixels = item.Type == EntryType.Image ? item.ImageWidth * item.ImageHeight : 0;
        var hugeClipboardImage = item.Type == EntryType.Image && (imgBytes > 900_000 || imgPixels > 1_200_000);
        var noSegmentDelays = sequentialSegmentIndex >= 0;
        if (!noSegmentDelays)
        {
            var focusSettleMs = item.Type switch
            {
                EntryType.Text => textLen > 12000 ? 150 : textLen > 4000 ? 120 : 70,
                EntryType.Image => hugeClipboardImage ? 220 : 160,
                _ => 50
            };
            await Task.Delay(focusSettleMs);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            ClipboardDiagnosticsLog.Write($"paste focusSettleMs={focusSettleMs} after Hide+SetForegroundAggressive");
        }
        else
        {
            if (sequentialSegmentIndex == 0)
                await Dispatcher.Yield();
            ClipboardDiagnosticsLog.Write($"paste sequential segment {sequentialSegmentIndex} (no focus settle delay)");
        }

        if (_hwnd != IntPtr.Zero)
        {
            var nudged = Win32.TryEmptyClipboardAfterOpen(_hwnd);
            ClipboardDiagnosticsLog.Write($"paste clipNudge EmptyClipboard ok={nudged}");
        }

        _isSettingClipboard = true;
        bool clipboardOk = false;
        var clipRetries = noSegmentDelays ? 8 : 2;
        var clipRetryDelayMs = noSegmentDelays ? 60 : 40;
        try
        {
            switch (item.Type)
            {
                case EntryType.Text:
                {
                    var clipText = sendNewlineAfterTextWhenCtrlEnterBatch && noSegmentDelays
                        ? item.TextContent! + Environment.NewLine
                        : item.TextContent!;
                    clipboardOk = await TrySetClipboardAsync(
                        () => System.Windows.Clipboard.SetText(clipText),
                        $"SetText len={clipText.Length}",
                        maxRetries: clipRetries,
                        delayMs: clipRetryDelayMs,
                        clipNudgeHwnd: _hwnd);
                    break;
                }
                case EntryType.Image:
                    // BitmapDecoder+Frame 在 using 结束后会释放流，而 SetImage 对 OLE 常延迟落盘，目标程序 Ctrl+V 拿到空/坏图。
                    // BitmapImage + OnLoad 在 EndInit 时把像素读入内存，Freeze 后可安全关闭流再写剪贴板。
                    BitmapImage? bi = null;
                    var swDec = Stopwatch.StartNew();
                    using (var ms = new MemoryStream(item.ImageData!))
                    {
                        bi = new BitmapImage();
                        bi.BeginInit();
                        bi.StreamSource = ms;
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.EndInit();
                    }
                    swDec.Stop();
                    if (bi == null) break;
                    if (bi.CanFreeze) bi.Freeze();
                    ClipboardDiagnosticsLog.Write(
                        $"paste image loadMs={swDec.ElapsedMilliseconds} frame={bi.PixelWidth}x{bi.PixelHeight} storedPng={item.ImageData?.Length ?? 0}");
                    clipboardOk = await TrySetClipboardAsync(
                        () => System.Windows.Clipboard.SetImage(bi),
                        $"SetImage {bi.PixelWidth}x{bi.PixelHeight}",
                        maxRetries: clipRetries,
                        delayMs: clipRetryDelayMs,
                        clipNudgeHwnd: _hwnd);
                    if (!clipboardOk && item.ImageData is { Length: > 0 })
                    {
                        string? tmpPath = null;
                        try
                        {
                            var dir = Path.Combine(Path.GetTempPath(), "ClipboardX");
                            Directory.CreateDirectory(dir);
                            tmpPath = Path.Combine(dir, $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}_fb.png");
                            File.WriteAllBytes(tmpPath, item.ImageData);
                            var flFb = new StringCollection();
                            flFb.Add(tmpPath);
                            clipboardOk = await TrySetClipboardAsync(
                                () => System.Windows.Clipboard.SetFileDropList(flFb),
                                "SetFileDropList imageFallback",
                                maxRetries: clipRetries,
                                delayMs: clipRetryDelayMs,
                                clipNudgeHwnd: _hwnd);
                            if (!clipboardOk)
                            {
                                try { File.Delete(tmpPath); } catch { /* ignore */ }
                            }
                            else
                                ClipboardDiagnosticsLog.Write($"paste image fallback SetFileDropList ok \"{tmpPath}\"");
                        }
                        catch (Exception ex)
                        {
                            ClipboardDiagnosticsLog.Write($"paste image fallback EX {ex.GetType().Name}: {ex.Message}");
                            if (tmpPath != null) try { File.Delete(tmpPath); } catch { /* ignore */ }
                        }
                    }
                    break;
                case EntryType.Files:
                    var fl = new StringCollection();
                    fl.AddRange(item.FilePaths!);
                    clipboardOk = await TrySetClipboardAsync(
                        () => System.Windows.Clipboard.SetFileDropList(fl),
                        $"SetFileDropList count={fl.Count} {SummarizeFileDropForLog(item.FilePaths!)}",
                        maxRetries: clipRetries,
                        delayMs: clipRetryDelayMs,
                        clipNudgeHwnd: _hwnd);
                    break;
            }
        }
        catch (Exception ex)
        {
            ClipboardDiagnosticsLog.Write($"paste unexpected before/during set {ex.GetType().Name}: {ex.Message}");
        }

        ClipboardDiagnosticsLog.Write($"paste END clipboardOk={clipboardOk} willSendCtrlV={clipboardOk}");

        if (!clipboardOk)
            _isSettingClipboard = false;
        else if (noSegmentDelays)
        {
            // 连续段：短让步后同步清标志，避免误标「外部复制」。
            await Task.Delay(8);
            _isSettingClipboard = false;
        }
        else
            _ = Dispatcher.BeginInvoke(DispatcherPriority.SystemIdle, () => _isSettingClipboard = false);

        if (clipboardOk)
        {
            if (!noSegmentDelays)
            {
                var prePasteDelayMs = item.Type == EntryType.Image ? 85 : 45;
                await Task.Delay(prePasteDelayMs);
            }

            SendCtrlV();

            // 连续粘贴：整轮结束后再 TailSettle；段间另有 SequentialInterSegmentDelayMs。单次粘贴保留回波窗口。
            if (!noSegmentDelays)
            {
                const int postEchoMs = 600;
                await Task.Delay(postEchoMs);
                ClipboardDiagnosticsLog.Write($"paste post-echo suppression window elapsed (ms={postEchoMs})");
            }
        }
        }
        finally
        {
            if (!_sequentialPasteHold)
                _pasteInProgress = false;
        }
    }

    // Shift+Insert 是系统级粘贴，但 Excel 对模拟输入更挑：
    // 1) Insert 必须按扩展键发送；
    // 2) 若呼出面板时 Ctrl/Alt/Win 仍处于按下态，最终组合键会被污染。
    // 因此这里在同一批 SendInput 中先释放当前真实按下的修饰键，再发送标准的 Shift+Insert。
    private static void SendCtrlV()
    {
        var ctrlHeld = (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0;
        var altHeld = (Win32.GetAsyncKeyState(Win32.VK_MENU) & 0x8000) != 0;
        var lWinHeld = (Win32.GetAsyncKeyState(Win32.VK_LWIN) & 0x8000) != 0;
        var rWinHeld = (Win32.GetAsyncKeyState(Win32.VK_RWIN) & 0x8000) != 0;

        var inputs = new Win32.INPUT[(ctrlHeld ? 1 : 0) + (altHeld ? 1 : 0) + (lWinHeld ? 1 : 0) + (rWinHeld ? 1 : 0) + 4];
        var i = 0;

        if (ctrlHeld)
        {
            inputs[i].type = Win32.INPUT_KEYBOARD;
            inputs[i].u.ki.wVk = Win32.VK_CONTROL;
            inputs[i].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
            i++;
        }
        if (altHeld)
        {
            inputs[i].type = Win32.INPUT_KEYBOARD;
            inputs[i].u.ki.wVk = Win32.VK_MENU;
            inputs[i].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
            i++;
        }
        if (lWinHeld)
        {
            inputs[i].type = Win32.INPUT_KEYBOARD;
            inputs[i].u.ki.wVk = Win32.VK_LWIN;
            inputs[i].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
            i++;
        }
        if (rWinHeld)
        {
            inputs[i].type = Win32.INPUT_KEYBOARD;
            inputs[i].u.ki.wVk = Win32.VK_RWIN;
            inputs[i].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
            i++;
        }

        inputs[i].type = Win32.INPUT_KEYBOARD;
        inputs[i].u.ki.wVk = Win32.VK_SHIFT;
        i++;

        inputs[i].type = Win32.INPUT_KEYBOARD;
        inputs[i].u.ki.wVk = Win32.VK_INSERT;
        inputs[i].u.ki.dwFlags = Win32.KEYEVENTF_EXTENDEDKEY;
        i++;

        inputs[i].type = Win32.INPUT_KEYBOARD;
        inputs[i].u.ki.wVk = Win32.VK_INSERT;
        inputs[i].u.ki.dwFlags = Win32.KEYEVENTF_EXTENDEDKEY | Win32.KEYEVENTF_KEYUP;
        i++;

        inputs[i].type = Win32.INPUT_KEYBOARD;
        inputs[i].u.ki.wVk = Win32.VK_SHIFT;
        inputs[i].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;

        Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    /// <summary>文本是否为严格 JSON（<see cref="JsonDocument"/> 可解析，不允许注释与尾逗号）。</summary>
    private static bool IsWellFormedJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var _ = JsonDocument.Parse(
                text,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 将临时文件路径写入剪贴板文件列表并模拟粘贴，供资源管理器接收。
    /// </summary>
    private async Task CompletePasteTempFileToExplorerAsync(string path, string beginLogDetail, string setClipboardLogOp)
    {
        if (_targetWindow != IntPtr.Zero && !Win32.IsWindow(_targetWindow))
            _targetWindow = IntPtr.Zero;

        ClipboardDiagnosticsLog.Write(
            $"pasteAsFile BEGIN {beginLogDetail} temp=\"{path}\" target=0x{_targetWindow.ToInt64():X}");

        HidePopup();
        if (_targetWindow != IntPtr.Zero)
            Win32.SetForegroundWindowAggressive(_targetWindow);
        await Task.Delay(85);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

        if (_hwnd != IntPtr.Zero)
            Win32.TryEmptyClipboardAfterOpen(_hwnd);

        _isSettingClipboard = true;
        var fl = new StringCollection();
        fl.Add(path);
        bool clipboardOk = await TrySetClipboardAsync(
            () => System.Windows.Clipboard.SetFileDropList(fl),
            setClipboardLogOp,
            clipNudgeHwnd: _hwnd);

        ClipboardDiagnosticsLog.Write($"pasteAsFile END clipboardOk={clipboardOk}");

        if (!clipboardOk)
        {
            _isSettingClipboard = false;
            try { File.Delete(path); } catch { /* ignore */ }
        }
        else
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.SystemIdle, () => _isSettingClipboard = false);
        }

        if (clipboardOk)
        {
            await Task.Delay(60);
            SendCtrlV();
            await Task.Delay(600);
            ClipboardDiagnosticsLog.Write("pasteAsFile post-echo suppression window elapsed");
        }
    }

    /// <summary>
    /// 将剪贴板图片历史写入临时 PNG 并放到系统剪贴板为文件列表，便于在资源管理器中 Ctrl+V 直接保存文件。
    /// </summary>
    private async void PasteImageAsFileForExplorer()
    {
        if (ItemsList.SelectedItem is not ClipboardEntry item || item.Type != EntryType.Image) return;
        if (item.ImageData is not { Length: > 0 }) return;
        _pasteInProgress = true;
        try
        {
        ClearPendingDelete();

        if (!item.IsQuickPaste)
        {
            var idx = _allItems.IndexOf(item);
            if (idx > 0) { _allItems.RemoveAt(idx); _allItems.Insert(0, item); }
            item.TouchCopiedTime();
            if (item.PersistedId is long pid)
                _historyStore.TryUpdateCopiedAt(pid, item.CopiedAt);
        }

        var dir = Path.Combine(Path.GetTempPath(), "ClipboardX");
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch { return; }

        var path = Path.Combine(dir, $"clip_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        try
        {
            File.WriteAllBytes(path, item.ImageData);
        }
        catch { return; }

        await CompletePasteTempFileToExplorerAsync(
            path,
            $"pngBytes={item.ImageData?.Length ?? 0}",
            $"SetFileDropList explorer_temp_png file=\"{path}\"");
        }
        finally
        {
            _pasteInProgress = false;
        }
    }

    /// <summary>
    /// 文本为合法 JSON 时写入临时 .json 文件并置于剪贴板文件列表，在资源管理器中粘贴即可落盘。
    /// </summary>
    private async void PasteJsonAsFileForExplorer()
    {
        if (ItemsList.SelectedItem is not ClipboardEntry item || item.Type != EntryType.Text) return;
        var text = item.TextContent;
        if (string.IsNullOrWhiteSpace(text) || !IsWellFormedJson(text)) return;

        _pasteInProgress = true;
        try
        {
        ClearPendingDelete();

        if (!item.IsQuickPaste)
        {
            var idx = _allItems.IndexOf(item);
            if (idx > 0) { _allItems.RemoveAt(idx); _allItems.Insert(0, item); }
            item.TouchCopiedTime();
            if (item.PersistedId is long pid)
                _historyStore.TryUpdateCopiedAt(pid, item.CopiedAt);
        }

        var dir = Path.Combine(Path.GetTempPath(), "ClipboardX");
        try { Directory.CreateDirectory(dir); }
        catch { return; }

        var path = Path.Combine(dir, $"clip_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        try
        {
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch { return; }

        await CompletePasteTempFileToExplorerAsync(
            path,
            $"jsonChars={text.Length}",
            $"SetFileDropList explorer_temp_json file=\"{path}\"");
        }
        finally
        {
            _pasteInProgress = false;
        }
    }

    #endregion

    #region UI Event Handlers

    private void ClearPendingDelete()
    {
        if (_pendingDeleteEntry == null) return;
        _pendingDeleteEntry.IsPendingDelete = false;
        _pendingDeleteEntry = null;
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is ClipboardEntry sel && ReferenceEquals(sel, _pendingDeleteEntry))
            return;
        ClearPendingDelete();
        SyncEntryPreviewWithSelection();
    }

    private void ItemsList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 || _displayItems.Count == 0) return;
        var sv = GetListScrollViewer();
        if (sv == null) return;

        double itemHeight = sv.ExtentHeight / _displayItems.Count;
        if (itemHeight <= 0) return;

        int newFirstVisible = Math.Max(0, (int)(sv.VerticalOffset / itemHeight));
        if (newFirstVisible == _firstVisibleIndex) return;

        // 仅同步「当前首行」与快速粘贴序号显示。勿在此处改写 SelectedIndex：
        // ↓ 键 + ScrollIntoView 会先更新选中再滚动，若按「旧首行 + 新选中」推算 relSelection，
        // 会把选中项错误推到 newFirstVisible + relSelection（例如从末行再下移一条时被甩到更下方）。
        _firstVisibleIndex = newFirstVisible;
        UpdateVisibleIndices(newFirstVisible);
    }

    private void ItemsList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            var elementM = e.OriginalSource as DependencyObject;
            while (elementM != null && elementM is not ListBoxItem)
                elementM = VisualTreeHelper.GetParent(elementM);
            if (elementM is not ListBoxItem lbiM || lbiM.DataContext is not ClipboardEntry entryM) return;

            ItemsList.SelectedItem = entryM;
            ItemsList.ScrollIntoView(entryM);
            _mouseShiftAnchorIndex = _displayItems.IndexOf(entryM);
            if (_mouseShiftAnchorIndex >= 0)
            {
                _selectionRangeAnchor = _mouseShiftAnchorIndex;
                _selectionCursorEnd = _mouseShiftAnchorIndex;
            }
            ShowEntryPreviewBubble();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;

        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not ListBoxItem)
            element = VisualTreeHelper.GetParent(element);
        if (element is not ListBoxItem lbi || lbi.DataContext is not ClipboardEntry entry) return;

        int idx = _displayItems.IndexOf(entry);
        if (idx < 0) return;

        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (shift)
        {
            int anchor = _mouseShiftAnchorIndex >= 0
                ? _mouseShiftAnchorIndex
                : (ItemsList.SelectedIndex >= 0 ? ItemsList.SelectedIndex : idx);
            int a = Math.Min(anchor, idx);
            int b = Math.Max(anchor, idx);
            ItemsList.SelectedItems.Clear();
            for (int i = a; i <= b; i++)
                ItemsList.SelectedItems.Add(_displayItems[i]);
            ItemsList.ScrollIntoView(_displayItems[idx]);
            _selectionRangeAnchor = a;
            _selectionCursorEnd = b;
            e.Handled = true;
            return;
        }

        if (ctrl)
        {
            if (ItemsList.SelectedItems.Contains(entry))
                ItemsList.SelectedItems.Remove(entry);
            else
                ItemsList.SelectedItems.Add(entry);
            _mouseShiftAnchorIndex = idx;
            _selectionRangeAnchor = idx;
            _selectionCursorEnd = idx;
            ItemsList.ScrollIntoView(entry);
            e.Handled = true;
            return;
        }

        ItemsList.SelectedItems.Clear();
        ItemsList.SelectedItems.Add(entry);
        _mouseShiftAnchorIndex = idx;
        _selectionRangeAnchor = idx;
        _selectionCursorEnd = idx;
        e.Handled = true;
    }

    private void ItemsList_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 || (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            return;
        if (ItemsList.SelectedItems.Count != 1) return;
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not ListBoxItem)
            element = VisualTreeHelper.GetParent(element);
        if (element is ListBoxItem lbi && lbi.DataContext is ClipboardEntry sel)
        {
            ItemsList.SelectedItem = sel;
            PasteSelectedItem();
        }
    }

    private void ItemsList_PreviewMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not ListBoxItem)
            element = VisualTreeHelper.GetParent(element);
        if (element is ListBoxItem lbi && lbi.DataContext is ClipboardEntry entry)
        {
            SyncContextMenuForEntry(entry);
            ContextPopup.Placement = PlacementMode.Mouse;
            ContextPopup.PlacementTarget = null;
            _ctxAltCloseMenuArmed = false;
            ContextPopup.IsOpen = true;
            RebuildContextMenuNav();
            _contextNavIndex = 0;
            ApplyContextMenuHighlight();
            e.Handled = true;
        }
    }

    private void SyncContextMenuForEntry(ClipboardEntry entry)
    {
        _contextEntry = entry;
        ItemsList.SelectedItem = entry;
        CtxShortcutText.Text = entry.IsQuickPaste ? "⚡ 修改快捷短语" : "⚡ 设为快捷短语";
        CtxPasteAsFileBorder.Visibility = entry.Type == EntryType.Image
            ? Visibility.Visible
            : Visibility.Collapsed;
        CtxPasteJsonFileBorder.Visibility = entry.Type == EntryType.Text && IsWellFormedJson(entry.TextContent)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OpenContextMenuFromKeyboard()
    {
        if (_displayItems.Count == 0) return;
        if (ItemsList.SelectedItem is not ClipboardEntry entry) return;

        SyncContextMenuForEntry(entry);
        RebuildContextMenuNav();
        if (_contextMenuNav.Count == 0) return;

        _contextNavIndex = 0;
        if (ItemsList.ItemContainerGenerator.ContainerFromItem(entry) is ListBoxItem li)
        {
            ContextPopup.PlacementTarget = li;
            ContextPopup.Placement = PlacementMode.Right;
            ContextPopup.HorizontalOffset = 8;
            ContextPopup.VerticalOffset = 0;
        }
        else
        {
            ContextPopup.PlacementTarget = MainBorder;
            ContextPopup.Placement = PlacementMode.Center;
            ContextPopup.HorizontalOffset = 0;
            ContextPopup.VerticalOffset = 0;
        }

        _ctxAltCloseMenuArmed = false;
        ContextPopup.IsOpen = true;
        ApplyContextMenuHighlight();
    }

    private void RebuildContextMenuNav()
    {
        _contextMenuNav.Clear();
        void Add(Border b, Action a)
        {
            if (b.Visibility == Visibility.Visible)
                _contextMenuNav.Add((b, a));
        }

        Add(CtxPasteBorder, ActivateCtxPaste);
        Add(CtxPasteAsFileBorder, ActivateCtxPasteAsFile);
        Add(CtxPasteJsonFileBorder, ActivateCtxPasteJsonFile);
        Add(CtxShortcutBorder, ActivateCtxShortcut);
        Add(CtxDeleteBorder, ActivateCtxDelete);
    }

    private void ApplyContextMenuHighlight()
    {
        var hi = FindResource("SelectedBrush") as Brush ?? System.Windows.Media.Brushes.LightGray;
        for (int i = 0; i < _contextMenuNav.Count; i++)
        {
            var row = _contextMenuNav[i].Row;
            row.Background = i == _contextNavIndex ? hi : System.Windows.Media.Brushes.Transparent;
        }
    }

    private void MoveContextMenuHighlight(int delta)
    {
        if (_contextMenuNav.Count == 0) return;
        _contextNavIndex = (_contextNavIndex + delta + _contextMenuNav.Count) % _contextMenuNav.Count;
        ApplyContextMenuHighlight();
    }

    private void ActivateContextMenuHighlight()
    {
        if (_contextMenuNav.Count == 0) return;
        if (_contextNavIndex < 0 || _contextNavIndex >= _contextMenuNav.Count) return;
        _contextMenuNav[_contextNavIndex].Activate();
    }

    private void CloseContextMenuPopup()
    {
        foreach (var (row, _) in _contextMenuNav)
            row.ClearValue(Border.BackgroundProperty);
        _contextMenuNav.Clear();
        _contextNavIndex = 0;
        ContextPopup.IsOpen = false;
        _ctxAltCloseMenuArmed = false;
    }

    private void ActivateCtxPaste()
    {
        CloseContextMenuPopup();
        if (_contextEntry != null)
        {
            ItemsList.SelectedItem = _contextEntry;
            PasteSelectedItem();
        }
    }

    private void ActivateCtxPasteAsFile()
    {
        CloseContextMenuPopup();
        if (_contextEntry is { Type: EntryType.Image })
        {
            ItemsList.SelectedItem = _contextEntry;
            PasteImageAsFileForExplorer();
        }
    }

    private void ActivateCtxPasteJsonFile()
    {
        CloseContextMenuPopup();
        if (_contextEntry is { Type: EntryType.Text } && IsWellFormedJson(_contextEntry.TextContent))
        {
            ItemsList.SelectedItem = _contextEntry;
            PasteJsonAsFileForExplorer();
        }
    }

    private void ActivateCtxShortcut()
    {
        CloseContextMenuPopup();
        if (_contextEntry != null)
            AddQuickPaste(_contextEntry);
    }

    private void ActivateCtxDelete()
    {
        var del = _contextEntry;
        CloseContextMenuPopup();
        if (del != null)
            RemoveEntry(del);
    }

    private void CtxPaste_Click(object sender, MouseButtonEventArgs e) => ActivateCtxPaste();

    private void CtxPasteAsFile_Click(object sender, MouseButtonEventArgs e) => ActivateCtxPasteAsFile();

    private void CtxPasteJsonFile_Click(object sender, MouseButtonEventArgs e) => ActivateCtxPasteJsonFile();

    private void CtxShortcut_Click(object sender, MouseButtonEventArgs e) => ActivateCtxShortcut();

    private void CtxDelete_Click(object sender, MouseButtonEventArgs e) => ActivateCtxDelete();

    private void RemoveEntry(ClipboardEntry entry)
    {
        if (ReferenceEquals(_pendingDeleteEntry, entry))
        {
            entry.IsPendingDelete = false;
            _pendingDeleteEntry = null;
        }
        else
            ClearPendingDelete();
        _batchQueue.Remove(entry);
        UpdateBatchOrderProperties();
        if (entry.IsQuickPaste)
            _quickPastes.RemoveAll(q => q.Content == entry.TextContent);
        else
            _historyStore.TryDelete(entry.PersistedId);
        var removedListIndex = _displayItems.IndexOf(entry);
        _allItems.Remove(entry);
        RefreshFilter(removedListIndex >= 0 ? removedListIndex : null);
        if (entry.IsQuickPaste) SaveQuickPastes();
    }

    /// <summary>Del：首次给当前选中项加删除线；同一项再按 Del 才删除。换选或 Esc 取消删除线。</summary>
    private void DeleteSelectedItemWithConfirm()
    {
        if (ItemsList.SelectedItem is not ClipboardEntry entry) return;
        if (entry.IsPendingDelete)
        {
            RemoveEntry(entry);
            return;
        }
        ClearPendingDelete();
        entry.IsPendingDelete = true;
        _pendingDeleteEntry = entry;
    }

    private void TypeFilter_Click(object sender, MouseButtonEventArgs e) => CycleTypeFilter();

    private void CycleTypeFilter()
    {
        _quickPhraseOnly = false;
        _typeFilter = _typeFilter switch
        {
            null => EntryType.Text,
            EntryType.Text => EntryType.Image,
            EntryType.Image => EntryType.Files,
            _ => null
        };
        TypeFilterText.Text = _typeFilter switch
        {
            EntryType.Text => "📝 文本",
            EntryType.Image => "🖼️ 图片",
            EntryType.Files => "📁 文件",
            _ => "全部"
        };
        RefreshFilter();
    }

    private void ToggleQuickPhraseFilter()
    {
        _quickPhraseOnly = !_quickPhraseOnly;
        if (_quickPhraseOnly)
        {
            _typeFilter = null;
            TypeFilterText.Text = "⚡ 短语";
        }
        else
        {
            TypeFilterText.Text = "全部";
        }
        RefreshFilter();
    }

    private void PasteByIndex(int index)
    {
        var item = _displayItems.FirstOrDefault(i => i.DisplayIndex == index);
        if (item == null) return;
        ItemsList.SelectedItem = item;
        int li = _displayItems.IndexOf(item);
        if (li >= 0) _mouseShiftAnchorIndex = li;
        PasteSelectedItem();
    }

    private void Settings_Click(object sender, MouseButtonEventArgs e)
    {
        HidePopup();
        SettingsRequested?.Invoke();
    }

    private void Header_DragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _isDragging = true;
        Win32.GetCursorPos(out _dragLastPt);
    }

    #endregion
}
