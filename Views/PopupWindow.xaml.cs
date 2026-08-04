using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipboardManager.Models;
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
    private readonly BulkObservableCollection<ClipboardEntry> _displayItems = new();

    /// <summary>FIFO/LIFO 下：多选 Enter 入队、新复制可自动入队；出队后条目不占批量角标，回到底部列表排序。</summary>
    private readonly List<ClipboardEntry> _batchQueue = new();
#if CLIPX_CLIPBOARD
    /// <summary>全局 Ctrl+V / Shift+Insert 松键出队防抖（毫秒，TickCount64）。</summary>
    private long _lastGlobalPasteQueueAdvanceTick;
    /// <summary>FIFO/LIFO 下列队已贴完，等待下一次他处粘键后切回普通模式（<see cref="AppSettings.BatchQueueAutoSwitchToNormalAfterQueueDone"/>）。</summary>
    private bool _batchQueueAwaitingNextPasteToSwitchOff;
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

#if CLIPX_FILEJUMP
    /// <summary>在系统「确定/保存/打开」等主按钮上点击时记录当前文件夹；与弹窗、首点自动跳转钩独立。</summary>
    private static readonly Win32.LowLevelMouseProc s_fileJumpPersistMouseThunk = StaticFileJumpPersistMouseProc;
    private static IntPtr s_fileJumpPersistMouseHookForNext;
    private static PopupWindow? s_fileJumpPersistMouseOwner;
#endif

    /// <summary>SetWinEventHook 同样会把托管委托交给系统；须用静态委托避免 Unhook 后晚到回调撞上已回收的实例委托。</summary>
    private static readonly Win32.WinEventDelegate s_popupWinEventThunk = StaticWinEventProc;
    private static PopupWindow? s_popupWinEventOwner;

    private bool _isSettingClipboard;
    /// <summary>最近一次自写完成时的剪贴板序列号；OnClipboardUpdate 看到序列号 ≤ 此值则视为自写回波。
    /// 解决 _isSettingClipboard 推迟清旗仍偶发漏标的根因（WPF SystemIdle 与 Win32 消息泵相对顺序并不严格保证）。</summary>
    private uint _lastSelfWriteClipboardSeq;
    /// <summary>最近一次自写的时间戳（TickCount64 ms），与 <see cref="_lastSelfWriteClipboardSeq"/> 配合做兜底窗口。</summary>
    private long _lastSelfWriteTickMs;
    /// <summary>自写回波时间窗（ms）：序列号未变 + 时间窗内即视为自写。</summary>
    private const int SelfWriteEchoWindowMs = 500;
    /// <summary>从历史粘贴整段流程中：禁止监控线程读剪贴板，避免 Contains/Get 与即将执行的 Set 在同 UI 线程上交错 OpenClipboard。</summary>
    private bool _pasteInProgress;
    /// <summary>连续粘贴多段（批量/队列）时保持 true，整轮结束后才清除 <see cref="_pasteInProgress"/>，避免段间剪贴板回波或 FIFO 自动入队插队。</summary>
    private bool _sequentialPasteHold;
    private bool _isPopupVisible;
    private string _searchText = "";

    /// <summary>列表内 <see cref="SearchHighlightTextBlock"/> 绑定用：当前搜索词（Trim），随 <see cref="UpdateSearchUI"/> 更新。</summary>
    public static readonly DependencyProperty HighlightSearchQueryProperty = DependencyProperty.Register(
        nameof(HighlightSearchQuery),
        typeof(string),
        typeof(PopupWindow),
        new PropertyMetadata(""));

    public string HighlightSearchQuery
    {
        get => (string)GetValue(HighlightSearchQueryProperty);
        set => SetValue(HighlightSearchQueryProperty, value ?? "");
    }

    private EntryType? _typeFilter;
    private bool _quickPhraseOnly;
    private ClipboardEntry? _contextEntry;
    /// <summary>已按下 Alt，等待 KeyUp：无组合键则打开右键菜单。</summary>
    private bool _ctxAltAwaitRelease;
    private bool _ctxAltComboDuringRelease;
    /// <summary>右键菜单已打开时再次按下 Alt，松开时若无组合键则关闭菜单。</summary>
    private bool _ctxAltCloseMenuArmed;
    /// <summary>
    /// 含 Alt 的全局热键（如 Alt+`）打开面板后焦点仍在宿主（VS Code）；若用户先松 Alt，KeyUp 会进入宿主并抢走菜单焦点。
    /// 在短时内在本钩子中吞掉「热键收尾」的 Alt 松开；此期间不 arms <see cref="_ctxAltAwaitRelease"/>，避免与自动重复 Down 冲突。
    /// </summary>
    private bool _awaitHotkeyAltChordCleanup;
    private long _hotkeyAltChordCleanupDeadlineTick;
    /// <summary>与 RegisterHotKey 的 WM_HOTKEY 并行时防抖，避免 CycleBatchPasteMode 连跳两档。</summary>
    private long _cycleBatchPasteDebounceTick;
    /// <summary>与 WM_HOTKEY 并行时防抖，避免 TogglePopup 连切两次。</summary>
    private long _togglePopupDebounceTick;
    /// <summary>
    /// 主面板吞掉 Alt KeyDown 后，系统往往仍报告 Alt 未按下；锁存到 Alt KeyUp，供 Alt+/、Alt+` 等与 VkToChar 防录入对齐。
    /// </summary>
    private bool _swallowedMenuAltLatch;
    /// <summary>修饰键 Down 锁存（MOD_* / MOD_CAPS），供按键穿透在钩子链上游可靠识别组合键。</summary>
    private uint _passthroughModifierLatch;
    /// <summary>Win+V 被本程序拦截后，吞掉后续 Win KeyUp 以防止开始菜单弹出。</summary>
    private bool _winVIntercepted;
    private readonly List<(Border Row, Action Activate)> _contextMenuNav = new();
    private int _contextNavIndex;
    /// <summary>当前标为「待二次 Del 删除」的条目，与 <see cref="ClipboardEntry.IsPendingDelete"/> 同步。</summary>
    private ClipboardEntry? _pendingDeleteEntry;

    private int _pageSize = 8;
    private uint _panelPageScrollUpModifiers = Win32.MOD_CONTROL;
    private uint _panelPageScrollUpKey = 0xBD;
    private uint _panelPageScrollDownModifiers = Win32.MOD_CONTROL;
    private uint _panelPageScrollDownKey = 0xBB;
    private int _firstVisibleIndex;

    private uint _hotkeyModifiers;
    private uint _hotkeyKey;
    private int _maxItems;
    private string _popupPosition = "Caret";
    private double _popupOpacity = 1.0;
    private bool _hideOnSameAppClick = true;
    /// <summary>置顶：粘贴后不关窗，且不因点击外部/失焦自动关闭。</summary>
    private bool _popupPinned;
    private string _panelModifierKey = "Ctrl";
    private bool _isDragging;
    private bool _userHasResized;
    private bool _isResizing;
    private Win32.POINT _dragLastPt;
    private long _lastDragMoveTick;
    private int _pendingDragX, _pendingDragY;
    private bool _hasPendingDragMove;
    /// <summary>标题栏拖动时由鼠标钩 SetWindowPos 维护的 HWND 物理左上角；用于识别 Shell/贴靠对窗口的偷跑。</summary>
    private int _hookAuthPhysLeft, _hookAuthPhysTop;
    /// <summary>标题栏拖动松手后第一次 Sync：若 HWND 已被壳/DWM 甩离钩子最后一帧位置则拉回（与 H15 日志配对）。</summary>
    private int _postDragHookAuthLeft = int.MinValue, _postDragHookAuthTop;
    /// <summary>WM_DPICHANGED 后若干次 WINDOWPOSCHANGING 不再强制 SWP_NOMOVE，否则系统无法应用 DPI 建议矩形。</summary>
    private int _windowPosNomoveSkipCount;
    private bool _clickReceivedByPopup;
    private int _pendingPhysX, _pendingPhysY;
    private bool _isOurSetWindowPos;
    /// <summary>上次成功通过 caret/UIA 解析到的目标窗口对应的 caret 物理像素位置（含 caretGap）；用于自绘 caret 应用（Word/Office/Chromium）冷启动失败时的兜底。</summary>
    private IntPtr _lastCaretCacheHwnd = IntPtr.Zero;
    private int _lastCaretCachePhysX, _lastCaretCachePhysY;
    private long _lastCaretCacheTickMs;
    /// <summary>Show/UpdateLayout 过程中阻止 WPF 改写位置，避免先出现在 (0,0) 或顶边再跳到目标点。</summary>
    private bool _lockPopupWindowNomove;
    private List<QuickPasteEntry> _quickPastes = new();
    private readonly ClipboardHistoryStore _historyStore = new();
    private ImageOcrQueue? _imageOcrQueue;
    private AppSettings? _appSettings;
    private IntPtr _lastForegroundForDialogTrack = IntPtr.Zero;
    private long _lastFileDialogSeenTick;
    private const int FileDialogAliveWindowMs = 2000;
    /// <summary>前台切换风暴时合并到单次 UI 回调，避免关闭跳转列表时 Dispatcher 队列爆炸。</summary>
    private int _foregroundChangeCoalesceGen;
    /// <summary>自上次 UI 处理以来，原生 WinEvent 前台回调次数（合并前）。</summary>
    private int _foregroundNativeBurst;
    /// <summary>因序号过期而跳过的 UI 调度次数（合并丢弃的 BeginInvoke）。</summary>
    private int _foregroundUiDispatchSuperseded;

    #region agent log
    private int _agentDbgDragMoveLogCount;
    private int _agentDbgH17WpcSkipLogCount;
    private int _agentDbgH20LogCount;
    private int _agentDbgCachedPrimarySeamX = int.MinValue;
    private int _agentDbgH21MismatchLogCount;
    private int _agentDbgH22WpfHwndDipLogCount;

    /// <summary>历史调试入口；统一转到异步诊断日志，避免同步写项目根目录造成 UI 抖动。</summary>
    private static void AgentDbgLog(string hypothesisId, string location, string message, object? data = null)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                hypothesisId,
                location,
                message,
                data
            });
            ClipboardDiagnosticsLog.Write($"agent {payload}");
        }
        catch { /* 调试日志失败不影响主流程 */ }
    }
    #endregion

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
    private IntPtr _fileJumpAutoOpenRetryRoot;
    private int _fileJumpAutoOpenRetryCount;
    private int _fileJumpDelaySession;
    /// <summary>「对话框到前台自动执行」路径采集异步化，避免与 UI 线程争抢；递增后过时结果丢弃。</summary>
    private int _fileJumpAutoForegroundCollectGen;
    /// <summary>手动跳转热键路径采集异步化；连按热键时仅最后一次结果生效（极端情况下或影响「双按直跳」窗口期）。</summary>
    private int _fileJumpHotkeyCollectGen;
    private uint _fileJumpHotkeyModifiers;
    private uint _fileJumpHotkeyKey;

    private IntPtr _fileJumpAutoMouseHook;
#if CLIPX_FILEJUMP
    private IntPtr _fileJumpPersistMouseHook;
#endif
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
    private IntPtr _snapshotFolderDebounceHwnd;
    private long _snapshotFolderDebounceTick;
    /// <summary>最近一次离开外部文件管理器时记录到的路径；用于切回文件对话框时优先同步。</summary>
    private string _lastExternalFolder = "";
    private IntPtr _lastExternalManagerRoot = IntPtr.Zero;
    /// <summary>Picker 打开时轮询 Explorer 窗口路径变化的定时器。</summary>
    private System.Windows.Threading.DispatcherTimer? _explorerPathPollTimer;
    private string _explorerPathPollLastPath = "";
    private IntPtr _explorerPathPollHwnd;
    private IntPtr _fileJumpNavigationSuppressRoot;
    private string _fileJumpNavigationSuppressPath = "";
    private long _fileJumpNavigationSuppressUntilTick;

    public event Action? SettingsRequested;

    /// <summary>
    /// 呼出剪贴板面板时前台为开始菜单/搜索等 Shell；Win11 等系统可能将此类界面置于普通应用 HWND_TOPMOST 之上，
    /// 用户态无法可靠置顶，订阅方可提示用户先按 Esc 关闭 Shell。
    /// </summary>
    public event Action? ShellForegroundMayOccludePopup;

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
        ApplyPopupPanelLayout(settings);

#if CLIPX_CLIPBOARD
        LoadHistoryFromStore();
        LoadQuickPastes();
        _imageOcrQueue = new ImageOcrQueue(_historyStore, Dispatcher);
        _imageOcrQueue.EnqueueBackfill(_allItems, settings, OnImageOcrEntryUpdated);
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

        WarmUpUiaCaretProxy();

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
        InstallFileJumpPersistFolderHook();
#endif
        UpdateEmptyState();
        UpdateBatchHeaderUi();
        TextEntryEditPopup.CustomPopupPlacementCallback = TextEntryEditCustomPlacement;

#if CLIPX_CLIPBOARD
        // 根据设置初始化键盘钩子（如果启用了 ReplaceSystemWinV 或其他需要钩子的功能）
        SyncBatchPasteKeyboardHook();
