using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CoreSpectre.Services;

namespace CoreSpectre.Views;

public enum CompactOsState
{
    Off,
    On,
    Enabling,
    Disabling,
    Unknown
}

public record CleanTargetItem(string Key, string Name, string Path, string FormattedSize, long Bytes, bool IsSelected = true, bool RequiresAdmin = false);

public partial class StorageView : UserControl, INotifyPropertyChanged
{
    private bool _isStorageOperationRunning = false;
    private bool _isScanningTargets = false;
    private bool _isCompactOsPromptOpen = false;
    private CompactOsState _compactOsState = CompactOsState.Unknown;
    private readonly CancellationTokenSource _compactLifetime = new();
    private Task<bool?>? _compactQuery;
    private bool _stopped;
    private int _analyzerViewMode = 0; // 0 = Targets, 1 = Large Folders, 2 = Large Files & Backups
    private int _storageCooldownRemaining = 0;
    private DispatcherTimer? _storageCooldownTimer;
    private readonly DispatcherTimer _autoScanTimer;
    private CancellationTokenSource? _cleanupCts;
    private CancellationTokenSource? _analyzerCts;
    private DateTime _lastScanUtc = DateTime.MinValue;
    private string _watchedDriveLetter = "C";
    private string _currentSortMode = "SizeDesc";
    private UserSettingsModel _settings = new();
    private Action? _saveSettingsCallback;

    private List<CleanTargetItem> _cachedTargets = new();

    public DateTime LastScanUtc => _lastScanUtc;

    public event Action<FrameworkElement>? OnDiskDropdownRequested;
    public event Action<string>? OnStatusChanged;
    public event Action? OnCleanupCompleted;
    public event PropertyChangedEventHandler? PropertyChanged;
    public Func<string, string, string, string, string, Task<bool>>? ShowConfirmDialogAsync { get; set; }

