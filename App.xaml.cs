using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using Application = System.Windows.Application;

namespace ClipboardManager;

public partial class App : Application
{
    private static Mutex? _mutex;
    private WinForms.NotifyIcon? _trayIcon;
    private PopupWindow? _popup;
#if CLIPX_FILEJUMP
    private ExplorerQuickFindController? _explorerQuickFind;
#endif
    private AppSettings _settings = new();
    private static bool _probingAssemblyResolveRegistered;

    /// <summary>
    /// 部分宿主下 <see cref="AppContext.BaseDirectory"/> 与主程序集所在目录不一致，会导致无法找到 NPinyin 等旁路 dll；
    /// 从 <see cref="Assembly.Location"/> 目录补解析（单文件时 Location 为空，回退 BaseDirectory）。
    /// </summary>
    private static void RegisterProbingAssemblyResolve()
    {
        if (_probingAssemblyResolveRegistered) return;
        _probingAssemblyResolveRegistered = true;

        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            try
            {
                var an = new AssemblyName(args.Name);
                if (string.IsNullOrEmpty(an.Name)) return null;

                var dir = GetDependencyProbeDirectory();
                if (string.IsNullOrEmpty(dir)) return null;

                var dll = Path.Combine(dir, an.Name + ".dll");
                if (File.Exists(dll))
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);

                if (!string.IsNullOrEmpty(an.CultureName))
                {
                    var sat = Path.Combine(dir, an.CultureName, an.Name + ".dll");
                    if (File.Exists(sat))
                        return AssemblyLoadContext.Default.LoadFromAssemblyPath(sat);
                }
            }
            catch
            {
                /* 由 CLR 继续尝试默认探测 */
            }
            return null;
        };
    }

    private static string? GetDependencyProbeDirectory()
    {
        try
        {
            var loc = typeof(App).Assembly.Location;
            if (!string.IsNullOrEmpty(loc))
            {
                var d = Path.GetDirectoryName(loc);
                if (!string.IsNullOrEmpty(d)) return d;
            }
        }
        catch
        {
            /* ignore */
        }

        var b = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(b)) return null;
        return b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterProbingAssemblyResolve();
        base.OnStartup(e);

        AppPaths.Initialize();

        // WinForms 托盘/上下文菜单在 WPF 宿主中更稳妥
        WinForms.Application.EnableVisualStyles();
        WinForms.Application.SetCompatibleTextRenderingDefault(false);

        if (PerUserInstall.TryProcessUninstallArgs(e.Args))
        {
            Shutdown();
            return;
        }

        if (PerUserInstall.TryInstallToUserProgramsAndRelaunch(e.Args))
        {
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, AppPaths.MutexName, out bool isNew);
        if (!isNew)
        {
#if DEBUG
            try { Console.WriteLine("ClipboardX 已在运行中（互斥锁），本进程将退出。请查看托盘或结束旧进程。"); } catch { }
#endif
            System.Windows.MessageBox.Show("ClipboardX 已在运行中", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();
        ThemeManager.Apply(_settings.Theme);
        PerUserInstall.EnsureUninstallRegistrationIfNeeded();
        StartupRegistration.Apply(_settings.RunAtStartup);

        _popup = new PopupWindow();
        _popup.Initialize(_settings);
        _popup.SettingsRequested += OpenSettings;

#if CLIPX_FILEJUMP
        SyncExplorerQuickFindHook();
#endif

        SetupTrayIcon();
        _ = CheckForUpdatesOnStartupAsync();
    }

    /// <summary>
    /// 启动约 45 秒后静默请求 GitHub；有新版本时仅托盘气泡提示，同一发行版只提示一次。
    /// </summary>
    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (!_settings.CheckUpdatesOnStartup) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        }
        catch { /* Task.Delay 取消等 */ }

        if (_trayIcon == null) return;

        GitHubUpdateService.LatestReleaseInfo info;
        try
        {
            info = await GitHubUpdateService.FetchLatestReleaseAsync().ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        var current = AppInfo.DisplayVersion;
        await Dispatcher.InvokeAsync(() =>
        {
            if (_trayIcon == null) return;

            if (!GitHubUpdateService.IsRemoteNewerThanCurrent(info.TagName, current))
            {
                if (!string.IsNullOrEmpty(_settings.LastStartupUpdateNotifiedTag))
                {
                    _settings.LastStartupUpdateNotifiedTag = null;
                    _settings.Save();
                }
                return;
            }

            var tagNorm = info.TagName.Trim();
            if (string.Equals(tagNorm, _settings.LastStartupUpdateNotifiedTag, StringComparison.OrdinalIgnoreCase))
                return;

            _settings.LastStartupUpdateNotifiedTag = tagNorm;
            _settings.Save();
            var ver = tagNorm.TrimStart('v', 'V');
            _trayIcon.ShowBalloonTip(
                12000,
                "ClipboardX — 发现新版本",
                $"版本 {ver} 已发布，托盘右键「检查更新…」可下载安装。（当前 {current}）",
                WinForms.ToolTipIcon.Info);
        });
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = $"ClipboardX ({_settings.HotkeyDisplayName})",
            Visible = true
        };

        var menu = new WinForms.ContextMenuStrip();