#endif
    }

    public void Cleanup()
    {
#if CLIPX_FILEJUMP
        UninstallFileJumpPersistFolderHook();
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
        // 清理所有内存集合，释放图片 byte[] 和缩略图
        _allItems.Clear();
        _displayItems.Clear();
        _batchQueue.Clear();
        // 主动 GC：BitmapImage 的非托管像素缓冲区依赖 finalizer 延迟释放，
        // 这里强制回收以立即降低内存占用
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
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

    private void ApplyPopupPanelLayout(AppSettings settings)
    {
        AppSettings.NormalizePopupPanelSettings(settings);
        _pageSize = settings.PopupPageItems;
        _panelPageScrollUpModifiers = settings.PanelPageScrollUpModifiers;
        _panelPageScrollUpKey = settings.PanelPageScrollUpKey;
        _panelPageScrollDownModifiers = settings.PanelPageScrollDownModifiers;
        _panelPageScrollDownKey = settings.PanelPageScrollDownKey;
        Width = settings.PopupPanelWidth;
        MaxHeight = settings.PopupPanelMaxHeight;
        if (settings.PopupPanelHeight > 0)
        {
            _userHasResized = true;
            SizeToContent = SizeToContent.Manual;
            Height = settings.PopupPanelHeight;
        }
    }

    public void ApplySettings(AppSettings settings)
    {
        _appSettings = settings;
        _maxItems = settings.MaxItems;
        _popupPosition = settings.PopupPosition;
        _popupOpacity = settings.PopupOpacity;
        _hideOnSameAppClick = settings.HideOnSameAppClick;
        _panelModifierKey = settings.PanelModifierKey;
        ClipboardEntry.PreviewMaxLines = settings.PreviewMaxLines;
        Opacity = _popupOpacity;
        _quickPastes = settings.QuickPastes;
        ApplyPopupPanelLayout(settings);
        TrimItems();
        UpdateBatchHeaderUi();

        if (!settings.KeyPassthroughEnabled)
            _passthroughModifierLatch = 0;

#if CLIPX_CLIPBOARD
        if (settings.ImageOcrEnabled && _imageOcrQueue != null)
            _imageOcrQueue.EnqueueBackfill(_allItems, settings, OnImageOcrEntryUpdated);
#endif

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

#if CLIPX_CLIPBOARD
        // 更新键盘钩子状态（可能因为 ReplaceSystemWinV 设置变化需要激活/停用钩子）
        SyncBatchPasteKeyboardHook();
#endif
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
            _historyStore.PruneExcessImages(_appSettings.MaxImageItems);
            // 轻量加载：不读取 image_blob，图片数据按需从数据库获取，大幅降低启动内存
            var batch = _historyStore.LoadNewestFirstLite(_maxItems);
            // 为每条图片条目设置懒加载委托
            var storeRef = _historyStore;
            for (int i = batch.Count - 1; i >= 0; i--)
            {
                var entry = batch[i];
                if (entry.Type == EntryType.Image)
                    entry.ImageDataLoader = id => storeRef.LoadImageData(id);
                _allItems.Insert(0, entry);
            }
        }
        catch { /* ignore */ }
    }

    private void OnImageOcrEntryUpdated()
    {
        RefreshFilter();
        SyncEntryPreviewWithSelection();
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
    private ClipboardEntry? _entryTextEditTarget;
    /// <summary>打开「编辑文本」前记录的原宿主 HWND，关闭编辑后用于恢复键盘焦点（宿主内光标位置由系统保留）。</summary>
    private IntPtr _textEditRestoreForegroundHwnd;
    /// <summary>编辑文本期间临时去掉 WS_EX_NOACTIVATE，关闭编辑后需写回。</summary>
    private bool _wsExNoActivateLiftedForEntryTextEdit;

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

    private void BeginEntryTextEdit(ClipboardEntry entry)
    {
        if (entry.Type != EntryType.Text) return;
        _textEditRestoreForegroundHwnd = IntPtr.Zero;
        if (_targetWindow != IntPtr.Zero && Win32.IsWindow(_targetWindow) && _targetWindow != _hwnd)
            _textEditRestoreForegroundHwnd = _targetWindow;

        CloseContextMenuPopup();
        _entryTextEditTarget = entry;
        EntryTextEditBox.Text = entry.TextContent ?? "";
        TextEntryEditPopup.IsOpen = true;
    }

    private void TextEntryEditPopup_Opened(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LiftNoActivateForEntryTextEditIfNeeded();
            Win32.SetForegroundWindowAggressive(_hwnd);
            EntryTextEditBox.Focus();
            System.Windows.Input.Keyboard.Focus(EntryTextEditBox);
            EntryTextEditBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>编辑弹窗紧贴 MainBorder 右侧，留出间隙避免被主面板遮挡。</summary>
    private CustomPopupPlacement[] TextEntryEditCustomPlacement(
        System.Windows.Size popupSize,
        System.Windows.Size targetSize,
        System.Windows.Point offset)
    {
        const double gap = 14;
        return new[]
        {
            new CustomPopupPlacement(new System.Windows.Point(targetSize.Width + gap, 0), PopupPrimaryAxis.Vertical)
        };
    }

    private void LiftNoActivateForEntryTextEditIfNeeded()
    {
        if (_hwnd == IntPtr.Zero) return;
        var ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        var v = ex.ToInt64();
        if ((v & Win32.WS_EX_NOACTIVATE) == 0) return;
        _wsExNoActivateLiftedForEntryTextEdit = true;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(v & ~Win32.WS_EX_NOACTIVATE));
    }

    private void RestoreNoActivateAfterEntryTextEditIfLifted()
    {
        if (!_wsExNoActivateLiftedForEntryTextEdit) return;
        _wsExNoActivateLiftedForEntryTextEdit = false;
        if (_hwnd == IntPtr.Zero) return;
        var ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex.ToInt64() | Win32.WS_EX_NOACTIVATE));
    }

    private void RestoreFocusAfterTextEntryEdit()
    {
        RestoreNoActivateAfterEntryTextEditIfLifted();
        var h = _textEditRestoreForegroundHwnd;
        _textEditRestoreForegroundHwnd = IntPtr.Zero;
        if (h == IntPtr.Zero || h == _hwnd || !Win32.IsWindow(h)) return;
        Win32.SetForegroundWindowAggressive(h);
    }

    private void CommitEntryTextEdit()
    {
        if (_entryTextEditTarget is not ClipboardEntry entry) return;
        var newText = EntryTextEditBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(newText))
        {
            System.Windows.MessageBox.Show("文本不能为空。", "编辑文本",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Dispatcher.BeginInvoke(() =>
            {
                EntryTextEditBox.Focus();
                System.Windows.Input.Keyboard.Focus(EntryTextEditBox);
            }, DispatcherPriority.Input);
            return;
        }

        var oldText = entry.TextContent ?? "";
        if (entry.IsQuickPaste)
        {
            var phrase = entry.ShortcutPhrase ?? "";
            for (var i = 0; i < _quickPastes.Count; i++)
            {
                if (_quickPastes[i].Content != oldText) continue;
                if (!string.IsNullOrEmpty(phrase) && _quickPastes[i].Phrase != phrase) continue;
                var ph = _quickPastes[i].Phrase;
                _quickPastes[i] = new QuickPasteEntry { Phrase = ph, Content = newText };
                break;
            }
            entry.TextContent = newText;
            SaveQuickPastes();
        }
        else
        {
            entry.TextContent = newText;
            if (entry.PersistedId is long pid)
                _historyStore.TryUpdateText(pid, newText);
        }

        entry.RaiseTextDisplayPropertiesChanged();
        RefreshFilter();
        TextEntryEditPopup.IsOpen = false;
        _entryTextEditTarget = null;
        Dispatcher.BeginInvoke(RestoreFocusAfterTextEntryEdit, DispatcherPriority.Background);
    }

    private void CancelEntryTextEdit()
    {
        TextEntryEditPopup.IsOpen = false;
        _entryTextEditTarget = null;
        Dispatcher.BeginInvoke(RestoreFocusAfterTextEntryEdit, DispatcherPriority.Background);
    }

    private void CtxEditText_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ActivateCtxEditText();
    }

    private void ActivateCtxEditText()
    {
        CloseContextMenuPopup();
        if (_contextEntry is { Type: EntryType.Text })
            BeginEntryTextEdit(_contextEntry);
    }

    private void EntryTextEditSave_Click(object sender, MouseButtonEventArgs e) => CommitEntryTextEdit();

    private void EntryTextEditCancel_Click(object sender, MouseButtonEventArgs e) => CancelEntryTextEdit();

    private void EntryTextEditBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            CancelEntryTextEdit();
            return;
        }

        if (e.Key == System.Windows.Input.Key.Enter
            && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            CommitEntryTextEdit();
        }
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
                    }
                });
                handled = true;
                return IntPtr.Zero;

            case Win32.WM_ENTERSIZEMOVE:
                _isResizing = true;
                break;

            case Win32.WM_EXITSIZEMOVE:
                _isResizing = false;
                SavePopupSize();
                break;

            case Win32.WM_MOUSEACTIVATE:
                handled = true;
                return TextEntryEditPopup.IsOpen
                    ? new IntPtr(Win32.MA_ACTIVATE)
                    : new IntPtr(Win32.MA_NOACTIVATE);

            case Win32.WM_DPICHANGED:
                // 允许紧随其后的 WINDOWPOSCHANGING 带上位置/尺寸，否则会与 WM_DPICHANGED 建议矩形冲突，跨屏缩放时表现为突然缩放、位置漂移。
                _windowPosNomoveSkipCount = 8;
                if (_isDragging)
                {
                    Win32.GetCursorPos(out _dragLastPt);
                    #region agent log
                    try
                    {
                        Win32.RECT suggested = default;
                        if (lParam != IntPtr.Zero)
                            suggested = Marshal.PtrToStructure<Win32.RECT>(lParam);
                        Win32.GetWindowRect(hwnd, out var winRc);
                        AgentDbgLog("H10", "WndProc WM_DPICHANGED", "drag: suppress default; suggested vs hwnd rect",
                            new
                            {
                                dpiWParam = wParam.ToInt64(),
                                suggested = new { suggested.Left, suggested.Top, suggested.Right, suggested.Bottom },
                                winRect = new { winRc.Left, winRc.Top, winRc.Right, winRc.Bottom }
                            });
                    }
                    catch
                    {
                        /* 调试日志 */
                    }
                    #endregion
                    // 主屏右缘前约一窗宽内窗口会「跨屏」，系统发 WM_DPICHANGED 且 lParam 为建议矩形；WPF/DefWindowProc 若应用该矩形会与
                    // 鼠标钩里的 SetWindowPos 抢位置，表现为危险区内跳变。拖动中吞掉本消息，松手后 Sync 再对齐 DPI/布局。
                    handled = true;
                    return IntPtr.Zero;
                }

                Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => SyncWindowPhysicalPositionToWpf("wmDpiChanged")));
                break;

            case Win32.WM_WINDOWPOSCHANGING:
                // WM_DPICHANGED 之后系统会连发若干 WINDOWPOSCHANGING；此间若强行 SWP_NOMOVE，跨屏拖动时壳/DWM 无法按新 DPI 微调位置，
                // 会与鼠标钩 SetWindowPos 拉锯，表现为跨越边界时乱跳；松手后反而正常。故在计数窗口内一律不拦（与是否拖动无关）。
                if (_windowPosNomoveSkipCount > 0 && !_isOurSetWindowPos)
                {
                    _windowPosNomoveSkipCount--;
                    #region agent log
                    if (_isDragging && _agentDbgH17WpcSkipLogCount < 32)
                    {
                        _agentDbgH17WpcSkipLogCount++;
                        AgentDbgLog("H17", "WndProc WM_WINDOWPOSCHANGING", "skip NOMOVE (DPI chain)",
                            new { remainingAfter = _windowPosNomoveSkipCount, our = _isOurSetWindowPos });
                    }
                    #endregion
                    break;
                }

                // 外部发起的移动：弹窗常态锁位置；拖动时仅由钩子 SetWindowPos，禁止系统再改 x/y（尺寸仍可随 DPI 变）。
                // resize 时不能锁位置，否则拖左边缘时右边缘不动的约束会失效。
                if (!_isOurSetWindowPos && !_isResizing && (_isDragging || _isPopupVisible || _lockPopupWindowNomove))
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
                if (IsForegroundAppExcluded(_appSettings)) break;
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
    private static bool TryReadClipboardBool(Func<bool> read, string tag, int maxRetries = 4, int delayMs = 5)
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

    private static T? TryReadClipboard<T>(Func<T> read, string tag, int maxRetries = 4, int delayMs = 5) where T : class
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

    /// <summary>记录一次自写后的剪贴板序列号与时间戳，供 OnClipboardUpdate 回波识别兜底。</summary>
    private void MarkSelfWroteClipboard()
    {
        _lastSelfWriteClipboardSeq = Win32.GetClipboardSequenceNumber();
        _lastSelfWriteTickMs = Environment.TickCount64;
    }

    /// <summary>批量粘贴的合并产物（合并字符串 / 合并 FileDropList）作为新条目入库，与系统剪贴板保持一致。
    /// 必须在 UI 线程调用；调用方需保证 OnClipboardUpdate 路径已被 <see cref="MarkSelfWroteClipboard"/> 拦截，避免重复入库。</summary>
    private void InsertBatchMergedEntry(ClipboardEntry entry)
    {
        if (entry.Type == EntryType.Text)
        {
            if (string.IsNullOrEmpty(entry.TextContent)) return;
            DeduplicateText(entry.TextContent);
        }
        else if (entry.Type == EntryType.Files)
        {
            if (entry.FilePaths is null || entry.FilePaths.Length == 0) return;
            DeduplicateFiles(entry.FilePaths);
        }
        else
        {
            return;
        }
        _allItems.Insert(0, entry);
        TrimItems();
        _historyStore.TryInsert(entry);
        RefreshFilter();
    }

    private void OnClipboardUpdate()
    {
        // 仅跳过：不得在 async Set 尚未收尾时清 _isSettingClipboard，否则下一条 WM_CLIPBOARDUPDATE 会当作用户复制 → AutoBatchEnqueue → TryPush 风暴与 CLIPBRD_E 重试卡顿。
        if (_isSettingClipboard)
        {
            ClipboardDiagnosticsLog.Write("monitor skip self_set");
            return;
        }
        // 兜底：_isSettingClipboard 推迟清旗仍可能与本拍 WM 相对错位；序列号 ≤ 自写记录 + 时间窗内 → 仍判定自写回波。
        if (_lastSelfWriteClipboardSeq != 0)
        {
            var nowSeq = Win32.GetClipboardSequenceNumber();
            var dtMs = Environment.TickCount64 - _lastSelfWriteTickMs;
            if (nowSeq == _lastSelfWriteClipboardSeq && dtMs >= 0 && dtMs <= SelfWriteEchoWindowMs)
            {
                ClipboardDiagnosticsLog.Write($"monitor skip self_set_echo seq={nowSeq} dtMs={dtMs}");
                return;
            }
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
                    AutoBatchEnqueueIfNeeded(fe, fromClipboardMonitor: true);
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
                    AutoBatchEnqueueIfNeeded(te, fromClipboardMonitor: true);
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
                            // 单张图片大小限制：超过则跳过入库，避免单张超大图片吃掉大量内存
                            if (pngData.Length > _appSettings.MaxImageSizeBytes)
                            {
                                ClipboardDiagnosticsLog.Write(
                                    $"monitor image skipped: too large {pngData.Length} > {_appSettings.MaxImageSizeBytes}");
                                return;
                            }
                            DeduplicateImageByMd5(pngData);
                            var ie = new ClipboardEntry
                            {
                                Type = EntryType.Image, ImageData = pngData,
                                ImageWidth = pw, ImageHeight = ph
                            };
                            // 设置懒加载委托，以便后续 ImageData 被释放后可按需重新加载
                            ie.ImageDataLoader = id => _historyStore.LoadImageData(id);
                            _allItems.Insert(0, ie);
                            TrimItems();
                            _historyStore.TryInsert(ie);
                            AutoBatchEnqueueIfNeeded(ie, fromClipboardMonitor: true);
                            _imageOcrQueue?.Enqueue(ie, _appSettings, OnImageOcrEntryUpdated);
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
        var removedFromQ = _batchQueue.RemoveAll(x => x.Type == EntryType.Text && !x.IsQuickPaste && x.TextContent == text);
#if CLIPX_CLIPBOARD
        if (removedFromQ > 0)
            RequestBatchQueueHeadClipboardResyncAfterDedup();
#endif
    }

    private void DeduplicateFiles(string[] paths)
    {
        var key = string.Join("|", paths);
        foreach (var x in _allItems.Where(x => x.Type == EntryType.Files && string.Join("|", x.FilePaths ?? []) == key))
            _historyStore.TryDelete(x.PersistedId);
        _allItems.RemoveAll(x => x.Type == EntryType.Files && string.Join("|", x.FilePaths ?? []) == key);
        var removedFromQ = _batchQueue.RemoveAll(x => x.Type == EntryType.Files && string.Join("|", x.FilePaths ?? []) == key);
#if CLIPX_CLIPBOARD
        if (removedFromQ > 0)
            RequestBatchQueueHeadClipboardResyncAfterDedup();
#endif
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
        var removedFromQ = _batchQueue.RemoveAll(x => x.Type == EntryType.Image && !x.IsQuickPaste && x.ImageContentMd5Hex == hex);
#if CLIPX_CLIPBOARD
        if (removedFromQ > 0)
            RequestBatchQueueHeadClipboardResyncAfterDedup();
#endif
    }

    private void TrimItems()
    {
        var regular = _allItems.Where(x => !x.IsQuickPaste).ToList();
#if CLIPX_CLIPBOARD
        var queueTouched = false;
#endif
        // 1. 总条数上限
        while (regular.Count > _maxItems)
        {
            var last = regular[^1];
            _historyStore.TryDelete(last.PersistedId);
            _allItems.Remove(last);
#if CLIPX_CLIPBOARD
            if (_batchQueue.Remove(last))
                queueTouched = true;
#else
            _batchQueue.Remove(last);
#endif
            regular.RemoveAt(regular.Count - 1);
        }

        // 2. 图片专项上限——图片占用远大于文本，独立裁剪
        var maxImages = _appSettings.MaxImageItems;
        if (maxImages > 0)
        {
            var images = regular.Where(x => x.Type == EntryType.Image).ToList();
            if (images.Count > maxImages)
            {
                var toRemove = images.Skip(maxImages).ToList();
                foreach (var entry in toRemove)
                {
                    _historyStore.TryDelete(entry.PersistedId);
                    _allItems.Remove(entry);
#if CLIPX_CLIPBOARD
                    if (_batchQueue.Remove(entry))
                        queueTouched = true;
#else
                    _batchQueue.Remove(entry);
#endif
                }
            }
        }

#if CLIPX_CLIPBOARD
        if (queueTouched)
            RequestBatchQueueHeadClipboardResyncAfterDedup();
#endif
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
        _firstVisibleIndex = 0;
        var query = _searchText.Trim();

        IEnumerable<ClipboardEntry> items = _allItems;

        if (_quickPhraseOnly)
            items = items.Where(i => i.IsQuickPaste);

        if (_typeFilter.HasValue)
            items = items.Where(i => i.Type == _typeFilter.Value
                || (_typeFilter.Value == EntryType.Image && i.IsImageFile));

        if (!string.IsNullOrEmpty(query))
            items = items.Where(i => i.MatchesSearch(query));

        // 批量更新：抑制逐条 CollectionChanged，结束时一次 Reset 通知
        using (_displayItems.BeginBulkUpdate())
        {
            _displayItems.Clear();

            var filtered = items.ToList();
            // 用引用比较的 HashSet 避免对 ClipboardEntry 的大量 GetHashCode 调用
            var filteredSet = new HashSet<ClipboardEntry>(filtered, System.Collections.Generic.ReferenceEqualityComparer.Instance);
            UpdateBatchOrderProperties();
            var qset = new HashSet<ClipboardEntry>(_batchQueue, System.Collections.Generic.ReferenceEqualityComparer.Instance);

            var queuePart = new List<ClipboardEntry>(_batchQueue.Count);
            foreach (var e in _batchQueue)
                if (filteredSet.Contains(e))
                    queuePart.Add(e);

            var rest = new List<ClipboardEntry>(filtered.Count);
            foreach (var e in filtered)
                if (!qset.Contains(e))
                    rest.Add(e);

            // 稳定排序：快捷短语优先（有搜索词时），否则按时间倒序
            var hasSearch = !string.IsNullOrEmpty(_searchText);
            rest.Sort((a, b) =>
            {
                if (hasSearch)
                {
                    var aQuick = a.IsQuickPaste ? 1 : 0;
                    var bQuick = b.IsQuickPaste ? 1 : 0;
                    if (aQuick != bQuick) return bQuick.CompareTo(aQuick);
                }
                return b.CopiedAt.CompareTo(a.CopiedAt);
            });

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
        SetCurrentValue(HighlightSearchQueryProperty, _searchText.Trim());
        if (hasSearch)
        {
            var primary = TryFindResource("PrimaryText") as Brush ?? System.Windows.Media.Brushes.White;
            var accent = TryFindResource("AccentBg") as Brush ?? System.Windows.Media.Brushes.Teal;
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
#if CLIPX_CLIPBOARD
        _batchQueueAwaitingNextPasteToSwitchOff = false;
#endif
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
        var now = Environment.TickCount64;
        if (now - _cycleBatchPasteDebounceTick < 45) return;
        _cycleBatchPasteDebounceTick = now;
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
    /// <summary>面板显示、FIFO/LIFO 且队列非空、或「队已贴完等下一次粘键切回普通」、或启用了替换系统 Win+V 时需 WH_KEYBOARD_LL。</summary>
    private void SyncBatchPasteKeyboardHook()
    {
        var need = _isPopupVisible
            || (GetBatchMode() != BatchPasteQueueMode.Off && _batchQueue.Count > 0)
            || _awaitHotkeyAltChordCleanup
            || _batchQueueAwaitingNextPasteToSwitchOff
            || (_appSettings?.ReplaceSystemWinV ?? false);
        if (need)
            InstallKeyboardHook();
        else
            UninstallKeyboardHook();
    }
#else
    private void SyncBatchPasteKeyboardHook() { }
#endif

    /// <summary>热键 Alt 收尾窗口超时后卸下钩子（若不再需要）。</summary>
    private void TryExpireHotkeyAltChordCleanupDeadline()
    {
        if (!_awaitHotkeyAltChordCleanup || _hotkeyAltChordCleanupDeadlineTick == 0)
            return;
        if (Environment.TickCount64 <= _hotkeyAltChordCleanupDeadlineTick)
            return;
        _awaitHotkeyAltChordCleanup = false;
        _hotkeyAltChordCleanupDeadlineTick = 0;
#if CLIPX_CLIPBOARD
        SyncBatchPasteKeyboardHook();
#else
        if (!_isPopupVisible)
            UninstallKeyboardHook();
#endif
    }

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

        var headBefore = _batchQueue.Count > 0 ? _batchQueue[0] : null;
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
        SchedulePushBatchQueueHeadIfChanged(headBefore, mode);
        if (_batchQueue.Count > 0)
            _batchQueueAwaitingNextPasteToSwitchOff = false;
    }

    private void MarkAwaitingBatchQueueNextPasteToSwitchToNormalIfEnabled()
    {
        var mode = GetBatchMode();
        if (mode != BatchPasteQueueMode.Fifo && mode != BatchPasteQueueMode.Lifo) return;
        if (!(_appSettings?.BatchQueueAutoSwitchToNormalAfterQueueDone ?? true)) return;
        _batchQueueAwaitingNextPasteToSwitchOff = true;
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
        if (_batchQueue.Count == 0 && (GetBatchMode() == BatchPasteQueueMode.Fifo || GetBatchMode() == BatchPasteQueueMode.Lifo))
            MarkAwaitingBatchQueueNextPasteToSwitchToNormalIfEnabled();
        SyncBatchPasteKeyboardHook();
        _ = TryPushClipboardQueueHeadAsync();
    }

    /// <summary>批量队列推剪贴板时：已关模式、队列为空或队首已换则不得再覆盖系统剪贴板（避免 FIFO/LIFO 异步写回盖住用户刚复制的内容）。</summary>
    private bool BatchQueueHeadStillThisEntry(ClipboardEntry item) =>
        GetBatchMode() != BatchPasteQueueMode.Off
        && _batchQueue.Count > 0
        && ReferenceEquals(_batchQueue[0], item);

    /// <summary>仅写剪贴板为队首，不发按键；供入队后目标中 Ctrl+V / Shift+Insert 粘贴衔接。</summary>
    private async Task TryPushClipboardQueueHeadAsync()
    {
        await _queueClipboardPushLock.WaitAsync();
        try
        {
            if (GetBatchMode() == BatchPasteQueueMode.Off || _batchQueue.Count == 0) return;
            var item = _batchQueue[0];

            _isSettingClipboard = true;
            try
            {
                if (!BatchQueueHeadStillThisEntry(item)) return;

                if (_hwnd != IntPtr.Zero)
                    Win32.TryEmptyClipboardAfterOpen(_hwnd);

                var ok = false;
                const int clipRetries = 8;
                const int clipRetryDelayMs = 55;
                Func<bool> queueCoherence = () => BatchQueueHeadStillThisEntry(item);
                try
                {
                    switch (item.Type)
                    {
                        case EntryType.Text:
                            ok = await TrySetClipboardTextWithNativeFallbackAsync(
                                item.TextContent ?? "",
                                _hwnd,
                                maxRetries: clipRetries,
                                baseDelayMs: clipRetryDelayMs,
                                canContinueBeforeEachAttempt: queueCoherence);
                            break;
                        case EntryType.Image:
                        {
                            BitmapImage? bi = null;
                            var imgBytes = item.TryGetImageData();
                            using (var ms = new MemoryStream(imgBytes!))
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
                                ok = await TrySetClipboardImageWithNativeFallbackAsync(
                                    bi,
                                    imgBytes,
                                    _hwnd,
                                    maxRetries: clipRetries,
                                    baseDelayMs: clipRetryDelayMs);
                            }
                            break;
                        }
                        case EntryType.Files:
                            ok = await TrySetClipboardFileDropListWithNativeFallbackAsync(
                                item.FilePaths!,
                                $"queueHead count={item.FilePaths!.Length}",
                                _hwnd,
                                maxRetries: clipRetries,
                                baseDelayMs: clipRetryDelayMs,
                                canContinueBeforeEachAttempt: queueCoherence);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    ClipboardDiagnosticsLog.Write($"queueHead unexpected {ex.GetType().Name}: {ex.Message}");
                }

                ClipboardDiagnosticsLog.Write($"queueHead ok={ok}");
                if (ok && BatchQueueHeadStillThisEntry(item))
                    await Task.Delay(4);
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

    /// <summary>
    /// 去重时从队列移除了条目，队首可能已变；须把剪贴板重新对齐队首（与 AutoBatchEnqueue 中「FIFO 队尾追加不推队首」配合）。
    /// </summary>
    private void RequestBatchQueueHeadClipboardResyncAfterDedup()
    {
        if (GetBatchMode() == BatchPasteQueueMode.Off || _batchQueue.Count == 0) return;
        _ = TryPushClipboardQueueHeadAsync();
    }

    /// <summary>
    /// FIFO 下新复制入队尾时队首引用不变，反复 Set 队首会与系统剪贴板互锁并卡顿；仅当队首变化或 LIFO 时再写剪贴板。
    /// </summary>
    private void SchedulePushBatchQueueHeadIfChanged(ClipboardEntry? headBeforeMutation, BatchPasteQueueMode mode)
    {
        var headAfter = _batchQueue.Count > 0 ? _batchQueue[0] : null;
        if (mode == BatchPasteQueueMode.Fifo && ReferenceEquals(headBeforeMutation, headAfter))
            return;
        _ = TryPushClipboardQueueHeadAsync();
    }
#else
    private void EnqueueSelectedForBatchPasteMode() { }
    private void TryAdvancePasteQueueAfterGlobalPaste() { }
    private Task TryPushClipboardQueueHeadAsync() => Task.CompletedTask;
#endif

    /// <param name="fromClipboardMonitor">为 true 表示 entry 是由 <see cref="OnClipboardUpdate"/> 刚从系统剪贴板读出来的，
    /// 此刻 OS 剪贴板内容 = entry，不需要再回写一次（LIFO 下入队首会触发冗余 SetClipboard，
    /// 与源应用 OLE 通告链争抢导致 8 次×55ms ≈ 440ms 重试卡顿；图片更可能再 fallback 落盘）。</param>
    private void AutoBatchEnqueueIfNeeded(ClipboardEntry entry, bool fromClipboardMonitor = false)
    {
#if CLIPX_CLIPBOARD
        if (entry.IsQuickPaste) return;
        var mode = GetBatchMode();
        if (mode == BatchPasteQueueMode.Off) return;

        var headBefore = _batchQueue.Count > 0 ? _batchQueue[0] : null;
        _batchQueue.Remove(entry);
        if (mode == BatchPasteQueueMode.Fifo)
            _batchQueue.Add(entry);
        else
            _batchQueue.Insert(0, entry);
        UpdateBatchOrderProperties();
        ReorderAllItemsQueueFirst();
        SyncBatchPasteKeyboardHook();
        if (!fromClipboardMonitor)
            SchedulePushBatchQueueHeadIfChanged(headBefore, mode);
        else if (mode == BatchPasteQueueMode.Fifo)
            _ = TryPushClipboardQueueHeadAsync();
        if (_batchQueue.Count > 0)
            _batchQueueAwaitingNextPasteToSwitchOff = false;
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
#if CLIPX_CLIPBOARD
        var wasFifoOrLifo = GetBatchMode() is BatchPasteQueueMode.Fifo or BatchPasteQueueMode.Lifo;
#endif
        _batchQueue.Clear();
        UpdateBatchOrderProperties();
        RefreshFilter(0);
        SyncBatchPasteKeyboardHook();
        var mergeText = _appSettings?.BatchPasteMergeText ?? true;
        if (IsAllTextEntries(list) && mergeText)
            await RunAllTextBatchSingleClipboardAsync(list, newlineAfterEachTextWhenCtrlEnter: false,
                hidePopupAfter: HidePopupAfterUserAction);
        else if (mergeText)
            await RunOrderedPastesWithAdjacentTextMergeAsync(list, newlineAfterEachTextWhenCtrlEnter: false);
        else
            await RunSequentialPastesAsync(list);
#if CLIPX_CLIPBOARD
        if (wasFifoOrLifo && list.Count > 0 && (_appSettings?.BatchQueueAutoSwitchToNormalAfterQueueDone ?? true))
            _batchQueueAwaitingNextPasteToSwitchOff = true;
        SyncBatchPasteKeyboardHook();
#endif
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

    /// <summary>单选图片且已有 OCR 文字时，粘贴纯文本而非图片。</summary>
    private void HandleShiftEnterKey()
    {
        if (ItemsList.SelectedItems.Count == 1
            && ItemsList.SelectedItem is ClipboardEntry entry
            && entry.Type == EntryType.Image
            && !string.IsNullOrWhiteSpace(entry.OcrText))
        {
            _ = PasteOcrTextFromImageAsync(entry);
            return;
        }
        HandleMainEnterKey(false);
    }

    private async Task PasteOcrTextFromImageAsync(ClipboardEntry imageEntry)
    {
        if (imageEntry.Type != EntryType.Image || string.IsNullOrWhiteSpace(imageEntry.OcrText)) return;
        var textOnly = new ClipboardEntry
        {
            Type = EntryType.Text,
            TextContent = imageEntry.OcrText.Trim(),
            IsQuickPaste = true
        };
        await PasteEntryAsync(textOnly, hidePopupAfter: HidePopupAfterUserAction);
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
            await PasteEntryAsync(item, hidePopupAfter: HidePopupAfterUserAction);
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
        if (_batchQueue.Count == 0)
            MarkAwaitingBatchQueueNextPasteToSwitchToNormalIfEnabled();
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
            await PasteEntryAsync(ordered[0], hidePopupAfter: HidePopupAfterUserAction);
            return;
        }

        var mergeText = _appSettings?.BatchPasteMergeText ?? true;
        if (IsAllTextEntries(ordered) && mergeText)
            await RunAllTextBatchSingleClipboardAsync(ordered, newlineAfterEachTextWhenCtrlEnter,
                hidePopupAfter: HidePopupAfterUserAction);
        else if (mergeText)
            await RunOrderedPastesWithAdjacentTextMergeAsync(ordered, newlineAfterEachTextWhenCtrlEnter);
        else
            await RunSequentialPastesAsync(ordered, newlineAfterEachTextWhenCtrlEnter);
    }

    private static bool IsAllTextEntries(IReadOnlyList<ClipboardEntry> items) =>
        items.Count > 0 && items.All(e => e.Type == EntryType.Text);

    /// <summary>保持顺序将列表按「Text vs 非 Text」二分聚合：相邻 Text 归一段；相邻 Image/Files 归一段（统一以 FileDropList 一次粘贴）。</summary>
    private static List<List<ClipboardEntry>> BuildAdjacentRuns(IReadOnlyList<ClipboardEntry> ordered)
    {
        static bool SameKind(EntryType a, EntryType b) =>
            (a == EntryType.Text) == (b == EntryType.Text);
        var segments = new List<List<ClipboardEntry>>();
        var i = 0;
        while (i < ordered.Count)
        {
            var anchor = ordered[i].Type;
            var run = new List<ClipboardEntry> { ordered[i] };
            i++;
            while (i < ordered.Count && SameKind(anchor, ordered[i].Type))
            {
                run.Add(ordered[i]);
                i++;
            }
            segments.Add(run);
        }
        return segments;
    }

    private static bool IsAllImageOrFilesEntries(IReadOnlyList<ClipboardEntry> items) =>
        items.Count > 0 && items.All(e => e.Type == EntryType.Image || e.Type == EntryType.Files);

    /// <summary>开启合并粘贴且条目类型混合时：仅将相邻的纯文本合并成一段粘贴，图/文件等仍分段粘贴。</summary>
    private async Task RunOrderedPastesWithAdjacentTextMergeAsync(
        IReadOnlyList<ClipboardEntry> items,
        bool newlineAfterEachTextWhenCtrlEnter)
    {
        var segments = BuildAdjacentRuns(items);
        _sequentialPasteHold = true;
        // 批量入口立刻 Hide：每段自己 hidePopupAfter=isLast 会让前 N-1 段时面板仍前台，
        // SetForegroundWindowAggressive 抢回目标不可靠；先 Hide 让 Win32.SetForegroundWindow 直接成功。
        if (_isPopupVisible && HidePopupAfterUserAction) HidePopup();
        try
        {
            var opIndex = 0;
            // 任一段是「合并粘贴」（≥2 条文本聚合 / ≥2 条图+文件聚合）时，已通过 InsertBatchMergedEntry 把合并产物置顶；
            // 此时不能再对原选中条目做置顶重排，否则会把它们盖到合并条目之前。
            bool anyMergedSegment = false;
            for (var s = 0; s < segments.Count; s++)
            {
                var seg = segments[s];
                var isLast = s == segments.Count - 1;
                bool segIsImageOrFiles = false;
                if (IsAllTextEntries(seg))
                {
                    if (seg.Count >= 2)
                    {
                        anyMergedSegment = true;
                        await RunAllTextBatchSingleClipboardAsync(
                            seg,
                            newlineAfterEachTextWhenCtrlEnter,
                            hidePopupAfter: false,
                            applyHistoryReorder: false,
                            ownsGlobalPasteState: false);
                    }
                    else
                    {
                        await PasteEntryAsync(
                            seg[0],
                            hidePopupAfter: false,
                            sequentialSegmentIndex: opIndex,
                            sendNewlineAfterTextWhenCtrlEnterBatch: newlineAfterEachTextWhenCtrlEnter);
                    }
                }
                else if (IsAllImageOrFilesEntries(seg))
                {
                    segIsImageOrFiles = true;
                    // 1 张图/1 条文件：保留旧路径（PasteEntryAsync 对单条已是最稳）；
                    // ≥2 条（图+图、文件+文件、图+文件混合）：合并为一次 FileDropList 粘贴。
                    if (seg.Count == 1)
                    {
                        await PasteEntryAsync(
                            seg[0],
                            hidePopupAfter: false,
                            sequentialSegmentIndex: opIndex,
                            sendNewlineAfterTextWhenCtrlEnterBatch: newlineAfterEachTextWhenCtrlEnter);
                    }
                    else
                    {
                        anyMergedSegment = true;
                        await RunBatchImagesAndFilesAsFileDropAsync(
                            seg,
                            hidePopupAfter: false,
                            applyHistoryReorder: false,
                            ownsGlobalPasteState: false);
                    }
                }
                else
                {
                    var single = seg[0];
                    segIsImageOrFiles = single.Type == EntryType.Image || single.Type == EntryType.Files;
                    await PasteEntryAsync(
                        single,
                        hidePopupAfter: false,
                        sequentialSegmentIndex: opIndex,
                        sendNewlineAfterTextWhenCtrlEnterBatch: newlineAfterEachTextWhenCtrlEnter);
                }
                opIndex++;
                if (!isLast)
                {
                    // 等目标真正读取剪贴板再继续下一段；否则 Word 等慢消费方会出现「漏粘上段、当前段被粘两次」。
                    await WaitForTargetClipboardConsumeAsync(afterImageSegment: segIsImageOrFiles);
                    if (SequentialInterSegmentDelayMs > 0)
                        await Task.Delay(SequentialInterSegmentDelayMs);
                }
            }

            // 简化原则：本轮有「合并粘贴段」时，合并产物已由 InsertBatchMergedEntry 顶到列表第 0 位；
            // 不再对原选中条目做置顶重排，让它们保持原位（用户期望：仅合并字符串顶上去，原条目不动）。
            if (items.Count > 0 && !anyMergedSegment)
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

            // 让出一帧给前台切换，比固定 26ms 更短且更稳定。
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

            if (_hwnd != IntPtr.Zero)
                Win32.TryEmptyClipboardAfterOpen(_hwnd);

            _isSettingClipboard = true;
            var ok = await TrySetClipboardTextWithNativeFallbackAsync(
                combined,
                _hwnd,
                maxRetries: 10,
                baseDelayMs: 60);
            bool insertedMerged = false;
            if (ok)
            {
                MarkSelfWroteClipboard();
                // 合并产物入库：与系统剪贴板状态对齐，便于用户后续复用整段。回波拦截（旗 + 序列号）确保 OnClipboardUpdate 不会重复入库。
                if (ordered.Count >= 2)
                {
                    InsertBatchMergedEntry(new ClipboardEntry { Type = EntryType.Text, TextContent = combined });
                    insertedMerged = true;
                }
            }
            // 不立刻清 _isSettingClipboard：必须等本次 SetText 触发的 WM_CLIPBOARDUPDATE 派发完，
            // 否则该消息到达时旗已落，OnClipboardUpdate 会把合并文本当成「用户复制」入库。
            // 用 SystemIdle 排队保证本拍消息泵已处理完后再清；序列号 + 时间窗作为兜底。
            _ = Dispatcher.BeginInvoke(DispatcherPriority.SystemIdle, () => _isSettingClipboard = false);

            if (ok)
            {
                SendPasteToTarget();
            }

            // 已插入合并条目时不再对原条目重排：用户期望「仅合并字符串顶上去，原条目不动」。
            if (applyHistoryReorder && !insertedMerged)
                ApplyDeferredSequentialPasteHistoryOrder(ordered);
            await Task.Delay(20);
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

    /// <summary>多张图片 / 文件 / 图+文混合：图片落盘为 PNG，与文件路径合成同一个 FileDropList，一次 SetClipboard + 一次粘贴。
    /// 这是替代「逐张 SetImage 串行粘贴」的核心稳态路径——目标程序（Word/微信/邮件等）会把 FileDropList 中每个图片当作单独图片插入，
    /// 既消除了段间剪贴板覆盖的时序竞态，又避免了 OLE Image 通告链在多次 SetImage 时累积失败。</summary>
    private async Task RunBatchImagesAndFilesAsFileDropAsync(
        IReadOnlyList<ClipboardEntry> ordered,
        bool hidePopupAfter,
        bool applyHistoryReorder,
        bool ownsGlobalPasteState)
    {
        var paths = new StringCollection();
        var tempPathsToCleanupOnFailure = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "ClipboardX");
        try { Directory.CreateDirectory(dir); } catch { }

        foreach (var e in ordered)
        {
            if (e.Type == EntryType.Files && e.FilePaths is { Length: > 0 })
            {
                foreach (var p in e.FilePaths)
                    if (!string.IsNullOrWhiteSpace(p)) paths.Add(p);
            }
            else if (e.Type == EntryType.Image)
            {
                var imgData = e.TryGetImageData();
                if (imgData is { Length: > 0 })
                {
                    try
                    {
                        var p = Path.Combine(dir, $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.png");
                        File.WriteAllBytes(p, imgData);
                        paths.Add(p);
                        tempPathsToCleanupOnFailure.Add(p);
                    }
                    catch (Exception ex)
                    {
                        ClipboardDiagnosticsLog.Write($"BATCH_FILEDROP image temp write failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }

        if (paths.Count == 0)
        {
            ClipboardDiagnosticsLog.Write("BATCH_FILEDROP no usable paths; skip");
            return;
        }

        if (ownsGlobalPasteState)
            _sequentialPasteHold = true;
        _pasteInProgress = true;
        try
        {
            ClearPendingDelete();
            if (_targetWindow != IntPtr.Zero && !Win32.IsWindow(_targetWindow))
                _targetWindow = IntPtr.Zero;

            ClipboardDiagnosticsLog.Write(
                $"paste BATCH_FILEDROP_ONE_SHOT count={paths.Count} entries={ordered.Count} outerHold={ownsGlobalPasteState}");

            if (hidePopupAfter)
                HidePopup();
            if (_targetWindow != IntPtr.Zero)
                Win32.SetForegroundWindowAggressive(_targetWindow);

            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

            if (_hwnd != IntPtr.Zero)
                Win32.TryEmptyClipboardAfterOpen(_hwnd);

            _isSettingClipboard = true;
            var pathArr = new string[paths.Count];
            paths.CopyTo(pathArr, 0);
            var ok = await TrySetClipboardFileDropListWithNativeFallbackAsync(
                pathArr,
                $"batchAllAsFiles count={paths.Count}",
                _hwnd,
                maxRetries: 10,
                baseDelayMs: 60);
            bool insertedMerged = false;
            if (ok)
            {
                MarkSelfWroteClipboard();
                // 合并产物入库：与文本批量对齐。注意此处的 paths 既包含原始文件路径，也包含图片落盘的临时 PNG 路径。
                if (ordered.Count >= 2)
                {
                    var arr = new string[paths.Count];
                    paths.CopyTo(arr, 0);
                    InsertBatchMergedEntry(new ClipboardEntry { Type = EntryType.Files, FilePaths = arr });
                    insertedMerged = true;
                }
            }
            // 同 RunAllTextBatchSingleClipboardAsync：延迟到 SystemIdle 清旗 + 序列号兜底，避免 WM_CLIPBOARDUPDATE 漏标自写而入历史。
            _ = Dispatcher.BeginInvoke(DispatcherPriority.SystemIdle, () => _isSettingClipboard = false);

            if (ok)
            {
                // FileDrop 写入 → 目标 Open 通常需要 1 帧；用 Background 让一帧而非固定 30ms。
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                SendPasteToTarget();
            }
            else
            {
                ClipboardDiagnosticsLog.Write("BATCH_FILEDROP SetFileDropList GAVE_UP; cleaning temp PNGs");
                foreach (var p in tempPathsToCleanupOnFailure)
                {
                    try { File.Delete(p); } catch { /* ignore */ }
                }
            }

            if (applyHistoryReorder && !insertedMerged)
                ApplyDeferredSequentialPasteHistoryOrder(ordered);
            await Task.Delay(20);
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

    /// <summary>连续段之间最小让步：仅作为下限，真正的等待由 <see cref="WaitForTargetClipboardConsumeAsync"/> 决定。
    /// 早期硬编码 22ms 在 Word 接收图片 OLE（常需 100~300ms）场景下会出现「漏粘上一段、当前段被粘两次」的伪重复。</summary>
    private const int SequentialInterSegmentDelayMs = 22;

    /// <summary>段间等待目标程序消费（OpenClipboard/读取）剪贴板的最长时长；超时则按下一段直接覆盖处理。</summary>
    private const int SequentialInterSegmentMaxWaitMs = 350;

    /// <summary>图片段写入剪贴板后，目标程序对 OLE 通告链处理常显著慢于文本，给出更宽松的等待上限。</summary>
    private const int SequentialInterSegmentMaxWaitMsForImage = 600;

    /// <summary>整轮结束后稍晚再解除「粘贴中」。</summary>
    private const int SequentialTailSettleMs = 85;

    /// <summary>
    /// 段间等待：以剪贴板序列号 + 「目标 OpenClipboard 持有过的瞬间」为信号，确认目标程序已消化上一段；
    /// 超时则放弃等待按下一段继续。返回实际等待毫秒数。
    /// </summary>
    /// <param name="afterImageSegment">为 true 时使用图片专用的更长上限。</param>
    private async Task<int> WaitForTargetClipboardConsumeAsync(bool afterImageSegment)
    {
        var maxMs = afterImageSegment ? SequentialInterSegmentMaxWaitMsForImage : SequentialInterSegmentMaxWaitMs;
        var startSeq = Win32.GetClipboardSequenceNumber();
        var sw = Stopwatch.StartNew();
        var lastOwner = IntPtr.Zero;
        bool sawForeignOpen = false;
        // 轮询步长 12ms：和我们 Set 后到目标 Open 的典型时延量级匹配，且 30 次内即可铺满 350ms 上限。
        while (sw.ElapsedMilliseconds < maxMs)
        {
            await Task.Delay(12);
            // 序列号变化意味着剪贴板已被「另一次写入」覆盖（不期望发生，跳出立即返回让外层日志记录）。
            if (Win32.GetClipboardSequenceNumber() != startSeq)
            {
                ClipboardDiagnosticsLog.Write($"interSeg wait: seq changed earlier than expected ({sw.ElapsedMilliseconds}ms)");
                break;
            }
            var owner = Win32.GetOpenClipboardWindow();
            if (owner != IntPtr.Zero && owner != _hwnd && owner != lastOwner)
            {
                lastOwner = owner;
                sawForeignOpen = true;
                // 目标已开始读取——再让出极短时间给 IDataObject GetData 完成
                await Task.Delay(20);
                break;
            }
        }
        sw.Stop();
        ClipboardDiagnosticsLog.Write(
            $"interSeg waitMs={sw.ElapsedMilliseconds} max={maxMs} sawForeignOpen={sawForeignOpen} afterImage={afterImageSegment}");
        return (int)sw.ElapsedMilliseconds;
    }

    /// <summary>多段粘贴共享外部「粘贴进行中」标志，避免每段之间剪贴板监听插队。</summary>
    private async Task RunSequentialPastesAsync(IReadOnlyList<ClipboardEntry> items, bool newlineAfterEachTextWhenCtrlEnter = false)
    {
        _sequentialPasteHold = true;
        if (_isPopupVisible && HidePopupAfterUserAction) HidePopup();
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var cur = items[i];
                await PasteEntryAsync(
                    cur,
                    hidePopupAfter: false,
                    sequentialSegmentIndex: i,
                    sendNewlineAfterTextWhenCtrlEnterBatch: newlineAfterEachTextWhenCtrlEnter);
                if (i < items.Count - 1)
                {
                    await WaitForTargetClipboardConsumeAsync(
                        afterImageSegment: cur.Type == EntryType.Image || cur.Type == EntryType.Files);
                    if (SequentialInterSegmentDelayMs > 0)
                        await Task.Delay(SequentialInterSegmentDelayMs);
                }
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

    /// <summary>用户主动粘贴/批量粘贴后是否应关闭面板（置顶时保持打开）。</summary>
    private bool HidePopupAfterUserAction => !_popupPinned;

    #endregion

    #region Popup Show/Hide

    public void TogglePopup()
    {
        var now = Environment.TickCount64;
        if (now - _togglePopupDebounceTick < 45) return;
        _togglePopupDebounceTick = now;
        if (_isPopupVisible) HidePopup(closingViaClipboardHotkey: true);
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
            #region agent log
            AgentDbgLog("H23", "ShowPopup", "phase=pre-show",
                new { _targetWindow = _targetWindow.ToInt64(), ActualHeight, MaxHeight });
            #endregion
            PositionPopup();
            TryApplyPendingPositionAsWpfLeftTop();
            ApplyPendingPositionSetWindowPos();
            TryApplyPendingPositionAsWpfLeftTop();

            Show();
            UpdateLayout();

            #region agent log
            AgentDbgLog("H23", "ShowPopup", "phase=post-show",
                new { ActualHeight });
            #endregion
            PositionPopup();

            _isPopupVisible = true;

            ApplyPendingPositionSetWindowPos();
            TryApplyPendingPositionAsWpfLeftTop();
            ReassertPopupTopmostZOrder();
            ApplyShellForegroundZOrderFix();
        }
        finally
        {
            _lockPopupWindowNomove = false;
        }

        // Show()+UpdateLayout 后再做一次定位 + SetWindowPos，确保最终帧停在第二次（caret/UIA 命中）的坐标后才点亮 Opacity，避免「先错位再闪到正确位置」
        Opacity = _popupOpacity;

        if (_displayItems.Count > 0)
            ItemsList.SelectedIndex = 0;

        _awaitHotkeyAltChordCleanup = (_hotkeyModifiers & Win32.MOD_ALT) != 0;
        _hotkeyAltChordCleanupDeadlineTick =
            _awaitHotkeyAltChordCleanup ? Environment.TickCount64 + 750 : 0;

#if CLIPX_CLIPBOARD
        SyncBatchPasteKeyboardHook();
#else
        InstallKeyboardHook();
#endif
        InstallMouseHook();

        // Shell 会在后续帧继续改 Z 序：延迟重申相对置顶（尽力而为）。
        Dispatcher.BeginInvoke(ReassertPopupTopmostZOrder, DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(() =>
        {
            ReassertPopupTopmostZOrder();
            ApplyShellForegroundZOrderFix();
        }, DispatcherPriority.ContextIdle);

        if (IsShellForegroundWindow(Win32.GetForegroundWindow()))
            ShellForegroundMayOccludePopup?.Invoke();

        _popupPinned = false;
        UpdatePinHeaderUi();
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

    /// <summary>
    /// 在定位后重申 HWND_TOPMOST（单次），不抢焦点。
    /// </summary>
    private void ReassertPopupTopmostZOrder()
    {
        if (_hwnd == IntPtr.Zero) return;
        _isOurSetWindowPos = true;
        try
        {
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST,
                0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
        finally
        {
            _isOurSetWindowPos = false;
        }
    }

    /// <summary>
    /// 当前前台是否为开始菜单、搜索等 Shell 全屏层；用于相对 Z 序与定时刷新。
    /// </summary>
    private static bool IsShellForegroundWindow(IntPtr fg)
    {
        if (fg == IntPtr.Zero) return false;
        _ = Win32.GetWindowThreadProcessId(fg, out var pid);
        if (pid == 0) return false;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            var name = p.ProcessName;
            if (IsDedicatedShellHostProcess(name))
                return true;

            // 任务栏搜索等：explorer.exe + WinUI CoreWindow（勿把 CabinetWClass 文件窗口当成 Shell）
            if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase))
            {
                var cls = Win32.GetWindowClassName(fg);
                return cls.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDedicatedShellHostProcess(string name) =>
        name.Equals("SearchHost", StringComparison.OrdinalIgnoreCase)
        || name.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ShellHost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 当前前台进程是否在排除列表中；若匹配则应跳过 ClipboardX 全局快捷键。
    /// </summary>
    internal static bool IsForegroundAppExcluded(AppSettings? settings)
    {
        if (settings == null || settings.ExclusionApps.Count == 0) return false;
        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        Win32.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0) return false;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            var name = proc.ProcessName;
            return settings.ExclusionApps.Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// 尽力将本窗口插在 Shell 根窗口之上并重申 TOPMOST。Win11 25H2 等版本可能将开始/搜索置于更高 Z 带，
    /// 此时用户态无法保证显示在最前。
    /// </summary>
    private void ApplyShellForegroundZOrderFix()
    {
        if (_hwnd == IntPtr.Zero) return;
        var fg = Win32.GetForegroundWindow();
        if (!IsShellForegroundWindow(fg) || fg == _hwnd) return;

        var root = Win32.GetAncestor(fg, Win32.GA_ROOT);
        var insertAfter = root != IntPtr.Zero ? root : fg;
        if (insertAfter == IntPtr.Zero || insertAfter == _hwnd) return;

        // 先进 TOPMOST 带，再插在 Shell 根窗口之上，避免只做相对 Z 序时被后续 TOPMOST 冲掉顺序。
        ReassertPopupTopmostZOrder();

        _isOurSetWindowPos = true;
        try
        {
            Win32.SetWindowPos(_hwnd, insertAfter, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
        finally
        {
            _isOurSetWindowPos = false;
        }
    }

    private void SavePopupSize()
    {
        if (_appSettings == null) return;
        _appSettings.PopupPanelWidth = Width;
        _appSettings.PopupPanelMaxHeight = MaxHeight;
        if (_userHasResized && ActualHeight > 0)
            _appSettings.PopupPanelHeight = ActualHeight;
        _appSettings.Save();
    }

    private void HidePopup(bool closingViaClipboardHotkey = false)
    {
        if (_isResizing) return;
        _swallowedMenuAltLatch = false;
        _passthroughModifierLatch = 0;
        _popupPinned = false;
        _isPopupVisible = false;
        _lockPopupWindowNomove = false;
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
        TextEntryEditPopup.IsOpen = false;
        _entryTextEditTarget = null;
        _textEditRestoreForegroundHwnd = IntPtr.Zero;
        RestoreNoActivateAfterEntryTextEditIfLifted();
        ClearPendingDelete();
        if (closingViaClipboardHotkey && (_hotkeyModifiers & Win32.MOD_ALT) != 0)
        {
            _awaitHotkeyAltChordCleanup = true;
            _hotkeyAltChordCleanupDeadlineTick = Environment.TickCount64 + 750;
            _ctxAltAwaitRelease = false;
            _ctxAltComboDuringRelease = false;
            _ctxAltCloseMenuArmed = false;
        }
        else
        {
            _awaitHotkeyAltChordCleanup = false;
            _hotkeyAltChordCleanupDeadlineTick = 0;
        }
#if CLIPX_CLIPBOARD
        SyncBatchPasteKeyboardHook();
#else
        if (_awaitHotkeyAltChordCleanup)
            InstallKeyboardHook();
        else
            UninstallKeyboardHook();
#endif
        SavePopupSize();
        Hide();
    }

    /// <summary>
    /// 开始菜单/搜索等 Shell 前台时，光标与插入点位置不可靠，不跟随；固定在当前显示器工作区左上，减少与居中 Shell 重叠。
    /// </summary>
    private void PositionPopupFixedShellWorkArea()
    {
        System.Drawing.Rectangle work;
        if (_targetWindow != IntPtr.Zero && Win32.GetWindowRect(_targetWindow, out var fgRect))
        {
            int cx = (fgRect.Left + fgRect.Right) / 2;
            int cy = (fgRect.Top + fgRect.Bottom) / 2;
            work = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cx, cy)).WorkingArea;
        }
        else
        {
            work = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                   ?? new System.Drawing.Rectangle(0, 0,
                       (int)SystemParameters.PrimaryScreenWidth,
                       (int)SystemParameters.PrimaryScreenHeight);
        }

        var hMon = Win32.MonitorFromPoint(
            new Win32.POINT { X = work.Left + work.Width / 2, Y = work.Top + work.Height / 2 },
            Win32.MONITOR_DEFAULTTONEAREST);
        Win32.GetDpiForMonitor(hMon, 0, out uint monDpiX, out uint monDpiY);
        double scaleX = monDpiX / 96.0;
        double scaleY = monDpiY / 96.0;

        int popupW = (int)(Width * scaleX);
        double actualH = ActualHeight > 0 ? ActualHeight : MaxHeight;
        int popupH = (int)(actualH * scaleY);

        const int margin = 16;
        int x = work.Left + margin;
        int y = work.Top + margin;
        if (x + popupW > work.Right) x = Math.Max(work.Left, work.Right - popupW);
        if (y + popupH > work.Bottom) y = Math.Max(work.Top, work.Bottom - popupH);

        _pendingPhysX = x;
        _pendingPhysY = y;
    }

    private void PositionPopup()
    {
        const int caretGap = 24;

        if (IsShellForegroundWindow(_targetWindow))
        {
            PositionPopupFixedShellWorkArea();
            #region agent log
            AgentDbgLog("H23", "PositionPopup", "branch=ShellWorkArea",
                new { _targetWindow = _targetWindow.ToInt64(), _pendingPhysX, _pendingPhysY });
            #endregion
            return;
        }

        if (_popupPosition == "Mouse")
        {
            Win32.GetCursorPos(out var pt);
            SetPositionWithOffset(pt.X + 8, pt.Y + 20);
            #region agent log
            AgentDbgLog("H23", "PositionPopup", "branch=MousePref",
                new { pt.X, pt.Y, _pendingPhysX, _pendingPhysY });
            #endregion
            return;
        }

        // 资源管理器驱动的桌面（壁纸/图标层）：无可靠文本光标，跟随鼠标更符合直觉
        if (IsExplorerDesktopForeground(_targetWindow))
        {
            Win32.GetCursorPos(out var deskPt);
            SetPositionWithOffset(deskPt.X + 8, deskPt.Y + 20);
            #region agent log
            AgentDbgLog("H23", "PositionPopup", "branch=ExplorerDesktop",
                new { deskPt.X, deskPt.Y, _pendingPhysX, _pendingPhysY });
            #endregion
            return;
        }

        // UIA 优先（FocusedElement+TextPattern.Selection 在 Office/Chromium 上必然是文档真实 caret）；
        // 历史上把 GetGUIThreadInfo 放第一位会把 Word ribbon 上的 user32 Edit 控件 caret 当成「输入位置」，
        // 导致同一文档第二次呼出时焦点恢复到 ribbon 上的次级 Edit、弹窗错位到 ribbon 区域。
        if (TryGetCaretByAutomation(out double uiaX, out double uiaY))
        {
            SetPositionWithOffset(uiaX, uiaY + caretGap);
            CacheCaretSuccess(_targetWindow, _pendingPhysX, _pendingPhysY);
            #region agent log
            AgentDbgLog("H23", "PositionPopup", "branch=UIA",
                new { uiaX, uiaY, _pendingPhysX, _pendingPhysY });
            #endregion
            return;
        }

        var fgThread = Win32.GetWindowThreadProcessId(_targetWindow, out _);
        var gti = new Win32.GUITHREADINFO { cbSize = Marshal.SizeOf<Win32.GUITHREADINFO>() };

        if (Win32.GetGUIThreadInfo(fgThread, ref gti) && gti.hwndCaret != IntPtr.Zero)
        {
            // 校验 rcCaret 尺寸：Electron/Chromium 等无真实 Win32 caret 的应用，
            // hwndCaret 常被填成主窗口、rcCaret 为 (0,0,0,0)，ClientToScreen 后会
            // 返回窗口左上角而非真实光标位置（用户反馈的副屏全屏 CodeBuddy 呼出
            // 弹窗落到主屏右上角即由此而来）。要求 rcCaret 有非零尺寸才认定为有效 caret。
            // 真实 Win32 caret 通常为 1~2px 宽、16~20px 高，绝不会是 (0,0,0,0)。
            bool hasCaretSize = gti.rcCaret.Right > gti.rcCaret.Left
                               && gti.rcCaret.Bottom > gti.rcCaret.Top;
            if (hasCaretSize)
            {
                var pt = new Win32.POINT { X = gti.rcCaret.Left, Y = gti.rcCaret.Bottom };
                Win32.ClientToScreen(gti.hwndCaret, ref pt);
                if (pt.X > 0 || pt.Y > 0)
                {
                    SetPositionWithOffset(pt.X, pt.Y + caretGap);
                    CacheCaretSuccess(_targetWindow, _pendingPhysX, _pendingPhysY);
                    #region agent log
                    AgentDbgLog("H23", "PositionPopup", "branch=GUIThreadInfoCaret",
                        new { pt.X, pt.Y, _pendingPhysX, _pendingPhysY });
                    #endregion
                    return;
                }
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
                        CacheCaretSuccess(_targetWindow, _pendingPhysX, _pendingPhysY);
                        #region agent log
                        AgentDbgLog("H23", "PositionPopup", "branch=AttachedGetCaretPos",
                            new { caretPos.X, caretPos.Y, _pendingPhysX, _pendingPhysY });
                        #endregion
                        return;
                    }
                }
            }
            finally { Win32.AttachThreadInput(myThread, fgThread, false); }
        }
        catch { }

        // UIA 冷启动/Word 自绘 caret 兜底：30s 内同窗口曾成功定位过则复用，避免每次呼出都贴到鼠标处
        if (TryUseCachedCaret(_targetWindow, out int cachedX, out int cachedY))
        {
            _pendingPhysX = cachedX;
            _pendingPhysY = cachedY;
            #region agent log
            AgentDbgLog("H23", "PositionPopup", "branch=CachedCaret",
                new { cachedX, cachedY, ageMs = Environment.TickCount64 - _lastCaretCacheTickMs });
            #endregion
            return;
        }

        Win32.GetCursorPos(out var cursor);
        SetPositionWithOffset(cursor.X + 8, cursor.Y + 20);
        #region agent log
        AgentDbgLog("H23", "PositionPopup", "branch=CursorFallback",
            new { cursor.X, cursor.Y, _pendingPhysX, _pendingPhysY });
        #endregion
    }

    private void CacheCaretSuccess(IntPtr hwnd, int physX, int physY)
    {
        _lastCaretCacheHwnd = hwnd;
        _lastCaretCachePhysX = physX;
        _lastCaretCachePhysY = physY;
        _lastCaretCacheTickMs = Environment.TickCount64;
    }

    private bool TryUseCachedCaret(IntPtr hwnd, out int physX, out int physY)
    {
        physX = physY = 0;
        if (_lastCaretCacheHwnd == IntPtr.Zero || hwnd != _lastCaretCacheHwnd) return false;
        if (Environment.TickCount64 - _lastCaretCacheTickMs > 30_000) return false;
        physX = _lastCaretCachePhysX;
        physY = _lastCaretCachePhysY;
        return true;
    }

    /// <summary>当前前台是否为 Windows 桌面（Progman/WorkerW），即点击壁纸或桌面图标时的焦点窗体。</summary>
    private static bool IsExplorerDesktopForeground(IntPtr foregroundHwnd)
    {
        if (foregroundHwnd == IntPtr.Zero) return false;
        var cls = Win32.GetWindowClassName(foregroundHwnd);
        return cls.Equals("Progman", StringComparison.OrdinalIgnoreCase)
               || cls.Equals("WorkerW", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断指定进程是否为 Chromium-based 应用（Electron / Chromium / Chrome / Edge 等）。
    /// 这类应用的 UIA FocusedElement.BoundingRectangle 返回的是整个 BrowserWindow 矩形，
    /// 而非真实光标位置，不能直接用作光标定位。识别方式：进程名匹配已知 Chromium 应用名，
    /// 或加载了 chrome.dll / electron.exe 等模块。
    /// </summary>
    private static bool IsChromiumBasedProcess(int processId)
    {
        try
        {
            var proc = Process.GetProcessById(processId);
            var name = proc.ProcessName;
            // 已知 Chromium-based 应用进程名（不含扩展名，小写匹配）
            switch (name.ToLowerInvariant())
            {
                case "codebuddy":
                case "codebuddy cn":   // 国行版 CodeBuddy 进程名带空格 + CN 后缀
                case "code":
                case "code - insiders":
                case "cursor":
                case "windsurf":
                case "msedge":
                case "chrome":
                case "slack":
                case "discord":
                case "teams":
                case "spotify":
                case "atom":
                case "github desktop":
                case "postman":
                case "notion":
                case "obsidian":
                case "edge":
                    return true;
            }

            // 兜底：进程名包含 "codebuddy"（覆盖未来可能的 CodeBuddy Pro / Dev 等变体）
            if (name.IndexOf("codebuddy", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // 兜底：检查主模块文件名是否包含 chromium 特征
            try
            {
                var mainModule = proc.MainModule;
                if (mainModule != null)
                {
                    var fn = mainModule.FileName ?? "";
                    if (fn.IndexOf("electron", StringComparison.OrdinalIgnoreCase) >= 0
                        || fn.IndexOf("chromium", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch
            {
                // 64位进程访问 32位进程模块会失败，忽略
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>[DIAG] 临时诊断辅助：安全获取进程名，避免访问失败时抛异常。</summary>
    private static string GetProcessNameSafe(int processId)
    {
        try { return Process.GetProcessById(processId).ProcessName; }
        catch { return "?"; }
    }

    private static bool TryGetCaretByAutomation(out double x, out double y)
    {
        x = y = 0;
        var sw = Stopwatch.StartNew();
        string outcome = "init";
        string source = "none";
        try
        {
            var task = Task.Run<(bool ok, double x, double y, string src)>(() =>
            {
                try
                {
                    var focused = System.Windows.Automation.AutomationElement.FocusedElement;
                    if (focused == null) return (false, 0, 0, "no-focused");

                    if (focused.TryGetCurrentPattern(
                            System.Windows.Automation.TextPattern.Pattern, out var p))
                    {
                        var sel = ((System.Windows.Automation.TextPattern)p).GetSelection();
                        if (sel.Length > 0)
                        {
                            var rects = sel[0].GetBoundingRectangles();
                            if (rects.Length > 0 && (rects[0].X > 0 || rects[0].Y > 0))
                            {
                                var selRect = rects[0];
                                // 判据：TextPattern 选区矩形的 ClassName 为空时，视为 Electron/Chromium
                                // 容器返回的伪选区（日志验证：CodeBuddy ClassName='' 返回 1920x1032 容器矩形）。
                                // 真实输入控件（mmui::ChatInputField / TermControl / WPF Edit 等）都有 ClassName。
                                // 注意：不能仅靠尺寸判别——终端 TermControl 矩形 2346x1142 比 CodeBuddy 还大但是合法的。
                                if (string.IsNullOrEmpty(focused.Current.ClassName))
                                {
                                    ClipboardDiagnosticsLog.Write(
                                        $"[CLIPX_DIAG] text-sel reject empty-cls " +
                                        $"proc={GetProcessNameSafe(focused.Current.ProcessId)} " +
                                        $"selRect=({selRect.X:F0},{selRect.Y:F0},{selRect.Width:F0}x{selRect.Height:F0})");
                                    return (false, 0, 0, "text-sel-empty-cls");
                                }
                                return (true, selRect.X, selRect.Bottom + 4, "text-sel");
                            }
                        }
                    }

                    // FocusedElement 没有 TextPattern 时，退化到 BoundingRectangle。
                    // 对大多数 Win32/WinForm/WPF 输入控件，这个矩形就是控件本身，可作为光标位置使用。
                    // 但对 Electron/Chromium 应用，FocusedElement 可能返回非 caret 的容器矩形：
                    //   (1) CodeBuddy: ClassName='' 的内部 div，矩形 1920x1032
                    //   (2) Trae Work: ClassName='Chrome_WidgetWin_1' 的顶层窗口，矩形 = fgRect，cover=1.0
                    // 用双判据覆盖两种模式：
                    //   - ClassName 为空 → 容器 div（CodeBuddy 模式）
                    //   - rect 与前台窗口矩形面积比 ≥ 0.95 → 窗口级矩形（Trae Work 模式）
                    // 真实输入控件（mmui::ChatInputField cover=0.14 / TermControl cover=0.91）都不会被误判。
                    var rect = focused.Current.BoundingRectangle;
                    if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
                    {
                        // [DIAG] 始终输出 bound-rect 详细信息 + 前台窗口矩形对比，用于发现新场景问题
                        var fgHwnd2 = Win32.GetForegroundWindow();
                        Win32.GetWindowRect(fgHwnd2, out var fgRect2);
                        double fgArea2 = (double)(fgRect2.Right - fgRect2.Left) * (fgRect2.Bottom - fgRect2.Top);
                        double rectArea2 = rect.Width * rect.Height;
                        double coverRatio2 = fgArea2 > 0 ? rectArea2 / fgArea2 : 0;
                        bool clsEmpty = string.IsNullOrEmpty(focused.Current.ClassName);
                        bool windowLevel = coverRatio2 >= 0.95;
                        ClipboardDiagnosticsLog.Write(
                            $"[CLIPX_DIAG] bound-rect pid={focused.Current.ProcessId} " +
                            $"proc={GetProcessNameSafe(focused.Current.ProcessId)} " +
                            $"cls='{focused.Current.ClassName}' clsEmpty={clsEmpty} " +
                            $"rect=({rect.X:F0},{rect.Y:F0},{rect.Width:F0}x{rect.Height:F0}) " +
                            $"fgRect=({fgRect2.Left},{fgRect2.Top},{fgRect2.Right - fgRect2.Left}x{fgRect2.Bottom - fgRect2.Top}) " +
                            $"cover={coverRatio2:F2} windowLevel={windowLevel}");

                        if (clsEmpty)
                        {
                            return (false, 0, 0, "bound-rect-empty-cls");
                        }
                        if (windowLevel)
                        {
                            return (false, 0, 0, "bound-rect-window-level");
                        }
                        return (true, rect.X + 20, rect.Bottom + 4, "bound-rect");
                    }

                    return (false, 0, 0, "no-pattern-no-rect");
                }
                catch (Exception ex) { return (false, 0, 0, "ex:" + ex.GetType().Name); }
            });

            // Word/Office/Chromium 走 UIA 文本模式，冷启动 200ms 常超时；放宽到 500ms 才能首次命中
            if (task.Wait(500))
            {
                var result = task.Result;
                source = result.src;
                if (result.ok) { x = result.x; y = result.y; outcome = "ok"; return true; }
                outcome = "miss";
            }
            else
            {
                outcome = "timeout-500ms";
            }
        }
        catch (Exception ex) { outcome = "ex:" + ex.GetType().Name; }
        finally
        {
            sw.Stop();
            #region agent log
            AgentDbgLog("H23", "TryGetCaretByAutomation", outcome,
                new { ms = sw.ElapsedMilliseconds, source, x, y });
            #endregion
        }
        return false;
    }

    /// <summary>启动后异步预热 UIA TextPattern 代理，避免首次从 Word/Office 调出剪贴板时 UIA 200~500ms 冷启动导致定位落到鼠标兜底。</summary>
    private static void WarmUpUiaCaretProxy()
    {
        Task.Run(() =>
        {
            try
            {
                var focused = System.Windows.Automation.AutomationElement.FocusedElement;
                if (focused != null)
                {
                    _ = focused.TryGetCurrentPattern(
                        System.Windows.Automation.TextPattern.Pattern, out _);
                    _ = focused.Current.BoundingRectangle;
                }
            }
            catch { }
        });
    }

    private void SetPositionWithOffset(double physX, double physY)
    {
        // 注意：必须用 Win32 MonitorFromPoint + GetMonitorInfo 拿 rcWork，
        // 而非 System.Windows.Forms.Screen.WorkingArea —— 后者在 per-monitor DPI 高分屏
        // 上返回的是逻辑坐标（DIP），与函数内全程使用的物理像素坐标不一致，
        // 会导致 y + popupH > work.Bottom 误触发，弹窗被错误上推到主显示器顶部。
        var hMon = Win32.MonitorFromPoint(
            new Win32.POINT { X = (int)physX, Y = (int)physY },
            Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        var work = Win32.GetMonitorInfo(hMon, ref mi)
            ? mi.rcWork
            : new Win32.RECT { Left = 0, Top = 0, Right = 65535, Bottom = 65535 };

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
    /// 全局屏幕物理点不能仅用 CompositionTarget.TransformFromDevice：其随 HWND 所在监视器矩阵变化，跨 DPI 副屏会错（用户反馈「恢复成最初问题」）。
    /// 使用 MonitorFromRect（有 HWND 尺寸时）或 MonitorFromPoint + GetDpiForMonitor 计算相对监视器的 DIP 偏移；监视器原点用 PhysicalToLogical(桌面) 或 VirtualScreen 拼接（避免 API 恒等返回）。
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

            int pw = 0, ph = 0;
            if (_hwnd != IntPtr.Zero && Win32.GetWindowRect(_hwnd, out var rcWin))
            {
                pw = rcWin.Right - rcWin.Left;
                ph = rcWin.Bottom - rcWin.Top;
            }

            if (TryPhysicalScreenTopLeftToWpfDip(_pendingPhysX, _pendingPhysY, pw, ph, out var dipX, out var dipY))
            {
                #region agent log
                AgentDbgLog("H1", "TryApplyPendingPositionAsWpfLeftTop", "per-monitor physical→DIP",
                    new { _pendingPhysX, _pendingPhysY, dipX, dipY });
                #endregion
                Left = dipX;
                Top = dipY;
                return true;
            }

            var src = HwndSource.FromHwnd(_hwnd);
            if (src?.CompositionTarget == null) return false;

            var dip = src.CompositionTarget.TransformFromDevice.Transform(
                new System.Windows.Point(_pendingPhysX, _pendingPhysY));
            #region agent log
            AgentDbgLog("H1", "TryApplyPendingPositionAsWpfLeftTop", "fallback TransformFromDevice",
                new { _pendingPhysX, _pendingPhysY, dip.X, dip.Y });
            #endregion
            Left = dip.X;
            Top = dip.Y;
            return true;
        }
        catch (Exception ex)
        {
            #region agent log
            AgentDbgLog("H1", "TryApplyPendingPositionAsWpfLeftTop", "exception",
                new { ex.GetType().Name, ex.Message });
            #endregion
            return false;
        }
    }

    /// <summary>
    /// 将屏幕物理像素表示的窗口左上角转为 WPF 全局 DIP（与 <see cref="Window.Left"/> / <see cref="Window.Top"/> 一致）。
    /// <paramref name="physW"/>/<paramref name="physH"/> 为窗口物理宽高时，用 <see cref="Win32.MonitorFromRect"/> 选监视器（与窗体交集最大），避免跨屏时仅用左上角误判已进入高 DPI 屏。
    /// </summary>
    private static bool TryPhysicalScreenTopLeftToWpfDip(int physX, int physY, int physW, int physH, out double dipX, out double dipY, bool agentLogH19 = true)
    {
        dipX = dipY = 0;
        IntPtr hMon;
        string monitorPick;
        if (physW > 0 && physH > 0)
        {
            var rcSel = new Win32.RECT
            {
                Left = physX,
                Top = physY,
                Right = physX + physW,
                Bottom = physY + physH
            };
            hMon = Win32.MonitorFromRect(ref rcSel, Win32.MONITOR_DEFAULTTONEAREST);
            monitorPick = "rect";
        }
        else
        {
            hMon = IntPtr.Zero;
            monitorPick = "point";
        }

        if (hMon == IntPtr.Zero)
        {
            var pt = new Win32.POINT { X = physX, Y = physY };
            hMon = Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTONEAREST);
            monitorPick = "point";
        }

        if (hMon == IntPtr.Zero) return false;

        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(hMon, ref mi)) return false;

        uint dpiX = 96, dpiY = 96;
        if (Win32.GetDpiForMonitor(hMon, 0, out uint dpx, out uint dpy) == 0)
        {
            dpiX = dpx;
            dpiY = dpy;
        }

        double relPhysX = physX - mi.rcMonitor.Left;
        double relPhysY = physY - mi.rcMonitor.Top;
        double relLogX = relPhysX * 96.0 / dpiX;
        double relLogY = relPhysY * 96.0 / dpiY;

        var desk = Win32.GetDesktopWindow();
        if (desk == IntPtr.Zero) return false;

        var monPt = new Win32.POINT { X = mi.rcMonitor.Left, Y = mi.rcMonitor.Top };
        if (!Win32.ScreenToClient(desk, ref monPt))
            return false;

        var monLog = monPt;
        bool physToLogOk = Win32.PhysicalToLogicalPointForPerMonitorDPI(desk, ref monLog);

        // 日志：PhysicalToLogical 有时对「监视器原点」仍输出与物理数值同量纲，会把 DIP 当物理；高 DPI 下用 VirtualScreen 拼接原点
        bool identitySuspect = physToLogOk &&
            Math.Abs(monLog.X - mi.rcMonitor.Left) < 1.0 &&
            Math.Abs(monLog.Y - mi.rcMonitor.Top) < 1.0 &&
            (dpiX > 96 || dpiY > 96);

        double monLeftDip, monTopDip;
        if (!physToLogOk || identitySuspect)
        {
            int vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
            int vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
            monLeftDip = SystemParameters.VirtualScreenLeft + (mi.rcMonitor.Left - vx) * 96.0 / dpiX;
            monTopDip = SystemParameters.VirtualScreenTop + (mi.rcMonitor.Top - vy) * 96.0 / dpiY;
        }
        else
        {
            monLeftDip = monLog.X;
            monTopDip = monLog.Y;
        }

        dipX = monLeftDip + relLogX;
        dipY = monTopDip + relLogY;
        #region agent log
        if (agentLogH19)
        {
            int vxLog = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
            int vyLog = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
            AgentDbgLog("H19", "TryPhysicalScreenTopLeftToWpfDip", "origin+dip",
                new
                {
                    monitorPick,
                    physW,
                    physH,
                    physToLogOk,
                    identitySuspect,
                    branch = !physToLogOk || identitySuspect ? "virtual" : "ptol",
                    monPtClient = new { monPt.X, monPt.Y },
                    monLog = new { monLog.X, monLog.Y },
                    rcMon = new { mi.rcMonitor.Left, mi.rcMonitor.Top },
                    vxLog,
                    vyLog,
                    dpiX,
                    dpiY,
                    monLeftDip,
                    monTopDip,
                    dipX,
                    dipY
                });
        }
        #endregion
        return true;
    }

    /// <summary>
    /// DPI 切换或拖动结束后，用 HWND 物理矩形同步 WPF Left/Top（DIP）。
    /// 勿在跨屏拖动每一帧调用：全局物理坐标经 CompositionTarget.TransformFromDevice 时，若与 HWND 当前监视器 DPI 不一致会算错 Left/Top。
    /// </summary>
    private void SyncWindowPhysicalPositionToWpf(string syncSource)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (!Win32.GetWindowRect(_hwnd, out var rc)) return;

        // 松手后第一次同步：壳可能在 WH_MOUSE_LL 与 Dispatcher 之间移动 HWND（H2 曾见 Left=-985）；拉回钩子最后一帧权威位置。
        if (_postDragHookAuthLeft != int.MinValue)
        {
            int authL = _postDragHookAuthLeft, authT = _postDragHookAuthTop;
            _postDragHookAuthLeft = int.MinValue;
            int dL = Math.Abs(rc.Left - authL);
            int dT = Math.Abs(rc.Top - authT);
            if (dL > 8 || dT > 8)
            {
                #region agent log
                AgentDbgLog("H15", "SyncWindowPhysicalPositionToWpf", "post-drag rect drift vs hook auth; restoring",
                    new { rc.Left, rc.Top, authL, authT, dL, dT });
                #endregion
                _isOurSetWindowPos = true;
                try
                {
                    Win32.SetWindowPos(_hwnd, IntPtr.Zero, authL, authT, 0, 0,
                        Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOSENDCHANGING);
                }
                finally
                {
                    _isOurSetWindowPos = false;
                }
                if (!Win32.GetWindowRect(_hwnd, out rc)) return;
            }
        }

        int vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int vw = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        int vh = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
        int w = rc.Right - rc.Left;
        int h = rc.Bottom - rc.Top;
        if (w > 0 && h > 0 && vw > 0 && vh > 0 &&
            (rc.Left < vx - 64 || rc.Top < vy - 64 || rc.Left > vx + vw - 32 || rc.Top > vy + vh - 32))
        {
            int ol = rc.Left, ot = rc.Top;
            int nl = Math.Clamp(rc.Left, vx, Math.Max(vx, vx + vw - w));
            int nt = Math.Clamp(rc.Top, vy, Math.Max(vy, vy + vh - h));
            _isOurSetWindowPos = true;
            try
            {
                Win32.SetWindowPos(_hwnd, IntPtr.Zero, nl, nt, 0, 0,
                    Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOSENDCHANGING);
            }
            finally
            {
                _isOurSetWindowPos = false;
            }
            if (!Win32.GetWindowRect(_hwnd, out rc)) return;
            #region agent log
            AgentDbgLog("H14", "SyncWindowPhysicalPositionToWpf", "clamped off-screen hwnd rect (virtual)",
                new { vx, vy, vw, vh, before = new { ol, ot }, after = new { nl, nt } });
            #endregion
        }

        _pendingPhysX = rc.Left;
        _pendingPhysY = rc.Top;
        #region agent log
        AgentDbgLog("H2", "SyncWindowPhysicalPositionToWpf", "before TryApply",
            new { syncSource, rc.Left, rc.Top, rc.Right, rc.Bottom, _pendingPhysX, _pendingPhysY });
        #endregion
        TryApplyPendingPositionAsWpfLeftTop();
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
        if (_appSettings == null) return;
        if (hwnd == IntPtr.Zero) return;
        if (!FileDialogJumpHelper.QuickMayBeUnderFileDialog(hwnd)) return;

        var dlg = FileDialogJumpHelper.ResolveFileDialogHwndFromWindowOrAncestor(hwnd);
        if (dlg == IntPtr.Zero) return;

        var runOpenList = _appSettings.FileJumpPickerOpenWhenDialogForeground;
        var runAutoNavigate = _appSettings.FileJumpAutoOnFirstClick;
        if (!runOpenList && !runAutoNavigate)
            return;

        if (runOpenList || runAutoNavigate)
            ScheduleSnapshotFolderFromDialog(dlg);

        Dispatcher.BeginInvoke(() =>
        {
            // 「自动弹列表」开：走列表路径（开+开时该路径内部会先直跳再弹列表）。
            // 仅「自动跳转」开：走纯直跳路径；同时武装鼠标钩，覆盖无前台事件的宿主。
            if (runOpenList)
                TryAutoOpenFileJumpPickerWhenDialogForeground(dlg);
            else if (runAutoNavigate)
                TryAutoNavigateBestPathWhenDialogForeground(dlg);

            // 仅在「自动跳转」开 + 「自动弹列表」关时才需要鼠标钩兜底（弹列表已由前台事件触发，无需点击）。
            if (runAutoNavigate && !runOpenList)
                UpdateFileJumpClickToNavigateArm(dlg);
        });
    }

    private void OnForegroundChanged(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        Interlocked.Increment(ref _foregroundNativeBurst);
        var prev = _lastForegroundForDialogTrack;
        _lastForegroundForDialogTrack = hwnd;

        if (_activeFileJumpPicker != null && hwnd != new WindowInteropHelper(_activeFileJumpPicker).Handle)
        {
            var ownerHwnd = _activeFileJumpPicker.OwnerDialogHwnd;
            if (ownerHwnd == IntPtr.Zero || !Win32.IsWindow(ownerHwnd))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_activeFileJumpPicker != null)
                    {
                        try { _activeFileJumpPicker.Close(); } catch { }
                        _activeFileJumpPicker = null;
                        StopExplorerPathPoll();
                    }
                }, DispatcherPriority.Send);
            }
        }

        int seq = Interlocked.Increment(ref _foregroundChangeCoalesceGen);
        var prevCap = prev;
        var hwndCap = hwnd;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (seq != Volatile.Read(ref _foregroundChangeCoalesceGen))
            {
                Interlocked.Increment(ref _foregroundUiDispatchSuperseded);
                return;
            }

            int nat = Interlocked.Exchange(ref _foregroundNativeBurst, 0);
            int super = Interlocked.Exchange(ref _foregroundUiDispatchSuperseded, 0);
            ProcessForegroundChangedUi(prevCap, hwndCap, nat, super);
        });
    }

    /// <summary>
    /// 原 OnForegroundChanged 内多次 BeginInvoke 会在前台连发时撑爆队列（尤其关跳转列表瞬间），
    /// 与跳转窗共用同一 Dispatcher 时表现为长时间卡顿。
    /// </summary>
    private void ProcessForegroundChangedUi(IntPtr prev, IntPtr hwnd, int nativeBurst, int supersededDispatches)
    {
        var sw = Stopwatch.StartNew();

        if (_activeFileJumpPicker == null)
        {
            TryRememberFolderFromDialog(prev);
        }
        TryRememberExternalManagerFolder(prev);
        TryRememberExternalManagerFolder(hwnd);

        if (_activeFileJumpPicker == null)
        {
            var dialogForForeground = FileDialogJumpHelper.ResolveFileDialogHwndFromWindowOrAncestor(hwnd);
            if (dialogForForeground != IntPtr.Zero)
            {
                _lastFileDialogSeenTick = Environment.TickCount64;
                var prevForAutoSync = prev;
                ScheduleSnapshotFolderFromDialog(dialogForForeground);
                var navigationSuppressed = IsFileJumpNavigationSuppressed(dialogForForeground);
                if (_appSettings != null && !navigationSuppressed)
                {
                    if (_appSettings.FileJumpPickerOpenWhenDialogForeground)
                        TryAutoOpenFileJumpPickerWhenDialogForeground(dialogForForeground);
                    else if (_appSettings.FileJumpAutoOnFirstClick)
                        TryAutoNavigateBestPathWhenDialogForeground(dialogForForeground);
                }

                if (!navigationSuppressed)
                    TryAutoSyncPathOnDialogReturn(hwnd, prevForAutoSync);
            }

            var armTarget = dialogForForeground != IntPtr.Zero
                ? dialogForForeground
                : FileDialogJumpHelper.ResolveFileDialogHwndFromWindowOrAncestor(hwnd);
            UpdateFileJumpClickToNavigateArm(armTarget != IntPtr.Zero ? armTarget : hwnd);
        }
        else
        {
            var dlgPick = FileDialogJumpHelper.ResolveFileDialogHwndFromWindowOrAncestor(hwnd);
            var dialogForForeground = dlgPick;

            // picker 打开时切回文件对话框 → 触发 auto-sync
            if (dialogForForeground != IntPtr.Zero)
            {
                if (!IsFileJumpNavigationSuppressed(dialogForForeground))
                    TryAutoSyncPathOnDialogReturn(hwnd, prev);
            }
            // picker 打开时切到外部管理器 → 触发采集刷新列表（新 Explorer 路径会被加入候选）
            else if (dialogForForeground == IntPtr.Zero && _activeFileJumpPicker != null)
                TryRefreshPickerForNewExternalFolder(hwnd);

            UpdateFileJumpClickToNavigateArm(dlgPick != IntPtr.Zero ? dlgPick : hwnd);
        }

        var shouldHidePopup = _isPopupVisible
            && !_popupPinned
            && !_isResizing
            && hwnd != _hwnd
            && hwnd != _targetWindow;
        if (shouldHidePopup)
        {
            Win32.GetCursorPos(out var cursor);
            if (Win32.WindowFromPoint(cursor) != _hwnd)
                HidePopup();
        }

        sw.Stop();
        int ms = (int)sw.ElapsedMilliseconds;
        bool pickerOpen = _activeFileJumpPicker != null;
        bool logFg = nativeBurst >= 2 || supersededDispatches > 0 || ms >= 15 || pickerOpen
            || _fileJumpPickerOpenInProgress || ms >= 40;
        if (logFg)
        {
            var slow = ms >= 40 ? " SLOW" : "";
            ShellNavigateLog.Write("filejump",
                $"fg_ui nat={nativeBurst} super={supersededDispatches} ms={ms} prev=0x{prev.ToInt64():X} hwnd=0x{hwnd.ToInt64():X} picker={pickerOpen} openInProg={_fileJumpPickerOpenInProgress}{slow}");
        }
    }

    /// <summary>对话框成为前台后分层短等再读路径：先快试，读不到再逐段补等（总上限接近原先单次长等）。</summary>
    private void ScheduleSnapshotFolderFromDialog(IntPtr dialogHwnd)
    {
        if (_appSettings == null || dialogHwnd == IntPtr.Zero) return;
        var nowSnap = Environment.TickCount64;
        if (dialogHwnd == _snapshotFolderDebounceHwnd
            && nowSnap - _snapshotFolderDebounceTick < 450)
            return;
        _snapshotFolderDebounceHwnd = dialogHwnd;
        _snapshotFolderDebounceTick = nowSnap;

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
                    // DLL 注入读取路径较慢，放到后台线程避免阻塞 UI
                    var capturedTarget = target;
                    var capturedPhase = p;
                    var th = new Thread(() =>
                    {
                        if (FileDialogJumpHelper.TryReadCurrentFolder(capturedTarget, out var folder)
                            && !string.IsNullOrEmpty(folder))
                        {
                            Dispatcher.BeginInvoke(() => RememberLastDialogFolder(folder), DispatcherPriority.Background);
                            return;
                        }
                        if (capturedPhase < 2)
                            Dispatcher.BeginInvoke(() => SchedulePhase(capturedPhase + 1), DispatcherPriority.Background);
                    }) { IsBackground = true, Name = "ClipboardX-SnapshotRead" };
                    th.Start();
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
        // DLL 注入读取路径较慢，放到后台线程避免阻塞 UI
        var capturedDlg = dlg;
        var th = new Thread(() =>
        {
            if (!FileDialogJumpHelper.TryReadCurrentFolder(capturedDlg, out var folder)
                || string.IsNullOrEmpty(folder)) return;
            Dispatcher.BeginInvoke(() => RememberLastDialogFolder(folder), DispatcherPriority.Background);
        }) { IsBackground = true, Name = "ClipboardX-RememberFolder" };
        th.Start();
    }

    private void RememberLastDialogFolder(string folder)
    {
        if (_appSettings == null) return;
        _appSettings.RecordFolderConfirmation(folder);
    }

    private static List<string>? CopyRecentForJump(AppSettings? settings)
    {
        if (settings?.RecentFileDialogFolders == null || settings.RecentFileDialogFolders.Count == 0)
            return null;
        var maxCount = settings.RecentFolderMaxCount;
        if (maxCount < 1) maxCount = 5;
        var list = new List<string>();
        foreach (var p in settings.RecentFileDialogFolders)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            list.Add(p.Trim());
            if (list.Count >= maxCount) break;
        }

        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// 记录最近一次活跃外部文件管理器的路径；切回文件对话框时优先将其作为同步目标。
    /// </summary>
    private void TryRememberExternalManagerFolder(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == _hwnd || !Win32.IsWindow(hwnd)) return;
        if (FileDialogJumpHelper.IsLikelyFileDialog(hwnd)) return;

        // 跳过需要通过剪贴板通信的文件管理器（XYplorer、Total Commander），
        // 避免每次前台切换都发送脚本导致 scripting console 异常和剪贴板被覆盖。
        // 这些管理器的路径仅在文件对话框打开时按需采集（CollectCandidates）。
        var rootCls = Win32.GetWindowClassName(Win32.GetAncestor(hwnd, Win32.GA_ROOT));
        if (rootCls is "ThunderRT6FormDC" or "TTOTAL_CMD") return;

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
        if (dialogHwnd == IntPtr.Zero)
        {
            OpenGlobalFavoritesPicker();
            return;
        }

        // 列表窗正在 new 的过程中尚未赋给 _activeFileJumpPicker：忽略重复热键，避免再次排队打开。
        if (_fileJumpPickerOpenInProgress && _activeFileJumpPicker == null)
            return;

        var mem = _appSettings.LastFileDialogFolder?.Trim();
        var allowShellInject = _appSettings.EnableShellNavigateInject;

        unchecked { _fileJumpHotkeyCollectGen++; }
        var gen = _fileJumpHotkeyCollectGen;
        var dialogHwndCapture = dialogHwnd;
        var memCapture = mem;
        var recentCapture = CopyRecentForJump(_appSettings);
        var allowCapture = allowShellInject;

        void StaCollect()
        {
            if (gen != _fileJumpHotkeyCollectGen) return;
            List<FileJumpCandidate> quick;
            try
            {
                quick = FileManagerPathCollector.CollectCandidates(dialogHwndCapture, memCapture,
                    skipAlternateUiAutomation: true, stopAfterCandidateCount: 2,
                    shouldAbort: () => gen != _fileJumpHotkeyCollectGen,
                    recentFolders: recentCapture);
            }
            catch (Exception ex)
            {
                ShellNavigateLog.Write("filejump", "CollectCandidates quick (hotkey): " + ex);
                quick = new List<FileJumpCandidate>();
            }

            if (gen != _fileJumpHotkeyCollectGen) return;

            if (quick.Count >= 2)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (gen != _fileJumpHotkeyCollectGen) return;
                    TryJumpFileDialogToLastFolderContinueAfterCollect(
                        dialogHwndCapture,
                        quick,
                        allowCapture,
                        afterPickerAssigned: () => StartFullCollectForHotkey(dialogHwndCapture, memCapture, recentCapture, gen));
                }, DispatcherPriority.Input);
                return;
            }

            if (gen != _fileJumpHotkeyCollectGen) return;

            List<FileJumpCandidate> candidates;
            try
            {
                candidates = FileManagerPathCollector.CollectCandidates(dialogHwndCapture, memCapture,
                    shouldAbort: () => gen != _fileJumpHotkeyCollectGen,
                    recentFolders: recentCapture);
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

    /// <summary>全局 Ctrl+G：非文件对话框界面打开收藏/常用文件夹快速选择窗口。</summary>
    private void OpenGlobalFavoritesPicker()
    {
        if (_appSettings == null) return;
        if (_activeFileJumpPicker != null || _fileJumpPickerOpenInProgress) return;

        var candidates = new List<FileJumpCandidate>();

        foreach (var fav in _appSettings.FolderFavorites)
        {
            if (string.IsNullOrWhiteSpace(fav.Path)) continue;
            try
            {
                var full = Path.GetFullPath(fav.Path.Trim());
                if (Directory.Exists(full))
                    candidates.Add(new FileJumpCandidate("收藏", full));
            }
            catch { /* ignore */ }
        }

        foreach (var r in _appSettings.RecentFileDialogFolders)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            try
            {
                var full = Path.GetFullPath(r.Trim());
                if (Directory.Exists(full) && !candidates.Any(c =>
                    string.Equals(c.Path, full, StringComparison.OrdinalIgnoreCase)))
                    candidates.Add(new FileJumpCandidate("常用", full));
            }
            catch { /* ignore */ }
        }

        Win32.GetCursorPos(out var pos);
        var picker = new FileDialogJumpPickerWindow(
            candidates, 0, pos.X, pos.Y, _appSettings, IntPtr.Zero,
            autoForegroundStickyMode: false);
        _activeFileJumpPicker = picker;
        picker.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeFileJumpPicker, picker))
                _activeFileJumpPicker = null;
        };
        picker.Show();
    }

    private void TryJumpFileDialogToLastFolderContinueAfterCollect(
        IntPtr dialogHwnd,
        List<FileJumpCandidate> candidates,
        bool allowShellInject,
        Action? afterPickerAssigned = null)
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

        var prefer = PreferCandidateIndex(dialogHwnd, candidates);

        if (_activeFileJumpPicker != null)
        {
            _fileJumpPickerSession++;
            ClearFileJumpDoubleTapState();
            NavigateToFolderInBackground(dialogHwnd, candidates[prefer].Path, allowShellInject);
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
            NavigateToFolderInBackground(dialogHwnd, path, allowShellInject);
            Dispatcher.BeginInvoke(() => _activeFileJumpPicker?.Close(),
                System.Windows.Threading.DispatcherPriority.Normal);
            return;
        }

        if (!_appSettings.FileJumpPickerAutoPopup)
        {
            ClearFileJumpDoubleTapState();
            NavigateToFolderInBackground(dialogHwnd, candidates[prefer].Path, allowShellInject);
            return;
        }

        ScheduleFileJumpPickerOpen(dialogHwnd, candidates.ToList(), prefer, armHotkeyDoubleTap: true, allowShellInject,
            autoForegroundStickyMode: false, afterPickerAssigned);
    }

    /// <summary>在后台 STA 线程执行文件对话框导航，避免 Thread.Sleep 阻塞 UI 线程。</summary>
    private void NavigateToFolderInBackground(IntPtr dialogHwnd, string path, bool allowShellInject,
        Action<bool>? onCompleted = null)
    {
        MarkFileJumpNavigationSuppressed(dialogHwnd, path);
        var th = new Thread(() =>
        {
            try
            {
                var ok = FileDialogJumpHelper.TryNavigateToFolder(dialogHwnd, path, allowShellInject);
                onCompleted?.Invoke(ok);
            }
            catch (Exception ex)
            {
                ShellNavigateLog.Write("filejump", "NavigateToFolderInBackground: " + ex);
                onCompleted?.Invoke(false);
            }
        })
        {
            IsBackground = true,
            Name = "ClipboardX-FileJump-Navigate",
        };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
    }

    /// <summary>完整采集（含 UIA）完成后刷新已打开的跳转列表。</summary>
    private void StartFullCollectForHotkey(IntPtr dialogHwnd, string? mem, List<string>? recentFolders, int gen)
    {
        Task.Run(() =>
        {
            if (!WaitForFileJumpFullCollectQuietWindow(dialogHwnd, () => gen != _fileJumpHotkeyCollectGen))
                return;

            List<FileJumpCandidate> full;
            try
            {
                full = FileManagerPathCollector.CollectCandidates(dialogHwnd, mem,
                    shouldAbort: () => gen != _fileJumpHotkeyCollectGen,
                    recentFolders: recentFolders);
            }
            catch (Exception ex)
            {
                ShellNavigateLog.Write("filejump", "CollectCandidates (hotkey full): " + ex);
                full = new List<FileJumpCandidate>();
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (gen != _fileJumpHotkeyCollectGen) return;
                TryJumpFileDialogRefreshPickerIfOpen(dialogHwnd, full);
            }, DispatcherPriority.Normal);
        });
    }

    /// <summary>完整采集（含 UIA）完成后刷新已打开的跳转列表。</summary>
    private void StartFullCollectForAutoForeground(IntPtr dialogHwnd, string? mem, List<string>? recentFolders,
        int gen)
    {
        Task.Run(() =>
        {
            if (!WaitForFileJumpFullCollectQuietWindow(dialogHwnd, () => gen != _fileJumpAutoForegroundCollectGen))
                return;

            List<FileJumpCandidate> full;
            try
            {
                full = FileManagerPathCollector.CollectCandidates(dialogHwnd, mem,
                    shouldAbort: () => gen != _fileJumpAutoForegroundCollectGen,
                    recentFolders: recentFolders);
            }
            catch (Exception ex)
            {
                ShellNavigateLog.Write("filejump", "CollectCandidates (auto fg full): " + ex);
                full = new List<FileJumpCandidate>();
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (gen != _fileJumpAutoForegroundCollectGen) return;
                TryJumpFileDialogRefreshPickerIfOpen(dialogHwnd, full);
            }, DispatcherPriority.Normal);
        });
    }

    /// <summary>
    /// Shell.Application.Windows 会跨进程碰 Explorer；拖动文件对话框时触发会造成拖动卡顿。
    /// 完整采集不影响首屏弹出，等待鼠标释放并稳定后再扫。
    /// </summary>
    private static bool WaitForFileJumpFullCollectQuietWindow(IntPtr dialogHwnd, Func<bool> shouldAbort)
    {
        const int maxWaitMs = 5000;
        const int quietMs = 420;
        const int stepMs = 60;
        var waited = 0;
        var quietFor = 0;

        while (waited < maxWaitMs)
        {
            if (shouldAbort()) return false;

            var busy = IsPrimaryMouseButtonDown() || IsWindowThreadInMoveSize(dialogHwnd);
            if (busy)
                quietFor = 0;
            else
            {
                quietFor += stepMs;
                if (quietFor >= quietMs)
                    return true;
            }

            Thread.Sleep(stepMs);
            waited += stepMs;
        }

        ClipboardDiagnosticsLog.Write(
            $"filejump.perf full_collect_cancel_not_quiet hwnd=0x{dialogHwnd.ToInt64():X} waitedMs={waited}");
        return false;
    }

    private static bool IsPrimaryMouseButtonDown()
        => (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0;

    private static bool IsWindowThreadInMoveSize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return false;
        var tid = Win32.GetWindowThreadProcessId(hwnd, out _);
        if (tid == 0) return false;
        var gti = new Win32.GUITHREADINFO { cbSize = Marshal.SizeOf<Win32.GUITHREADINFO>() };
        return Win32.GetGUIThreadInfo(tid, ref gti) && gti.hwndMoveSize != IntPtr.Zero;
    }

    private void TryJumpFileDialogRefreshPickerIfOpen(IntPtr dialogHwnd, List<FileJumpCandidate> full)
    {
        if (_activeFileJumpPicker == null) return;
        var root = Win32.GetAncestor(dialogHwnd, Win32.GA_ROOT);
        if (root == IntPtr.Zero || !ActivePickerMatchesDialog(root)) return;
        _activeFileJumpPicker.RefreshCandidatesFromExternal(full);
    }

    /// <summary>
    /// picker 打开时切换到外部文件管理器，触发一次采集以刷新 picker 列表（将新 Explorer 路径加入候选）。
    /// </summary>
    private void TryRefreshPickerForNewExternalFolder(IntPtr foregroundHwnd)
    {
        if (_activeFileJumpPicker == null) return;
        if (_appSettings == null) return;

        var dialogHwnd = _activeFileJumpPicker.OwnerDialogHwnd;
        if (dialogHwnd == IntPtr.Zero || !Win32.IsWindow(dialogHwnd)) return;

        var mem = _appSettings.LastFileDialogFolder?.Trim();
        var recentCapture = CopyRecentForJump(_appSettings);

        unchecked { _fileJumpAutoSyncCollectGen++; }
        var gen = _fileJumpAutoSyncCollectGen;
        var dialogCapture = dialogHwnd;

        var th = new Thread(() =>
        {
            List<FileJumpCandidate> candidates;
            try
            {
                candidates = FileManagerPathCollector.CollectCandidates(dialogCapture, mem,
                    recentFolders: recentCapture);
            }
            catch { return; }

            Dispatcher.BeginInvoke(() =>
            {
                if (gen != _fileJumpAutoSyncCollectGen) return;
                TryJumpFileDialogRefreshPickerIfOpen(dialogCapture, candidates);
            }, DispatcherPriority.Normal);
        })
        {
            IsBackground = true,
            Name = "ClipboardX-FileJump-RefreshOnExternal",
        };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();

        // 启动轮询：检测 Explorer 窗口内导航导致的路径变化
        StartExplorerPathPoll(foregroundHwnd);
    }

    /// <summary>Picker 打开时，定时轮询指定 Explorer 窗口的路径变化并刷新列表。</summary>
    private void StartExplorerPathPoll(IntPtr explorerHwnd)
    {
        StopExplorerPathPoll();
        if (explorerHwnd == IntPtr.Zero || !Win32.IsWindow(explorerHwnd)) return;
        if (_activeFileJumpPicker == null) return;

        _explorerPathPollHwnd = explorerHwnd;
        _explorerPathPollLastPath = FileManagerPathCollector.TryGetFolderForWindow(explorerHwnd, fresh: true) ?? "";

        _explorerPathPollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _explorerPathPollTimer.Tick += ExplorerPathPollTick;
        _explorerPathPollTimer.Start();
    }

    private void StopExplorerPathPoll()
    {
        if (_explorerPathPollTimer != null)
        {
            _explorerPathPollTimer.Stop();
            _explorerPathPollTimer.Tick -= ExplorerPathPollTick;
            _explorerPathPollTimer = null;
        }
        _explorerPathPollLastPath = "";
        _explorerPathPollHwnd = IntPtr.Zero;
    }

    private void ExplorerPathPollTick(object? sender, EventArgs e)
    {
        if (_activeFileJumpPicker == null || _explorerPathPollHwnd == IntPtr.Zero)
        {
            StopExplorerPathPoll();
            return;
        }
        if (!Win32.IsWindow(_explorerPathPollHwnd))
        {
            StopExplorerPathPoll();
            return;
        }
        // 前台已不在该 Explorer 窗口，停止轮询
        var fg = Win32.GetForegroundWindow();
        if (fg != _explorerPathPollHwnd)
        {
            StopExplorerPathPoll();
            return;
        }

        var currentPath = FileManagerPathCollector.TryGetFolderForWindow(_explorerPathPollHwnd, fresh: true) ?? "";
        if (string.Equals(currentPath, _explorerPathPollLastPath, StringComparison.OrdinalIgnoreCase))
            return;

        // 路径变化了，刷新 picker 列表
        _explorerPathPollLastPath = currentPath;
        TryRefreshPickerForNewExternalFolder(_explorerPathPollHwnd);
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
        var delayMs = Math.Clamp(_appSettings.FileJumpPickerShowDelayMs, 0, 10000);
        // 打开/保存窗到前台时，系统常在极短时间内多次触发；延时为 0 时仍用约一帧的防抖合并，避免并行多个 STA 采集线程。
        // 原先固定 80ms 会明显拖慢「立即弹出」的体感。
        var effectiveMs = delayMs <= 0 ? 16 : delayMs;

        _fileJumpAutoOpenDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(effectiveMs) };
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

    private void ScheduleAutoOpenFileJumpPickerRetry(IntPtr dialogHwnd, IntPtr dialogRoot)
    {
        if (dialogHwnd == IntPtr.Zero || dialogRoot == IntPtr.Zero) return;
        if (_fileJumpAutoOpenRetryRoot != dialogRoot)
        {
            _fileJumpAutoOpenRetryRoot = dialogRoot;
            _fileJumpAutoOpenRetryCount = 0;
        }
        if (_fileJumpAutoOpenRetryCount >= 2) return;

        _fileJumpAutoOpenRetryCount++;
        _fileJumpAutoOpenDebounceTimer?.Stop();
        _fileJumpAutoOpenDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_fileJumpAutoOpenRetryCount == 1 ? 160 : 260)
        };
        var capturedDialog = dialogHwnd;
        _fileJumpAutoOpenDebounceTimer.Tick += (_, _) =>
        {
            _fileJumpAutoOpenDebounceTimer?.Stop();
            _fileJumpAutoOpenDebounceTimer = null;
            TryAutoOpenFileJumpPickerWhenDialogForegroundAfterDebounce(capturedDialog);
        };
        _fileJumpAutoOpenDebounceTimer.Start();
    }

    /// <summary>
    /// 对话框成为前台并经过短延时后：按设置自动弹出跳转列表或直跳最优路径（与 FileJumpPickerAutoPopup 一致，含仅 1 条候选）。
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
        if (IsFileJumpNavigationSuppressed(dialogHwnd)) return;

        var dialogRoot = Win32.GetAncestor(dialogHwnd, Win32.GA_ROOT);
        if (dialogRoot == IntPtr.Zero) return;

        if (!IsForegroundFocusOnFileDialogRoot(dialogRoot))
            return;

        if (_fileJumpAutoOpenRetryRoot != dialogRoot)
        {
            _fileJumpAutoOpenRetryRoot = dialogRoot;
            _fileJumpAutoOpenRetryCount = 0;
        }

        if (dialogRoot == _fileJumpAutoOpenPickerDoneRoot)
            return;

        var mem = _appSettings.LastFileDialogFolder?.Trim();
        var allowShellInject = _appSettings.EnableShellNavigateInject;

        unchecked { _fileJumpAutoForegroundCollectGen++; }
        var gen = _fileJumpAutoForegroundCollectGen;
        var dialogHwndCapture = dialogHwnd;
        var dialogRootCapture = dialogRoot;
        var memCapture = mem;
        var recentCapture = CopyRecentForJump(_appSettings);
        var allowCapture = allowShellInject;

        void StaCollect()
        {
            if (gen != _fileJumpAutoForegroundCollectGen) return;
            List<FileJumpCandidate> quick;
            try
            {
                quick = FileManagerPathCollector.CollectCandidates(dialogHwndCapture, memCapture,
                    skipAlternateUiAutomation: true, stopAfterCandidateCount: 2,
                    shouldAbort: () => gen != _fileJumpAutoForegroundCollectGen,
                    recentFolders: recentCapture);
            }
            catch (Exception ex)
            {
                ShellNavigateLog.Write("filejump", "CollectCandidates quick (auto fg): " + ex);
                quick = new List<FileJumpCandidate>();
            }

            if (gen != _fileJumpAutoForegroundCollectGen) return;

            if (quick.Count >= 2)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (gen != _fileJumpAutoForegroundCollectGen) return;
                    TryAutoOpenFileJumpPickerAfterCollect(
                        dialogHwndCapture,
                        dialogRootCapture,
                        quick,
                        allowCapture,
                        afterPickerAssigned: () =>
                            StartFullCollectForAutoForeground(dialogHwndCapture, memCapture, recentCapture, gen));
                }, DispatcherPriority.Input);
                return;
            }

            if (gen != _fileJumpAutoForegroundCollectGen) return;

            List<FileJumpCandidate> candidates;
            try
            {
                candidates = FileManagerPathCollector.CollectCandidates(dialogHwndCapture, memCapture,
                    shouldAbort: () => gen != _fileJumpAutoForegroundCollectGen,
                    recentFolders: recentCapture);
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
        bool allowShellInject,
        Action? afterPickerAssigned = null)
    {
        if (_appSettings == null || !_appSettings.FileJumpPickerOpenWhenDialogForeground) return;
        if (dialogHwnd == IntPtr.Zero || !Win32.IsWindow(dialogHwnd)) return;

        var dialogRootNow = Win32.GetAncestor(dialogHwnd, Win32.GA_ROOT);
        if (dialogRootNow == IntPtr.Zero || dialogRootNow != dialogRoot) return;

        // 采集在线程里异步完成；列表窗可能已通过 BeginInvoke 打开：避免再次直跳/调度造成 WPS Qt 路径重复导航
        if (_activeFileJumpPicker != null) return;
        if (_fileJumpPickerOpenInProgress) return;

        if (!IsForegroundFocusOnFileDialogRoot(dialogRootNow)) return;
        if (IsFileJumpNavigationSuppressed(dialogHwnd)) return;

        if (dialogRootNow == _fileJumpAutoOpenPickerDoneRoot) return;

        if (candidates.Count == 0)
        {
            ScheduleAutoOpenFileJumpPickerRetry(dialogHwnd, dialogRootNow);
            return;
        }

        _fileJumpAutoOpenRetryRoot = IntPtr.Zero;
        _fileJumpAutoOpenRetryCount = 0;
        var prefer = PreferCandidateIndex(dialogHwnd, candidates);
        _fileJumpAutoOpenPickerDoneRoot = dialogRootNow;

        // A 方案：「自动跳转到最佳路径」与「自动弹出列表」可叠加 ——
        // 弹出列表的同时立刻直跳首条，用户能在列表里再换。
        var autoNavigate = _appSettings.FileJumpAutoOnFirstClick;
        if (autoNavigate)
        {
            NavigateToFolderInBackground(dialogHwnd, candidates[prefer].Path, allowShellInject);
            _fileJumpAutoFirstJumpDoneRoot = dialogRootNow;
            DisarmFileJumpClickToNavigate();
        }

        ScheduleFileJumpPickerOpen(dialogHwnd, candidates.ToList(), prefer, armHotkeyDoubleTap: false, allowShellInject,
            autoForegroundStickyMode: true, afterPickerAssigned);
    }

    /// <summary>
    /// 「自动跳转到最佳路径」开 + 「自动弹列表」关：对话框到前台后采集候选并直跳首条，不弹列表。
    /// 同一对话框 root 仅成功一次。配合 <see cref="UpdateFileJumpClickToNavigateArm"/> 的鼠标钩兜底。
    /// </summary>
    private void TryAutoNavigateBestPathWhenDialogForeground(IntPtr foregroundHwnd)
    {
        if (_appSettings == null) return;
        if (_appSettings.FileJumpPickerOpenWhenDialogForeground) return; // 该路径仅用于纯直跳
        if (!_appSettings.FileJumpAutoOnFirstClick) return;
        if (foregroundHwnd == IntPtr.Zero) return;

        if (_fileJumpAutoFirstJumpDoneRoot != IntPtr.Zero
            && !Win32.IsWindow(_fileJumpAutoFirstJumpDoneRoot))
            _fileJumpAutoFirstJumpDoneRoot = IntPtr.Zero;

        var fgNow = Win32.GetForegroundWindow();
        var dialogHwnd = ResolveFileJumpTargetHwndInternal(fgNow);
        if (dialogHwnd == IntPtr.Zero) return;
        if (IsFileJumpNavigationSuppressed(dialogHwnd)) return;

        var dialogRoot = Win32.GetAncestor(dialogHwnd, Win32.GA_ROOT);
        if (dialogRoot == IntPtr.Zero) return;
        if (dialogRoot == _fileJumpAutoFirstJumpDoneRoot) return;
        if (!IsForegroundFocusOnFileDialogRoot(dialogRoot)) return;

        var mem = _appSettings.LastFileDialogFolder?.Trim();
        var allowShellInject = _appSettings.EnableShellNavigateInject;
        var recentCapture = CopyRecentForJump(_appSettings);
        var dialogHwndCapture = dialogHwnd;
        var dialogRootCapture = dialogRoot;

        void StaCollect()
        {
            List<FileJumpCandidate> candidates;
            try
            {
                candidates = FileManagerPathCollector.CollectCandidates(dialogHwndCapture, mem,
                    recentFolders: recentCapture);
            }
            catch (Exception ex)
            {
                ShellNavigateLog.Write("filejump", "CollectCandidates (auto navigate fg): " + ex);
                candidates = new List<FileJumpCandidate>();
            }

            if (candidates.Count == 0) return;

            Dispatcher.BeginInvoke(() =>
            {
                if (_appSettings == null) return;
                if (_appSettings.FileJumpPickerOpenWhenDialogForeground) return;
                if (!_appSettings.FileJumpAutoOnFirstClick) return;
                if (!Win32.IsWindow(dialogHwndCapture)) return;
                var rootNow = Win32.GetAncestor(dialogHwndCapture, Win32.GA_ROOT);
                if (rootNow == IntPtr.Zero || rootNow != dialogRootCapture) return;
                if (rootNow == _fileJumpAutoFirstJumpDoneRoot) return;
                if (!IsForegroundFocusOnFileDialogRoot(rootNow)) return;

                var prefer = PreferCandidateIndex(dialogHwndCapture, candidates);
                var capturedRoot = rootNow;
                NavigateToFolderInBackground(dialogHwndCapture, candidates[prefer].Path, allowShellInject,
                    ok =>
                    {
                        if (ok)
                        {
                            _fileJumpAutoFirstJumpDoneRoot = capturedRoot;
                            DisarmFileJumpClickToNavigate();
                        }
                    });
            }, DispatcherPriority.Normal);
        }

        var th = new Thread(StaCollect)
        {
            IsBackground = true,
            Name = "ClipboardX-FileJump-AutoNav-Collect",
        };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
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
        if (IsFileJumpNavigationSuppressed(dialogHwnd)) return;

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

        // 直接读取前一个窗口的最新路径，而非使用可能过时的 _lastExternalFolder
        var preferredExternalFolder = "";
        if (preferLastExternal && previousForegroundHwnd != IntPtr.Zero)
        {
            var directPath = FileManagerPathCollector.TryGetFolderForWindow(previousForegroundHwnd, fresh: true);
            if (!string.IsNullOrEmpty(directPath) && Directory.Exists(directPath))
                preferredExternalFolder = directPath.Trim();
            else if (!string.IsNullOrEmpty(_lastExternalFolder))
                preferredExternalFolder = _lastExternalFolder.Trim();
        }

        var mem = !string.IsNullOrEmpty(preferredExternalFolder)
            ? preferredExternalFolder
            : _appSettings.LastFileDialogFolder?.Trim();

        var recentCapture = CopyRecentForJump(_appSettings);

        unchecked { _fileJumpAutoSyncCollectGen++; }
        var gen = _fileJumpAutoSyncCollectGen;
        var dialogCapture = dialogHwnd;
        var dialogRootCapture = dialogRoot;

        void StaCollect()
        {
            List<FileJumpCandidate> candidates;
            try
            {
                candidates = FileManagerPathCollector.CollectCandidates(dialogCapture, mem,
                    recentFolders: recentCapture);
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

                // DLL 注入读取路径较慢，放到后台线程避免阻塞 UI
                var capturedDialog = dialogCapture;
                var capturedRoot = dialogRootCapture;
                var capturedPreferred = preferredPath;
                var capturedAllowInject = allowShellInject;
                var thRead = new Thread(() =>
                {
                    string? currentFolder = null;
                    if (FileDialogJumpHelper.TryReadCurrentFolder(capturedDialog, out var currentFolderRead)
                        && !string.IsNullOrEmpty(currentFolderRead))
                    {
                        currentFolder = currentFolderRead;
                        var norm1 = NormalizeFolderPathForCompare(currentFolderRead);
                        var norm2 = NormalizeFolderPathForCompare(capturedPreferred);
                        if (string.Equals(norm1, norm2, StringComparison.OrdinalIgnoreCase))
                            return;
                    }

                    Dispatcher.BeginInvoke(() =>
                    {
                        if (IsFileJumpNavigationSuppressed(capturedDialog, capturedPreferred))
                            return;
                        if (TryNavigateViaActivePicker(capturedDialog, capturedRoot, capturedPreferred))
                            return;
                        ShellNavigateLog.Write("filejump",
                            $"auto-sync navigating from \"{currentFolder ?? "(unreadable)"}\" to \"{capturedPreferred}\"");
                        NavigateToFolderInBackground(capturedDialog, capturedPreferred, capturedAllowInject);
                    }, DispatcherPriority.Normal);
                }) { IsBackground = true, Name = "ClipboardX-AutoSyncRead" };
                thRead.Start();
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

    private void MarkFileJumpNavigationSuppressed(IntPtr dialogHwnd, string path)
    {
        var dialogRoot = dialogHwnd != IntPtr.Zero && Win32.IsWindow(dialogHwnd)
            ? Win32.GetAncestor(dialogHwnd, Win32.GA_ROOT)
            : IntPtr.Zero;
        _fileJumpNavigationSuppressRoot = dialogRoot;
        _fileJumpNavigationSuppressPath = NormalizeFolderPathForCompare(path);
        _fileJumpNavigationSuppressUntilTick = Environment.TickCount64 + 1500;
    }

    private bool IsFileJumpNavigationSuppressed(IntPtr dialogHwnd, string? path = null)
    {
        if (_fileJumpNavigationSuppressRoot == IntPtr.Zero)
            return false;
        if (_fileJumpNavigationSuppressUntilTick != 0
            && Environment.TickCount64 > _fileJumpNavigationSuppressUntilTick)
        {
            _fileJumpNavigationSuppressRoot = IntPtr.Zero;
            _fileJumpNavigationSuppressPath = "";
            _fileJumpNavigationSuppressUntilTick = 0;
            return false;
        }
        if (dialogHwnd == IntPtr.Zero || !Win32.IsWindow(dialogHwnd))
            return false;

        var dialogRoot = Win32.GetAncestor(dialogHwnd, Win32.GA_ROOT);
        if (dialogRoot == IntPtr.Zero || dialogRoot != _fileJumpNavigationSuppressRoot)
            return false;

        var normalizedPath = NormalizeFolderPathForCompare(path);
        return string.IsNullOrEmpty(normalizedPath)
            || string.IsNullOrEmpty(_fileJumpNavigationSuppressPath)
            || string.Equals(normalizedPath, _fileJumpNavigationSuppressPath, StringComparison.OrdinalIgnoreCase);
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
        bool autoForegroundStickyMode,
        Action? afterPickerAssigned = null)
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
                var shown = false;
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
                        StopExplorerPathPoll();
                        ClearFileJumpDoubleTapState();
                    };
                    // ShowDialog 会开启嵌套消息循环；跳转窗本身已经通过全局钩子/Closed 维护生命周期，
                    // 使用 modeless Show 避免主 UI Dispatcher 被模态循环长期占住，减轻拖动和关闭延迟。
                    picker.Show();
                    shown = true;
                    if (afterPickerAssigned != null)
                    {
                        var pickerCapture = picker;
                        DispatcherTimer? fullCollectDelay = null;
                        fullCollectDelay = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(900)
                        };
                        fullCollectDelay.Tick += (_, _) =>
                        {
                            fullCollectDelay?.Stop();
                            fullCollectDelay = null;
                            if (session != _fileJumpPickerSession) return;
                            if (!ReferenceEquals(_activeFileJumpPicker, pickerCapture)) return;
                            if (!pickerCapture.IsLoaded) return;

                            try
                            {
                                afterPickerAssigned.Invoke();
                            }
                            catch (Exception ex)
                            {
                                ShellNavigateLog.Write("filejump", "afterPickerAssigned delayed: " + ex);
                            }
                        };
                        fullCollectDelay.Start();
                    }
                }
                catch (Exception ex)
                {
                    ShellNavigateLog.Write("filejump", "show jump picker: " + ex);
                    if (picker != null && ReferenceEquals(_activeFileJumpPicker, picker))
                        _activeFileJumpPicker = null;
                    StopExplorerPathPoll();
                }
                finally
                {
                    if (!shown && picker != null && ReferenceEquals(_activeFileJumpPicker, picker))
                    {
                        _activeFileJumpPicker = null;
                        StopExplorerPathPoll();
                    }
                    _fileJumpPickerOpenInProgress = false;
                }
            }, DispatcherPriority.Input);
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

        // 仅当前台就是上次对话框时才复用，避免后台残留对话框导致 Ctrl+G 误导航。
        if (_fileJumpLastDialogHwnd != IntPtr.Zero
            && _fileJumpLastDialogHwnd == fgNow
            && Win32.IsWindow(_fileJumpLastDialogHwnd)
            && FileDialogJumpHelper.ClassifyFileDialog(_fileJumpLastDialogHwnd) != FileDialogKind.None)
            return _fileJumpLastDialogHwnd;

        if (_fileJumpLastDialogHwnd != IntPtr.Zero
            && _fileJumpLastDialogHwnd == fgNow
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

        // 「对话框打开时自动弹出列表」开启时，前台事件已经会触发自动弹列表/直跳，不需要再装低级鼠标钩兜底；
        // 装钩会让全局鼠标消息绕一次 UI 线程，体感卡顿。仅在纯「自动跳转到最佳路径」+ 弹列表关闭时才需要兜底。
        if (_appSettings.FileJumpPickerOpenWhenDialogForeground)
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
        if (IsFileJumpNavigationSuppressed(dialogHwnd))
        {
            DisarmFileJumpClickToNavigate();
            return;
        }
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
            var recentCapture = CopyRecentForJump(_appSettings);
            var candidates = FileManagerPathCollector.CollectCandidates(dlg, mem,
                recentFolders: recentCapture);
            if (candidates.Count == 0) return;

            var doneRoot = Win32.GetAncestor(dlg, Win32.GA_ROOT);
            DisarmFileJumpClickToNavigate();
            NavigateToFolderInBackground(dlg, candidates[0].Path, _appSettings.EnableShellNavigateInject,
                ok =>
                {
                    if (ok && doneRoot != IntPtr.Zero)
                        _fileJumpAutoFirstJumpDoneRoot = doneRoot;
                });
        }
        catch (Exception ex)
        {
            ShellNavigateLog.Write("filejump", "TryFileJumpAutoNavigateAfterClick: " + ex);
            DisarmFileJumpClickToNavigate();
        }
    }

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
            ("Shift+Enter", "单选图片时粘贴 OCR 识别文字（无文字则等同 Enter）"),
            ($"{m}+Tab", "仅看快捷短语（再按切换）"),
            ("Tab", "循环类型筛选"),
            ("a-z", "拼音搜索"),
        };
#if CLIPX_CLIPBOARD
        lines.Add(("Space/中键", "预览当前条目（多图条目预览时 ← → 切换图片）"));
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
            $"与 {m} 同时按下时（在设置中可更换面板主键）：")
            { Foreground = bodyBrush });
        ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.LineBreak());
        foreach (var (key, label) in new (string Key, string Label)[]
        {
            ("1～9", "粘贴列表第 1～9 条"),
            ("Tab", "仅看快捷短语开/关"),
        })
        {
            ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.Run($"{m}+{key}") { Foreground = hintBrush });
            ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.Run("　"));
            ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.Run(label) { Foreground = bodyBrush });
            ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.LineBreak());
        }

        string pageUpFull = AppSettings.FormatHotkey(_panelPageScrollUpModifiers, _panelPageScrollUpKey);
        string pageDnFull = AppSettings.FormatHotkey(_panelPageScrollDownModifiers, _panelPageScrollDownKey);
        ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.Run(pageUpFull) { Foreground = hintBrush });
        ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.Run("　"));
        ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.Run("列表向上翻页") { Foreground = bodyBrush });
        ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.LineBreak());
        ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.Run(pageDnFull) { Foreground = hintBrush });
        ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.Run("　"));
        ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.Run("列表向下翻页（可在设置中修改）") { Foreground = bodyBrush });
        ShortcutHelpFullText.Inlines.Add(new System.Windows.Documents.LineBreak());
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

    /// <summary>多图预览时当前查看的图片索引（0 起）；切换条目时重置。</summary>
    private int _previewImageFileIndex;
    /// <summary>当前预览气泡绑定的多图文件路径列表（仅 Files 多图条目有值）。</summary>
    private string[]? _previewImageFiles;
    /// <summary>当前预览气泡绑定的条目引用（用于检测切换条目并重置索引）。</summary>
    private ClipboardEntry? _previewImageFilesSource;

    private void CloseEntryPreviewBubble()
    {
        EntryPreviewPopup.IsOpen = false;
        _previewImageFiles = null;
        _previewImageFileIndex = 0;
        _previewImageFilesSource = null;
    }

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
        if (MainBorder.ActualWidth <= 0 || !IsVisible)
        {
            EntryPreviewPopup.PlacementTarget = MainBorder;
            EntryPreviewPopup.Placement = PlacementMode.Right;
            EntryPreviewPopup.HorizontalOffset = 10;
            EntryPreviewPopup.VerticalOffset = 0;
            return;
        }

        const double gap = 10;

        // 开始菜单/搜索等 Shell 前台：主面板固定在屏幕一侧，预览改到主窗口正下方，避免横向弹出压住开始菜单。
        if (IsShellForegroundWindow(Win32.GetForegroundWindow()))
        {
            EntryPreviewPopup.PlacementTarget = MainBorder;
            EntryPreviewPopup.Placement = PlacementMode.Bottom;
            EntryPreviewPopup.HorizontalOffset = 0;
            EntryPreviewPopup.VerticalOffset = gap;
            return;
        }

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

        // 相对选中行略偏上（数值可按观感微调）
        const double previewVerticalNudgeUp = 24;

        ListBoxItem? selectedRow = null;
        if (ItemsList.SelectedItem != null
            && ItemsList.ItemContainerGenerator.ContainerFromItem(ItemsList.SelectedItem) is ListBoxItem row
            && row.IsVisible)
            selectedRow = row;

        // 右侧有空间时：以选中行容器为锚点。Popup.Right 的目标原点为「行首右上角」，预览顶与行顶对齐；
        // 若仍用 MainBorder 顶边 + (itemTop - topLeft)，在列表中部选中时易与触边重算叠加，观感像对齐到行中部。
        if (placeRight && selectedRow != null)
        {
            EntryPreviewPopup.PlacementTarget = selectedRow;
            EntryPreviewPopup.Placement = PlacementMode.Right;
            EntryPreviewPopup.HorizontalOffset = gap;
            EntryPreviewPopup.VerticalOffset = -previewVerticalNudgeUp;
            return;
        }

        EntryPreviewPopup.PlacementTarget = MainBorder;
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
        if (selectedRow != null)
        {
            var itemTop = selectedRow.PointToScreen(new System.Windows.Point(0, 0));
            verticalOffset = itemTop.Y - topLeft.Y - previewVerticalNudgeUp;
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
        EntryPreviewImageMeta.Visibility = Visibility.Collapsed;
        EntryPreviewImageMeta.Text = "";
        EntryPreviewImageNav.Visibility = Visibility.Collapsed;
        EntryPreviewOcrSeparator.Visibility = Visibility.Collapsed;
        EntryPreviewOcrHeader.Visibility = Visibility.Collapsed;
        EntryPreviewOcrText.Visibility = Visibility.Collapsed;
        EntryPreviewOcrText.Text = "";

        // 切换条目时重置多图导航状态
        if (_previewImageFiles == null || entry == null || !ReferenceEquals(_previewImageFilesSource, entry))
        {
            _previewImageFiles = null;
            _previewImageFileIndex = 0;
            _previewImageFilesSource = entry;
        }

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
            var showedImage = false;
            if (entry.Type == EntryType.Image)
            {
                EntryPreviewImageMeta.Text = $"{entry.ImageWidth}×{entry.ImageHeight} 图片";
                EntryPreviewImageMeta.Visibility = Visibility.Visible;
            }
            else if (entry.IsMultiImageFiles)
            {
                // 多图：初始化图片路径列表与索引
                _previewImageFiles = entry.GetImageFilePaths();
                if (_previewImageFiles.Length > 0)
                {
                    if (_previewImageFileIndex >= _previewImageFiles.Length) _previewImageFileIndex = 0;
                    EntryPreviewImageMeta.Text = $"共 {entry.FilePaths!.Length} 个文件 · {_previewImageFiles.Length} 张图片";
                    EntryPreviewImageMeta.Visibility = Visibility.Visible;
                }
            }
            else if (entry.IsImageFile)
            {
                EntryPreviewImageMeta.Text = $"{entry.FilePaths!.Length} 个文件";
                EntryPreviewImageMeta.Visibility = Visibility.Visible;
            }

            var bmp = LoadEntryPreviewBitmap(entry, _previewImageFileIndex);
            if (bmp != null)
            {
                EntryPreviewImage.Source = bmp;
                EntryPreviewImage.Visibility = Visibility.Visible;
                showedImage = true;
            }

            // 多图导航条
            if (entry.IsMultiImageFiles && _previewImageFiles is { Length: > 1 })
            {
                EntryPreviewImgIndex.Text = $"{_previewImageFileIndex + 1} / {_previewImageFiles.Length}";
                EntryPreviewImageNav.Visibility = Visibility.Visible;
            }

            if (entry.Type == EntryType.Image)
            {
                if (entry.IsOcrPending)
                {
                    EntryPreviewOcrSeparator.Visibility = showedImage ? Visibility.Visible : Visibility.Collapsed;
                    EntryPreviewOcrHeader.Text = "识别文字";
                    EntryPreviewOcrHeader.Visibility = Visibility.Visible;
                    EntryPreviewOcrText.Text = "识别中，请稍候…";
                    EntryPreviewOcrText.Visibility = Visibility.Visible;
                    return;
                }

                if (entry.HasOcrPreviewBody)
                {
                    EntryPreviewOcrSeparator.Visibility = showedImage ? Visibility.Visible : Visibility.Collapsed;
                    EntryPreviewOcrHeader.Text = "识别文字";
                    EntryPreviewOcrHeader.Visibility = Visibility.Visible;
                    EntryPreviewOcrText.Text = entry.OcrPreviewBody;
                    EntryPreviewOcrText.Visibility = Visibility.Visible;
                    return;
                }
            }

            if (!showedImage)
            {
                EntryPreviewText.Text = entry.Type == EntryType.Image
                    ? "（无法解码该图片）"
                    : string.Join(Environment.NewLine, entry.FilePaths ?? Array.Empty<string>());
                EntryPreviewText.Visibility = Visibility.Visible;
            }
            return;
        }

        EntryPreviewText.Text = entry.FilePaths is { Length: > 0 }
            ? string.Join(Environment.NewLine, entry.FilePaths)
            : "（无路径）";
        EntryPreviewText.Visibility = Visibility.Visible;
    }

    private static BitmapSource? LoadEntryPreviewBitmap(ClipboardEntry entry, int imageFileIndex = 0)
    {
        try
        {
            if (entry.Type == EntryType.Image)
            {
                var bytes = entry.TryGetImageData();
                if (bytes is { Length: > 0 })
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
                return null;
            }

            if (entry.IsImageFile && entry.FilePaths!.Length > 0)
            {
                // 多图：按索引选择；单图或索引越界时回退到首张
                var paths = entry.GetImageFilePaths();
                string p = paths.Length > 0
                    ? paths[Math.Clamp(imageFileIndex, 0, paths.Length - 1)]
                    : entry.FilePaths[0];
                if (!File.Exists(p)) return null;
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

    /// <summary>多图预览：前进/后退一张（delta = ±1）。仅在多图 Files 条目预览时生效。</summary>
    private void NavigatePreviewImage(int delta)
    {
        if (!EntryPreviewPopup.IsOpen) return;
        if (_previewImageFiles is not { Length: > 1 }) return;
        var n = _previewImageFiles.Length;
        _previewImageFileIndex = ((_previewImageFileIndex + delta) % n + n) % n;
        if (ItemsList.SelectedItem is ClipboardEntry e)
            UpdateEntryPreviewBubbleContent(e);
        PositionEntryPreviewPopup();
    }

    private void EntryPreviewImgPrev_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        NavigatePreviewImage(-1);
    }

    private void EntryPreviewImgNext_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        NavigatePreviewImage(1);
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

        double newOffset = sv.VerticalOffset + direction * _pageSize * itemHeight;
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
    /// 使用 await Task.Delay 重试，避免 Thread.Sleep 卡死 UI；CANT_OPEN 时记录占用者与耗时，指数退避。
    /// </summary>
    private static int ComputeClipboardRetryDelayMs(int failedAttemptIndex, int baseDelayMs)
    {
        var mult = 1 << Math.Min(failedAttemptIndex, 3);
        return Math.Min(baseDelayMs * mult, 400);
    }

    private static async Task<bool> TrySetClipboardTextWithNativeFallbackAsync(
        string text,
        IntPtr hwnd,
        int maxRetries,
        int baseDelayMs,
        Func<bool>? canContinueBeforeEachAttempt = null)
    {
        for (var i = 0; i < maxRetries; i++)
        {
            if (canContinueBeforeEachAttempt != null && !canContinueBeforeEachAttempt())
            {
                ClipboardDiagnosticsLog.Write($"TrySetClipboardTextNative aborted coherence len={text.Length} attempt={i + 1}");
                return false;
            }

            var sw = Stopwatch.StartNew();
            if (Win32.TrySetClipboardTextNative(text, hwnd))
            {
                sw.Stop();
                if (i > 0)
                    ClipboardDiagnosticsLog.Write($"TrySetClipboardTextNative OK after retry i={i} len={text.Length} elapsedMs={sw.ElapsedMilliseconds}");
                return true;
            }
            sw.Stop();
            ClipboardDiagnosticsLog.Write(
                $"TrySetClipboardTextNative fail attempt={i + 1}/{maxRetries} len={text.Length} elapsedMs={sw.ElapsedMilliseconds} holder={Win32.DescribeClipboardHolder()}");

            if (i >= maxRetries - 1) break;

            if (canContinueBeforeEachAttempt != null && !canContinueBeforeEachAttempt())
            {
                ClipboardDiagnosticsLog.Write($"TrySetClipboardTextNative aborted coherence before retry delay len={text.Length}");
                return false;
            }

            if (hwnd != IntPtr.Zero && Win32.TryEmptyClipboardAfterOpen(hwnd))
                ClipboardDiagnosticsLog.Write($"TrySetClipboardTextNative reNudge afterAttempt={i + 1}");

            await Task.Delay(ComputeClipboardRetryDelayMs(i, baseDelayMs));
        }

        ClipboardDiagnosticsLog.Write(
            $"TrySetClipboardTextNative GAVE_UP len={text.Length} holder={Win32.DescribeClipboardHolder()} → fallback OLE SetText");
        return await TrySetClipboardAsync(
            () => System.Windows.Clipboard.SetText(text),
            $"SetText-OLE len={text.Length}",
            maxRetries: maxRetries,
            delayMs: baseDelayMs,
            clipNudgeHwnd: hwnd,
            canContinueBeforeEachAttempt: canContinueBeforeEachAttempt);
    }

    private static byte[]? CreateDibBytesFromBitmapSource(BitmapSource source)
    {
        try
        {
            var bs = source;
            if (bs.Format != PixelFormats.Bgr24)
            {
                var converted = new FormatConvertedBitmap(bs, PixelFormats.Bgr24, null, 0);
                converted.Freeze();
                bs = converted;
            }

            var width = bs.PixelWidth;
            var height = bs.PixelHeight;
            if (width <= 0 || height <= 0) return null;

            const int bpp = 3;
            var stride = (width * bpp + 3) & ~3;
            var pixels = new byte[stride * height];
            bs.CopyPixels(pixels, stride, 0);

            const int headerSize = 40;
            var dib = new byte[headerSize + stride * height];
            BitConverter.TryWriteBytes(dib.AsSpan(0, 4), headerSize);
            BitConverter.TryWriteBytes(dib.AsSpan(4, 4), width);
            BitConverter.TryWriteBytes(dib.AsSpan(8, 4), height);
            BitConverter.TryWriteBytes(dib.AsSpan(12, 2), (short)1);
            BitConverter.TryWriteBytes(dib.AsSpan(14, 2), (short)24);
            BitConverter.TryWriteBytes(dib.AsSpan(20, 4), stride * height);

            for (var y = 0; y < height; y++)
                Buffer.BlockCopy(pixels, y * stride, dib, headerSize + (height - 1 - y) * stride, stride);

            return dib;
        }
        catch (Exception ex)
        {
            ClipboardDiagnosticsLog.Write($"CreateDibBytesFromBitmapSource EX {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task<bool> TryNativeClipboardWithRetriesAsync(
        string tag,
        Func<bool> trySet,
        IntPtr hwnd,
        int maxRetries,
        int baseDelayMs,
        Func<bool>? canContinueBeforeEachAttempt = null)
    {
        for (var i = 0; i < maxRetries; i++)
        {
            if (canContinueBeforeEachAttempt != null && !canContinueBeforeEachAttempt())
            {
                ClipboardDiagnosticsLog.Write($"TryNativeClipboard aborted coherence tag={tag} attempt={i + 1}");
                return false;
            }

            var sw = Stopwatch.StartNew();
            if (trySet())
            {
                sw.Stop();
                if (i > 0)
                    ClipboardDiagnosticsLog.Write($"TryNativeClipboard OK after retry i={i} tag={tag} elapsedMs={sw.ElapsedMilliseconds}");
                return true;
            }
            sw.Stop();
            ClipboardDiagnosticsLog.Write(
                $"TryNativeClipboard fail attempt={i + 1}/{maxRetries} tag={tag} elapsedMs={sw.ElapsedMilliseconds} holder={Win32.DescribeClipboardHolder()}");

            if (i >= maxRetries - 1) break;

            if (canContinueBeforeEachAttempt != null && !canContinueBeforeEachAttempt())
            {
                ClipboardDiagnosticsLog.Write($"TryNativeClipboard aborted coherence before retry delay tag={tag}");
                return false;
            }

            if (hwnd != IntPtr.Zero && Win32.TryEmptyClipboardAfterOpen(hwnd))
                ClipboardDiagnosticsLog.Write($"TryNativeClipboard reNudge tag={tag} afterAttempt={i + 1}");

            await Task.Delay(ComputeClipboardRetryDelayMs(i, baseDelayMs));
        }
        return false;
    }

    private static async Task<bool> TrySetClipboardImageWithNativeFallbackAsync(
        BitmapSource bitmap,
        byte[]? pngFallbackBytes,
        IntPtr hwnd,
        int maxRetries,
        int baseDelayMs)
    {
        var dib = CreateDibBytesFromBitmapSource(bitmap);
        if (dib != null)
        {
            var ok = await TryNativeClipboardWithRetriesAsync(
                $"SetDib {bitmap.PixelWidth}x{bitmap.PixelHeight} bytes={dib.Length}",
                () => Win32.TrySetClipboardDibNative(dib, hwnd),
                hwnd,
                maxRetries,
                baseDelayMs);
            if (ok) return true;
            ClipboardDiagnosticsLog.Write(
                $"TrySetClipboardDibNative GAVE_UP holder={Win32.DescribeClipboardHolder()} → try native HDROP");
        }

        if (pngFallbackBytes is { Length: > 0 })
        {
            string? tmpPath = null;
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "ClipboardX");
                Directory.CreateDirectory(dir);
                tmpPath = Path.Combine(dir, $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}_fb.png");
                File.WriteAllBytes(tmpPath, pngFallbackBytes);

                var ok = await TryNativeClipboardWithRetriesAsync(
                    "SetFileDropListNative imageFallback",
                    () => Win32.TrySetClipboardFileDropListNative(new[] { tmpPath }, hwnd),
                    hwnd,
                    maxRetries,
                    baseDelayMs);
                if (ok)
                {
                    ClipboardDiagnosticsLog.Write($"paste image native HDROP ok \"{tmpPath}\"");
                    return true;
                }

                try { File.Delete(tmpPath); } catch { /* ignore */ }
                tmpPath = null;
            }
            catch (Exception ex)
            {
                ClipboardDiagnosticsLog.Write($"paste image native HDROP EX {ex.GetType().Name}: {ex.Message}");
                if (tmpPath != null) try { File.Delete(tmpPath); } catch { /* ignore */ }
            }

            ClipboardDiagnosticsLog.Write(
                $"TrySetClipboardFileDropListNative GAVE_UP holder={Win32.DescribeClipboardHolder()} → fallback OLE SetImage");
        }

        var oleImageOk = await TrySetClipboardAsync(
            () => System.Windows.Clipboard.SetImage(bitmap),
            $"SetImage-OLE {bitmap.PixelWidth}x{bitmap.PixelHeight}",
            maxRetries: maxRetries,
            delayMs: baseDelayMs,
            clipNudgeHwnd: hwnd);
        if (oleImageOk || pngFallbackBytes is not { Length: > 0 }) return oleImageOk;

        string? oleTmpPath = null;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "ClipboardX");
            Directory.CreateDirectory(dir);
            oleTmpPath = Path.Combine(dir, $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}_fb.png");
            File.WriteAllBytes(oleTmpPath, pngFallbackBytes);
            var flFb = new StringCollection();
            flFb.Add(oleTmpPath);
            var oleFbOk = await TrySetClipboardAsync(
                () => System.Windows.Clipboard.SetFileDropList(flFb),
                "SetFileDropList-OLE imageFallback",
                maxRetries: maxRetries,
                delayMs: baseDelayMs,
                clipNudgeHwnd: hwnd);
            if (!oleFbOk)
            {
                try { File.Delete(oleTmpPath); } catch { /* ignore */ }
            }
            else
                ClipboardDiagnosticsLog.Write($"paste image OLE HDROP fallback ok \"{oleTmpPath}\"");
            return oleFbOk;
        }
        catch (Exception ex)
        {
            ClipboardDiagnosticsLog.Write($"paste image OLE HDROP fallback EX {ex.GetType().Name}: {ex.Message}");
            if (oleTmpPath != null) try { File.Delete(oleTmpPath); } catch { /* ignore */ }
            return false;
        }
    }

    private static async Task<bool> TrySetClipboardFileDropListWithNativeFallbackAsync(
        IReadOnlyList<string> paths,
        string logTag,
        IntPtr hwnd,
        int maxRetries,
        int baseDelayMs,
        Func<bool>? canContinueBeforeEachAttempt = null)
    {
        var ok = await TryNativeClipboardWithRetriesAsync(
            $"SetFileDropListNative {logTag}",
            () => Win32.TrySetClipboardFileDropListNative(paths, hwnd),
            hwnd,
            maxRetries,
            baseDelayMs,
            canContinueBeforeEachAttempt);
        if (ok) return true;

        ClipboardDiagnosticsLog.Write(
            $"TrySetClipboardFileDropListNative GAVE_UP tag={logTag} holder={Win32.DescribeClipboardHolder()} → fallback OLE");
        var fl = new StringCollection();
        foreach (var p in paths) fl.Add(p);
        return await TrySetClipboardAsync(
            () => System.Windows.Clipboard.SetFileDropList(fl),
            $"SetFileDropList-OLE {logTag}",
            maxRetries: maxRetries,
            delayMs: baseDelayMs,
            clipNudgeHwnd: hwnd,
            canContinueBeforeEachAttempt: canContinueBeforeEachAttempt);
    }

    private static async Task<bool> TrySetClipboardAsync(
        Action setAction,
        string logOp,
        int maxRetries = 6,
        int delayMs = 60,
        IntPtr clipNudgeHwnd = default,
        Func<bool>? canContinueBeforeEachAttempt = null)
    {
        Exception? last = null;
        for (var i = 0; i < maxRetries; i++)
        {
            if (canContinueBeforeEachAttempt != null && !canContinueBeforeEachAttempt())
            {
                ClipboardDiagnosticsLog.Write($"TrySetClipboard aborted coherence op={logOp} attempt={i + 1}");
                return false;
            }
            var sw = Stopwatch.StartNew();
            try
            {
                Win32.CloseClipboard();
                setAction();
                sw.Stop();
                if (i > 0)
                    ClipboardDiagnosticsLog.Write($"TrySetClipboard OK after retry i={i} op={logOp} elapsedMs={sw.ElapsedMilliseconds}");
                return true;
            }
            catch (Exception ex)
            {
                sw.Stop();
                last = ex;
                var hr = ex is COMException com ? $" hr=0x{(uint)com.HResult:X8}" : "";
                var holder = IsClipboardCantOpen(ex) ? $" holder={Win32.DescribeClipboardHolder()}" : "";
                ClipboardDiagnosticsLog.Write(
                    $"TrySetClipboard fail attempt={i + 1}/{maxRetries} op={logOp} elapsedMs={sw.ElapsedMilliseconds}{holder} {ex.GetType().Name}: {ex.Message}{hr}");
                if (i >= maxRetries - 1) break;
                if (canContinueBeforeEachAttempt != null && !canContinueBeforeEachAttempt())
                {
                    ClipboardDiagnosticsLog.Write($"TrySetClipboard aborted coherence before retry delay op={logOp}");
                    return false;
                }
                if (IsClipboardCantOpen(ex) && clipNudgeHwnd != IntPtr.Zero && Win32.TryEmptyClipboardAfterOpen(clipNudgeHwnd))
                    ClipboardDiagnosticsLog.Write($"TrySetClipboard reNudge op={logOp} afterAttempt={i + 1}");
                await Task.Delay(ComputeClipboardRetryDelayMs(i, delayMs));
            }
        }
        var giveUpHolder = last != null && IsClipboardCantOpen(last) ? $" holder={Win32.DescribeClipboardHolder()}" : "";
        ClipboardDiagnosticsLog.Write($"TrySetClipboard GAVE_UP op={logOp}{giveUpHolder} last={last?.GetType().Name}: {last?.Message}");
        return false;
    }

    private async void PasteSelectedItem()
    {
        if (ItemsList.SelectedItem is not ClipboardEntry item) return;
        await PasteEntryAsync(item, hidePopupAfter: HidePopupAfterUserAction);
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
        // 单次粘贴成功后会改为 true，让 finally 跳过同步清标志，由后台定时器在回波窗口结束后清。
        bool deferPasteFlagToTimer = false;
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
            EntryType.Image => $"paste BEGIN Image pngBytes={item.TryGetImageData()?.Length ?? 0} target=0x{tgt:X}",
            EntryType.Files => $"paste BEGIN Files {SummarizeFileDropForLog(item.FilePaths ?? [])} target=0x{tgt:X}",
            _ => $"paste BEGIN type={item.Type} target=0x{tgt:X}"
        });

        // 在仍是弹窗前台时 OpenClipboard，常与目标进程（大段文本/富文本 OLE）争用 → 连续 CLIPBRD_E_CANT_OPEN。先 Hide + 强力切回目标并等待剪贴板释放。
        if (hidePopupAfter)
            HidePopup();
        if (_targetWindow != IntPtr.Zero)
            Win32.SetForegroundWindowAggressive(_targetWindow);
        var textLen = item.TextContent?.Length ?? 0;
        var imgBytes = (item.Type == EntryType.Image ? item.TryGetImageData()?.Length : null) ?? 0;
        var imgPixels = item.Type == EntryType.Image ? item.ImageWidth * item.ImageHeight : 0;
        var hugeClipboardImage = item.Type == EntryType.Image && (imgBytes > 900_000 || imgPixels > 1_200_000);
        var noSegmentDelays = sequentialSegmentIndex >= 0;
        if (!noSegmentDelays)
        {
            // 焦点切回后等待目标进程消息泵处理一轮；以前的较大值是为兼容个别拖慢的 OLE 富文本场景，
            // 实测 99% 的小文本场景下根本无需等待，零延时即可成功；只在大文本/图片上保留延长。
            // 早期还多做了一次 Dispatcher.InvokeAsync(Background) 让步，但 await Task.Delay 本身已经让步过，
            // 再额外排队一次到 UI 队列只会让点击响应更晚一帧（~16ms 起），故移除。
            var focusSettleMs = item.Type switch
            {
                EntryType.Text => textLen > 12000 ? 100 : textLen > 4000 ? 50 : 0,
                EntryType.Image => hugeClipboardImage ? 160 : 90,
                _ => 15
            };
            if (focusSettleMs > 0)
                await Task.Delay(focusSettleMs);
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
        var clipRetries = noSegmentDelays ? 8 : 6;
        var clipRetryDelayMs = 60;
        try
        {
            switch (item.Type)
            {
                case EntryType.Text:
                {
                    var clipText = sendNewlineAfterTextWhenCtrlEnterBatch && noSegmentDelays
                        ? item.TextContent! + Environment.NewLine
                        : item.TextContent!;
                    clipboardOk = await TrySetClipboardTextWithNativeFallbackAsync(
                        clipText,
                        _hwnd,
                        maxRetries: clipRetries,
                        baseDelayMs: clipRetryDelayMs);
                    break;
                }
                case EntryType.Image:
                    // BitmapDecoder+Frame 在 using 结束后会释放流，而 SetImage 对 OLE 常延迟落盘，目标程序 Ctrl+V 拿到空/坏图。
                    // BitmapImage + OnLoad 在 EndInit 时把像素读入内存，Freeze 后可安全关闭流再写剪贴板。
                    BitmapImage? bi = null;
                    var swDec = Stopwatch.StartNew();
                    var pasteImgData = item.TryGetImageData();
                    if (pasteImgData is not { Length: > 0 })
                    {
                        ClipboardDiagnosticsLog.Write("paste image: no image data available");
                        break;
                    }
                    using (var ms = new MemoryStream(pasteImgData))
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
                        $"paste image loadMs={swDec.ElapsedMilliseconds} frame={bi.PixelWidth}x{bi.PixelHeight} storedPng={pasteImgData.Length}");
                    clipboardOk = await TrySetClipboardImageWithNativeFallbackAsync(
                        bi,
                        pasteImgData,
                        _hwnd,
                        maxRetries: clipRetries,
                        baseDelayMs: clipRetryDelayMs);
                    break;
                case EntryType.Files:
                    clipboardOk = await TrySetClipboardFileDropListWithNativeFallbackAsync(
                        item.FilePaths!,
                        $"count={item.FilePaths!.Length} {SummarizeFileDropForLog(item.FilePaths!)}",
                        _hwnd,
                        maxRetries: clipRetries,
                        baseDelayMs: clipRetryDelayMs);
                    break;
            }
        }
        catch (Exception ex)
        {
            ClipboardDiagnosticsLog.Write($"paste unexpected before/during set {ex.GetType().Name}: {ex.Message}");
        }

        ClipboardDiagnosticsLog.Write($"paste END clipboardOk={clipboardOk} willSendPaste={clipboardOk}");

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
                // 写完剪贴板到发送 Ctrl+V 之间，只有 OLE 图像需要等一拍让 IDataObject 真正落盘；
                // 文本/文件路径下立即 SendInput 即可，避免「按下到看见粘贴出来」之间多出一帧延迟。
                var prePasteDelayMs = item.Type == EntryType.Image ? 50 : 0;
                if (prePasteDelayMs > 0)
                    await Task.Delay(prePasteDelayMs);
            }

            SendPasteToTarget();

            // 连续粘贴：整轮结束后再 TailSettle；段间另有 SequentialInterSegmentDelayMs。
            // 单次粘贴：以前同步 await 600ms 回波窗口，导致「按完按钮要等一下」的卡顿；
            // 改为后台定时器在 _pasteInProgress 上挡回波，方法立即返回不阻塞调用方。
            if (!noSegmentDelays)
            {
                const int postEchoMs = 600;
                deferPasteFlagToTimer = true;
                _ = Task.Delay(postEchoMs).ContinueWith(_ =>
                {
                    if (!_sequentialPasteHold) _pasteInProgress = false;
                    ClipboardDiagnosticsLog.Write($"paste post-echo suppression window elapsed (ms={postEchoMs})");
                }, TaskScheduler.Default);
            }
        }
        }
        finally
        {
            if (!_sequentialPasteHold && !deferPasteFlagToTimer)
                _pasteInProgress = false;
        }
    }

    /// <summary>按设置向目标窗口发送粘贴（Ctrl+V 或 Shift+Insert）。</summary>
    private void SendPasteToTarget()
    {
        var mode = PasteSimulationModes.Normalize(_appSettings?.PasteSimulationMode);
        // 命令行/终端对 Ctrl+V 支持不一，检测到则临时改用 Shift+Insert（不改保存的设置）。
        if (mode == PasteSimulationModes.CtrlV && PasteTargetHeuristics.IsLikelyConsoleOrTerminal(_targetWindow))
            mode = PasteSimulationModes.ShiftInsert;

        if (mode == PasteSimulationModes.ShiftInsert)
            SendShiftInsertPaste();
        else
            SendCtrlVPaste();
    }

    // Shift+Insert 是系统级粘贴，但 Excel 对模拟输入更挑：
    // 1) Insert 必须按扩展键发送；
    // 2) 若呼出面板时 Ctrl/Alt/Win 仍处于按下态，最终组合键会被污染。
    // 因此这里在同一批 SendInput 中先释放当前真实按下的修饰键，再发送标准的 Shift+Insert。
    private static void SendShiftInsertPaste()
    {
        var ctrlHeld = (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0;
        var altHeld = (Win32.GetAsyncKeyState(Win32.VK_MENU) & 0x8000) != 0;
        var lWinHeld = (Win32.GetAsyncKeyState(Win32.VK_LWIN) & 0x8000) != 0;
        var rWinHeld = (Win32.GetAsyncKeyState(Win32.VK_RWIN) & 0x8000) != 0;

        var releaseCount = (ctrlHeld ? 1 : 0) + (altHeld ? 1 : 0) + (lWinHeld ? 1 : 0) + (rWinHeld ? 1 : 0);
        if (releaseCount > 0)
        {
            var release = new Win32.INPUT[releaseCount];
            var r = 0;
            if (ctrlHeld) { release[r].type = Win32.INPUT_KEYBOARD; release[r].u.ki.wVk = Win32.VK_CONTROL; release[r].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP; r++; }
            if (altHeld) { release[r].type = Win32.INPUT_KEYBOARD; release[r].u.ki.wVk = Win32.VK_MENU; release[r].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP; r++; }
            if (lWinHeld) { release[r].type = Win32.INPUT_KEYBOARD; release[r].u.ki.wVk = Win32.VK_LWIN; release[r].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP; r++; }
            if (rWinHeld) { release[r].type = Win32.INPUT_KEYBOARD; release[r].u.ki.wVk = Win32.VK_RWIN; release[r].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP; r++; }
            Win32.SendInput((uint)release.Length, release, Marshal.SizeOf<Win32.INPUT>());
            Thread.Sleep(1);
        }

        var combo = new Win32.INPUT[4];
        combo[0].type = Win32.INPUT_KEYBOARD; combo[0].u.ki.wVk = Win32.VK_SHIFT;
        combo[1].type = Win32.INPUT_KEYBOARD; combo[1].u.ki.wVk = Win32.VK_INSERT; combo[1].u.ki.dwFlags = Win32.KEYEVENTF_EXTENDEDKEY;
        combo[2].type = Win32.INPUT_KEYBOARD; combo[2].u.ki.wVk = Win32.VK_INSERT; combo[2].u.ki.dwFlags = Win32.KEYEVENTF_EXTENDEDKEY | Win32.KEYEVENTF_KEYUP;
        combo[3].type = Win32.INPUT_KEYBOARD; combo[3].u.ki.wVk = Win32.VK_SHIFT; combo[3].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        Win32.SendInput((uint)combo.Length, combo, Marshal.SizeOf<Win32.INPUT>());
    }

    /// <summary>先释放可能干扰的修饰键，再发送 Ctrl+V（避免呼出面板时 Shift 等仍按下导致「无格式粘贴」等）。
    /// 两次 SendInput 拆分提交：第一次释放真实按下的修饰键，让目标处理一帧；第二次再发 Ctrl+V。
    /// 历史上整批一次 SendInput 偶发出现目标先看到 V Down 再看到 Ctrl Down，进而把 V 当成普通字符输入（用户看到「v」字符）。</summary>
    private static void SendCtrlVPaste()
    {
        var lShiftHeld = (Win32.GetAsyncKeyState(Win32.VK_LSHIFT) & 0x8000) != 0;
        var rShiftHeld = (Win32.GetAsyncKeyState(Win32.VK_RSHIFT) & 0x8000) != 0;
        var ctrlHeld = (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0;
        var altHeld = (Win32.GetAsyncKeyState(Win32.VK_MENU) & 0x8000) != 0;
        var lWinHeld = (Win32.GetAsyncKeyState(Win32.VK_LWIN) & 0x8000) != 0;
        var rWinHeld = (Win32.GetAsyncKeyState(Win32.VK_RWIN) & 0x8000) != 0;

        var releaseCount = (lShiftHeld ? 1 : 0) + (rShiftHeld ? 1 : 0) + (ctrlHeld ? 1 : 0)
                           + (altHeld ? 1 : 0) + (lWinHeld ? 1 : 0) + (rWinHeld ? 1 : 0);
        if (releaseCount > 0)
        {
            var release = new Win32.INPUT[releaseCount];
            var r = 0;
            if (lShiftHeld) { release[r].type = Win32.INPUT_KEYBOARD; release[r].u.ki.wVk = Win32.VK_LSHIFT; release[r].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP; r++; }
            if (rShiftHeld) { release[r].type = Win32.INPUT_KEYBOARD; release[r].u.ki.wVk = Win32.VK_RSHIFT; release[r].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP; r++; }
            if (ctrlHeld) { release[r].type = Win32.INPUT_KEYBOARD; release[r].u.ki.wVk = Win32.VK_CONTROL; release[r].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP; r++; }
            if (altHeld) { release[r].type = Win32.INPUT_KEYBOARD; release[r].u.ki.wVk = Win32.VK_MENU; release[r].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP; r++; }
            if (lWinHeld) { release[r].type = Win32.INPUT_KEYBOARD; release[r].u.ki.wVk = Win32.VK_LWIN; release[r].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP; r++; }
            if (rWinHeld) { release[r].type = Win32.INPUT_KEYBOARD; release[r].u.ki.wVk = Win32.VK_RWIN; release[r].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP; r++; }
            Win32.SendInput((uint)release.Length, release, Marshal.SizeOf<Win32.INPUT>());
            // 让目标线程处理一帧释放事件，再注入组合键。极短让步（Sleep 0 即可触发线程切换）。
            Thread.Sleep(1);
        }

        var combo = new Win32.INPUT[4];
        combo[0].type = Win32.INPUT_KEYBOARD; combo[0].u.ki.wVk = Win32.VK_CONTROL;
        combo[1].type = Win32.INPUT_KEYBOARD; combo[1].u.ki.wVk = Win32.VK_V;
        combo[2].type = Win32.INPUT_KEYBOARD; combo[2].u.ki.wVk = Win32.VK_V; combo[2].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        combo[3].type = Win32.INPUT_KEYBOARD; combo[3].u.ki.wVk = Win32.VK_CONTROL; combo[3].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
        Win32.SendInput((uint)combo.Length, combo, Marshal.SizeOf<Win32.INPUT>());
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
    /// 返回 true 表示成功且调用方应推迟清 <see cref="_pasteInProgress"/>（后台回波窗口）。
    /// </summary>
    private async Task<bool> CompletePasteTempFileToExplorerAsync(string path, string beginLogDetail, string setClipboardLogOp)
    {
        if (_targetWindow != IntPtr.Zero && !Win32.IsWindow(_targetWindow))
            _targetWindow = IntPtr.Zero;

        ClipboardDiagnosticsLog.Write(
            $"pasteAsFile BEGIN {beginLogDetail} temp=\"{path}\" target=0x{_targetWindow.ToInt64():X}");

        if (HidePopupAfterUserAction)
            HidePopup();
        if (_targetWindow != IntPtr.Zero)
            Win32.SetForegroundWindowAggressive(_targetWindow);

        if (_hwnd != IntPtr.Zero)
            Win32.TryEmptyClipboardAfterOpen(_hwnd);

        _isSettingClipboard = true;
        bool clipboardOk = await TrySetClipboardFileDropListWithNativeFallbackAsync(
            new[] { path },
            setClipboardLogOp,
            _hwnd,
            maxRetries: 8,
            baseDelayMs: 60);

        ClipboardDiagnosticsLog.Write($"pasteAsFile END clipboardOk={clipboardOk}");

        if (!clipboardOk)
        {
            _isSettingClipboard = false;
            try { File.Delete(path); } catch { /* ignore */ }
            return false;
        }

        MarkSelfWroteClipboard();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.SystemIdle, () => _isSettingClipboard = false);

        SendPasteToTarget();

        const int postEchoMs = 600;
        _ = Task.Delay(postEchoMs).ContinueWith(_ =>
        {
            _pasteInProgress = false;
            ClipboardDiagnosticsLog.Write($"pasteAsFile post-echo suppression window elapsed (ms={postEchoMs})");
        }, TaskScheduler.Default);

        return true;
    }

    /// <summary>
    /// 将剪贴板图片历史写入临时 PNG 并放到系统剪贴板为文件列表，便于在资源管理器中 Ctrl+V 直接保存文件。
    /// </summary>
    private async void PasteImageAsFileForExplorer()
    {
        if (ItemsList.SelectedItem is not ClipboardEntry item || item.Type != EntryType.Image) return;
        var explorerImgData = item.TryGetImageData();
        if (explorerImgData is not { Length: > 0 }) return;
        _pasteInProgress = true;
        var deferClear = false;
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
            File.WriteAllBytes(path, explorerImgData);
        }
        catch { return; }

        deferClear = await CompletePasteTempFileToExplorerAsync(
            path,
            $"pngBytes={explorerImgData.Length}",
            $"explorer_temp_png file=\"{path}\"");
        }
        finally
        {
            if (!deferClear)
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
        var deferClear = false;
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

        deferClear = await CompletePasteTempFileToExplorerAsync(
            path,
            $"jsonChars={text.Length}",
            $"explorer_temp_json file=\"{path}\"");
        }
        finally
        {
            if (!deferClear)
                _pasteInProgress = false;
        }
    }

    /// <summary>
    /// 将普通文本写入临时 .txt 并置于剪贴板文件列表，在资源管理器中粘贴即可落盘。
    /// </summary>
    private async void PasteTextAsFileForExplorer()
    {
        if (ItemsList.SelectedItem is not ClipboardEntry item || item.Type != EntryType.Text) return;
        var text = item.TextContent;
        if (string.IsNullOrEmpty(text) || IsWellFormedJson(text)) return;

        _pasteInProgress = true;
        var deferClear = false;
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

        var path = Path.Combine(dir, $"clip_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        try
        {
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch { return; }

        deferClear = await CompletePasteTempFileToExplorerAsync(
            path,
            $"textChars={text.Length}",
            $"explorer_temp_txt file=\"{path}\"");
        }
        finally
        {
            if (!deferClear)
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
            if (e.OriginalSource is not DependencyObject srcM) return;
            if (ItemsList.ContainerFromElement(srcM) is not ListBoxItem lbiM || lbiM.DataContext is not ClipboardEntry entryM)
                return;

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

        if (e.OriginalSource is not DependencyObject src) return;
        if (ItemsList.ContainerFromElement(src) is not ListBoxItem lbi || lbi.DataContext is not ClipboardEntry entry)
            return;

        int idx = _displayItems.IndexOf(entry);
        if (idx < 0) return;

        // 双击才粘贴：必须在 Preview 内处理；若此处已 Handled，冒泡阶段收不到 MouseLeftButtonDown，ClickCount 也无意义。
        if (_appSettings?.PasteRequiresDoubleClick == true && e.ClickCount == 2)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 || (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                return;
            ItemsList.SelectedItems.Clear();
            ItemsList.SelectedItems.Add(entry);
            _mouseShiftAnchorIndex = idx;
            _selectionRangeAnchor = idx;
            _selectionCursorEnd = idx;
            e.Handled = true;
            PasteSelectedItem();
            return;
        }

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
        if (_appSettings?.PasteRequiresDoubleClick == true) return;
        if (e.OriginalSource is not DependencyObject srcUp) return;
        if (ItemsList.ContainerFromElement(srcUp) is ListBoxItem lbi && lbi.DataContext is ClipboardEntry sel)
        {
            ItemsList.SelectedItem = sel;
            PasteSelectedItem();
        }
    }

    private void ItemsList_PreviewMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject srcR) return;
        if (ItemsList.ContainerFromElement(srcR) is ListBoxItem lbi && lbi.DataContext is ClipboardEntry entry)
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
            || (entry.Type == EntryType.Text
                && !string.IsNullOrEmpty(entry.TextContent)
                && !IsWellFormedJson(entry.TextContent))
            ? Visibility.Visible
            : Visibility.Collapsed;
        CtxPasteOcrTextBorder.Visibility = entry.Type == EntryType.Image
                                             && !string.IsNullOrWhiteSpace(entry.OcrText)
            ? Visibility.Visible
            : Visibility.Collapsed;
        CtxPasteJsonFileBorder.Visibility = entry.Type == EntryType.Text && IsWellFormedJson(entry.TextContent)
            ? Visibility.Visible
            : Visibility.Collapsed;
        CtxEditTextBorder.Visibility = entry.Type == EntryType.Text
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
        Add(CtxPasteOcrTextBorder, ActivateCtxPasteOcrText);
        Add(CtxPasteAsFileBorder, ActivateCtxPasteAsFile);
        Add(CtxPasteJsonFileBorder, ActivateCtxPasteJsonFile);
        Add(CtxEditTextBorder, ActivateCtxEditText);
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

    private async void ActivateCtxPasteOcrText()
    {
        CloseContextMenuPopup();
        if (_contextEntry is not { Type: EntryType.Image } entry) return;
        if (string.IsNullOrWhiteSpace(entry.OcrText)) return;
        ItemsList.SelectedItem = entry;
        await PasteOcrTextFromImageAsync(entry);
    }

    private void ActivateCtxPasteAsFile()
    {
        CloseContextMenuPopup();
        if (_contextEntry is { Type: EntryType.Image })
        {
            ItemsList.SelectedItem = _contextEntry;
            PasteImageAsFileForExplorer();
        }
        else if (_contextEntry is { Type: EntryType.Text })
        {
            ItemsList.SelectedItem = _contextEntry;
            PasteTextAsFileForExplorer();
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

    private void CtxPasteOcrText_Click(object sender, MouseButtonEventArgs e) => ActivateCtxPasteOcrText();

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
        var removedFromPasteQueue = _batchQueue.Remove(entry);
        UpdateBatchOrderProperties();
#if CLIPX_CLIPBOARD
        if (removedFromPasteQueue)
            RequestBatchQueueHeadClipboardResyncAfterDedup();
#endif
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

    private void Pin_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _popupPinned = !_popupPinned;
        UpdatePinHeaderUi();
    }

    private void UpdatePinHeaderUi()
    {
        if (PinHeaderBorder == null || PinHeaderIcon == null) return;
        PinHeaderBorder.Background = _popupPinned
            ? (Brush)FindResource("BatchModeTagFill")
            : System.Windows.Media.Brushes.Transparent;
        PinHeaderIcon.Foreground = _popupPinned
            ? (Brush)FindResource("BatchModeTagFg")
            : (Brush)FindResource("SecondaryText");
        PinHeaderBorder.ToolTip = _popupPinned
            ? "已置顶：粘贴后保持窗口打开；再次点击取消或按 Esc 关闭"
            : "置顶：粘贴后保持窗口打开，便于连续粘贴";
    }

    private void Settings_Click(object sender, MouseButtonEventArgs e)
    {
        HidePopup();
        SettingsRequested?.Invoke();
    }

    private void Header_DragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        _isDragging = true;
        _lastDragMoveTick = 0;
        _hasPendingDragMove = false;
        Win32.GetCursorPos(out _dragLastPt);
        Win32.GetWindowRect(_hwnd, out var rc0);
        _hookAuthPhysLeft = rc0.Left;
        _hookAuthPhysTop = rc0.Top;
        #region agent log
        _agentDbgDragMoveLogCount = 0;
        _agentDbgH20LogCount = 0;
        _agentDbgH21MismatchLogCount = 0;
        _agentDbgH22WpfHwndDipLogCount = 0;
        _agentDbgCachedPrimarySeamX = int.MinValue;
        try
        {
            var hPrim = Win32.MonitorFromPoint(new Win32.POINT { X = 0, Y = 0 }, Win32.MONITOR_DEFAULTTOPRIMARY);
            var miPrim = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            if (hPrim != IntPtr.Zero && Win32.GetMonitorInfo(hPrim, ref miPrim))
                _agentDbgCachedPrimarySeamX = miPrim.rcMonitor.Right;
        }
        catch { /* 调试 */ }
        AgentDbgLog("H4", "Header_DragStart", "drag begin",
            new
            {
                _hwnd = _hwnd.ToInt64(),
                rc0.Left,
                rc0.Top,
                rc0.Right,
                rc0.Bottom,
                _dragLastPt.X,
                _dragLastPt.Y,
                wpfBeforeLeft = Left,
                wpfBeforeTop = Top,
                primarySeamX = _agentDbgCachedPrimarySeamX
            });
        #endregion
    }

    #endregion
}