    public CompactOsState CompactOsState
    {
        get => _compactOsState;
        internal set
        {
            if (_compactOsState != value)
            {
                _compactOsState = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CompactOsButtonText));
                OnPropertyChanged(nameof(CanExecuteCompactOs));
            }
        }
    }

    public string CompactOsButtonText => CompactOsState switch
    {
        CompactOsState.On => LocalizationService.Get("Storage.CompactOsOn"),
        CompactOsState.Enabling => LocalizationService.Get("Storage.CompactOsEnabling"),
        CompactOsState.Disabling => LocalizationService.Get("Storage.CompactOsDisabling"),
        CompactOsState.Unknown => LocalizationService.IsEnglish ? "CompactOS: Unknown" : "CompactOS: Неизвестно",
        _ => LocalizationService.Get("Storage.CompactOsOff")
    };

    public bool CanExecuteCompactOs =>
        CompactOsState is not (CompactOsState.Enabling or CompactOsState.Disabling);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(CompactOsButtonText) or nameof(CanExecuteCompactOs))
        {
            SyncCompactOsControls();
        }
    }

    private void SyncCompactOsControls()
    {
        if (BtnCompactOs != null)
        {
            BtnCompactOs.SetCurrentValue(IsEnabledProperty, CanExecuteCompactOs);
        }
        if (BtnCompactOsText != null)
        {
            BtnCompactOsText.SetCurrentValue(TextBlock.TextProperty, CompactOsButtonText);
        }
    }

    public StorageView()
    {
        InitializeComponent();
        RootGrid.DataContext = this;
        SyncCompactOsControls();
        _settings = SettingsPersistenceService.Load();
        _currentSortMode = _settings.StorageSortMode ?? "SizeDesc";
        UpdateSortButtonText();

        _autoScanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autoScanTimer.Tick += async (s, e) =>
        {
            if (IsVisible && !_isStorageOperationRunning && !_isScanningTargets && _analyzerViewMode == 0)
            {
                await ScanJunkAsync(showLog: false, isManualClick: false, isBackground: true);
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (_stopped) return;
            if (IsVisible) _autoScanTimer.Start();
            else _autoScanTimer.Stop();
            if (IsVisible && CompactOsState == CompactOsState.Unknown && !_isCompactOsPromptOpen)
                _ = RefreshCompactOsButtonAsync();
        };
    }

    public void StopAutoScan()
    {
        if (_stopped) return;
        _stopped = true;
        _autoScanTimer.Stop();
        _storageCooldownTimer?.Stop();
        _cleanupCts?.Cancel();
        _analyzerCts?.Cancel();
        _compactLifetime.Cancel();
        _compactLifetime.Dispose();
    }

    public void BindSettings(UserSettingsModel settings, Action saveCallback)
    {
        _settings = settings;
        _saveSettingsCallback = saveCallback;
        _currentSortMode = settings.StorageSortMode ?? "SizeDesc";
        UpdateSortButtonText();
        RefreshSelectionStates();
    }

    private void PersistSettings()
    {
        if (_saveSettingsCallback != null)
            _saveSettingsCallback();
        else
            SettingsPersistenceService.Save(_settings);
    }

    private void UpdateSortButtonText()
    {
        bool isEn = LocalizationService.IsEnglish;
        TxtSortTargets.Text = _currentSortMode switch
        {
            "SizeAsc" => isEn ? "Sort: Size ↑" : "Сорт: Размер ↑",
            "NameAsc" => isEn ? "Sort: Name A-Z" : "Сорт: Имя А-Я",
            _ => isEn ? "Sort: Size ↓" : "Сорт: Размер ↓"
        };
    }

    public void ApplyLanguage(string language)
    {
        bool isEn = language == "EN";
        string normalizedLang = isEn ? "EN" : "RU";
        if (LocalizationService.CurrentLanguage != normalizedLang)
        {
            LocalizationService.CurrentLanguage = normalizedLang;
        }
        StorageTabHeading.Text = isEn ? $"Storage & System Cleanup ({_watchedDriveLetter}:)" : $"Хранилище и системная очистка ({_watchedDriveLetter}:)";
        StorageTabSub.Text = LocalizationService.Get("Storage.Subtitle");
        StorageGaugeTitle.Text = isEn ? $"Storage Capacity ({_watchedDriveLetter}:):" : $"Емкость накопителя ({_watchedDriveLetter}:):";
        CleanTargetsTitle.Text = _analyzerViewMode switch
        {
            1 => isEn ? $"LARGE FOLDERS ON {_watchedDriveLetter}: (>50 MB)" : $"КРУПНЫЕ ПАПКИ НА {_watchedDriveLetter}: (>50 МБ)",
            2 => isEn ? $"LARGEST FILES (>100 MB) & BACKUPS" : $"КРУПНЫЕ ФАЙЛЫ (>100 МБ) И БЭКАПЫ",
            _ => LocalizationService.Get("Storage.Targets")
        };
        BtnScanJunkText.Text = LocalizationService.Get("Storage.ScanBtn");
        BtnCleanSystemText.Text = _isStorageOperationRunning ? (isEn ? "Cleaning..." : "Очистка...")
            : _storageCooldownRemaining > 0 ? (isEn ? $"Cooldown ({_storageCooldownRemaining}s)" : $"Ожидание ({_storageCooldownRemaining} сек)")
            : LocalizationService.Get("Storage.CleanBtn");
        BtnStorageDiskDropdown.ToolTip = isEn ? "Select Drive" : "Выбрать диск";
        BtnTempAgeFilter.ToolTip = isEn ? "Only delete Temp files older than N days" : "Удалять только временные файлы старше N дней";
        BtnLargeFoldersMode.ToolTip = isEn ? "Analyze large folders, files and backups" : "Анализировать крупные папки, файлы и бэкапы";
        BtnSortTargets.ToolTip = isEn ? "Sort Targets" : "Сортировать цели";
        BtnCancelCleanupText.Text = isEn ? "Cancel" : "Отмена";
        BtnRollbackSystemText.Text = LocalizationService.Get("Storage.RollbackBtn");
        TxtLargeFoldersMode.Text = _analyzerViewMode switch
        {
            1 => isEn ? "Files & Backups" : "Файлы и бэкапы",
            2 => isEn ? "Cleanup Targets" : "Цели очистки",
            _ => isEn ? "Analyze Disk" : "Анализ диска"
        };
        TxtTempAgeFilter.Text = _settings.TempMinAgeDays switch
        {
            3 => isEn ? "Temp: >3d" : "Temp: >3д",
            7 => isEn ? "Temp: >7d" : "Temp: >7д",
            _ => isEn ? "Temp: All" : "Temp: Все"
        };
        StorageLogTitle.Text = LocalizationService.Get("Storage.ConsoleTitle");

        if (StorageLogFooter.Text == "Ready" || StorageLogFooter.Text == "Готов к работе")
        {
            StorageLogFooter.Text = LocalizationService.Get("Status.Ready");
        }

        UpdateSortButtonText();
        OnPropertyChanged(nameof(CompactOsButtonText));
    }

    public void UpdateCapacity(string driveLetter, double freeGb, double totalGb)
    {
        string letter = StorageService.ValidateDriveLetter(driveLetter, "C");
        if (_watchedDriveLetter != letter)
        {
            _watchedDriveLetter = letter;
            if (_analyzerViewMode != 0) _ = RefreshAnalyzerAsync();
        }
        bool isEn = LocalizationService.IsEnglish;

        StorageTabHeading.Text = isEn ? $"Storage & System Cleanup ({letter}:)" : $"Хранилище и системная очистка ({letter}:)";
        StorageGaugeTitle.Text = isEn ? $"Storage Capacity ({_watchedDriveLetter}:):" : $"Емкость накопителя ({_watchedDriveLetter}:):";
        StorageHeaderStatus.Text = totalGb <= 0 ? (isEn ? "Drive unavailable" : "Диск недоступен") : isEn
            ? $"Free: {freeGb:0.0} GB of {totalGb:0.0} GB"
            : $"Свободно: {freeGb:0.0} ГБ из {totalGb:0.0} ГБ";

        double usedPercent = totalGb > 0 ? ((totalGb - freeGb) / totalGb) * 100.0 : 0;
        StorageProgressC.Value = Math.Clamp(usedPercent, 0, 100);
    }

    private bool IsTargetKeySelected(string key)
    {
        var disabled = _settings.DisabledCleanupTargets ?? new List<string>();
        if (disabled.Contains(key, StringComparer.OrdinalIgnoreCase))
            return false;

        return key switch
        {
            "user_temp" or "win_temp" => _settings.CleanTempFiles,
            "sw_dist" or "deliv_opt" => _settings.CleanSoftwareDistribution,
            "crash_dumps" or "wer" or "cbs_logs" => _settings.CleanLogsAndReports,
            "gpu_shader" => _settings.CleanGpuShaderCache,
            "browser_cache" => _settings.CleanBrowserCache,
            "recycle_bin" => _settings.CleanRecycleBin,
            "dns_cache" => _settings.CleanDnsCache,
            "dism_cleanup" => _settings.CleanDismComponentCleanup,
            "dism_resetbase" => _settings.CleanDismResetBase,
            _ => true
        };
    }

    private void RefreshSelectionStates()
    {
        if (_cachedTargets.Count == 0) return;
        _cachedTargets = _cachedTargets
            .Select(t => t with { IsSelected = IsTargetKeySelected(t.Key) })
            .ToList();
        ApplySorting();
    }

    public async Task PerformScanCoreAsync(bool showLog = false)
    {
        bool isEn = LocalizationService.IsEnglish;

        if (showLog)
        {
            LogStorage(isEn ? "--> Starting scan for cleanable cache targets..." : "--> Сканирование безопасных целей очистки...");
            OnStatusChanged?.Invoke(isEn ? "Scanning temporary junk files..." : "Сканирование временных файлов...");
        }

        try
        {
            var settingsSnapshot = new UserSettingsModel
            {
                CleanTempFiles = _settings.CleanTempFiles,
                CleanSoftwareDistribution = _settings.CleanSoftwareDistribution,
                CleanLogsAndReports = _settings.CleanLogsAndReports,
                CleanRecycleBin = _settings.CleanRecycleBin,
                CleanGpuShaderCache = _settings.CleanGpuShaderCache,
                CleanBrowserCache = _settings.CleanBrowserCache,
                TempMinAgeDays = _settings.TempMinAgeDays,
                DisabledCleanupTargets = [.. _settings.DisabledCleanupTargets]
            };
            List<CleanableTarget> targets;
            do
            {
                isEn = LocalizationService.IsEnglish;
                bool scanInEnglish = isEn;
                targets = await Task.Run(() => StorageService.ScanTempLocations(scanInEnglish, settingsSnapshot));
                if (_stopped) return;
            } while (isEn != LocalizationService.IsEnglish);

            var items = targets.Select(t => new CleanTargetItem(
                t.Key,
                t.Name,
                t.Path,
                StorageService.FormatBytes(t.Bytes),
                t.Bytes,
                IsTargetKeySelected(t.Key),
                t.RequiresAdmin
            )).ToList();

            items.Add(new CleanTargetItem(
                "dns_cache",
                isEn ? "Flush DNS Resolver Cache" : "Сброс сетевого кэша DNS",
                "ipconfig /flushdns",
                isEn ? "System" : "Команда",
                -1,
                IsTargetKeySelected("dns_cache"),
                false
            ));

            items.Add(new CleanTargetItem(
                "dism_cleanup",
                isEn ? "DISM Component Store Cleanup" : "Очистка компонентов Windows (DISM)",
                "DISM /StartComponentCleanup",
                "WinSxS",
                -1,
                IsTargetKeySelected("dism_cleanup"),
                true
            ));

            items.Add(new CleanTargetItem(
                "dism_resetbase",
                isEn ? "DISM ResetBase (Irreversible updates)" : "DISM ResetBase (Необратимая очистка обновлений)",
                "DISM /StartComponentCleanup /ResetBase",
                "ResetBase",
                -1,
                IsTargetKeySelected("dism_resetbase"),
                true
            ));

            _cachedTargets = items;
            _lastScanUtc = DateTime.UtcNow;

            ApplySorting();

            long selectedBytes = _cachedTargets.Where(t => t.IsSelected && t.Bytes > 0).Sum(t => t.Bytes);
            long totalBytes = targets.Sum(t => t.Bytes);

            if (showLog)
            {
                foreach (var t in targets)
                {
                    if (t.Bytes > 0)
                    {
                        string mark = IsTargetKeySelected(t.Key) ? "•" : "○";
                        LogStorage($"    {mark} {t.Name}: {StorageService.FormatBytes(t.Bytes)}");
                    }
                }

                string summary = isEn
                    ? $"[OK] Scan finished. Selected for cleanup: {StorageService.FormatBytes(selectedBytes)} (total found: {StorageService.FormatBytes(totalBytes)})."
                    : $"[OK] Сканирование завершено. Выбрано к очистке: {StorageService.FormatBytes(selectedBytes)} (всего найдено: {StorageService.FormatBytes(totalBytes)}).";
                LogStorage(summary);
                OnStatusChanged?.Invoke(summary);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Storage scan error", ex);
            if (showLog) LogStorage($"[ERROR] Scan error: {ex.Message}");
        }
    }

    private void ApplySorting()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ApplySorting);
            return;
        }

        var sv = FindScrollViewer(CleanTargetsList);
        double verticalOffset = sv?.VerticalOffset ?? 0;

        var sorted = _currentSortMode switch
        {
            "SizeAsc" => _cachedTargets.OrderBy(t => t.Bytes < 0 ? 1 : 0).ThenBy(t => t.Bytes).ThenBy(t => t.Name).ToList(),
            "NameAsc" => _cachedTargets.OrderBy(t => t.Name).ToList(),
            _ => _cachedTargets.OrderBy(t => t.Bytes < 0 ? 1 : 0).ThenByDescending(t => t.Bytes).ThenBy(t => t.Name).ToList()
        };

        CleanTargetsList.ItemsSource = sorted;

        if (sv != null && verticalOffset > 0)
        {
            Dispatcher.BeginInvoke(() => sv.ScrollToVerticalOffset(verticalOffset), DispatcherPriority.Loaded);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var res = FindScrollViewer(child);
            if (res != null) return res;
        }
        return null;
    }

    private void BtnLargeFoldersMode_Click(object sender, RoutedEventArgs e)
    {
        _analyzerViewMode = (_analyzerViewMode + 1) % 3;
        _ = RefreshAnalyzerAsync();
    }

    private async Task RefreshAnalyzerAsync()
    {
        _analyzerCts?.Cancel();
        if (_stopped) return;

        int expectedMode = _analyzerViewMode;
        bool analyzing = expectedMode != 0;
        CleanTargetsList.Visibility = analyzing ? Visibility.Collapsed : Visibility.Visible;
        LargeFoldersList.Visibility = analyzing ? Visibility.Visible : Visibility.Collapsed;
        BtnSortTargets.Visibility = analyzing ? Visibility.Collapsed : Visibility.Visible;
        LargeFoldersList.ItemsSource = null;
        ApplyLanguage(LocalizationService.CurrentLanguage);
        if (!analyzing) return;

        using var cts = new CancellationTokenSource();
        _analyzerCts = cts;
        var ct = cts.Token;
        string drive = _watchedDriveLetter;
        try
        {
            OnStatusChanged?.Invoke(LocalizationService.IsEnglish ? $"Analyzing drive {drive}:..." : $"Анализ диска {drive}:...");
            var items = await Task.Run(() =>
            {
                if (expectedMode == 1) return StorageService.ScanLargeDirectories(drive, 12, ct);
                var combined = StorageService.FindBackupAndArchiveAudits(ct);
                combined.AddRange(StorageService.ScanLargestFiles(drive, 100L * 1024 * 1024, 15, ct));
                return combined.DistinctBy(x => x.Path).OrderByDescending(x => x.SizeBytes).ToList();
            }, ct);
            ct.ThrowIfCancellationRequested();
            LargeFoldersList.ItemsSource = items;
            bool isEn = LocalizationService.IsEnglish;
            OnStatusChanged?.Invoke(expectedMode == 1
                ? (isEn ? $"Found {items.Count} directories > 50 MB on {drive}:." : $"Каталогов > 50 МБ на диске {drive}: найдено {items.Count}.")
                : (isEn ? $"Found {items.Count} large files / backups." : $"Найдено крупных файлов и бэкапов: {items.Count}."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.Error("Disk analysis failed", ex);
            if (!ct.IsCancellationRequested)
                OnStatusChanged?.Invoke(LocalizationService.IsEnglish ? "Disk analysis failed." : "Ошибка анализа диска.");
        }
        finally
        {
            if (ReferenceEquals(_analyzerCts, cts)) _analyzerCts = null;
        }
    }

    private void BtnTempAgeFilter_Click(object sender, RoutedEventArgs e)
    {
        _settings.TempMinAgeDays = _settings.TempMinAgeDays switch
        {
            0 => 3,
            3 => 7,
            _ => 0
        };
        PersistSettings();
        ApplyLanguage(LocalizationService.CurrentLanguage);
        _ = ScanJunkAsync(showLog: false);
    }

    private async void TargetCardInfo_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string key && !string.IsNullOrEmpty(key))
        {
            bool isEn = LocalizationService.IsEnglish;
            var previews = await Task.Run(() => StorageService.PreviewTargetFiles(key, 10));
            if (previews.Count == 0)
            {
                LogStorage(isEn ? $"[Preview] '{key}': no cleanable files found." : $"[Предпросмотр] '{key}': файлы не найдены.");
                return;
            }
            LogStorage(isEn ? $"[Preview] Top files in '{key}':" : $"[Предпросмотр] Крупные файлы в '{key}':");
            foreach (var p in previews)
            {
                LogStorage($"    • {p.Name} ({p.FormattedSize}, {p.LastModified})");
            }
        }
    }

    private Task<bool?> QueryCompactOsAsync()
    {
        if (_compactQuery is { IsCompleted: false }) return _compactQuery;
        var ct = _compactLifetime.Token;
        return _compactQuery = Task.Run(() => StorageService.IsCompactOsEnabledAsync(ct));
    }

    private async Task RefreshCompactOsButtonAsync()
    {
        if (_stopped || !CanExecuteCompactOs) return;
        var query = QueryCompactOsAsync();
        bool? enabled = await query;
        if (!_stopped && !_isCompactOsPromptOpen && CanExecuteCompactOs && ReferenceEquals(_compactQuery, query))
            ApplyCompactOsState(enabled);
    }

    private void ApplyCompactOsState(bool? enabled) => CompactOsState = enabled switch
    {
        true => CompactOsState.On,
        false => CompactOsState.Off,
        null => CompactOsState.Unknown
    };

    private async void BtnCompactOs_Click(object sender, RoutedEventArgs e)
    {
        if (_stopped || !CanExecuteCompactOs || _isCompactOsPromptOpen || _isStorageOperationRunning) return;
        _isCompactOsPromptOpen = true;
        bool enteredTransition = false;

        try
        {
            var ct = _compactLifetime.Token;
            bool? currentlyOn = await QueryCompactOsAsync();
            if (_stopped) return;
            ApplyCompactOsState(currentlyOn);
            bool isEn = LocalizationService.IsEnglish;
            if (!currentlyOn.HasValue)
            {
                OnStatusChanged?.Invoke(isEn ? "Could not determine CompactOS state." : "Не удалось определить состояние CompactOS.");
                return;
            }
            bool wantEnable = !currentlyOn.Value;
            bool isAdmin = StorageService.IsAdministrator();

            if (ShowConfirmDialogAsync != null)
            {
                string title = isAdmin ? "CompactOS" : (isEn ? "CompactOS (Admin Required)" : "CompactOS (Нужны права администратора)");
                string msg = wantEnable
                    ? (isEn ? "Enable Windows CompactOS compression? This safely compresses OS system files to save ~2-4 GB on SSD."
                            : "Включить сжатие системных файлов CompactOS? Это безопасно освободит ~2–4 ГБ на SSD без потери обновлений.")
                    : (isEn ? "Disable CompactOS compression and decompress OS system files?"
                            : "Отключить сжатие CompactOS и распаковать системные файлы?");
                if (!isAdmin)
                {
                    msg += isEn
                        ? "\n\nCoreSpectre will request Administrator privileges (UAC) via a secure background worker without closing the window."
                        : "\n\nCoreSpectre запросит права администратора (UAC) для фонового процесса, не закрывая окно приложения.";
                }
                string confirmBtn = isAdmin ? (isEn ? "Proceed" : "Выполнить") : (isEn ? "Grant Rights & Run" : "Дать права и выполнить");
                if (!await ShowConfirmDialogAsync(title, msg, confirmBtn, isEn ? "Cancel" : "Отмена", "IconHardDrive"))
                    return;
            }

            if (_stopped || _isStorageOperationRunning) return;
            enteredTransition = true;
            CompactOsState = wantEnable ? CompactOsState.Enabling : CompactOsState.Disabling;
            bool currentIsEn = LocalizationService.IsEnglish;
            LogStorage(currentIsEn
                ? "[CompactOS] Applying compression state (this may take several minutes)..."
                : "[CompactOS] Применение состояния сжатия (может занять несколько минут)...");
            OnStatusChanged?.Invoke(currentIsEn ? "Applying CompactOS state..." : "Применение состояния CompactOS...");

            if (isAdmin)
            {
                var (_, message) = await Task.Run(() => StorageService.SetCompactOsAsync(
                    wantEnable,
                    currentIsEn,
                    line => LogStorage($"[CompactOS] {line}"), ct));
                LogStorage($"[CompactOS] {message}");
                OnStatusChanged?.Invoke(message);
            }
            else
            {
                var job = new ElevatedCleanupJob(
                    IsEnglish: currentIsEn,
                    CleanTemp: false,
                    CleanSoftwareDistribution: false,
                    CleanLogsAndReports: false,
                    CleanGpuShaderCache: false,
                    CleanBrowserCache: false,
                    CleanDismComponentCleanup: false,
                    CleanDismResetBase: false,
                    DisabledTargetKeys: new List<string>(),
                    Action: wantEnable ? "compact_on" : "compact_off"
                );
                var run = await StorageService.ExecuteViaElevatedWorkerAsync(job, LogStorage, ct);
                string statusMsg = run.Result?.SummaryMessage ?? run.ErrorMessage ?? (LocalizationService.IsEnglish ? "CompactOS finished." : "CompactOS завершён.");
                if (run.ElevationCancelled || !string.IsNullOrEmpty(run.ErrorMessage))
                {
                    LogStorage($"[CompactOS] {statusMsg}");
                }
                OnStatusChanged?.Invoke(statusMsg);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("CompactOS operation failed", ex);
            if (!_stopped) OnStatusChanged?.Invoke(ex.Message);
        }
        finally
        {
            if (enteredTransition && !_stopped)
            {
                bool? enabled = await QueryCompactOsAsync();
                if (!_stopped)
                {
                    ApplyCompactOsState(enabled);
                    OnCleanupCompleted?.Invoke();
                }
            }
            _isCompactOsPromptOpen = false;
        }
    }

    private void BtnOpenLargeFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string folder)
        {
            try
            {
                string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                if (Directory.Exists(folder))
                {
                    using var process = Process.Start(new ProcessStartInfo(explorer, $"\"{Path.GetFullPath(folder)}\"") { UseShellExecute = true });
                }
                else if (File.Exists(folder))
                {
                    using var process = Process.Start(new ProcessStartInfo(explorer, $"/select,\"{Path.GetFullPath(folder)}\"") { UseShellExecute = true });
                }
            }
            catch (Exception ex) { Logger.Warn($"Open large folder error: {ex.Message}"); }
        }
    }

    private async void TargetCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not string key || string.IsNullOrEmpty(key))
            return;

        bool wantChecked = cb.IsChecked == true;
        _settings.DisabledCleanupTargets ??= new List<string>();

        if (key == "dns_cache")
        {
            _settings.CleanDnsCache = wantChecked;
        }
        else if (key == "dism_cleanup")
        {
            _settings.CleanDismComponentCleanup = wantChecked;
        }
        else if (key == "dism_resetbase")
        {
            if (wantChecked && ShowConfirmDialogAsync != null)
            {
                bool isEnDlg = LocalizationService.IsEnglish;
                string title = isEnDlg ? "DISM ResetBase Warning" : "Предупреждение DISM ResetBase";
                string msg = isEnDlg
                    ? "WARNING: After executing DISM /ResetBase, all currently installed Windows updates will become permanent and cannot be uninstalled. Do you want to enable this?"
                    : "ВНИМАНИЕ: После выполнения DISM /ResetBase все установленные обновления Windows станут постоянными и их нельзя будет удалить. Включить эту опцию?";
                bool confirmed = await ShowConfirmDialogAsync(title, msg, isEnDlg ? "Enable" : "Включить", isEnDlg ? "Cancel" : "Отмена", "IconShield");
                if (!confirmed)
                {
                    _settings.CleanDismResetBase = false;
                    PersistSettings();
                    RefreshSelectionStates();
                    return;
                }
            }
            _settings.CleanDismResetBase = wantChecked;
        }
        else if (key == "recycle_bin")
        {
            _settings.CleanRecycleBin = wantChecked;
            _settings.DisabledCleanupTargets.RemoveAll(k => k.Equals("recycle_bin", StringComparison.OrdinalIgnoreCase));
        }
        else if (key == "gpu_shader")
        {
            _settings.CleanGpuShaderCache = wantChecked;
            _settings.DisabledCleanupTargets.RemoveAll(k => k.Equals("gpu_shader", StringComparison.OrdinalIgnoreCase));
        }
        else if (key == "browser_cache")
        {
            _settings.CleanBrowserCache = wantChecked;
            _settings.DisabledCleanupTargets.RemoveAll(k => k.Equals("browser_cache", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            (bool groupEnabled, Action<bool> setGroup, string[] siblings) = key switch
            {
                "user_temp" or "win_temp" => (_settings.CleanTempFiles, (Action<bool>)(v => _settings.CleanTempFiles = v), new[] { "user_temp", "win_temp" }),
                "sw_dist" or "deliv_opt" => (_settings.CleanSoftwareDistribution, (Action<bool>)(v => _settings.CleanSoftwareDistribution = v), new[] { "sw_dist", "deliv_opt" }),
                "crash_dumps" or "wer" or "cbs_logs" => (_settings.CleanLogsAndReports, (Action<bool>)(v => _settings.CleanLogsAndReports = v), new[] { "crash_dumps", "wer", "cbs_logs" }),
                _ => (true, _ => { }, Array.Empty<string>())
            };

            if (wantChecked)
            {
                if (!groupEnabled)
                {
                    setGroup(true);
                    foreach (var s in siblings)
                    {
                        if (!s.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                            !_settings.DisabledCleanupTargets.Contains(s, StringComparer.OrdinalIgnoreCase))
                        {
                            _settings.DisabledCleanupTargets.Add(s);
                        }
                    }
                }
                _settings.DisabledCleanupTargets.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                if (!_settings.DisabledCleanupTargets.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    _settings.DisabledCleanupTargets.Add(key);
                }
                if (siblings.Length > 0 && siblings.All(s => _settings.DisabledCleanupTargets.Contains(s, StringComparer.OrdinalIgnoreCase)))
                {
                    setGroup(false);
                    foreach (var s in siblings)
                    {
                        _settings.DisabledCleanupTargets.RemoveAll(k => k.Equals(s, StringComparison.OrdinalIgnoreCase));
                    }
                }
            }
        }

        PersistSettings();
        RefreshSelectionStates();

        bool isEn = LocalizationService.IsEnglish;
        long selectedBytes = _cachedTargets.Where(t => t.IsSelected && t.Bytes > 0).Sum(t => t.Bytes);
        OnStatusChanged?.Invoke(isEn
            ? $"Selected for cleanup: {StorageService.FormatBytes(selectedBytes)}"
            : $"Выбрано к очистке: {StorageService.FormatBytes(selectedBytes)}");
    }

    public async Task ScanJunkAsync(bool showLog = true, bool isManualClick = false, bool isBackground = false)
    {
        if (_stopped || _isStorageOperationRunning || _isScanningTargets) return;
        if (isManualClick && _storageCooldownRemaining > 0) return;

        _isScanningTargets = true;
        if (!isBackground)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                BtnScanJunk.IsEnabled = false;
                if (_storageCooldownRemaining <= 0) BtnCleanSystem.IsEnabled = false;
            });
        }

        try
        {
            await PerformScanCoreAsync(showLog);
        }
        finally
        {
            _isScanningTargets = false;
            if (!isBackground)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_storageCooldownRemaining <= 0)
                    {
                        BtnScanJunk.IsEnabled = true;
                        BtnCleanSystem.IsEnabled = true;
                    }
                });
            }
        }
    }

    private async void BtnScanJunk_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ScanJunkAsync(showLog: true, isManualClick: true);
        }
        catch (Exception ex)
        {
            Logger.Error("Scan junk button click failed", ex);
        }
    }

    private void BtnCancelCleanup_Click(object sender, RoutedEventArgs e)
    {
        _cleanupCts?.Cancel();
        BtnCancelCleanup.IsEnabled = false;
    }

    private async void BtnCleanSystem_Click(object sender, RoutedEventArgs e)
    {
        if (_stopped || _isStorageOperationRunning || _isCompactOsPromptOpen || !CanExecuteCompactOs || _storageCooldownRemaining > 0) return;

        bool isEn = LocalizationService.IsEnglish;
        _isStorageOperationRunning = true;
        _cleanupCts = new CancellationTokenSource();

        BtnCleanSystem.IsEnabled = false;
        BtnScanJunk.IsEnabled = false;
        BtnCancelCleanup.Visibility = Visibility.Visible;
        BtnCancelCleanup.IsEnabled = true;
        BtnCleanSystemText.Text = isEn ? "Cleaning..." : "Очистка...";

        LogStorage(isEn ? "====== SYSTEM CACHE CLEANUP INITIATED ======" : "====== ЗАПУСК ОЧИСТКИ СИСТЕМНОГО КЭША ======");
        OnStatusChanged?.Invoke(isEn ? "Starting system cleanup..." : "Запуск системной очистки...");

        try
        {
            var opts = new StorageCleanupOptions(
                _settings.CleanTempFiles,
                _settings.CleanSoftwareDistribution,
                _settings.CleanLogsAndReports,
                _settings.CleanDnsCache,
                _settings.CleanRecycleBin,
                _settings.CleanGpuShaderCache,
                _settings.CleanBrowserCache,
                _settings.CleanDismComponentCleanup,
                _settings.CleanDismResetBase,
                new HashSet<string>(_settings.DisabledCleanupTargets ?? new List<string>(), StringComparer.OrdinalIgnoreCase),
                _settings.TempMinAgeDays
            );

            var ct = _cleanupCts.Token;
            var res = await Task.Run(() => StorageService.ExecuteDeepCleanupAsync(isEn, stepMsg =>
            {
                LogStorage(stepMsg);
            }, opts, adminStagesOnly: false, cancellationToken: ct));

            OnStatusChanged?.Invoke(res.SummaryMessage);

            if (ct.IsCancellationRequested) return;
            await PerformScanCoreAsync(showLog: false);
            OnCleanupCompleted?.Invoke();

            if (!_cleanupCts.IsCancellationRequested &&
                res.AdminSkippedStages > 0 &&
                !StorageService.IsAdministrator() &&
                ShowConfirmDialogAsync != null)
            {
                string title = isEn ? "Administrator Rights Required" : "Требуются права администратора";
                string msg = isEn
                    ? $"Cleanup finished, but {res.AdminSkippedStages} system area(s) still contain files that require Administrator privileges (Windows Temp, Windows Update cache, WER/CBS logs, or DISM).\n\nCoreSpectre will run a secure elevated background worker to clean the remaining system areas without closing the app. Grant rights?"
                    : $"Очистка завершена, но часть системных областей ({res.AdminSkippedStages} этап(а/ов)) не была очищена — для них требуются права администратора (Windows Temp, кэш обновлений, отчёты WER/CBS или DISM).\n\nCoreSpectre запустит защищённый фоновый процесс с правами администратора и доочистит оставшиеся области, не закрывая окно приложения. Дать права?";
                string confirmBtn = isEn ? "Grant Rights" : "Дать права";
                string cancelBtn = isEn ? "Skip" : "Нет";

                bool grantAdmin = await ShowConfirmDialogAsync(title, msg, confirmBtn, cancelBtn, "IconShield");
                if (ct.IsCancellationRequested) return;
                if (grantAdmin)
                {
                    await RunElevatedWorkerCleanupAsync(isEn, opts, ct);
                }
                else
                {
                    OnStatusChanged?.Invoke(isEn ? "Remaining protected files skipped." : "Защищённые системные файлы оставлены без изменений.");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("System cleanup exception", ex);
            LogStorage($"[ERROR] Cleanup exception: {ex.Message}");
            OnStatusChanged?.Invoke($"Cleanup error: {ex.Message}");
        }
        finally
        {
            BtnCancelCleanup.Visibility = Visibility.Collapsed;
            _cleanupCts?.Dispose();
            _cleanupCts = null;
            _isStorageOperationRunning = false;
            if (!_stopped) StartStorageCooldown(5);
        }
    }

    private async void BtnRollbackSystem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool isEn = LocalizationService.IsEnglish;
            bool isAdmin = StorageService.IsAdministrator();

            if (ShowConfirmDialogAsync != null)
            {
                string title = isEn ? "Windows System Restore" : "Восстановление системы Windows";
                string msg = isEn
                    ? "CoreSpectre automatically creates a Windows restore point ('CoreSpectre Before Deep Cleanup') before deep cleanup when elevated and System Protection is enabled.\n\nClick 'Open Restore' to launch the Windows System Restore wizard (rstrui.exe)."
                    : "CoreSpectre автоматически создаёт точку восстановления Windows («CoreSpectre Before Deep Cleanup») перед очисткой при наличии прав администратора и включённой Защите системы.\n\nНажмите «Открыть откат», чтобы запустить мастер восстановления Windows (rstrui.exe).";
                string confirmBtn = isEn ? "Open Restore" : "Открыть откат";
                string cancelBtn = isEn ? "Cancel" : "Отмена";

                bool proceed = await ShowConfirmDialogAsync(title, msg, confirmBtn, cancelBtn, "IconArrowUUpLeft");
                if (!proceed) return;
            }

            bool launched = StorageService.LaunchSystemRestore();
            if (launched)
            {
                string status = isEn ? "Windows System Restore wizard (rstrui.exe) launched." : "Мастер восстановления системы Windows (rstrui.exe) запущен.";
                LogStorage(status);
                OnStatusChanged?.Invoke(status);
            }
            else
            {
                string status = isEn
                    ? $"Could not launch rstrui.exe{(isAdmin ? "" : " (try running as Administrator)")}."
                    : $"Не удалось запустить rstrui.exe{(isAdmin ? "" : " (попробуйте запуск от имени администратора)")}.";
                LogStorage(status);
                OnStatusChanged?.Invoke(status);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Rollback launch failed", ex);
        }
    }

    private void BtnSortTargets_Click(object sender, RoutedEventArgs e)
    {
        _currentSortMode = _currentSortMode switch
        {
            "SizeDesc" => "SizeAsc",
            "SizeAsc" => "NameAsc",
            _ => "SizeDesc"
        };

        UpdateSortButtonText();
        ApplySorting();

        _settings.StorageSortMode = _currentSortMode;
        PersistSettings();
    }

    private async Task RunElevatedWorkerCleanupAsync(bool isEn, StorageCleanupOptions opts, CancellationToken ct)
    {
        LogStorage(isEn
            ? "[UAC] Starting elevated cleanup worker over ACL Named Pipe (window stays active)..."
            : "[UAC] Запуск фонового процесса очистки по защищённому каналу (окно остаётся открытым)...");
        OnStatusChanged?.Invoke(isEn ? "Running elevated system cleanup..." : "Выполняется очистка с правами администратора...");

        var job = new ElevatedCleanupJob(
            IsEnglish: isEn,
            CleanTemp: opts.CleanTemp,
            CleanSoftwareDistribution: opts.CleanSoftwareDistribution,
            CleanLogsAndReports: opts.CleanLogsAndReports,
            CleanGpuShaderCache: false,
            CleanBrowserCache: false,
            CleanDismComponentCleanup: opts.CleanDismComponentCleanup,
            CleanDismResetBase: opts.CleanDismResetBase,
            DisabledTargetKeys: opts.DisabledTargetKeys?.ToList() ?? [],
            Action: "cleanup",
            TempMinAgeDays: opts.TempMinAgeDays
        );

        var run = await StorageService.ExecuteViaElevatedWorkerAsync(job, LogStorage, ct);

        if (run.Result != null && !string.IsNullOrWhiteSpace(run.Result.SummaryMessage))
        {
            OnStatusChanged?.Invoke(run.Result.SummaryMessage);
        }
        else if (!string.IsNullOrWhiteSpace(run.ErrorMessage))
        {
            if (!run.ElevationCancelled) LogStorage($"[ERROR] {run.ErrorMessage}");
            else LogStorage(run.ErrorMessage);
            OnStatusChanged?.Invoke(run.ErrorMessage);
        }

        if (ct.IsCancellationRequested) return;
        await PerformScanCoreAsync(showLog: false);
        OnCleanupCompleted?.Invoke();
    }

    private void StartStorageCooldown(int seconds)
    {
        _storageCooldownRemaining = seconds;

        BtnCleanSystem.IsEnabled = false;
        BtnScanJunk.IsEnabled = false;
        BtnCleanSystemText.Text = LocalizationService.IsEnglish ? $"Cooldown ({seconds}s)" : $"Ожидание ({seconds} сек)";

        _storageCooldownTimer?.Stop();
        _storageCooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _storageCooldownTimer.Tick += (s, e) =>
        {
            bool isEn = LocalizationService.IsEnglish;
            _storageCooldownRemaining--;
            if (_storageCooldownRemaining <= 0)
            {
                _storageCooldownTimer.Stop();
                BtnCleanSystem.IsEnabled = true;
                BtnScanJunk.IsEnabled = true;
                BtnCleanSystemText.Text = LocalizationService.Get("Storage.CleanBtn");
            }
            else
            {
                BtnCleanSystemText.Text = isEn ? $"Cooldown ({_storageCooldownRemaining}s)" : $"Ожидание ({_storageCooldownRemaining} сек)";
            }
        };
        _storageCooldownTimer.Start();
    }

    public void LogStorage(string msg)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => LogStorage(msg));
            return;
        }
        if (_stopped) return;

        const int MaxLines = 300;
        string time = DateTime.Now.ToString("HH:mm:ss");
        string line = $"[{time}] {msg}";

        if (string.IsNullOrWhiteSpace(StorageLogTextBox.Text))
        {
            StorageLogTextBox.Text = line;
        }
        else
        {
            var lines = StorageLogTextBox.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length >= MaxLines)
            {
                StorageLogTextBox.Text = string.Join(Environment.NewLine, lines.Skip(lines.Length - MaxLines + 1)) + Environment.NewLine + line;
            }
            else
            {
                StorageLogTextBox.AppendText(Environment.NewLine + line);
            }
        }

        StorageLogScroll.ScrollToEnd();
        StorageLogFooter.Text = LocalizationService.IsEnglish ? $"Last: {time}" : $"Последнее: {time}";
    }

    private void BtnClearStorageLog_Click(object sender, RoutedEventArgs e)
    {
        StorageLogTextBox.Text = string.Empty;
        StorageLogFooter.Text = LocalizationService.IsEnglish ? "Console cleared" : "Консоль очищена";
    }

    private void BtnDiskDropdown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) OnDiskDropdownRequested?.Invoke(fe);
    }
}