#if CLIPX_CLIPBOARD
        menu.Items.Add($"显示 ({_settings.HotkeyDisplayName})", null, (_, _) =>
            Dispatcher.Invoke(() => _popup?.TogglePopup()));
#endif
        menu.Items.Add("设置", null, (_, _) =>
            Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("关于", null, (_, _) =>
            Dispatcher.Invoke(ShowAboutDialog));
        menu.Items.Add("检查更新…", null, (_, _) =>
            _ = CheckForUpdatesAsync());
#if DEBUG
        menu.Items.Add("采集窗口信息", null, (_, _) => StartWindowInspection());
#endif
#if CLIPX_FILEJUMP
        menu.Items.Add("添加自定义文件对话框…", null, (_, _) => StartCustomFileDialogWizard());
#endif
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("卸载…", null, (_, _) =>
            Dispatcher.Invoke(PerUserInstall.PromptUninstallFromTray));
        menu.Items.Add("退出", null, (_, _) =>
            Dispatcher.Invoke(Shutdown));
        _trayIcon.ContextMenuStrip = menu;

#if CLIPX_CLIPBOARD
        _trayIcon.DoubleClick += (_, _) =>
            Dispatcher.Invoke(() => _popup?.TogglePopup());
#endif

#if DEBUG
        try
        {
            Console.WriteLine("ClipboardX 已在运行。");
            Console.WriteLine("- dotnet run 时任务管理器里进程名多为「dotnet」，属于正常情况。");
            Console.WriteLine("- 图标在系统托盘：任务栏右侧「显示隐藏的图标」展开查找（蓝色剪贴板样式）。");
            Console.WriteLine("- 若已有一份在跑，再运行会弹窗提示「已在运行中」。");
            Console.WriteLine("按 Ctrl+C 可结束本控制台（会结束当前这次调试进程）。");
        }
        catch { /* 无控制台时忽略 */ }
#endif
    }

    private void UpdateTrayTooltip()
    {
        if (_trayIcon != null)
            _trayIcon.Text = $"ClipboardX ({_settings.HotkeyDisplayName})";
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            GitHubUpdateService.LatestReleaseInfo info;
            try
            {
                info = await GitHubUpdateService.FetchLatestReleaseAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"无法获取更新信息（请检查网络或稍后重试）：\n{ex.Message}",
                    "检查更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var current = AppInfo.DisplayVersion;
            if (!GitHubUpdateService.IsRemoteNewerThanCurrent(info.TagName, current))
            {
                System.Windows.MessageBox.Show(
                    $"当前已是最新版本（{current}）。",
                    "检查更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var asset = info.ChosenAsset;
            var note = GitHubUpdateService.TruncateNote(info.Body);
            var verRemote = info.TagName.TrimStart('v', 'V');
            var installDir = PerUserInstall.GetUpdateInstallDirectory();
            var pkgHint = asset.IsNoRuntimeVariant
                ? "已按当前运行方式匹配：**框架依赖**（与本机 dotnet 共享运行时，包较小）。"
                : "已按当前运行方式匹配：**自带运行时**（单文件内含 .NET，包较大）。";
            var prompt =
                $"发现新版本 {verRemote}（当前 {current}）。\n\n" +
                (note.Length > 0 ? $"说明：{note}\n\n" : "") +
                pkgHint + "\n\n" +
                $"将下载：{asset.Name}\n大小约 {GitHubUpdateService.FormatSizeMb(asset.Size)}\n\n" +
                $"安装目录：\n{installDir}\n\n" +
                "程序将关闭后自动替换并重新启动。\n是否继续？";

            if (System.Windows.MessageBox.Show(
                    prompt,
                    "ClipboardX 更新",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var staging = Path.Combine(Path.GetTempPath(), "ClipboardX-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var zipPath = Path.Combine(staging, "release.zip");
            var extractDir = Path.Combine(staging, "extract");
            var ps1 = Path.Combine(staging, "apply.ps1");
            var updateLaunched = false;
            try
            {
                await GitHubUpdateService.RunWithMarqueeAsync(
                    "正在从 GitHub 下载更新…",
                    () => GitHubUpdateService.DownloadToFileAsync(asset.DownloadUrl, zipPath));

                GitHubUpdateService.ExtractZipToDirectory(zipPath, extractDir);

                if (!File.Exists(Path.Combine(extractDir, AppInfo.PrimaryExecutableFileName)))
                {
                    System.Windows.MessageBox.Show(
                        $"压缩包内未找到 {AppInfo.PrimaryExecutableFileName}，已中止。",
                        "更新",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (System.Windows.MessageBox.Show(
                        "下载完成。是否立即退出并完成安装？（将自动重启 ClipboardX）",
                        "更新",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                GitHubUpdateService.LaunchDeferredReplaceAndRestart(extractDir, installDir, staging, ps1);
                updateLaunched = true;
                Shutdown();
            }
            finally
            {
                if (!updateLaunched && Directory.Exists(staging))
                    GitHubUpdateService.TryDeleteDirectory(staging);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"更新未成功：\n{ex.Message}",
                "检查更新",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static void ShowAboutDialog()
    {
        var body =
            $"版本：{AppInfo.DisplayVersion}\n" +
            $"GitHub：{AppInfo.GitHubUrl}\n\n" +
            "作者：mact\n" +
            "邮箱：chaoji000010@163.com";
        System.Windows.MessageBox.Show(
            body,
            "关于 ClipboardX",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OpenSettings()
    {
        var copy = _settings.ShallowCopy();
        var window = new SettingsWindow(copy);
        window.ClearHistoryRequested += () => _popup?.ClearHistory();
        window.ShowDialog();

        if (window.DialogResult == true)
        {
            _popup?.ApplySettings(copy);
            _settings.MaxItems = copy.MaxItems;
            _settings.HotkeyModifiers = copy.HotkeyModifiers;
            _settings.HotkeyKey = copy.HotkeyKey;
            _settings.FileJumpHotkeyModifiers = copy.FileJumpHotkeyModifiers;
            _settings.FileJumpHotkeyKey = copy.FileJumpHotkeyKey;
            _settings.FileJumpPickerShowDelayMs = copy.FileJumpPickerShowDelayMs;
            _settings.FileJumpPickerFollowMode = FileJumpPickerFollowModes.Normalize(copy.FileJumpPickerFollowMode);
            _settings.FileJumpPickerAutoPopup = copy.FileJumpPickerAutoPopup;
            _settings.FileJumpPickerOpenWhenDialogForeground = copy.FileJumpPickerOpenWhenDialogForeground;
            _settings.FileJumpAutoSyncOnReturn = copy.FileJumpAutoSyncOnReturn;
            _settings.Theme = copy.Theme;
            _settings.PopupPosition = copy.PopupPosition;
            _settings.PopupOpacity = copy.PopupOpacity;
            _settings.HideOnSameAppClick = copy.HideOnSameAppClick;
            _settings.RunAtStartup = copy.RunAtStartup;
            _settings.CheckUpdatesOnStartup = copy.CheckUpdatesOnStartup;
            _settings.EnableShellNavigateInject = copy.EnableShellNavigateInject;
            _settings.FileJumpAutoOnFirstClick = copy.FileJumpAutoOnFirstClick;
            _settings.PreviewMaxLines = copy.PreviewMaxLines;
            _settings.PanelModifierKey = copy.PanelModifierKey;
#if CLIPX_FILEJUMP
            _settings.ExplorerEverythingQuickFindEnabled = copy.ExplorerEverythingQuickFindEnabled;
            _settings.ExplorerEverythingQuickFindMaxResults = copy.ExplorerEverythingQuickFindMaxResults;
            SyncExplorerQuickFindHook();
#endif
            StartupRegistration.Apply(_settings.RunAtStartup);
            _settings.Save();
            UpdateTrayTooltip();
        }
    }

#if CLIPX_FILEJUMP
    /// <summary>按设置安装或卸载资源管理器内 Everything 筛选钩子。</summary>
    private void SyncExplorerQuickFindHook()
    {
        if (!_settings.ExplorerEverythingQuickFindEnabled)
        {
            _explorerQuickFind?.Dispose();
            _explorerQuickFind = null;
            return;
        }

        _explorerQuickFind ??= new ExplorerQuickFindController(Dispatcher, _settings);
        _explorerQuickFind.Start();
    }
#endif

#if DEBUG
    private async void StartWindowInspection()
    {
        _trayIcon?.ShowBalloonTip(2500, "ClipboardX",
            "3 秒后采集前台窗口信息，请切换到目标窗口…", WinForms.ToolTipIcon.Info);
        await Task.Delay(3000);

        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            Dispatcher.Invoke(() =>
                System.Windows.MessageBox.Show("未获取到前台窗口。", "采集窗口",
                    MessageBoxButton.OK, MessageBoxImage.Warning));
            return;
        }

        var info = CollectWindowInfo(hwnd);

        Dispatcher.Invoke(() =>
        {
            try { System.Windows.Clipboard.SetText(info); } catch { }
            System.Windows.MessageBox.Show(
                info + "\n（已复制到剪贴板）",
                "ClipboardX 窗口信息采集",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        });
    }
#endif

    internal async void StartCustomFileDialogWizard()
    {
        _trayIcon?.ShowBalloonTip(2600, "ClipboardX",
            "3 秒后采集前台窗口并尝试多种跳转校验，请先打开目标文件对话框并切到该窗口…",
            WinForms.ToolTipIcon.Info);
        await Task.Delay(3000);

        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd))
        {
            Dispatcher.Invoke(() =>
                System.Windows.MessageBox.Show("未获取到前台窗口。", "自定义文件对话框",
                    MessageBoxButton.OK, MessageBoxImage.Warning));
            return;
        }

        if (FileDialogJumpHelper.ClassifyFileDialog(hwnd) != FileDialogKind.None)
        {
            Dispatcher.Invoke(() =>
                System.Windows.MessageBox.Show(
                    "当前窗口已被内置识别为文件对话框（对话框识别不是 None），不会走自定义规则。\n请仅对内置识别为「无」的窗口使用本功能。",
                    "自定义文件对话框",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information));
            return;
        }

        var probePath = ResolveCustomDialogProbePath();
        if (string.IsNullOrEmpty(probePath) || !Directory.Exists(probePath))
        {
            Dispatcher.Invoke(() =>
                System.Windows.MessageBox.Show(
                    "无法确定用于校验的有效文件夹路径。\n请先在任意已支持跳转的对话框里浏览到目标文件夹（更新「上次路径」），或复制某个已存在目录的完整路径到剪贴板后再试。",
                    "自定义文件对话框",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning));
            return;
        }

        var confirm = Dispatcher.Invoke(() =>
            System.Windows.MessageBox.Show(
                "将依次尝试多种跳转方式，并通过读取当前路径判断是否已进入下列文件夹：\n\n" +
                probePath +
                "\n\n请确认该文件对话框当前**不在**此文件夹内，否则会误判。\n\n确定开始探测？",
                "自定义文件对话框",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question));

        if (confirm != MessageBoxResult.OK) return;

        var rule = CustomFileDialogRule.CreateFromWindow(hwnd);
        rule.StrategyOrder = CustomFileDialogStore.DefaultStrategyOrder.ToList();

        var ok = Dispatcher.Invoke(() =>
            FileDialogJumpHelper.TryProbeCustomStrategies(hwnd, probePath, _settings.EnableShellNavigateInject, rule));

        CustomFileDialogStore.UpsertRule(rule);

        Dispatcher.Invoke(() =>
        {
            var msg = ok
                ? $"已保存。已锁定优先策略：{rule.PinnedStrategy}"
                : "已保存。未能自动校验出有效策略，跳转时将按顺序依次尝试。\n建议：把对话框切换到其他文件夹后，可从托盘再运行一次本向导。";
            System.Windows.MessageBox.Show(msg, "自定义文件对话框", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        });
    }

    private string? ResolveCustomDialogProbePath()
    {
        var mem = _settings.LastFileDialogFolder?.Trim();
        if (!string.IsNullOrEmpty(mem))
        {
            try
            {
                var full = Path.GetFullPath(mem);
                if (Directory.Exists(full)) return full;
            }
            catch { /* ignore */ }
        }

        try
        {
            var clip = System.Windows.Clipboard.GetText()?.Trim();
            if (string.IsNullOrEmpty(clip)) return null;
            var line = clip.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .FirstOrDefault(s => s.Length > 0);
            if (string.IsNullOrEmpty(line)) return null;
            var full = Path.GetFullPath(line);
            return Directory.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

#if DEBUG
    private static string CollectWindowInfo(IntPtr hwnd)
    {
        var className = Win32.GetWindowClassName(hwnd);
        var title = Win32.GetWindowText(hwnd);
        Win32.GetWindowThreadProcessId(hwnd, out var pid);

        var exeName = "(unknown)";
        var exeFullPath = "";
        if (pid != 0)
        {
            var hProc = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    if (Win32.GetModuleFileNameEx(hProc, IntPtr.Zero, sb, sb.Capacity) > 0)
                    {
                        exeFullPath = sb.ToString();
                        exeName = Path.GetFileNameWithoutExtension(exeFullPath).ToLowerInvariant();
                    }
                }
                finally { Win32.CloseHandle(hProc); }
            }
        }

        var uiaName = "";
        try
        {
            var el = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            uiaName = el?.Current.Name ?? "";
        }
        catch { /* ignore */ }

        var kind = FileDialogJumpHelper.ClassifyFileDialog(hwnd);
        var customHit = CustomFileDialogStore.FindMatchingRule(hwnd);

        var childClasses = new List<string>();
        Win32.EnumChildWindows(hwnd, (ch, _) =>
        {
            if (childClasses.Count < 60)
                childClasses.Add(Win32.GetWindowClassName(ch));
            return true;
        }, IntPtr.Zero);

        var output = new StringBuilder();
        output.AppendLine("=== ClipboardX 窗口信息采集 ===");
        output.AppendLine($"句柄:     0x{hwnd.ToInt64():X}");
        output.AppendLine($"类名:     {className}");
        output.AppendLine($"标题:     {title}");
        output.AppendLine($"进程名:   {exeName}");
        output.AppendLine($"进程PID:  {pid}");
        output.AppendLine($"进程路径: {exeFullPath}");
        if (!string.IsNullOrEmpty(uiaName) && uiaName != title)
            output.AppendLine($"UIA名称: {uiaName}");
        output.AppendLine($"对话框识别: {kind}");
        if (kind == FileDialogKind.None)
        {
            if (customHit != null)
            {
                var pin = string.IsNullOrEmpty(customHit.PinnedStrategy) ? "按序尝试" : customHit.PinnedStrategy;
                output.AppendLine($"自定义跳转: 已保存（优先：{pin}）");
            }
            else
                output.AppendLine("自定义跳转: 未保存（设置 → 自定义文件对话框，或托盘向导）");
        }

        output.AppendLine();

        if (childClasses.Count > 0)
        {
            var grouped = childClasses.GroupBy(c => c).OrderByDescending(g => g.Count());
            output.AppendLine($"子窗口 ({childClasses.Count} 个):");
            foreach (var g in grouped)
                output.AppendLine(g.Count() > 1 ? $"  - {g.Key} (×{g.Count()})" : $"  - {g.Key}");
        }
        else
        {
            output.AppendLine("子窗口: (无)");
        }

        // Qt 等无子窗口时，浅层输出 UIA 子树帮助排查
        if (childClasses.Count == 0 || className.Contains("Qt", StringComparison.Ordinal))
        {
            try
            {
                var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                if (root != null)
                {
                    output.AppendLine();
                    output.AppendLine("UIA 子节点:");
                    var uiaChildren = root.FindAll(
                        System.Windows.Automation.TreeScope.Children,
                        System.Windows.Automation.Condition.TrueCondition);
                    foreach (System.Windows.Automation.AutomationElement child in uiaChildren)
                    {
                        try
                        {
                            var ct = child.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
                            var cn = child.Current.Name ?? "";
                            output.AppendLine($"  [{ct}] Name=\"{cn}\"");
                        }
                        catch { /* ignore */ }
                    }
                    if (uiaChildren.Count == 0)
                        output.AppendLine("  (无)");
                }
            }
            catch { /* ignore */ }
        }

        return output.ToString();
    }
#endif

    /// <summary>与托盘相同图稿，用于 WPF 窗口标题栏。</summary>
    public static ImageSource GetWindowIconSource()
    {
        using var icon = CreateTrayIcon();
        return Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            System.Windows.Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
    }

    private static Drawing.Icon CreateTrayIcon() => TrayIconSvg.CreateIcon(32);

    protected override void OnExit(ExitEventArgs e)
    {
        _popup?.Cleanup();
#if CLIPX_FILEJUMP
        _explorerQuickFind?.Dispose();
        _explorerQuickFind = null;
#endif
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
