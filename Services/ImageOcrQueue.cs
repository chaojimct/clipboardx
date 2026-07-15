using System.Windows.Threading;

namespace ClipboardManager;

/// <summary>图片 OCR 后台单线程队列：不阻塞剪贴板监听与 UI。</summary>
internal sealed class ImageOcrQueue
{
    private readonly ClipboardHistoryStore _store;
    private readonly Dispatcher _uiDispatcher;
    private readonly object _gate = new();
    private readonly Queue<ClipboardEntry> _pending = new();
    private readonly HashSet<long> _queuedIds = new();
    private bool _workerRunning;
    private bool _installPromptShown;
    private CancellationTokenSource? _waitInstallCts;

    public ImageOcrQueue(ClipboardHistoryStore store, Dispatcher uiDispatcher)
    {
        _store = store;
        _uiDispatcher = uiDispatcher;
    }

    public void Enqueue(ClipboardEntry entry, AppSettings? settings, Action? onEntryUpdated = null)
    {
        if (settings?.ImageOcrEnabled != true) return;
        if (entry.Type != EntryType.Image || entry.IsQuickPaste) return;
        // 不在此处调用 TryGetImageData——那会触发懒加载把图片字节读入内存。
        // 仅判断是否有可能获取到图片数据：
        //   - 内存中已有（新捕获的剪贴板图片），或
        //   - 配置了懒加载器且有 PersistedId（启动后从数据库按需读取）。
        // 真正的读取与 OCR 在 Worker 中逐条进行，处理完即释放，避免一次把所有图片读入内存。
        var hasInMemoryData = entry.ImageData is { Length: > 0 };
        var canLazyLoad = entry.ImageDataLoader != null
            && entry.PersistedId is long lid && lid > 0;
        if (!hasInMemoryData && !canLazyLoad) return;
        if (!string.IsNullOrWhiteSpace(entry.OcrText)) return;

        lock (_gate)
        {
            if (entry.PersistedId is long id && id > 0)
            {
                if (_queuedIds.Contains(id)) return;
                _queuedIds.Add(id);
            }
            _pending.Enqueue(entry);
            entry.IsOcrPending = true;
            EnsureWorker();
        }

        NotifyEntryUpdated(entry, onEntryUpdated);
    }

    public void EnqueueBackfill(IEnumerable<ClipboardEntry> entries, AppSettings? settings, Action? onEntryUpdated = null, int maxCount = 40)
    {
        if (settings?.ImageOcrEnabled != true) return;
        // 关键：过滤条件不能用 TryGetImageData——那会触发懒加载，
        // 把所有候选图片的完整 PNG 字节一次性读入内存（与启动时降内存的目标相反）。
        // 只用元数据（PersistedId 是否有效、OcrText 是否为空）筛选；
        // 真正的图片字节在 Worker 处理每条时按需加载、用完即释放。
        var list = entries
            .Where(e => e.Type == EntryType.Image && !e.IsQuickPaste
                        && e.PersistedId is long pid && pid > 0
                        && string.IsNullOrWhiteSpace(e.OcrText) && !e.IsOcrPending)
            .Take(maxCount)
            .ToList();
        foreach (var e in list)
            Enqueue(e, settings, onEntryUpdated);
    }

    private void EnsureWorker()
    {
        if (_workerRunning) return;
        _workerRunning = true;
        _ = Task.Run(WorkerLoopAsync);
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (true)
            {
                ClipboardEntry? entry;
                lock (_gate)
                {
                    if (_pending.Count == 0)
                    {
                        _workerRunning = false;
                        return;
                    }
                    entry = _pending.Dequeue();
                }

                if (entry == null) continue;
                await ProcessOneAsync(entry).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_gate) { _workerRunning = false; }
        }
    }

    private async Task ProcessOneAsync(ClipboardEntry entry)
    {
        try
        {
            if (!OcrLanguageInstaller.TryCreateEngine(out _))
            {
                var missing = OcrLanguageInstaller.GetFirstMissingPreferredLanguage();
                if (missing != null)
                    await TryPromptInstallAsync(missing).ConfigureAwait(false);

                if (!OcrLanguageInstaller.TryCreateEngine(out _))
                {
                    FinishEntry(entry, null);
                    return;
                }
            }

            // 此处才真正按需加载图片字节（启动 backfill 时未加载）。
            var bytes = entry.TryGetImageData();
            if (bytes is not { Length: > 0 })
            {
                FinishEntry(entry, null);
                return;
            }

            var text = await ImageOcrService.RecognizePngAsync(bytes).ConfigureAwait(false);

            // OCR 完成后立即释放原始字节：OCR 处理 N 张图片时只持有 1 张的字节在内存，
            // 而不是 N 张。下次需要（预览/粘贴）时通过懒加载重新从数据库读取。
            entry.ReleaseImageData();

            FinishEntry(entry, text);
        }
        catch
        {
            FinishEntry(entry, null);
        }
    }

    private void FinishEntry(ClipboardEntry entry, string? text)
    {
        entry.OcrText = text;
        entry.IsOcrPending = false;
        if (entry.PersistedId is long pid && pid > 0)
            _store.TryUpdateOcrText(pid, text);

        lock (_gate)
        {
            if (entry.PersistedId is long id && id > 0)
                _queuedIds.Remove(id);
        }

        _uiDispatcher.BeginInvoke(() => entry.RaiseOcrDisplayPropertiesChanged(), DispatcherPriority.Background);
    }

    private async Task TryPromptInstallAsync(string missingLanguageTag)
    {
        bool show;
        lock (_gate)
        {
            if (_installPromptShown) return;
            _installPromptShown = true;
            show = true;
        }

        if (!show) return;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _uiDispatcher.InvokeAsync(() =>
        {
            try
            {
                var owner = System.Windows.Application.Current?.MainWindow;
                var result = OcrInstallPromptWindow.ShowDialog(owner, missingLanguageTag);
                tcs.TrySetResult(result == OcrInstallPromptResult.InstallViaSettings
                                 || result == OcrInstallPromptResult.InstallElevated);
            }
            catch
            {
                tcs.TrySetResult(false);
            }
        });

        var startedInstall = await tcs.Task.ConfigureAwait(false);
        if (!startedInstall) return;

        _waitInstallCts?.Cancel();
        _waitInstallCts = new CancellationTokenSource();
        try
        {
            await OcrLanguageInstaller.WaitForLanguageInstalledAsync(
                missingLanguageTag,
                TimeSpan.FromMinutes(8),
                _waitInstallCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private static void NotifyEntryUpdated(ClipboardEntry entry, Action? onEntryUpdated)
    {
        entry.RaiseOcrDisplayPropertiesChanged();
        onEntryUpdated?.Invoke();
    }
}
