using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ClipboardManager;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly string _originalTheme;
    private uint _pendingModifiers;
    private uint _pendingKey;
    private bool _isRecordingHotkey;
    private uint _pendingFileJumpModifiers;
    private uint _pendingFileJumpKey;
    private bool _isRecordingFileJumpHotkey;
    private string _pendingTheme;
    private string _pendingPosition;
    private double _pendingOpacity;
    private bool _pendingHideOnClick;
    private bool _pendingRunAtStartup;
    private bool _pendingCheckUpdatesOnStartup;
    private bool _pendingEnableShellNavigateInject;
    private string _pendingFileJumpFollowMode = FileJumpPickerFollowModes.Dialog;
    private bool _pendingFileJumpAutoPopup = true;
    private bool _pendingFileJumpOpenWhenDialogForeground = true;
    private bool _pendingFileJumpAutoOnFirstClick;
    private bool _pendingFileJumpAutoSyncOnReturn;
#if CLIPX_FILEJUMP
    private bool _pendingExplorerEverythingQuickFind;
#endif
    private string _pendingModifierKey;

    private static readonly string[] ModifierOptions = ["Ctrl", "Alt", "Win", "CapsLock"];

    public event Action? ClearHistoryRequested;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Icon = App.GetWindowIconSource();
        _settings = settings;
        _originalTheme = settings.Theme;

#if !CLIPX_CLIPBOARD
        ClipboardTab.Visibility = Visibility.Collapsed;
#endif
#if !CLIPX_FILEJUMP
        FileJumpTab.Visibility = Visibility.Collapsed;
        ExperimentalFeaturesTab.Visibility = Visibility.Collapsed;
        CustomDialogTab.Visibility = Visibility.Collapsed;
#endif

        MaxItemsBox.Text = settings.MaxItems.ToString();
        _pendingModifiers = settings.HotkeyModifiers;
        _pendingKey = settings.HotkeyKey;
        HotkeyText.Text = settings.HotkeyDisplayName;

        _pendingFileJumpModifiers = settings.FileJumpHotkeyModifiers;
        _pendingFileJumpKey = settings.FileJumpHotkeyKey;
        FileJumpHotkeyText.Text = settings.FileJumpHotkeyDisplayName;
        FileJumpDelayMsBox.Text = settings.FileJumpPickerShowDelayMs.ToString();

        _pendingTheme = settings.Theme;
        ThemeText.Text = ThemeDisplayName(_pendingTheme);

        _pendingPosition = settings.PopupPosition;
        PositionText.Text = PositionDisplayName(_pendingPosition);

        _pendingOpacity = settings.PopupOpacity;
        OpacitySlider.Value = _pendingOpacity;
        OpacityValueText.Text = $"{(int)(_pendingOpacity * 100)}%";

        _pendingHideOnClick = settings.HideOnSameAppClick;
        ClickHideText.Text = _pendingHideOnClick ? "任意点击隐藏" : "仅切换应用隐藏";

        _pendingRunAtStartup = settings.RunAtStartup;
        StartupText.Text = _pendingRunAtStartup ? "开启" : "关闭";

        _pendingCheckUpdatesOnStartup = settings.CheckUpdatesOnStartup;
        CheckUpdateOnStartupText.Text = _pendingCheckUpdatesOnStartup ? "开启" : "关闭";

        _pendingEnableShellNavigateInject = settings.EnableShellNavigateInject;
        ShellInjectText.Text = _pendingEnableShellNavigateInject ? "开启" : "关闭";

        _pendingFileJumpFollowMode = FileJumpPickerFollowModes.Normalize(settings.FileJumpPickerFollowMode);
        FileJumpFollowText.Text = FileJumpPickerFollowModes.IsDialog(_pendingFileJumpFollowMode) ? "跟随对话框" : "跟随鼠标";

        _pendingFileJumpAutoPopup = settings.FileJumpPickerAutoPopup;
        FileJumpAutoPopupText.Text = _pendingFileJumpAutoPopup ? "开启" : "关闭";

        _pendingFileJumpOpenWhenDialogForeground = settings.FileJumpPickerOpenWhenDialogForeground;
        FileJumpOpenOnForegroundText.Text = _pendingFileJumpOpenWhenDialogForeground ? "开启" : "关闭";

        _pendingFileJumpAutoOnFirstClick = settings.FileJumpAutoOnFirstClick;
        FileJumpAutoClickText.Text = _pendingFileJumpAutoOnFirstClick ? "开启" : "关闭";

        _pendingFileJumpAutoSyncOnReturn = settings.FileJumpAutoSyncOnReturn;
        FileJumpAutoSyncText.Text = _pendingFileJumpAutoSyncOnReturn ? "开启" : "关闭";

