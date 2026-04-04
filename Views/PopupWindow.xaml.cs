using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
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

    private IntPtr _hwnd;
    private IntPtr _targetWindow;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private IntPtr _winEventHook;

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
    private bool _isPopupVisible;
    private string _searchText = "";
    private EntryType? _typeFilter;
    private bool _quickPhraseOnly;
    private ClipboardEntry? _contextEntry;
    /// <summary>当前标为「待二次 Del 删除」的条目，与 <see cref="ClipboardEntry.IsPendingDelete"/> 同步。</summary>
    private ClipboardEntry? _pendingDeleteEntry;

    private const int PageSize = 8;
    private int _firstVisibleIndex;

    private uint _hotkeyModifiers;
    private uint _hotkeyKey;
    private int _maxItems;
    private string _popupPosition = "Caret";
    private double _popupOpacity = 0.95;
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
        UpdateFooterHints();

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
    }

    public void ClearHistory()
    {
#if CLIPX_CLIPBOARD
        _historyStore.DeleteAll();
        _allItems.RemoveAll(x => !x.IsQuickPaste);
        RefreshFilter();
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
        PhraseEditBox.Text = source.ShortcutPhrase ?? "";
        PhraseEditPopup.IsOpen = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            var hwndSource = PresentationSource.FromVisual(PhraseEditBox) as System.Windows.Interop.HwndSource;
            if (hwndSource != null)
            {
                Win32.SetForegroundWindow(hwndSource.Handle);
                Win32.SetFocus(hwndSource.Handle);
            }
            PhraseEditBox.Focus();
            Keyboard.Focus(PhraseEditBox);
            PhraseEditBox.SelectAll();
        });
    }

    private void CommitPhraseEdit()
    {
        var phrase = PhraseEditBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(phrase) || _phraseEditEntry == null)
        {
            PhraseEditPopup.IsOpen = false;
            _phraseEditEntry = null;
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
    }

    private void PhraseEditBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { CommitPhraseEdit(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Escape) { PhraseEditPopup.IsOpen = false; _phraseEditEntry = null; e.Handled = true; }
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
        if (_isSettingClipboard)
        {
            _isSettingClipboard = false;
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
    }

    private void DeduplicateFiles(string[] paths)
    {
        var key = string.Join("|", paths);
        foreach (var x in _allItems.Where(x => x.Type == EntryType.Files && string.Join("|", x.FilePaths ?? []) == key))
            _historyStore.TryDelete(x.PersistedId);
        _allItems.RemoveAll(x => x.Type == EntryType.Files && string.Join("|", x.FilePaths ?? []) == key);
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
    }

    private void TrimItems()
    {
        var regular = _allItems.Where(x => !x.IsQuickPaste).ToList();
        while (regular.Count > _maxItems)
        {
            var last = regular[^1];
            _historyStore.TryDelete(last.PersistedId);
            _allItems.Remove(last);
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

        var sorted = items
            .OrderByDescending(i => i.IsQuickPaste && !string.IsNullOrEmpty(_searchText))
            .ThenByDescending(i => i.CopiedAt);

        int idx = 1;
        foreach (var item in sorted)
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

        InstallKeyboardHook();
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
        UninstallKeyboardHook();
        UninstallMouseHook();
        ContextPopup.IsOpen = false;
        PhraseEditPopup.IsOpen = false;
        _phraseEditEntry = null;
        ClearPendingDelete();
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
        if (owner != null)
            owner.OnForegroundChanged(hWinEventHook, eventType, hwnd, idObject, idChild, dwEventThread, dwmsEventTime);
    }

    private void InstallForegroundWatcher()
    {
        if (_winEventHook != IntPtr.Zero) return;
        s_popupWinEventOwner = this;
        _winEventHook = Win32.SetWinEventHook(
            Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, s_popupWinEventThunk, 0, 0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
    }

    private void UninstallForegroundWatcher()
    {
        if (_winEventHook != IntPtr.Zero)
        {
            Win32.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        if (s_popupWinEventOwner == this)
            s_popupWinEventOwner = null;
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

        if (FileDialogJumpHelper.IsLikelyFileDialog(hwnd))
        {
            var prevForAutoSync = prev;
            ScheduleSnapshotFolderFromDialog(hwnd);
            Dispatcher.BeginInvoke(() => TryAutoOpenFileJumpPickerWhenDialogForeground(hwnd));
            Dispatcher.BeginInvoke(() => TryAutoSyncPathOnDialogReturn(hwnd, prevForAutoSync));
        }

        Dispatcher.BeginInvoke(() => UpdateFileJumpClickToNavigateArm(hwnd));

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
                    if (Win32.GetForegroundWindow() != target) return;
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
        if (!FileDialogJumpHelper.IsLikelyFileDialog(previousHwnd)) return;
        if (!FileDialogJumpHelper.TryReadCurrentFolder(previousHwnd, out var folder)
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

        var fgRoot = Win32.GetAncestor(fgNow, Win32.GA_ROOT);
        if (fgRoot != dialogRoot)
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

        var fgNow = Win32.GetForegroundWindow();
        if (fgNow == IntPtr.Zero) return;
        var fgRoot = Win32.GetAncestor(fgNow, Win32.GA_ROOT);
        if (fgRoot != dialogRootNow) return;

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
        var fgNow = Win32.GetForegroundWindow();
        var fgRoot = Win32.GetAncestor(fgNow, Win32.GA_ROOT);
        return fgRoot == dialogRoot;
    }

    private void TryAutoSyncPathOnDialogReturnCore(IntPtr dialogHwnd, IntPtr dialogRoot, IntPtr previousForegroundHwnd)
    {
        if (_appSettings == null) return;
        if (!Win32.IsWindow(dialogHwnd)) return;

        var fgNow = Win32.GetForegroundWindow();
        var fgRoot = Win32.GetAncestor(fgNow, Win32.GA_ROOT);
        if (fgRoot != dialogRoot) return;

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
        if (FileDialogJumpHelper.ClassifyFileDialog(fgNow) != FileDialogKind.None)
            return fgNow;

        if (CustomFileDialogStore.FindMatchingRule(fgNow) != null)
            return fgNow;

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

        if (!FileDialogJumpHelper.IsLikelyFileDialog(foregroundHwnd))
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

        var dialogHwnd = foregroundHwnd;
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
            if (fg != IntPtr.Zero && Win32.GetAncestor(fg, Win32.GA_ROOT) != _fileJumpAutoArmedRoot)
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

    private static bool IsSystemExplorerForeground()
    {
        var fg = Win32.GetForegroundWindow();
        return FileManagerPathCollector.TryFindExplorerCabinetFrame(fg) != IntPtr.Zero;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isPopupVisible && wParam == (IntPtr)Win32.WM_KEYDOWN)
        {
            if (_activeFileJumpPicker != null)
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            // 剪贴板浮层仍显示但焦点已在系统资源管理器时，必须放行低级键盘链，
            // 否则后装的钩子无法收到按键（资源管理器内 Everything 筛选等）。
            if (IsSystemExplorerForeground())
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            var kb = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);

            if (PhraseEditPopup.IsOpen)
            {
                if (kb.vkCode == Win32.VK_ESCAPE)
                {
                    Dispatcher.BeginInvoke(() => { PhraseEditPopup.IsOpen = false; _phraseEditEntry = null; });
                    return (IntPtr)1;
                }
                if (kb.vkCode == Win32.VK_RETURN)
                {
                    Dispatcher.BeginInvoke(CommitPhraseEdit);
                    return (IntPtr)1;
                }
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }

            if (kb.vkCode is 0x10 or 0x11 or 0x12 or 0x14
                or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5
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
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }

            if (ctrlHeld || altHeld)
                return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            switch (kb.vkCode)
            {
                case Win32.VK_UP:
                    Dispatcher.BeginInvoke(() => MoveSelection(-1));
                    return (IntPtr)1;
                case Win32.VK_DOWN:
                    Dispatcher.BeginInvoke(() => MoveSelection(1));
                    return (IntPtr)1;
                case Win32.VK_LEFT:
                    Dispatcher.BeginInvoke(() => ScrollPage(-1));
                    return (IntPtr)1;
                case Win32.VK_RIGHT:
                    Dispatcher.BeginInvoke(() => ScrollPage(1));
                    return (IntPtr)1;
                case Win32.VK_RETURN:
                    Dispatcher.BeginInvoke(PasteSelectedItem);
                    return (IntPtr)1;
                case Win32.VK_ESCAPE:
                    Dispatcher.BeginInvoke(() =>
                    {
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
                case 0x09: // Tab → cycle type filter
                    Dispatcher.BeginInvoke(CycleTypeFilter);
                    return (IntPtr)1;
                case Win32.VK_DELETE:
                    Dispatcher.BeginInvoke(DeleteSelectedItemWithConfirm);
                    return (IntPtr)1;
            }

            var ch = VkToChar(kb.vkCode, kb.scanCode);
            if (ch.HasValue)
            {
                Dispatcher.BeginInvoke(() => { _searchText += ch.Value; RefreshFilter(); });
            }

            return (IntPtr)1;
        }

        return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
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
        FooterHints.Inlines.Clear();
        var hintBrush = FindResource("HintText") as System.Windows.Media.Brush;

        void AddHint(string key, string label, bool dot = true)
        {
            if (dot) FooterHints.Inlines.Add(new System.Windows.Documents.Run(" · "));
            FooterHints.Inlines.Add(new System.Windows.Documents.Run(key) { Foreground = hintBrush });
            FooterHints.Inlines.Add(new System.Windows.Documents.Run(label));
        }

        AddHint($"{m}+N", "快贴", false);
        AddHint("↑↓", "选择");
        AddHint($"{m}±", "翻页");
        AddHint("Enter", "粘贴");
        AddHint($"{m}+Tab", "短语");
        AddHint("Tab", "筛选");
        AddHint("a-z", "拼音");
        AddHint("Del×2", "删除");
        AddHint("Esc", "取消删除线");
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

    private void MoveSelection(int delta)
    {
        if (_displayItems.Count == 0) return;
        var idx = ItemsList.SelectedIndex + delta;
        if (idx < 0) idx = 0;
        if (idx >= _displayItems.Count) idx = _displayItems.Count - 1;
        ItemsList.SelectedIndex = idx;
        ItemsList.ScrollIntoView(ItemsList.SelectedItem);
    }

    private void ScrollPage(int direction)
    {
        if (_displayItems.Count == 0) return;
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
        HidePopup();
        if (_targetWindow != IntPtr.Zero)
            Win32.SetForegroundWindowAggressive(_targetWindow);
        var textLen = item.TextContent?.Length ?? 0;
        var imgBytes = item.ImageData?.Length ?? 0;
        var imgPixels = item.Type == EntryType.Image ? item.ImageWidth * item.ImageHeight : 0;
        var hugeClipboardImage = item.Type == EntryType.Image && (imgBytes > 900_000 || imgPixels > 1_200_000);
        var focusSettleMs = item.Type switch
        {
            EntryType.Text => textLen > 12000 ? 150 : textLen > 4000 ? 120 : 70,
            EntryType.Image => hugeClipboardImage ? 220 : 160,
            _ => 50
        };
        await Task.Delay(focusSettleMs);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        ClipboardDiagnosticsLog.Write($"paste focusSettleMs={focusSettleMs} after Hide+SetForegroundAggressive");

        if (_hwnd != IntPtr.Zero)
        {
            var nudged = Win32.TryEmptyClipboardAfterOpen(_hwnd);
            ClipboardDiagnosticsLog.Write($"paste clipNudge EmptyClipboard ok={nudged}");
        }

        _isSettingClipboard = true;
        bool clipboardOk = false;
        try
        {
            switch (item.Type)
            {
                case EntryType.Text:
                    clipboardOk = await TrySetClipboardAsync(
                        () => System.Windows.Clipboard.SetText(item.TextContent!),
                        $"SetText len={textLen}",
                        clipNudgeHwnd: _hwnd);
                    break;
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
        else
            // SystemIdle 晚于 WM_CLIPBOARDUPDATE，避免抢先清空标志把本次 Set 误判为外部剪贴板内容
            _ = Dispatcher.BeginInvoke(DispatcherPriority.SystemIdle, () => _isSettingClipboard = false);

        if (clipboardOk)
        {
            var prePasteDelayMs = item.Type == EntryType.Image ? 85 : 45;
            await Task.Delay(prePasteDelayMs);
            SendCtrlV();
            // Excel/WPS 等 OLE 应用在处理粘贴后可能调用 EmptyClipboard 或触发延迟渲染，
            // 产生的 WM_CLIPBOARDUPDATE 不应被识别为新的剪贴板内容。
            // 保持 _pasteInProgress 以抑制这些粘贴后「回波」。
            await Task.Delay(600);
            ClipboardDiagnosticsLog.Write("paste post-echo suppression window elapsed");
        }
        }
        finally
        {
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

        if (_targetWindow != IntPtr.Zero && !Win32.IsWindow(_targetWindow))
            _targetWindow = IntPtr.Zero;

        ClipboardDiagnosticsLog.Write(
            $"pasteAsFile BEGIN pngBytes={item.ImageData?.Length ?? 0} temp=\"{path}\" target=0x{_targetWindow.ToInt64():X}");

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
            $"SetFileDropList explorer_temp file=\"{path}\"",
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

        int relSelection = Math.Max(0, ItemsList.SelectedIndex - _firstVisibleIndex);
        _firstVisibleIndex = newFirstVisible;

        int newSel = Math.Clamp(newFirstVisible + relSelection, 0, _displayItems.Count - 1);
        ItemsList.SelectedIndex = newSel;

        UpdateVisibleIndices(newFirstVisible);
    }

    private void ItemsList_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not ListBoxItem)
            element = VisualTreeHelper.GetParent(element);
        if (element is ListBoxItem lbi && lbi.DataContext is ClipboardEntry)
        {
            ItemsList.SelectedItem = lbi.DataContext;
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
            _contextEntry = entry;
            ItemsList.SelectedItem = entry;
            CtxShortcutText.Text = entry.IsQuickPaste ? "⚡ 修改快捷短语" : "⚡ 设为快捷短语";
            CtxPasteAsFileBorder.Visibility = entry.Type == EntryType.Image
                ? Visibility.Visible
                : Visibility.Collapsed;
            ContextPopup.IsOpen = true;
            e.Handled = true;
        }
    }

    private void CtxPaste_Click(object sender, MouseButtonEventArgs e)
    {
        ContextPopup.IsOpen = false;
        if (_contextEntry != null) { ItemsList.SelectedItem = _contextEntry; PasteSelectedItem(); }
    }

    private void CtxPasteAsFile_Click(object sender, MouseButtonEventArgs e)
    {
        ContextPopup.IsOpen = false;
        if (_contextEntry is { Type: EntryType.Image })
        {
            ItemsList.SelectedItem = _contextEntry;
            PasteImageAsFileForExplorer();
        }
    }

    private void CtxShortcut_Click(object sender, MouseButtonEventArgs e)
    {
        ContextPopup.IsOpen = false;
        if (_contextEntry != null) AddQuickPaste(_contextEntry);
    }

    private void CtxDelete_Click(object sender, MouseButtonEventArgs e)
    {
        ContextPopup.IsOpen = false;
        if (_contextEntry == null) return;
        RemoveEntry(_contextEntry);
    }

    private void RemoveEntry(ClipboardEntry entry)
    {
        if (ReferenceEquals(_pendingDeleteEntry, entry))
        {
            entry.IsPendingDelete = false;
            _pendingDeleteEntry = null;
        }
        else
            ClearPendingDelete();
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