#if CLIPX_FILEJUMP
        _pendingExplorerEverythingQuickFind = settings.ExplorerEverythingQuickFindEnabled;
        ExplorerEverythingQuickFindText.Text = _pendingExplorerEverythingQuickFind ? "开启" : "关闭";
        ExplorerEverythingMaxResultsBox.Text = settings.ExplorerEverythingQuickFindMaxResults.ToString();
#else
        ExplorerEverythingQuickFindText.Text = "—";
        ExplorerEverythingMaxResultsBox.Text = "—";
        ExplorerEverythingQuickFindText.IsEnabled = false;
        ExplorerEverythingMaxResultsBox.IsEnabled = false;
#endif

        PreviewLinesBox.Text = settings.PreviewMaxLines.ToString();

        _pendingModifierKey = settings.PanelModifierKey;
        ModifierText.Text = ModifierDisplayName(_pendingModifierKey);

        CustomFileDialogStore.RulesChanged += OnCustomFileDialogRulesChanged;
        Closed += SettingsWindow_OnClosed;
        ReloadCustomFileDialogList();
        CustomRulesPathHint.Text = "存储文件：" + CustomFileDialogStore.PersistencePath;
    }

    private void SettingsWindow_OnClosed(object? sender, EventArgs e)
    {
        CustomFileDialogStore.RulesChanged -= OnCustomFileDialogRulesChanged;
    }

    private void OnCustomFileDialogRulesChanged()
    {
        if (Dispatcher.CheckAccess())
            ReloadCustomFileDialogList();
        else
            Dispatcher.Invoke(ReloadCustomFileDialogList);
    }

    private void ReloadCustomFileDialogList()
    {
        CustomRulesList.Items.Clear();
        foreach (var r in CustomFileDialogStore.GetRules())
            CustomRulesList.Items.Add(r);
    }

    private void CustomRuleDelete_Click(object sender, RoutedEventArgs e)
    {
        if (CustomRulesList.SelectedItem is not CustomFileDialogRule r)
            return;

        if (System.Windows.MessageBox.Show(
                $"删除此规则？\n{r.SummaryLine}",
                "确认",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        CustomFileDialogStore.RemoveRule(r.Id);
        ReloadCustomFileDialogList();
    }

    private void CustomRuleWizard_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
            app.StartCustomFileDialogWizard();
    }

    private void CustomRuleImportMerge_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON (*.json)|*.json|所有文件 (*.*)|*.*",
            Title = "导入规则（与本地合并）",
        };
        if (dlg.ShowDialog(this) != true)
            return;

        var n = CustomFileDialogStore.ImportMergeFromFile(dlg.FileName, out var err);
        if (err != null)
        {
            System.Windows.MessageBox.Show("导入失败：\n" + err, "自定义文件对话框",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        System.Windows.MessageBox.Show(
            n > 0 ? $"已合并写入 {n} 条有效规则。" : "文件中没有可导入的有效规则（需包含非空的 windowClass）。",
            "自定义文件对话框",
            MessageBoxButton.OK,
            n > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void CustomRuleImportReplace_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON (*.json)|*.json|所有文件 (*.*)|*.*",
            Title = "导入规则（完全替换）",
        };
        if (dlg.ShowDialog(this) != true)
            return;

        if (System.Windows.MessageBox.Show(
                "将删除当前所有自定义规则，并替换为文件中的列表。\n确定继续？",
                "确认替换",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        var n = CustomFileDialogStore.ImportReplaceFromFile(dlg.FileName, out var err);
        if (err != null)
        {
            System.Windows.MessageBox.Show("导入失败：\n" + err, "自定义文件对话框",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        System.Windows.MessageBox.Show(
            $"已替换为 {n} 条规则。",
            "自定义文件对话框",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void CustomRuleExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            FileName = "clipboardx_custom_file_dialogs.json",
            Title = "导出自定义文件对话框规则",
        };
        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            CustomFileDialogStore.ExportToFile(dlg.FileName);
            System.Windows.MessageBox.Show("导出完成。", "自定义文件对话框",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("导出失败：\n" + ex.Message, "自定义文件对话框",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string ThemeDisplayName(string t) => t switch
    {
        "Dark" => "暗色",
        "Light" => "亮色",
        _ => "跟随系统"
    };

    private static string PositionDisplayName(string p) => p switch
    {
        "Mouse" => "鼠标处",
        _ => "光标处"
    };

    private void HotkeyBox_Click(object sender, RoutedEventArgs e)
    {
        _isRecordingHotkey = true;
        HotkeyText.Text = "按下快捷键…";
        HotkeyText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBg");
        HotkeyBox.Focus();
    }

    private void HotkeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRecordingHotkey) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None) return;

        uint mod = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) mod |= Win32.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mod |= Win32.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mod |= Win32.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mod |= Win32.MOD_WIN;

        _pendingModifiers = mod;
        _pendingKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        _isRecordingHotkey = false;

        var tmp = new AppSettings { HotkeyModifiers = _pendingModifiers, HotkeyKey = _pendingKey };
        HotkeyText.Text = tmp.HotkeyDisplayName;
        HotkeyText.Foreground = (System.Windows.Media.Brush)FindResource("PrimaryText");
    }

    private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isRecordingHotkey)
        {
            _isRecordingHotkey = false;
            var tmp = new AppSettings { HotkeyModifiers = _pendingModifiers, HotkeyKey = _pendingKey };
            HotkeyText.Text = tmp.HotkeyDisplayName;
            HotkeyText.Foreground = (System.Windows.Media.Brush)FindResource("PrimaryText");
        }
    }

    private void FileJumpHotkeyBox_Click(object sender, RoutedEventArgs e)
    {
        _isRecordingFileJumpHotkey = true;
        FileJumpHotkeyText.Text = "按下快捷键…";
        FileJumpHotkeyText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBg");
        FileJumpHotkeyBox.Focus();
    }

    private void FileJumpHotkeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRecordingFileJumpHotkey) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None) return;

        uint mod = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) mod |= Win32.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mod |= Win32.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mod |= Win32.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mod |= Win32.MOD_WIN;

        _pendingFileJumpModifiers = mod;
        _pendingFileJumpKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        _isRecordingFileJumpHotkey = false;

        FileJumpHotkeyText.Text = AppSettings.FormatHotkey(_pendingFileJumpModifiers, _pendingFileJumpKey);
        FileJumpHotkeyText.Foreground = (System.Windows.Media.Brush)FindResource("PrimaryText");
    }

    private void FileJumpHotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isRecordingFileJumpHotkey)
        {
            _isRecordingFileJumpHotkey = false;
            FileJumpHotkeyText.Text = AppSettings.FormatHotkey(_pendingFileJumpModifiers, _pendingFileJumpKey);
            FileJumpHotkeyText.Foreground = (System.Windows.Media.Brush)FindResource("PrimaryText");
        }
    }

    private void ThemeCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingTheme = _pendingTheme switch
        {
            "System" => "Dark",
            "Dark" => "Light",
            _ => "System"
        };
        ThemeText.Text = ThemeDisplayName(_pendingTheme);
        ThemeManager.Apply(_pendingTheme);
    }

    private void PositionCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingPosition = _pendingPosition == "Caret" ? "Mouse" : "Caret";
        PositionText.Text = PositionDisplayName(_pendingPosition);
    }

    private void ClickHideCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingHideOnClick = !_pendingHideOnClick;
        ClickHideText.Text = _pendingHideOnClick ? "任意点击隐藏" : "仅切换应用隐藏";
    }

    private void StartupCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingRunAtStartup = !_pendingRunAtStartup;
        StartupText.Text = _pendingRunAtStartup ? "开启" : "关闭";
    }

    private void CheckUpdateOnStartupCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingCheckUpdatesOnStartup = !_pendingCheckUpdatesOnStartup;
        CheckUpdateOnStartupText.Text = _pendingCheckUpdatesOnStartup ? "开启" : "关闭";
    }

    private void ShellInjectCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingEnableShellNavigateInject = !_pendingEnableShellNavigateInject;
        ShellInjectText.Text = _pendingEnableShellNavigateInject ? "开启" : "关闭";
    }

    private void FileJumpFollowCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingFileJumpFollowMode = FileJumpPickerFollowModes.IsDialog(_pendingFileJumpFollowMode)
            ? FileJumpPickerFollowModes.Mouse
            : FileJumpPickerFollowModes.Dialog;
        FileJumpFollowText.Text = FileJumpPickerFollowModes.IsDialog(_pendingFileJumpFollowMode) ? "跟随对话框" : "跟随鼠标";
    }

    private void FileJumpAutoPopupCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingFileJumpAutoPopup = !_pendingFileJumpAutoPopup;
        FileJumpAutoPopupText.Text = _pendingFileJumpAutoPopup ? "开启" : "关闭";
    }

    private void FileJumpOpenOnForegroundCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingFileJumpOpenWhenDialogForeground = !_pendingFileJumpOpenWhenDialogForeground;
        FileJumpOpenOnForegroundText.Text = _pendingFileJumpOpenWhenDialogForeground ? "开启" : "关闭";
    }

    private void FileJumpAutoClickCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingFileJumpAutoOnFirstClick = !_pendingFileJumpAutoOnFirstClick;
        FileJumpAutoClickText.Text = _pendingFileJumpAutoOnFirstClick ? "开启" : "关闭";
    }

    private void FileJumpAutoSyncCycle_Click(object sender, RoutedEventArgs e)
    {
        _pendingFileJumpAutoSyncOnReturn = !_pendingFileJumpAutoSyncOnReturn;
        FileJumpAutoSyncText.Text = _pendingFileJumpAutoSyncOnReturn ? "开启" : "关闭";
    }

    private void ExplorerEverythingQuickFindCycle_Click(object sender, MouseButtonEventArgs e)
    {
#if CLIPX_FILEJUMP
        _pendingExplorerEverythingQuickFind = !_pendingExplorerEverythingQuickFind;
        ExplorerEverythingQuickFindText.Text = _pendingExplorerEverythingQuickFind ? "开启" : "关闭";
#endif
    }

    private static string ModifierDisplayName(string m) => m switch
    {
        "Alt" => "Alt",
        "Win" => "Win",
        "CapsLock" => "CapsLock",
        _ => "Ctrl",
    };

    private void ModifierCycle_Click(object sender, RoutedEventArgs e)
    {
        int idx = Array.IndexOf(ModifierOptions, _pendingModifierKey);
        _pendingModifierKey = ModifierOptions[(idx + 1) % ModifierOptions.Length];
        ModifierText.Text = ModifierDisplayName(_pendingModifierKey);
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValueText == null) return;
        _pendingOpacity = Math.Round(e.NewValue, 2);
        OpacityValueText.Text = $"{(int)(_pendingOpacity * 100)}%";
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show("确定要清空所有剪切板历史？\n（快捷短语不受影响）",
                "确认清空", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            ClearHistoryRequested?.Invoke();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MaxItemsBox.Text, out var maxItems) || maxItems < 10 || maxItems > 100000)
        {
            System.Windows.MessageBox.Show("最大记录数应在 10 ~ 100000 之间", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(PreviewLinesBox.Text, out var previewLines) || previewLines < 1 || previewLines > 10)
        {
            System.Windows.MessageBox.Show("预览行数应在 1 ~ 10 之间", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(FileJumpDelayMsBox.Text, out var jumpDelayMs) || jumpDelayMs < 0 || jumpDelayMs > 10000)
        {
            System.Windows.MessageBox.Show("跳转列表延时应在 0 ~ 10000 毫秒之间（0 表示立即弹出）", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_pendingModifiers == _pendingFileJumpModifiers && _pendingKey == _pendingFileJumpKey)
        {
            System.Windows.MessageBox.Show("呼出快捷键与文件对话框跳转键不能相同。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

#if CLIPX_FILEJUMP
        if (!int.TryParse(ExplorerEverythingMaxResultsBox.Text, out var explorerEvMax) || explorerEvMax < 1 || explorerEvMax > 2000)
        {
            System.Windows.MessageBox.Show("筛选最大条数应在 1 ~ 2000 之间", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
#endif

        _settings.MaxItems = maxItems;
        _settings.HotkeyModifiers = _pendingModifiers;
        _settings.HotkeyKey = _pendingKey;
        _settings.FileJumpHotkeyModifiers = _pendingFileJumpModifiers;
        _settings.FileJumpHotkeyKey = _pendingFileJumpKey;
        _settings.FileJumpPickerShowDelayMs = jumpDelayMs;
        _settings.Theme = _pendingTheme;
        _settings.PopupPosition = _pendingPosition;
        _settings.PopupOpacity = _pendingOpacity;
        _settings.HideOnSameAppClick = _pendingHideOnClick;
        _settings.RunAtStartup = _pendingRunAtStartup;
        _settings.CheckUpdatesOnStartup = _pendingCheckUpdatesOnStartup;
        _settings.EnableShellNavigateInject = _pendingEnableShellNavigateInject;
        _settings.FileJumpPickerFollowMode = FileJumpPickerFollowModes.Normalize(_pendingFileJumpFollowMode);
        _settings.FileJumpPickerAutoPopup = _pendingFileJumpAutoPopup;
        _settings.FileJumpPickerOpenWhenDialogForeground = _pendingFileJumpOpenWhenDialogForeground;
        _settings.FileJumpAutoOnFirstClick = _pendingFileJumpAutoOnFirstClick;
        _settings.FileJumpAutoSyncOnReturn = _pendingFileJumpAutoSyncOnReturn;
#if CLIPX_FILEJUMP
        _settings.ExplorerEverythingQuickFindEnabled = _pendingExplorerEverythingQuickFind;
        _settings.ExplorerEverythingQuickFindMaxResults = explorerEvMax;
        _settings.UseFindXSearch = false;
#endif
        _settings.PreviewMaxLines = previewLines;
        _settings.PanelModifierKey = _pendingModifierKey;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Apply(_originalTheme);
        DialogResult = false;
        Close();
    }
}
