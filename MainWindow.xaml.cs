using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CoreSpectre.Services;
using CoreSpectre.Views;

namespace CoreSpectre;

public partial class MainWindow : Window
{
    private UserSettingsModel _settings = new();
    private readonly DispatcherTimer _telemetryTimer;
    private readonly DispatcherTimer _clockTimer;
    private TelemetryHudWindow? _hudWindow;
    private TrayAndHotkeyService? _trayService;
    private bool _isSamplingTelemetry = false;
    private bool _isRamPurging = false;
    private bool _isCpuOptimizing;
    private bool _isStandbyPurging;
    private bool _isClosing;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _automaticBoost = new();
    private bool _mcXmxWarned = false;
    private DateTime _lastCpuAlertBalloon = DateTime.MinValue;
    private DateTime _lastRamAlertBalloon = DateTime.MinValue;
    private DateTime _lastDiskAlertBalloon = DateTime.MinValue;

    // Configurable runtime thresholds
    private double _telemetryIntervalSeconds = 1.0;
    private double _diskCThresholdGb = 7.0;
    private double _cpuAlertThreshold = 90.0;
    private double _ramAlertThreshold = 85.0;
    private bool _autoRamPurgeEnabled = true;
    private DateTime _lastAutoPurgeTime = DateTime.MinValue;
    private bool _autoGamePriorityEnabled = true;
    private string _watchedDriveLetter = "C";
    private string _currentLanguage = "EN";
    private bool _isLightTheme = false;
    private bool _use12HourTime = true;
    private bool _isCpuPurgeMode = false;
    private int _ramCooldownRemaining = 0;
    private DispatcherTimer? _ramCooldownTimer;

    // Navigation Tabs Model
    public class TabItemModel
    {
        public string Key { get; set; } = "";
        public int TagIndex { get; set; }
        public string EnTitle { get; set; } = "";
        public string RuTitle { get; set; } = "";
        public string IconKey { get; set; } = "";
        public Button? PillButton { get; set; }
        public TextBlock? TitleTextBlock { get; set; }
    }

    private readonly List<TabItemModel> _activeTabs = new();
    private readonly List<TabItemModel> _vaultTabs = new();
    private bool _isEditingTabs = false;
    private int _currentTabIndex = 0;
    private TabItemModel? _draggedTab;
    private FrameworkElement? _draggedRow;
    private Point _dragStartPoint;
    private int _dragStartIndex = -1;
    private int _targetDropIndex = -1;
    private TaskCompletionSource<bool>? _modalTcs;
    private readonly System.Threading.SemaphoreSlim _modalSemaphore = new(1, 1);

    public MainWindow()
    {
        InitializeComponent();
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;

        SettingsPersistenceService.OnSettingsError += HandleSettingsError;

        LoadAndApplyUserSettings();
        WireViewEvents();

        SourceInitialized += (_, _) =>
        {
            _trayService = new TrayAndHotkeyService(this);
            _trayService.OnRestoreRequested += RestoreFromTray;
            _trayService.OnToggleHudRequested += ToggleHudWindow;
            _trayService.OnToggleHudLockRequested += ToggleHudClickThrough;
            _trayService.OnQuickPurgeRequested += () => _ = ExecuteRamPurgeAsync();
            _trayService.OnExitRequested += ExitApplicationFromTray;
            _trayService.Initialize(_settings.EnableGlobalHotkeys);
        };

        _telemetryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_telemetryIntervalSeconds)
        };
        _telemetryTimer.Tick += (s, e) => UpdateTelemetry();
        _telemetryTimer.Start();

        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (s, e) => UpdateFooterClock();
        _clockTimer.Start();

        InitNavigationTabs();
        ApplyLanguageStrings();
        SetTheme(_isLightTheme);
        UpdateTelemetry();
        UpdateFooterClock();
        HighlightTab(_activeTabs.FirstOrDefault()?.TagIndex ?? 0);
        RefreshVaultUI();

        ViewPurge.Log(LocalizationService.IsRussian
            ? "CoreSpectre инициализирован. Фоновая телеметрия и мониторинг активны."
            : "CoreSpectre initialized. Background telemetry and security monitoring active.");

        _ = ViewStorage.ScanJunkAsync(showLog: false);
    }

    internal bool CanRestoreFromTray => _trayService?.IsTrayAvailable == true;

    private void HandleSettingsError(string msg)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (StatusFooterText != null) StatusFooterText.Text = msg;
        });
    }

    private void WireViewEvents()
    {
        // Overview View
        ViewOverview.OnCpuClicked += () => OpenPurgeTab(isCpu: true);
        ViewOverview.OnRamClicked += () => OpenPurgeTab(isCpu: false);
        ViewOverview.OnDiskCClicked += () => OpenDriveExplorer(_watchedDriveLetter);
        ViewOverview.OnDiskDClicked += () => OpenSecondaryDriveExplorer();
        ViewOverview.OnDiskDropdownClicked += fe => ShowDiskDropdown(fe);
        ViewOverview.OnQuickPurgeClicked += () => _ = ExecuteRamPurgeAsync();
        ViewOverview.OnToggleHudClicked += () => ToggleHudWindow();
        ViewOverview.OnCleanJunkClicked += () => HighlightTab(2);

        // Purge View
        ViewPurge.OnPurgeRequested += () => ExecuteQuickPurge();
        ViewPurge.OnSwitchPurgeModeRequested += () => ShowPurgeModeDropdown();
        ViewPurge.OnOpenTaskManagerRequested += () => LaunchTaskManager();
        ViewPurge.OnStandbyPurgeRequested += () => _ = ExecuteStandbyPurgeAsync();
        ViewPurge.OnRestoreCpuRequested += () => ExecuteRestoreCpu();

        // Storage View
        ViewStorage.OnDiskDropdownRequested += fe => ShowDiskDropdown(fe);
        ViewStorage.OnStatusChanged += msg => StatusFooterText.Text = msg;
        ViewStorage.OnCleanupCompleted += () => UpdateTelemetry();
        ViewStorage.ShowConfirmDialogAsync = ShowModernConfirmDialogAsync;

        // Games View
        ViewGames.OnBoostPriorityRequested += () => _ = ExecuteGameBoostAsync();
        ViewGames.OnRefreshActiveGamesRequested += UpdateTelemetry;
        ViewGames.OnGameDriveFilterRequested += fe => ShowGameDriveFilterDropdown(fe);
        ViewGames.OnIncludeLaunchersChanged += val =>
        {
            _settings.IncludeLaunchers = val;
            SaveCurrentUserSettings();
        };
        ViewGames.OnStatusChanged += msg => StatusFooterText.Text = msg;

        // Settings View
        ViewSettings.OnLanguageChanged += lang => SetLanguage(lang);
        ViewSettings.OnThemeChanged += isLight => SetTheme(isLight);
        ViewSettings.OnTimeFormatChanged += is12h => SetTimeFormat(is12h);
        ViewSettings.OnHudOpacityChanged += val => SetHudOpacity(val);
        ViewSettings.OnCpuAlertChanged += val =>
        {
            _cpuAlertThreshold = val;
            SaveCurrentUserSettings();
        };
        ViewSettings.OnRamAlertChanged += val =>
        {
            _ramAlertThreshold = val;
            SaveCurrentUserSettings();
        };
        ViewSettings.OnAutoRamChanged += val =>
        {
            _autoRamPurgeEnabled = val;
            SaveCurrentUserSettings();
            StatusFooterText.Text = val
                ? (LocalizationService.IsEnglish ? "Auto-RAM purge enabled." : "Авто-сброс ОЗУ включен.")
                : (LocalizationService.IsEnglish ? "Auto-RAM purge disabled." : "Авто-сброс ОЗУ выключен.");
        };
        ViewSettings.OnAutoGamePriorityChanged += val =>
        {
            SetAutomaticGamePriority(val);
            SaveCurrentUserSettings();
            StatusFooterText.Text = val
                ? (LocalizationService.IsEnglish ? "Automatic Game Priority enabled." : "Автоматический Game Priority включен.")
                : (LocalizationService.IsEnglish ? "Automatic Game Priority disabled." : "Автоматический Game Priority выключен.");
        };
        ViewSettings.OnGlobalHotkeysChanged += val =>
        {
            _settings.EnableGlobalHotkeys = val;
            _trayService?.SetHotkeysEnabled(val);
            SaveCurrentUserSettings();
        };
        ViewSettings.OnThresholdAlertsChanged += val =>
        {
            _settings.EnableThresholdAlerts = val;
            SaveCurrentUserSettings();
        };
        ViewSettings.OnProcessListsChanged += (whitelist, customGames) =>
        {
            _settings.ProcessWhitelist = whitelist;
            _settings.CustomGameProcesses = customGames;
            SaveCurrentUserSettings();
            StatusFooterText.Text = LocalizationService.IsEnglish
                ? $"Updated process whitelist ({whitelist.Count}) and custom games ({customGames.Count})."
                : $"Обновлены исключения ({whitelist.Count}) и свои игры ({customGames.Count}).";
        };
        ViewSettings.OnExportBackupRequested += () => ExportBackupToCsb();
        ViewSettings.OnImportBackupRequested += () => _ = ImportBackupFromCsbAsync();
        ViewSettings.OnExportReportRequested += () => _ = ExportSystemReportAsync();
        ViewSettings.OnRunAtStartupChanged += val =>
        {
            if (SettingsPersistenceService.SetRunAtStartup(val))
            {
                _settings.RunAtStartup = val;
                SaveCurrentUserSettings();
            }
            else
            {
                _settings.RunAtStartup = !val;
                ViewSettings.InitSettings(_settings, _isLightTheme);
                StatusFooterText.Text = LocalizationService.IsEnglish
                    ? "Failed to update Windows startup registry entry."
                    : "Не удалось изменить запись автозапуска в реестре Windows.";
            }
        };
        ViewSettings.OnResetSettingsRequested += () => _ = ResetSettingsAsync();
        ViewSettings.OnIntervalSelectorRequested += fe => ShowIntervalDropdown(fe);
        ViewSettings.OnThresholdSelectorRequested += fe => ShowThresholdDropdown(fe);
        ViewSettings.OnToggleTabEditRequested += () => ToggleTabEditMode();
        ViewSettings.OnTabEditShouldDisable += () => DisableTabEditMode();
        ViewSettings.OnOpenTabVaultRequested += () => ToggleTabVault();
        ViewSettings.OnRestoreTabRequested += key => RestoreTabByKey(key);
        ViewSettings.OnStatusChanged += msg => StatusFooterText.Text = msg;
    }

    private void LoadAndApplyUserSettings()
    {
        _settings = SettingsPersistenceService.Load();

        _currentLanguage = _settings.Language == "RU" ? "RU" : "EN";
        LocalizationService.CurrentLanguage = _currentLanguage;
        _isLightTheme = string.Equals(_settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        _use12HourTime = _settings.Use12HourTime;
        _telemetryIntervalSeconds = _settings.TelemetryInterval > 0 ? _settings.TelemetryInterval : 1.0;
        _diskCThresholdGb = _settings.DiskWarningThresholdGb > 0 ? _settings.DiskWarningThresholdGb : 7.0;
        _cpuAlertThreshold = _settings.CpuAlertThreshold > 0 ? _settings.CpuAlertThreshold : 90.0;
        _ramAlertThreshold = _settings.RamAlertThreshold > 0 ? _settings.RamAlertThreshold : 85.0;
        _autoRamPurgeEnabled = _settings.AutoRamPurge;
        SetAutomaticGamePriority(_settings.AutoGamePriority);
        if (!string.IsNullOrWhiteSpace(_settings.WatchedDriveLetter))
        {
            _watchedDriveLetter = SettingsPersistenceService.NormalizeDriveLetter(_settings.WatchedDriveLetter);
        }
        _isCpuPurgeMode = string.Equals(_settings.PurgeMode, "CPU", StringComparison.OrdinalIgnoreCase);

        ViewPurge.SetPurgeMode(_isCpuPurgeMode);
        ViewGames.SetIncludeLaunchers(_settings.IncludeLaunchers);
        ViewStorage.BindSettings(_settings, SaveCurrentUserSettings);
        ViewSettings.InitSettings(_settings, _isLightTheme);
    }

    private void SetAutomaticGamePriority(bool enabled)
    {
        if (_autoGamePriorityEnabled == enabled) return;
        _automaticBoost.Cancel();
        _automaticBoost.Dispose();
        _automaticBoost = new CancellationTokenSource();
        _autoGamePriorityEnabled = enabled;
        if (!enabled) GameBoostService.RestoreAllGamePriorities();
    }

    private void SaveCurrentUserSettings()
    {
        try
        {
            _settings.Language = _currentLanguage;
            _settings.Theme = _isLightTheme ? "Light" : "Dark";
            _settings.Use12HourTime = _use12HourTime;
            _settings.TelemetryInterval = _telemetryIntervalSeconds;
            _settings.DiskWarningThresholdGb = _diskCThresholdGb;
            _settings.CpuAlertThreshold = _cpuAlertThreshold;
            _settings.RamAlertThreshold = _ramAlertThreshold;
            _settings.AutoRamPurge = _autoRamPurgeEnabled;
            _settings.AutoGamePriority = _autoGamePriorityEnabled;
            _settings.WatchedDriveLetter = _watchedDriveLetter;
            _settings.PurgeMode = _isCpuPurgeMode ? "CPU" : "RAM";
            _settings.ActiveTabs = _activeTabs.Select(t => t.Key).ToList();
            _settings.VaultTabs = _vaultTabs.Select(t => t.Key).ToList();

            if (!SettingsPersistenceService.Save(_settings))
            {
                if (SettingsPersistenceService.LastError != null)
                {
                    StatusFooterText.Text = LocalizationService.IsEnglish
                        ? $"Settings save error: {SettingsPersistenceService.LastError}"
                        : $"Ошибка сохранения настроек: {SettingsPersistenceService.LastError}";
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to persist runtime settings: {ex.Message}");
            StatusFooterText.Text = LocalizationService.IsEnglish
                ? $"Settings save error: {ex.Message}"
                : $"Ошибка сохранения настроек: {ex.Message}";
        }
    }

    private void UpdateFooterClock()
    {
        var now = DateTime.Now;
        var offset = TimeZoneInfo.Local.GetUtcOffset(now);
        string offsetSign = offset >= TimeSpan.Zero ? "+" : "-";
        string offsetStr = offset.Minutes == 0
            ? $"UTC{offsetSign}{Math.Abs(offset.Hours)}"
            : $"UTC{offsetSign}{Math.Abs(offset.Hours)}:{Math.Abs(offset.Minutes):00}";

        string timePattern = _use12HourTime ? "hh:mm:ss tt" : "HH:mm:ss";
        string localTimeFormatted = now.ToString(timePattern, CultureInfo.InvariantCulture);

        FooterClockText.Text = $"{localTimeFormatted} ({offsetStr})";
    }

    public void UpdateTelemetry() => _ = UpdateTelemetryAsync();

    private async Task UpdateTelemetryAsync()
    {
        if (_isClosing || _isSamplingTelemetry) return;
        _isSamplingTelemetry = true;

        try
        {
            bool isEn = LocalizationService.IsEnglish;
            string watchedLetter = _watchedDriveLetter;
            var customGames = _settings.CustomGameProcesses?.ToArray() ?? Array.Empty<string>();
            var whitelist = _settings.ProcessWhitelist?.ToArray() ?? Array.Empty<string>();
            bool autoBoostGames = _autoGamePriorityEnabled;
            var autoBoostToken = _automaticBoost.Token;
            bool checkMinecraft = !_mcXmxWarned;

            var data = await Task.Run(() =>
            {
                var procSamples = ProcessTrackerService.SampleProcesses(ProcessSortMode.Cpu);
                var cpuByPid = new Dictionary<int, double>(procSamples.Count);
                foreach (var p in procSamples)
                {
                    cpuByPid[p.Pid] = p.CpuPercent;
                }

                var activeGames = GameBoostService.GetActiveGames(!isEn, customGames, whitelist, cpuByPid);
                var knownGame = activeGames.Count > 0 ? (true, activeGames[0].DisplayName) : (false, string.Empty);

                var sample = HardwareTelemetry.Sample(
                    includeSensors: true,
                    customGames: customGames,
                    excludedGames: whitelist,
                    knownActiveGame: knownGame);

                var extMem = MemoryService.GetPerformanceDetails();
                MemoryService.PruneExitedBackgroundProcesses();
                var watchedInfo = StorageService.GetDriveInfo(watchedLetter);
                string cpuName = HardwareTelemetry.GetProcessorName();
                int physCores = HardwareTelemetry.GetPhysicalCoreCount();

                if (activeGames.Count > 0)
                {
                    if (autoBoostGames && activeGames.Any(g => !g.IsHighPriority))
                    {
                        GameBoostService.BoostGames(!isEn, customGames, whitelist, autoBoostToken);
                    }
                }
                else
                {
                    GameBoostService.RestoreAllGamePriorities(autoBoostToken);
                }

                bool checkedMinecraft = checkMinecraft && sample.RamTotalBytes > 0 &&
                    activeGames.Any(g => g.DisplayName.Contains("Minecraft", StringComparison.OrdinalIgnoreCase) ||
                                         g.ProcessName.Contains("java", StringComparison.OrdinalIgnoreCase));
                string? minecraftWarning = checkedMinecraft ? GameBoostService.CheckMinecraftXmxWarning(sample.RamTotalBytes, isEn) : null;
                return (sample, watchedInfo, activeGames, cpuName, physCores, extMem, checkedMinecraft, minecraftWarning);
            });

            if (_isClosing) return;
            if (watchedLetter != _watchedDriveLetter || isEn != LocalizationService.IsEnglish)
            {
                _ = Dispatcher.BeginInvoke(UpdateTelemetry);
                return;
            }

            var sample = data.sample;
            var watchedInfo = data.watchedInfo;
            var activeGames = data.activeGames;

            double watchedTotalGb = watchedInfo?.TotalBytes / (1024.0 * 1024 * 1024) ?? 0;
            double watchedFreeGb = watchedInfo?.FreeBytes / (1024.0 * 1024 * 1024) ?? 0;

            // Quick metrics header (tracks watched drive consistently)
            QuickCpuText.Text = $"{sample.CpuLoadPercent:0}%";
            QuickRamText.Text = sample.RamTotalBytes > 0 ? $"{sample.RamUsedPercent:0}%" : "N/A";
            QuickDiskLabel.Text = isEn ? $"Drive {watchedLetter}: " : $"Диск {watchedLetter}: ";
            QuickDiskText.Text = watchedTotalGb <= 0 ? "—" : isEn ? $"{watchedFreeGb:0.0} GB" : $"{watchedFreeGb:0.0} ГБ";
            QuickDiskMetric.ToolTip = isEn
                ? $"Open Drive {watchedLetter}: in Windows Explorer"
                : $"Открыть диск {watchedLetter}: в Проводнике";

            double ramUsedGb = sample.RamUsedBytes / (1024.0 * 1024 * 1024);
            double ramTotalGb = sample.RamTotalBytes / (1024.0 * 1024 * 1024);

            // Health determination & threshold tray alerts
            string healthBadge = isEn ? "Healthy" : "В норме";
            string recommendation = isEn ? "All systems operating nominally" : "Все системы работают стабильно";
            bool cpuHigh = sample.CpuLoadPercent >= _cpuAlertThreshold;
            bool ramHigh = sample.RamTotalBytes > 0 && sample.RamUsedPercent >= _ramAlertThreshold;
            bool diskLow = watchedTotalGb > 0 && watchedFreeGb < _diskCThresholdGb;

            if (cpuHigh || ramHigh)
            {
                healthBadge = isEn ? "Warning" : "Внимание";
                recommendation = isEn ? "High system resource consumption" : "Высокая нагрузка на ресурсы";
            }
            else if (diskLow)
            {
                healthBadge = isEn ? "Low Space" : "Мало места";
                recommendation = isEn
                    ? $"Free space on {watchedLetter}: below threshold (< {_diskCThresholdGb:0.#} GB)"
                    : $"На диске {watchedLetter}: осталось мало места (< {_diskCThresholdGb:0.#} ГБ)";
            }

            if (_settings.EnableThresholdAlerts && _trayService != null)
            {
                var nowUtc = DateTime.UtcNow;
                if (cpuHigh && (nowUtc - _lastCpuAlertBalloon).TotalSeconds >= 120)
                {
                    _lastCpuAlertBalloon = nowUtc;
                    _trayService.ShowBalloon("CoreSpectre",
                        isEn ? $"CPU load reached {sample.CpuLoadPercent:0}% (threshold {_cpuAlertThreshold:0}%)"
                             : $"Нагрузка ЦП достигла {sample.CpuLoadPercent:0}% (порог {_cpuAlertThreshold:0}%)",
                        isWarning: true);
                }
                else if (ramHigh && (nowUtc - _lastRamAlertBalloon).TotalSeconds >= 120)
                {
                    _lastRamAlertBalloon = nowUtc;
                    _trayService.ShowBalloon("CoreSpectre",
                        isEn ? $"RAM usage reached {sample.RamUsedPercent:0}% (threshold {_ramAlertThreshold:0}%)"
                             : $"Память заполнена на {sample.RamUsedPercent:0}% (порог {_ramAlertThreshold:0}%)",
                        isWarning: true);
                }
                else if (diskLow && (nowUtc - _lastDiskAlertBalloon).TotalSeconds >= 120)
                {
                    _lastDiskAlertBalloon = nowUtc;
                    _trayService.ShowBalloon("CoreSpectre",
                        isEn ? $"Low free space on {watchedLetter}: ({watchedFreeGb:0.#} GB remaining)"
                             : $"Мало места на диске {watchedLetter}: (осталось {watchedFreeGb:0.#} ГБ)",
                        isWarning: true);
                }
            }

            string gameBadge = activeGames.Count > 0 ? (isEn ? "Active" : "Активен") : (isEn ? "Waiting" : "В ожидании");
            string gameDetail = activeGames.Count > 0
                ? $"{activeGames[0].DisplayName} (PID: {activeGames[0].Pid})"
                : (isEn ? "No games detected · Background monitoring active" : "Игры не обнаружены · Мониторинг активен");

            if (activeGames.Count > 0)
            {
                GameModeDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "BrushTextPrimary");
                GameModeText.Text = isEn ? "Active" : "Активен";
                GameModeText.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextPrimary");
                GameModeIndicatorPanel.ToolTip = isEn
                    ? $"Active game: {activeGames[0].DisplayName}"
                    : $"Активная игра: {activeGames[0].DisplayName}";
            }
            else
            {
                GameModeDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "BrushTextMuted");
                GameModeText.Text = isEn ? "Waiting for games" : "Ожидание игр";
                GameModeText.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
                GameModeIndicatorPanel.ToolTip = isEn ? "Open Game Mode" : "Открыть Игровой модуль";
            }

            ViewOverview.UpdateMetrics(
                sample.CpuLoadPercent,
                data.cpuName,
                sample.RamUsedPercent,
                ramUsedGb,
                ramTotalGb,
                watchedLetter,
                watchedFreeGb,
                watchedTotalGb,
                sample.HasSecondaryDataDrive ? sample.DataDriveLetter : null,
                sample.DiskDFreeGb,
                sample.DiskDTotalGb,
                gameBadge,
                gameDetail,
                healthBadge,
                recommendation,
                sample.CpuHistory,
                sample.RamHistory,
                sample.GpuLoadPercent,
                sample.GpuTempCelsius
            );

            ViewPurge.UpdateMetrics(
                ramUsedGb,
                ramTotalGb,
                sample.CpuLoadPercent,
                data.cpuName,
                data.physCores,
                Environment.ProcessorCount,
                data.extMem
            );

            ViewStorage.UpdateCapacity(watchedLetter, watchedFreeGb, watchedTotalGb);
            ViewGames.UpdateActiveGames(activeGames);
            _hudWindow?.ApplySnapshot(sample);

            if (data.checkedMinecraft)
            {
                _mcXmxWarned = true;
                string? warn = data.minecraftWarning;
                if (!string.IsNullOrEmpty(warn))
                {
                    StatusFooterText.Text = warn;
                    _trayService?.ShowBalloon("CoreSpectre", warn, isWarning: true);
                }
            }

            // Auto-RAM purge with 60s cooldown
            if (_autoRamPurgeEnabled && sample.RamTotalBytes > 0 && sample.RamUsedPercent >= _ramAlertThreshold &&
                (DateTime.Now - _lastAutoPurgeTime).TotalSeconds >= 60)
            {
                _lastAutoPurgeTime = DateTime.Now;
                _ = ExecuteRamPurgeAsync(isAutomatic: true);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Telemetry update warning: {ex.Message}");
        }
        finally
        {
            _isSamplingTelemetry = false;
        }
    }

    private void ApplyLanguageStrings()
    {
        bool isEn = LocalizationService.IsEnglish;
        NavHeaderTitle.Text = LocalizationService.Get("Nav.Navigation");
        QuickCpuMetric.ToolTip = LocalizationService.Get("Tip.CpuAction");
        QuickRamMetric.ToolTip = LocalizationService.Get("Tip.RamAction");
        BtnToggleHud.ToolTip = LocalizationService.Get("Tip.ToggleHud");
        BtnMinimize.ToolTip = isEn ? "Minimize" : "Свернуть";
        BtnMaximize.ToolTip = isEn ? "Maximize" : "Развернуть";
        BtnClose.ToolTip = isEn ? "Close" : "Закрыть";

        foreach (var tab in _activeTabs.Concat(_vaultTabs))
        {
            if (tab.Key == "storage")
            {
                tab.EnTitle = $"Storage ({_watchedDriveLetter}:)";
                tab.RuTitle = $"Хранилище ({_watchedDriveLetter}:)";
            }
            else if (tab.Key == "ram")
            {
                tab.EnTitle = _isCpuPurgeMode ? "CPU Optimization" : "RAM Purge";
                tab.RuTitle = _isCpuPurgeMode ? "Оптимизация ЦП" : "Очистка ОЗУ";
                tab.IconKey = _isCpuPurgeMode ? "IconCpu" : "IconRam";
            }
        }
        RebuildNavPills();
        RefreshVaultUI();

        ViewOverview.ApplyLanguage(_currentLanguage);
        ViewPurge.UpdateVisuals();
        ViewStorage.ApplyLanguage(_currentLanguage);
        ViewGames.ApplyLanguage(_currentLanguage);
        ViewSettings.ApplyLanguage(_currentLanguage);
        UpdateTelemetry();
    }

    public void SetLanguage(string lang)
    {
        _currentLanguage = lang == "RU" ? "RU" : "EN";
        LocalizationService.CurrentLanguage = _currentLanguage;
        SaveCurrentUserSettings();
        ApplyLanguageStrings();
        _hudWindow?.ApplyLocalizationAndTheme();
        _ = ViewStorage.ScanJunkAsync(showLog: false);
    }

    public void SetTheme(bool isLight)
    {
        _isLightTheme = isLight;
        try
        {
            var uri = new Uri(isLight
                ? "pack://application:,,,/CoreSpectre;component/Assets/Themes/AppTheme.Light.xaml"
                : "pack://application:,,,/CoreSpectre;component/Assets/Themes/AppTheme.xaml", UriKind.Absolute);
            Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = uri };
        }
        catch
        {
            try
            {
                var relUri = new Uri(isLight ? "Assets/Themes/AppTheme.Light.xaml" : "Assets/Themes/AppTheme.xaml", UriKind.Relative);
                Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = relUri };
            }
            catch (Exception ex)
            {
                Logger.Warn($"Theme apply warning: {ex.Message}");
            }
        }

        if (BrandLogoImage != null)
        {
            try
            {
                BrandLogoImage.Source = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri(isLight
                        ? "pack://application:,,,/CoreSpectre;component/Assets/Icons/logo_dark.png"
                        : "pack://application:,,,/CoreSpectre;component/Assets/Icons/logo.png", UriKind.Absolute));
            }
            catch
            {
                try
                {
                    BrandLogoImage.Source = new System.Windows.Media.Imaging.BitmapImage(
                        new Uri(isLight ? "Assets/Icons/logo_dark.png" : "Assets/Icons/logo.png", UriKind.Relative));
                }
                catch (Exception ex) { Logger.Debug($"Logo fallback load skipped: {ex.Message}"); }
            }
        }

        SaveCurrentUserSettings();
        ViewSettings.RefreshThemeState(isLight);
        ViewGames.UpdateLauncherToggleVisual();
        RebuildNavPills();
        RefreshVaultUI();
    }

    public void SetTimeFormat(bool is12h)
    {
        _use12HourTime = is12h;
        SaveCurrentUserSettings();
        UpdateFooterClock();
    }

    public void SetHudOpacity(double val)
    {
        _settings.HudOpacity = val;
        if (_hudWindow != null && _hudWindow.IsLoaded)
        {
            _hudWindow.Opacity = val / 100.0;
        }
        SaveCurrentUserSettings();
    }

    public void SetPurgeTabMode(bool isCpu, bool saveSettings = true)
    {
        _isCpuPurgeMode = isCpu;
        ViewPurge.SetPurgeMode(isCpu);

        var ramTab = _activeTabs.Concat(_vaultTabs).FirstOrDefault(t => t.Key == "ram");
        if (ramTab != null)
        {
            ramTab.EnTitle = isCpu ? "CPU Optimization" : "RAM Purge";
            ramTab.RuTitle = isCpu ? "Оптимизация ЦП" : "Очистка ОЗУ";
            ramTab.IconKey = isCpu ? "IconCpu" : "IconRam";
        }

        RebuildNavPills();
        RefreshVaultUI();
        UpdateTelemetry();
        if (saveSettings) SaveCurrentUserSettings();
    }

    private void LaunchTaskManager()
    {
        try
        {
            string taskmgrPath = Path.Combine(Environment.SystemDirectory, "taskmgr.exe");
            using var process = Process.Start(new ProcessStartInfo(taskmgrPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = LocalizationService.IsEnglish
                ? $"Failed to launch taskmgr: {ex.Message}"
                : $"Не удалось запустить Диспетчер задач: {ex.Message}";
        }
    }

    private void OpenDriveExplorer(string driveLetter)
    {
        try
        {
            string validLetter = StorageService.ValidateDriveLetter(driveLetter);
            string explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var process = Process.Start(new ProcessStartInfo(explorerPath, $"{validLetter}:\\") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusFooterText.Text = LocalizationService.IsEnglish
                ? $"Failed to open explorer: {ex.Message}"
                : $"Не удалось открыть Проводник: {ex.Message}";
        }
    }

    private async void OpenSecondaryDriveExplorer()
    {
        try
        {
            string sys = HardwareTelemetry.GetSystemDriveLetter();
            var secondary = await Task.Run(() => DriveInfo.GetDrives()
                .FirstOrDefault(d => d.DriveType == DriveType.Fixed && d.IsReady &&
                    !d.Name.StartsWith(sys, StringComparison.OrdinalIgnoreCase)));
            if (!_isClosing && secondary != null) OpenDriveExplorer(secondary.Name.TrimEnd('\\', ':'));
        }
        catch (Exception ex) { Logger.Warn($"Could not resolve secondary drive: {ex.Message}"); }
    }

    private void ToggleHudWindow()
    {
        if (_hudWindow == null || !_hudWindow.IsLoaded)
        {
            _hudWindow = new TelemetryHudWindow();
            _hudWindow.Opacity = _settings.HudOpacity / 100.0;
            _hudWindow.RestoreState(_settings.HudLeft, _settings.HudTop, _settings.HudDriveLetter);
            _hudWindow.OnPositionChanged += (left, top) =>
            {
                _settings.HudLeft = left;
                _settings.HudTop = top;
                SaveCurrentUserSettings();
            };
            _hudWindow.OnDriveChanged += drive =>
            {
                _settings.HudDriveLetter = drive;
                SaveCurrentUserSettings();
            };
            _hudWindow.Show();
        }
        else
        {
            if (_hudWindow.Visibility == Visibility.Visible)
                _hudWindow.Hide();
            else
            {
                _hudWindow.Opacity = _settings.HudOpacity / 100.0;
                _hudWindow.Show();
            }
        }
    }

    private void ToggleHudClickThrough()
    {
        if (_hudWindow == null || !_hudWindow.IsLoaded || _hudWindow.Visibility != Visibility.Visible)
        {
            ToggleHudWindow();
        }
        _hudWindow?.ToggleClickThrough();
    }

    private void ExecuteQuickPurge()
    {
        if (ViewPurge.IsCpuPurgeMode)
            _ = ExecuteCpuOptimization();
        else
            _ = ExecuteRamPurgeAsync();
    }

    private async Task ExecuteRamPurgeAsync(bool isAutomatic = false)
    {
        if (_isClosing || _ramCooldownRemaining > 0 || _isRamPurging || _isCpuOptimizing) return;
        _isRamPurging = true;

        bool isEn = LocalizationService.IsEnglish;
        StatusFooterText.Text = isEn ? "Trimming process working sets..." : "Выполняется сброс рабочих наборов процессов...";

        try
        {
            var whitelist = _settings.ProcessWhitelist?.ToList() ?? new List<string>();
            var res = await Task.Run(() => MemoryService.PurgeMemory(whitelist));
            if (_isClosing) return;
            string freedStr = StorageService.FormatBytes(res.BytesFreed);
            string nowTime = DateTime.Now.ToString("HH:mm:ss");

            string logMsg = isEn
                ? $"[{nowTime}] Purged: {freedStr} physical RAM ({res.ProcessesOptimized} processes optimized). Was: {res.UsedPercentBefore:0}%, now: {res.UsedPercentAfter:0}%."
                : $"[{nowTime}] Очищено: {freedStr} физической памяти ({res.ProcessesOptimized} процессов оптимизировано). Было: {res.UsedPercentBefore:0}%, стало: {res.UsedPercentAfter:0}%.";

            ViewPurge.Log(logMsg);
            StatusFooterText.Text = isAutomatic
                ? (isEn ? $"[Auto-Purge] {freedStr} RAM freed." : $"[Авто-сброс] Освобождено {freedStr} RAM.")
                : (isEn ? $"Cleanup completed. Freed {freedStr} RAM." : $"Очистка завершена. Высвобождено {freedStr} RAM.");
        }
        catch (Exception ex)
        {
            Logger.Error("Ram purge failed", ex);
            StatusFooterText.Text = isEn ? "Memory purge failed." : "Ошибка при очистке памяти.";
        }
        finally
        {
            _isRamPurging = false;
            UpdateTelemetry();
            if (!_isClosing) StartRamCooldown(3);
        }
    }

    private async Task ExecuteCpuOptimization()
    {
        if (_isClosing || _ramCooldownRemaining > 0 || _isCpuOptimizing || _isRamPurging) return;
        _isCpuOptimizing = true;

        bool isEn = LocalizationService.IsEnglish;
        StatusFooterText.Text = isEn ? "Optimizing CPU thread scheduling and background load..." : "Оптимизация очередей ЦП и фоновой нагрузки...";

        try
        {
            var whitelist = _settings.ProcessWhitelist.ToArray();
            var ct = _lifetime.Token;
            var (cpuBefore, cpuAfter, optimizedCount) = await Task.Run(async () =>
            {
                ct.ThrowIfCancellationRequested();
                double before = HardwareTelemetry.Sample(includeSensors: false).CpuLoadPercent;
                int count = MemoryService.OptimizeBackgroundProcesses(whitelist, ct);
                await Task.Delay(500, ct);
                HardwareTelemetry.Sample(includeSensors: false);
                await Task.Delay(800, ct);
                double after = HardwareTelemetry.Sample(includeSensors: false).CpuLoadPercent;
                return (before, after, count);
            }, ct);
            if (_isClosing) return;

            string nowTime = DateTime.Now.ToString("HH:mm:ss");
            string status = (cpuAfter < cpuBefore)
                ? (isEn ? $"CPU optimized: {cpuBefore:0}% -> {cpuAfter:0}% ({optimizedCount} background utilities normalized)" : $"ЦП оптимизирован: {cpuBefore:0}% -> {cpuAfter:0}% ({optimizedCount} фоновых служб нормализовано)")
                : (isEn ? $"Background processes throttled: {optimizedCount} (CPU: {cpuAfter:0}%)" : $"Фоновые процессы ограничены: {optimizedCount} (ЦП: {cpuAfter:0}%)");

            ViewPurge.Log($"[{nowTime}] {status}");
            StatusFooterText.Text = status;
        }
        catch (OperationCanceledException) when (_isClosing) { }
        catch (Exception ex)
        {
            Logger.Error("CPU optimization error", ex);
        }
        finally
        {
            _isCpuOptimizing = false;
            ViewPurge.UpdateVisuals();
            UpdateTelemetry();
            if (!_isClosing) StartRamCooldown(3);
        }
    }

    private async Task ExecuteStandbyPurgeAsync()
    {
        if (_isStandbyPurging || _isClosing) return;
        _isStandbyPurging = true;
        ViewPurge.SetStandbyPurgeBusy(true);
        try { await ExecuteStandbyPurgeCoreAsync(_lifetime.Token); }
        catch (Exception ex) { Logger.Error("Standby purge failed", ex); }
        finally
        {
            _isStandbyPurging = false;
            ViewPurge.SetStandbyPurgeBusy(false);
        }
    }

    private async Task ExecuteStandbyPurgeCoreAsync(CancellationToken cancellationToken)
    {
        bool isEn = LocalizationService.IsEnglish;
        if (StorageService.IsAdministrator())
        {
            var res = await Task.Run(() => MemoryService.PurgeStandbyList(isEn), cancellationToken);
            string nowTime = DateTime.Now.ToString("HH:mm:ss");
            ViewPurge.Log($"[{nowTime}] {res.Message}");
            StatusFooterText.Text = res.Message;
            UpdateTelemetry();
            return;
        }

        string title = isEn ? "Administrator Rights Required" : "Требуются права администратора";
        string msg = isEn
            ? "Clearing the Windows Standby List requires Administrator privileges.\n\nCoreSpectre will run a secure elevated background worker without closing the window. Grant rights?"
            : "Для очистки резервного списка памяти (Standby List) требуются права администратора.\n\nCoreSpectre выполнит сброс в защищённом фоновом процессе, не закрывая окно приложения. Дать права?";
        bool confirmed = await ShowModernConfirmDialogAsync(
            title,
            msg,
            isEn ? "Grant Rights" : "Дать права",
            isEn ? "Cancel" : "Отмена",
            "IconShield");
        if (!confirmed || _isClosing) return;

        ViewPurge.SetStandbyPurgeBusy(true);
        StatusFooterText.Text = isEn ? "Clearing Windows Standby List..." : "Очистка Standby List Windows...";
        try
        {
            var job = new ElevatedCleanupJob(
                IsEnglish: isEn,
                CleanTemp: false,
                CleanSoftwareDistribution: false,
                CleanLogsAndReports: false,
                CleanGpuShaderCache: false,
                CleanBrowserCache: false,
                CleanDismComponentCleanup: false,
                CleanDismResetBase: false,
                DisabledTargetKeys: new List<string>(),
                Action: "standby"
            );
            var run = await StorageService.ExecuteViaElevatedWorkerAsync(job, line =>
            {
                string nowTime = DateTime.Now.ToString("HH:mm:ss");
                ViewPurge.Log($"[{nowTime}] {line}");
            }, cancellationToken);

            string statusMsg = run.Result?.SummaryMessage ?? run.ErrorMessage ?? (isEn ? "Standby List cleared." : "Standby List очищен.");
            if (run.ElevationCancelled || !string.IsNullOrEmpty(run.ErrorMessage))
            {
                string nowTime = DateTime.Now.ToString("HH:mm:ss");
                ViewPurge.Log($"[{nowTime}] {statusMsg}");
            }
            StatusFooterText.Text = statusMsg;
            UpdateTelemetry();
        }
        finally
        {
            ViewPurge.SetStandbyPurgeBusy(false);
        }
    }

    private async void ExecuteRestoreCpu()
    {
        if (_isCpuOptimizing || _isClosing) return;
        _isCpuOptimizing = true;
        bool isEn = LocalizationService.IsEnglish;
        try
        {
            int restored = await Task.Run(MemoryService.RestoreBackgroundProcesses);
            if (_isClosing) return;
            ViewPurge.UpdateVisuals();
            string nowTime = DateTime.Now.ToString("HH:mm:ss");
            string msg = isEn ? $"Restored original priorities for {restored} processes." : $"Восстановлены исходные приоритеты {restored} процессов.";
            ViewPurge.Log($"[{nowTime}] {msg}");
            StatusFooterText.Text = msg;
            UpdateTelemetry();
        }
        catch (Exception ex) { Logger.Error("CPU priority restore failed", ex); }
        finally { _isCpuOptimizing = false; }
    }

    private async Task ExportSystemReportAsync()
    {
        bool isEn = LocalizationService.IsEnglish;
        try
        {
            string downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(downloadsDir)) downloadsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = isEn ? "Export Diagnostic Report" : "Экспорт диагностического отчёта",
                Filter = "Text Report (*.txt)|*.txt",
                DefaultExt = ".txt",
                InitialDirectory = downloadsDir,
                FileName = $"CoreSpectre_Report_{DateTime.Now:yyyyMMdd_HHmm}.txt"
            };
            if (dlg.ShowDialog(this) != true) return;

            string filePath = dlg.FileName;
            await Task.Run(() => WriteSystemReport(filePath));
            StatusFooterText.Text = isEn ? $"Report exported: {Path.GetFileName(filePath)}" : $"Отчёт сохранён: {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            Logger.Warn($"ExportSystemReport failed: {ex.Message}");
            StatusFooterText.Text = isEn ? "Failed to export report." : "Не удалось сохранить отчёт.";
        }
    }

    private static void WriteSystemReport(string filePath)
    {
        var sample = HardwareTelemetry.Sample(includeSensors: true);
        var extMem = MemoryService.GetPerformanceDetails();
        string cpuName = HardwareTelemetry.GetProcessorName();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== CoreSpectre System Diagnostic Report ===");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"CPU: {cpuName} ({HardwareTelemetry.GetPhysicalCoreCount()}C / {Environment.ProcessorCount}T) @ {sample.CpuLoadPercent:0}%");
        sb.AppendLine($"RAM: {sample.RamUsedBytes / (1024.0 * 1024 * 1024):0.00} / {sample.RamTotalBytes / (1024.0 * 1024 * 1024):0.00} GB ({sample.RamUsedPercent:0}%)");
        if (extMem != null)
        {
            sb.AppendLine($"Commit Charge: {extMem.CommitTotalBytes / (1024.0 * 1024 * 1024):0.00} / {extMem.CommitLimitBytes / (1024.0 * 1024 * 1024):0.00} GB");
            sb.AppendLine($"System Cache: {extMem.SystemCacheBytes / (1024.0 * 1024):0} MB");
        }
        if (sample.GpuLoadPercent.HasValue || sample.GpuTempCelsius.HasValue)
        {
            sb.AppendLine($"GPU: Load={sample.GpuLoadPercent?.ToString("0") ?? "N/A"}%, Temp={sample.GpuTempCelsius?.ToString("0") ?? "N/A"}°C");
        }
        foreach (var d in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
        {
            sb.AppendLine($"Drive {d.Name}: {d.AvailableFreeSpace / (1024.0 * 1024 * 1024):0.0} GB free / {d.TotalSize / (1024.0 * 1024 * 1024):0.0} GB total");
        }
        sb.AppendLine();
        sb.AppendLine("Top Processes (by RAM):");
        foreach (var p in ProcessTrackerService.SampleProcesses(ProcessSortMode.Ram).Take(12))
        {
            sb.AppendLine($"  {p.DisplayName} (PID {p.Pid}): {p.FormattedRam}, CPU {p.FormattedCpu}");
        }
        File.WriteAllText(filePath, sb.ToString(), System.Text.Encoding.UTF8);
    }

    private void StartRamCooldown(int seconds)
    {
        _ramCooldownRemaining = seconds;
        _ramCooldownTimer?.Stop();
        _ramCooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ramCooldownTimer.Tick += (s, e) =>
        {
            _ramCooldownRemaining--;
            if (_ramCooldownRemaining <= 0)
            {
                _ramCooldownTimer.Stop();
            }
        };
        _ramCooldownTimer.Start();
    }

    private async Task ExecuteGameBoostAsync()
    {
        bool isEn = LocalizationService.IsEnglish;
        GameBoostResult res;
        try
        {
            var customGames = _settings.CustomGameProcesses.ToArray();
            var whitelist = _settings.ProcessWhitelist.ToArray();
            var ct = _lifetime.Token;
            res = await Task.Run(() => GameBoostService.BoostGames(!isEn, customGames, whitelist, ct));
        }
        catch (Exception ex)
        {
            Logger.Error("Game priority boost failed", ex);
            return;
        }
        if (_isClosing) return;
        if (res.BoostedCount > 0)
        {
            StatusFooterText.Text = isEn
                ? $"Boosted priority for {res.BoostedCount} game process(es) to High ({string.Join(", ", res.AffectedGames)})."
                : $"Приоритет повышен до Высокого для {res.BoostedCount} процессов ({string.Join(", ", res.AffectedGames)}).";
        }
        else if (res.AlreadyHighCount > 0)
        {
            StatusFooterText.Text = isEn
                ? $"All active game processes ({res.AlreadyHighCount}) already have High priority."
                : $"Все активные игровые процессы ({res.AlreadyHighCount}) уже работают с высоким приоритетом.";
        }
        else
        {
            StatusFooterText.Text = isEn ? "No active games found to boost." : "Активные игровые процессы для оптимизации не найдены.";
        }
        UpdateTelemetry();
    }

    // Modern Confirm Dialog
    public async Task<bool> ShowModernConfirmDialogAsync(string title, string message, string confirmText, string cancelText, string iconKey)
    {
        if (_isClosing) return false;
        await _modalSemaphore.WaitAsync();
        IInputElement? previousFocus = null;
        try
        {
            if (_isClosing) return false;
            previousFocus = Keyboard.FocusedElement;
            ModalDialogTitle.Text = title;
            ModalDialogSub.Text = LocalizationService.IsEnglish ? "Action requires confirmation" : "Действие требует подтверждения";
            ModalDialogMessage.Text = message;
            BtnModalConfirm.Content = confirmText;
            BtnModalCancel.Content = cancelText;

            if (TryFindResource(iconKey) is Geometry g)
            {
                ModalIconPath.Data = g;
            }

            _modalTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            BackgroundHeader.IsEnabled = BackgroundWorkspace.IsEnabled = BackgroundFooter.IsEnabled = false;
            ModernModalOverlay.Visibility = Visibility.Visible;
            BtnModalCancel.Focus();
            return await _modalTcs.Task;
        }
        finally
        {
            _modalTcs = null;
            ModernModalOverlay.Visibility = Visibility.Collapsed;
            BackgroundHeader.IsEnabled = BackgroundWorkspace.IsEnabled = BackgroundFooter.IsEnabled = true;
            if (!_isClosing && previousFocus != null) Keyboard.Focus(previousFocus);
            _modalSemaphore.Release();
        }
    }

    private void BtnModalConfirm_Click(object sender, RoutedEventArgs e) => _modalTcs?.TrySetResult(true);
    private void BtnModalCancel_Click(object sender, RoutedEventArgs e) => _modalTcs?.TrySetResult(false);

    // Dropdown helpers
    private async void ShowDiskDropdown(FrameworkElement target)
    {
        DiskInfo[] drives;
        try
        {
            drives = await Task.Run(() => DriveInfo.GetDrives()
                .Select(d => StorageService.GetDriveInfo(d.Name)).OfType<DiskInfo>().ToArray());
        }
        catch (Exception ex)
        {
            Logger.Warn($"Drive enumeration failed: {ex.Message}");
            return;
        }
        if (_isClosing || !target.IsVisible || !target.IsEnabled) return;
        var cm = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        bool isEn = LocalizationService.IsEnglish;
        foreach (var d in drives)
        {
            string letter = d.Name;
            string vol = d.VolumeLabel;
            string freeStr = StorageService.FormatBytes(d.FreeBytes);
            bool isCurrent = string.Equals(letter, _watchedDriveLetter, StringComparison.OrdinalIgnoreCase);

            var item = new MenuItem
            {
                Header = isEn ? $"Drive {letter}: [{vol}] · {freeStr} free" : $"Диск {letter}: [{vol}] · свободно {freeStr}",
                FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal
            };
            item.Click += (s, ev) =>
            {
                _watchedDriveLetter = letter;
                ViewStorage.UpdateCapacity(letter, 0, 0);
                SaveCurrentUserSettings();
                UpdateTelemetry();
                ApplyLanguageStrings();
                _ = ViewStorage.ScanJunkAsync(showLog: false);
            };
            cm.Items.Add(item);
        }
        cm.IsOpen = true;
    }

    private void ShowPurgeModeDropdown()
    {
        var cm = new ContextMenu { PlacementTarget = ViewPurge.BtnSwitchPurgeMode, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        bool isEn = LocalizationService.IsEnglish;

        var itemRam = new MenuItem
        {
            Header = isEn ? "Memory Purge (RAM)" : "Очистка оперативной памяти (ОЗУ)",
            FontWeight = !_isCpuPurgeMode ? FontWeights.Bold : FontWeights.Normal
        };
        itemRam.Click += (s, e) => SetPurgeTabMode(false);

        var itemCpu = new MenuItem
        {
            Header = isEn ? "CPU Optimization" : "Оптимизация процессора (ЦП)",
            FontWeight = _isCpuPurgeMode ? FontWeights.Bold : FontWeights.Normal
        };
        itemCpu.Click += (s, e) => SetPurgeTabMode(true);

        cm.Items.Add(itemRam);
        cm.Items.Add(itemCpu);
        cm.IsOpen = true;
    }

    private async void ShowGameDriveFilterDropdown(FrameworkElement target)
    {
        DriveInfo[] drives;
        try
        {
            drives = await Task.Run(() => DriveInfo.GetDrives()
                .Where(d => (d.DriveType is DriveType.Fixed or DriveType.Removable) && d.IsReady).ToArray());
        }
        catch (Exception ex)
        {
            Logger.Warn($"Game drive enumeration failed: {ex.Message}");
            return;
        }
        if (_isClosing || !target.IsVisible || !target.IsEnabled) return;
        var cm = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        bool isEn = LocalizationService.IsEnglish;

        var allItem = new MenuItem
        {
            Header = isEn ? "All Drives" : "Все диски",
            FontWeight = ViewGames.GameDriveFilter == "ALL" ? FontWeights.Bold : FontWeights.Normal
        };
        allItem.Click += (s, e) => ViewGames.UpdateDriveFilter("ALL");
        cm.Items.Add(allItem);

        foreach (var d in drives)
        {
            string letter = d.Name.TrimEnd(':', '\\');
            var item = new MenuItem
            {
                Header = isEn ? $"Drive {letter}:" : $"Диск {letter}:",
                FontWeight = ViewGames.GameDriveFilter.Equals(letter, StringComparison.OrdinalIgnoreCase) ? FontWeights.Bold : FontWeights.Normal
            };
            item.Click += (s, e) => ViewGames.UpdateDriveFilter(letter);
            cm.Items.Add(item);
        }
        cm.IsOpen = true;
    }

    private void ShowIntervalDropdown(FrameworkElement target)
    {
        var cm = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        bool isEn = LocalizationService.IsEnglish;

        var presets = new (double Sec, string EnLabel, string RuLabel)[]
        {
            (0.5, "0.5s (High Precision)", "0.5 сек (Высокая точность)"),
            (1.0, "1.0s (Fast)", "1.0 сек (Быстро)"),
            (1.5, "1.5s (Default)", "1.5 сек (По умолчанию)"),
            (3.0, "3.0s (Eco)", "3.0 сек (Эко)"),
            (5.0, "5.0s (Power Saver)", "5.0 сек (Энергосбережение)")
        };

        void ApplyInterval(double sec)
        {
            _telemetryIntervalSeconds = sec;
            _telemetryTimer.Interval = TimeSpan.FromSeconds(sec);
            SaveCurrentUserSettings();
            ViewSettings.InitSettings(_settings, _isLightTheme);
            StatusFooterText.Text = isEn ? $"Telemetry interval: {sec:0.#}s." : $"Интервал телеметрии: {sec:0.#} сек.";
        }

        foreach (var p in presets)
        {
            var item = new MenuItem
            {
                Header = isEn ? p.EnLabel : p.RuLabel,
                FontWeight = Math.Abs(_telemetryIntervalSeconds - p.Sec) < 0.05 ? FontWeights.Bold : FontWeights.Normal
            };
            item.Click += (s, e) => ApplyInterval(p.Sec);
            cm.Items.Add(item);
        }

        // Inline Custom item
        var customPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var customLabel = new TextBlock
        {
            Text = isEn ? "Custom (s): " : "Своё (сек): ",
            Foreground = (Brush)FindResource("BrushTextPrimary"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        var customBox = new TextBox
        {
            Text = _telemetryIntervalSeconds.ToString("0.#", CultureInfo.InvariantCulture),
            Width = 54,
            Height = 22,
            Padding = new Thickness(4, 1, 4, 1),
            FontSize = 11.5,
            Background = (Brush)FindResource("BrushBgInner"),
            Foreground = (Brush)FindResource("BrushTextPrimary"),
            BorderBrush = (Brush)FindResource("BrushBorderCard"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };

        customBox.KeyDown += (s, ev) =>
        {
            if (ev.Key == Key.Enter)
            {
                if (double.TryParse(customBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double sec)
                    && sec >= 0.5 && sec <= 30.0)
                {
                    ApplyInterval(sec);
                    cm.IsOpen = false;
                }
                else
                {
                    StatusFooterText.Text = isEn
                        ? "Telemetry interval must be between 0.5s and 30s."
                        : "Интервал телеметрии должен быть от 0.5 до 30 сек.";
                }
                ev.Handled = true;
            }
        };

        customPanel.Children.Add(customLabel);
        customPanel.Children.Add(customBox);

        cm.Items.Add(new MenuItem { Header = customPanel, StaysOpenOnClick = true });
        cm.Opened += (s, ev) =>
        {
            customBox.Focus();
            customBox.SelectAll();
        };
        cm.IsOpen = true;
    }

    private void ShowThresholdDropdown(FrameworkElement target)
    {
        var cm = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        bool isEn = LocalizationService.IsEnglish;

        var presets = new (double Gb, string EnLabel, string RuLabel)[]
        {
            (3.0, "3 GB", "3 ГБ"),
            (5.0, "5 GB", "5 ГБ"),
            (7.0, "7 GB (Default)", "7 ГБ (По умолчанию)"),
            (10.0, "10 GB", "10 ГБ"),
            (15.0, "15 GB", "15 ГБ")
        };

        void ApplyThreshold(double gb)
        {
            _diskCThresholdGb = gb;
            SaveCurrentUserSettings();
            ViewSettings.InitSettings(_settings, _isLightTheme);
            UpdateTelemetry();
            StatusFooterText.Text = isEn ? $"Disk warning threshold set to {gb:0.#} GB." : $"Порог предупреждения: {gb:0.#} ГБ.";
        }

        foreach (var p in presets)
        {
            var item = new MenuItem
            {
                Header = isEn ? p.EnLabel : p.RuLabel,
                FontWeight = Math.Abs(_diskCThresholdGb - p.Gb) < 0.1 ? FontWeights.Bold : FontWeights.Normal
            };
            item.Click += (s, e) => ApplyThreshold(p.Gb);
            cm.Items.Add(item);
        }

        // Inline Custom item
        var customPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var customLabel = new TextBlock
        {
            Text = isEn ? "Custom (GB): " : "Своё (ГБ): ",
            Foreground = (Brush)FindResource("BrushTextPrimary"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        var customBox = new TextBox
        {
            Text = _diskCThresholdGb.ToString("0.#", CultureInfo.InvariantCulture),
            Width = 54,
            Height = 22,
            Padding = new Thickness(4, 1, 4, 1),
            FontSize = 11.5,
            Background = (Brush)FindResource("BrushBgInner"),
            Foreground = (Brush)FindResource("BrushTextPrimary"),
            BorderBrush = (Brush)FindResource("BrushBorderCard"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };

        customBox.KeyDown += (s, ev) =>
        {
            if (ev.Key == Key.Enter)
            {
                if (double.TryParse(customBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double gb)
                    && gb >= 1.0 && gb <= 500.0)
                {
                    ApplyThreshold(gb);
                    cm.IsOpen = false;
                }
                ev.Handled = true;
            }
        };

        customPanel.Children.Add(customLabel);
        customPanel.Children.Add(customBox);

        cm.Items.Add(new MenuItem { Header = customPanel, StaysOpenOnClick = true });
        cm.Opened += (s, ev) =>
        {
            customBox.Focus();
            customBox.SelectAll();
        };
        cm.IsOpen = true;
    }

    private async Task ResetSettingsAsync()
    {
        try
        {
            bool isEn = LocalizationService.IsEnglish;
            string msg = isEn
                ? "Are you sure you want to reset all parameters, alert thresholds and tab layouts to factory defaults?"
                : "Вы уверены, что хотите сбросить все параметры, пороги алертов и навигационные вкладки на значения по умолчанию?";
            bool ok = await ShowModernConfirmDialogAsync(isEn ? "Reset Confirmation" : "Подтверждение сброса", msg, isEn ? "Reset" : "Сбросить", isEn ? "Cancel" : "Отмена", "IconArrowClockwise");
            if (!ok) return;

            _isEditingTabs = false;
            ViewSettings.SetTabEditState(false);
            ViewSettings.SetVaultDrawerOpen(false);
            ViewSettings.CollapseAllAccordions();

            _settings = new UserSettingsModel();
            SettingsPersistenceService.Save(_settings);
            ApplyFullReloadedSettings();

            StatusFooterText.Text = isEn
                ? "Settings have been reset to factory defaults."
                : "Настройки сброшены на значения по умолчанию.";
        }
        catch (Exception ex)
        {
            Logger.Error("Reset settings failed", ex);
        }
    }

    private void ApplyFullReloadedSettings()
    {
        LoadAndApplyUserSettings();
        _trayService?.SetHotkeysEnabled(_settings.EnableGlobalHotkeys);
        InitNavigationTabs();
        if (!_activeTabs.Any(t => t.TagIndex == _currentTabIndex)) HighlightTab(_activeTabs[0].TagIndex);
        _telemetryTimer.Interval = TimeSpan.FromSeconds(_telemetryIntervalSeconds);
        ApplyLanguageStrings();
        SetTheme(_isLightTheme);
        UpdateTelemetry();
        UpdateFooterClock();
        _ = ViewStorage.ScanJunkAsync(showLog: false);
    }

    private static string GetDownloadsDirectory()
    {
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void ExportBackupToCsb()
    {
        bool isEn = LocalizationService.IsEnglish;
        try
        {
            SaveCurrentUserSettings();
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = isEn ? "Save Settings Backup (.csb)" : "Сохранить резервную копию настроек (.csb)",
                Filter = isEn ? "CoreSpectre Backup (*.csb)|*.csb" : "Резервная копия CoreSpectre (*.csb)|*.csb",
                DefaultExt = ".csb",
                InitialDirectory = GetDownloadsDirectory(),
                FileName = $"CoreSpectre-backup-{DateTime.Now:yyyy-MM-dd}.csb"
            };

            if (dlg.ShowDialog(this) == true)
            {
                var (ok, message) = SettingsPersistenceService.ExportBackup(dlg.FileName, _settings, isEn);
                StatusFooterText.Text = message;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Export .csb backup failed", ex);
            StatusFooterText.Text = isEn ? $"Backup export failed: {ex.Message}" : $"Ошибка экспорта .csb: {ex.Message}";
        }
    }

    private async Task ImportBackupFromCsbAsync()
    {
        bool isEn = LocalizationService.IsEnglish;
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = isEn ? "Load Settings Backup (.csb)" : "Загрузить резервную копию настроек (.csb)",
                Filter = isEn ? "CoreSpectre Backup (*.csb)|*.csb" : "Резервная копия CoreSpectre (*.csb)|*.csb",
                DefaultExt = ".csb",
                InitialDirectory = GetDownloadsDirectory()
            };

            if (dlg.ShowDialog(this) != true) return;
            await ImportBackupFileAsync(dlg.FileName);
        }
        catch (Exception ex)
        {
            Logger.Error("Import .csb backup failed", ex);
            StatusFooterText.Text = isEn ? $"Backup import failed: {ex.Message}" : $"Ошибка загрузки .csb: {ex.Message}";
        }
    }

    public async Task ImportBackupFileAsync(string filePath)
    {
        bool isEn = LocalizationService.IsEnglish;
        try
        {
            var (ok, imported, message) = await Task.Run(() => SettingsPersistenceService.ImportBackup(filePath, isEn));
            if (_isClosing) return;
            if (!ok || imported == null)
            {
                StatusFooterText.Text = message;
                return;
            }

            string confirmTitle = isEn ? "Apply These Settings?" : "Применить данные настройки?";
            string confirmMsg = isEn
                ? $"Apply configuration from '{Path.GetFileName(filePath)}'?\nCurrent theme, language, alert thresholds, tabs, cleanup choices, and whitelist will be updated."
                : $"Применить данные настройки из «{Path.GetFileName(filePath)}»?\nТекущие тема, язык, пороги, вкладки, параметры очистки и исключения будут обновлены.";

            bool confirmed = await ShowModernConfirmDialogAsync(
                confirmTitle,
                confirmMsg,
                isEn ? "Apply" : "Применить",
                isEn ? "Cancel" : "Отмена",
                "IconFloppyDisk");

            if (!confirmed) return;

            _settings = imported;
            SettingsPersistenceService.Save(_settings);
            ApplyFullReloadedSettings();
            StatusFooterText.Text = message;
        }
        catch (Exception ex)
        {
            Logger.Error("Import .csb file failed", ex);
            StatusFooterText.Text = isEn ? $"Backup import failed: {ex.Message}" : $"Ошибка загрузки .csb: {ex.Message}";
        }
    }

    public void HandleExternalCsbOpen(string? csbPath)
    {
        if (_isClosing) return;
        RestoreFromTray();
        Topmost = true;
        Topmost = false;
        Activate();

        if (!string.IsNullOrWhiteSpace(csbPath) && Path.IsPathFullyQualified(csbPath) &&
            Path.GetExtension(csbPath).Equals(".csb", StringComparison.OrdinalIgnoreCase))
        {
            _ = ImportBackupFileAsync(csbPath);
        }
    }

    // Navigation Tabs & Vault
    private void InitNavigationTabs()
    {
        string storageTitleEn = $"Storage ({_watchedDriveLetter}:)";
        string storageTitleRu = $"Хранилище ({_watchedDriveLetter}:)";

        var allKnownTabs = new Dictionary<string, TabItemModel>
        {
            ["overview"] = new() { Key = "overview", TagIndex = 0, EnTitle = "Overview", RuTitle = "Обзор", IconKey = "IconSquaresFour" },
            ["ram"] = new()
            {
                Key = "ram",
                TagIndex = 1,
                EnTitle = _isCpuPurgeMode ? "CPU Optimization" : "RAM Purge",
                RuTitle = _isCpuPurgeMode ? "Оптимизация ЦП" : "Очистка ОЗУ",
                IconKey = _isCpuPurgeMode ? "IconCpu" : "IconRam"
            },
            ["storage"] = new() { Key = "storage", TagIndex = 2, EnTitle = storageTitleEn, RuTitle = storageTitleRu, IconKey = "IconHardDrive" },
            ["games"] = new() { Key = "games", TagIndex = 3, EnTitle = "Game Mode", RuTitle = "Игровой модуль", IconKey = "IconGamepad" },
            ["settings"] = new() { Key = "settings", TagIndex = 4, EnTitle = "Settings", RuTitle = "Настройки", IconKey = "IconGear" }
        };

        _activeTabs.Clear();
        _vaultTabs.Clear();

        var savedActive = _settings.ActiveTabs ?? new List<string> { "overview", "ram", "storage", "games", "settings" };
        foreach (var key in savedActive)
        {
            if (allKnownTabs.TryGetValue(key, out var tab))
            {
                _activeTabs.Add(tab);
                allKnownTabs.Remove(key);
            }
        }

        if (!_activeTabs.Any(t => t.Key == "settings") && allKnownTabs.TryGetValue("settings", out var settingsTab))
        {
            _activeTabs.Add(settingsTab);
            allKnownTabs.Remove("settings");
        }

        if (_settings.VaultTabs != null)
        {
            foreach (var key in _settings.VaultTabs)
            {
                if (allKnownTabs.TryGetValue(key, out var tab))
                {
                    _vaultTabs.Add(tab);
                    allKnownTabs.Remove(key);
                }
            }
        }

        foreach (var kvp in allKnownTabs)
        {
            _activeTabs.Add(kvp.Value);
        }

        RebuildNavPills();
        RefreshVaultUI();
    }

    private void RebuildNavPills()
    {
        if (NavPillsContainer == null) return;
        FinishTabDrag(commit: false);
        NavPillsContainer.Children.Clear();
        bool isEn = LocalizationService.IsEnglish;

        var activeBrush = (SolidColorBrush)FindResource("BrushBgButton");
        var activeBorder = (SolidColorBrush)FindResource("BrushBorderButton");
        var activeText = (SolidColorBrush)FindResource("BrushTextPrimary");
        var inactiveBrush = Brushes.Transparent;
        var inactiveBorder = Brushes.Transparent;
        var inactiveText = (SolidColorBrush)FindResource("BrushTextSecondary");
        var navPillStyle = (Style)FindResource("BtnNavPill");

        for (int i = 0; i < _activeTabs.Count; i++)
        {
            var tab = _activeTabs[i];
            bool isSelected = tab.TagIndex == _currentTabIndex;

            var rowGrid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (_isEditingTabs)
            {
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            var btn = new Button
            {
                Style = navPillStyle,
                Tag = tab.TagIndex,
                Background = isSelected ? activeBrush : inactiveBrush,
                BorderBrush = isSelected ? activeBorder : inactiveBorder,
                Foreground = isSelected ? activeText : inactiveText,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            btn.Click += (s, e) => HighlightTab(tab.TagIndex);

            var sp = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            if (TryFindResource(tab.IconKey) is Geometry geom)
            {
                var path = new System.Windows.Shapes.Path
                {
                    Data = geom,
                    Width = 14,
                    Height = 14,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                path.SetBinding(System.Windows.Shapes.Path.FillProperty, new System.Windows.Data.Binding("Foreground") { Source = btn });
                sp.Children.Add(path);
            }

            var txt = new TextBlock
            {
                Text = isEn ? tab.EnTitle : tab.RuTitle,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            sp.Children.Add(txt);
            btn.Content = sp;

            tab.PillButton = btn;
            tab.TitleTextBlock = txt;
            Grid.SetColumn(btn, 0);
            rowGrid.Children.Add(btn);

            if (_isEditingTabs)
            {
                if (tab.Key != "settings")
                {
                    var btnDel = new Button
                    {
                        Style = (Style)FindResource("BtnIconCircle"),
                        Tag = tab,
                        Width = 26,
                        Height = 26,
                        Margin = new Thickness(4, 0, 2, 0),
                        ToolTip = isEn ? "Remove to Tab Storage" : "Удалить в Хранилище вкладок",
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    btnDel.Content = new System.Windows.Shapes.Path
                    {
                        Data = (Geometry)FindResource("IconClose"),
                        Width = 9,
                        Height = 9,
                        Fill = (SolidColorBrush)FindResource("BrushTextSecondary"),
                        Stretch = Stretch.Uniform
                    };
                    btnDel.Click += BtnRemoveTab_Click;
                    Grid.SetColumn(btnDel, 1);
                    rowGrid.Children.Add(btnDel);
                }

                var gripBorder = new Border
                {
                    Tag = tab,
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(6),
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    ToolTip = isEn ? "Hold and drag to reorder" : "Зажмите для перемещения",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 0, 0)
                };
                gripBorder.Child = new System.Windows.Shapes.Path
                {
                    Data = (Geometry)FindResource("IconGripLines"),
                    Width = 12,
                    Height = 12,
                    Fill = (SolidColorBrush)FindResource("BrushTextMuted"),
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                gripBorder.PreviewMouseLeftButtonDown += TabGrip_PreviewMouseLeftButtonDown;
                gripBorder.PreviewMouseMove += TabGrip_PreviewMouseMove;
                gripBorder.PreviewMouseLeftButtonUp += TabGrip_PreviewMouseLeftButtonUp;
                gripBorder.LostMouseCapture += TabGrip_LostMouseCapture;
                Grid.SetColumn(gripBorder, 2);
                rowGrid.Children.Add(gripBorder);
            }

            NavPillsContainer.Children.Add(rowGrid);
        }
    }

    public void HighlightTab(int index)
    {
        if (!_activeTabs.Any(t => t.TagIndex == index)) return;
        _currentTabIndex = index;

        if (index != 4)
        {
            DisableTabEditMode();
        }

        // Toggle user controls
        ViewOverview.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewPurge.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        ViewStorage.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        ViewGames.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        ViewSettings.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;

        if (index == 2 && (DateTime.UtcNow - ViewStorage.LastScanUtc).TotalSeconds >= 10)
        {
            _ = ViewStorage.ScanJunkAsync(showLog: false, isManualClick: false, isBackground: true);
        }

        var activeBrush = (SolidColorBrush)FindResource("BrushBgButton");
        var activeBorder = (SolidColorBrush)FindResource("BrushBorderButton");
        var activeText = (SolidColorBrush)FindResource("BrushTextPrimary");
        var inactiveBrush = Brushes.Transparent;
        var inactiveBorder = Brushes.Transparent;
        var inactiveText = (SolidColorBrush)FindResource("BrushTextSecondary");

        foreach (var tab in _activeTabs)
        {
            if (tab.PillButton != null)
            {
                bool isSelected = tab.TagIndex == index;
                tab.PillButton.Background = isSelected ? activeBrush : inactiveBrush;
                tab.PillButton.BorderBrush = isSelected ? activeBorder : inactiveBorder;
                tab.PillButton.Foreground = isSelected ? activeText : inactiveText;
                if (tab.TitleTextBlock != null)
                {
                    tab.TitleTextBlock.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Medium;
                }
            }
        }
    }

    private void DisableTabEditMode()
    {
        if (!_isEditingTabs) return;
        _isEditingTabs = false;
        ViewSettings.SetTabEditState(false);
        RebuildNavPills();
    }

    private void ToggleTabEditMode()
    {
        _isEditingTabs = !_isEditingTabs;
        bool isEn = LocalizationService.IsEnglish;
        ViewSettings.SetTabEditState(_isEditingTabs);
        RebuildNavPills();
        StatusFooterText.Text = _isEditingTabs
            ? (isEn ? "Tab edit mode active. Drag ☰ to reorder, ✕ to remove." : "Режим редактирования вкладок: зажимайте ☰ для перемещения, ✕ для удаления.")
            : (isEn ? "Tab order saved." : "Порядок вкладок сохранен.");
    }

    private void ToggleTabVault()
    {
        bool willOpen = !ViewSettings.IsVaultOpen;
        ViewSettings.SetVaultDrawerOpen(willOpen);
        if (willOpen)
        {
            RefreshVaultUI();
        }
        bool isEn = LocalizationService.IsEnglish;
        StatusFooterText.Text = willOpen
            ? (isEn ? "Tab vault opened." : "Хранилище вкладок открыто.")
            : (isEn ? "Tab vault closed." : "Хранилище вкладок закрыто.");
    }

    private void RefreshVaultUI()
    {
        bool isEn = LocalizationService.IsEnglish;
        var entries = _vaultTabs.Select(t => new TabVaultEntry(t.Key, isEn ? t.EnTitle : t.RuTitle, t.IconKey));
        ViewSettings.RefreshVaultItems(entries);
    }

    private async void BtnRemoveTab_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement fe && fe.Tag is TabItemModel tab)
            {
                bool isEn = LocalizationService.IsEnglish;
                string title = isEn ? "Remove Tab?" : "Удалить вкладку?";
                string msg = isEn
                    ? $"Move tab '{tab.EnTitle}' to Tab Storage? You can restore it anytime in Settings."
                    : $"Переместить вкладку «{tab.RuTitle}» в Хранилище вкладок? Вы сможете вернуть её в любой момент через настройки.";

                bool ok = await ShowModernConfirmDialogAsync(title, msg, isEn ? "Remove" : "Удалить", isEn ? "Cancel" : "Отмена", "IconClose");
                if (!ok) return;

                if (!_activeTabs.Remove(tab)) return;
                if (!_vaultTabs.Contains(tab))
                {
                    _vaultTabs.Add(tab);
                }

                if (_currentTabIndex == tab.TagIndex)
                {
                    var nextTab = _activeTabs.FirstOrDefault();
                    if (nextTab != null) HighlightTab(nextTab.TagIndex);
                }

                RebuildNavPills();
                RefreshVaultUI();
                SaveCurrentUserSettings();
                StatusFooterText.Text = isEn
                    ? $"Tab '{tab.EnTitle}' moved to Tab Storage."
                    : $"Вкладка «{tab.RuTitle}» отправлена в Хранилище вкладок.";
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to remove tab", ex);
        }
    }

    private void RestoreTabByKey(string key)
    {
        var tab = _vaultTabs.FirstOrDefault(t => t.Key == key);
        if (tab == null) return;

        _vaultTabs.Remove(tab);
        if (!_activeTabs.Contains(tab))
        {
            _activeTabs.Add(tab);
        }

        RebuildNavPills();
        RefreshVaultUI();
        SaveCurrentUserSettings();

        bool isEn = LocalizationService.IsEnglish;
        StatusFooterText.Text = isEn
            ? $"Tab '{tab.EnTitle}' restored to navigation."
            : $"Вкладка «{tab.RuTitle}» добавлена в навигацию.";
    }

    private void TabGrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement grip && grip.Tag is TabItemModel tab)
        {
            _draggedTab = tab;
            _dragStartIndex = _activeTabs.IndexOf(tab);
            _targetDropIndex = _dragStartIndex;
            _draggedRow = FindVisualParent<Grid>(grip);
            if (_draggedRow != null)
            {
                _dragStartPoint = e.GetPosition(NavPillsContainer);
                _draggedRow.Opacity = 0.85;
                Panel.SetZIndex(_draggedRow, 999);
                if (!grip.CaptureMouse()) FinishTabDrag(commit: false);
                e.Handled = true;
            }
        }
    }

    private void TabGrip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedTab == null || _draggedRow == null || _dragStartIndex < 0 || e.LeftButton != MouseButtonState.Pressed) return;

        Point curPos = e.GetPosition(NavPillsContainer);
        double deltaY = curPos.Y - _dragStartPoint.Y;
        _draggedRow.RenderTransform = new TranslateTransform(0, deltaY);

        double rowH = _draggedRow.ActualHeight > 0 ? _draggedRow.ActualHeight + 4 : 40;
        int slotOffset = (int)Math.Round(deltaY / rowH);
        _targetDropIndex = Math.Clamp(_dragStartIndex + slotOffset, 0, _activeTabs.Count - 1);

        for (int i = 0; i < NavPillsContainer.Children.Count; i++)
        {
            if (i == _dragStartIndex) continue;
            var child = (UIElement)NavPillsContainer.Children[i];
            if (_dragStartIndex < _targetDropIndex)
            {
                child.RenderTransform = (i > _dragStartIndex && i <= _targetDropIndex) ? new TranslateTransform(0, -rowH) : null;
            }
            else if (_dragStartIndex > _targetDropIndex)
            {
                child.RenderTransform = (i >= _targetDropIndex && i < _dragStartIndex) ? new TranslateTransform(0, rowH) : null;
            }
            else
            {
                child.RenderTransform = null;
            }
        }
    }

    private void FinishTabDrag(bool commit)
    {
        if (_draggedTab is not { } tab) return;
        var row = _draggedRow;
        int from = _dragStartIndex;
        int to = _targetDropIndex;
        _draggedTab = null;
        _draggedRow = null;
        _dragStartIndex = _targetDropIndex = -1;

        if (row != null)
        {
            row.Opacity = 1;
            Panel.SetZIndex(row, 0);
            if (row.IsMouseCaptureWithin) Mouse.Capture(null);
        }
        foreach (UIElement child in NavPillsContainer.Children)
            child.RenderTransform = null;

        if (commit && from >= 0 && to >= 0 && from != to)
        {
            _activeTabs.RemoveAt(from);
            _activeTabs.Insert(to, tab);
            RebuildNavPills();
            SaveCurrentUserSettings();
        }
    }

    private void TabGrip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => FinishTabDrag(commit: true);

    private void TabGrip_LostMouseCapture(object sender, MouseEventArgs e) => FinishTabDrag(commit: false);

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? parent = VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is T typed) return typed;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    // Window Caption Handlers
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleFullscreen();
        }
        else if (WindowState != WindowState.Maximized && e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void ToggleFullscreen()
    {
        if (WindowState == WindowState.Maximized)
        {
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            var workArea = SystemParameters.WorkArea;
            Width = Math.Min(1080, workArea.Width);
            Height = Math.Min(720, workArea.Height);
            Left = workArea.Left + (workArea.Width - Width) / 2;
            Top = workArea.Top + (workArea.Height - Height) / 2;
        }
        else
        {
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void BtnMaximize_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplicationFromTray() => Close();

    private void OpenPurgeTab(bool isCpu)
    {
        if (!_activeTabs.Any(t => t.TagIndex == 1)) return;
        SetPurgeTabMode(isCpu);
        HighlightTab(1);
    }
    private void CpuMetric_Click(object sender, MouseButtonEventArgs e) => OpenPurgeTab(isCpu: true);
    private void RamMetric_Click(object sender, MouseButtonEventArgs e) => OpenPurgeTab(isCpu: false);
    private void DiskMetric_Click(object sender, MouseButtonEventArgs e) => OpenDriveExplorer(_watchedDriveLetter);
    private void GameModeIndicator_Click(object sender, MouseButtonEventArgs e) => HighlightTab(3);
    private void BtnToggleHud_Click(object sender, RoutedEventArgs e) => ToggleHudWindow();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (ModernModalOverlay.Visibility == Visibility.Visible)
            {
                _modalTcs?.TrySetResult(false);
                e.Handled = true;
            }
            else if (_draggedTab != null)
            {
                FinishTabDrag(commit: false);
                e.Handled = true;
            }
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        _isClosing = true;
        _modalTcs?.TrySetResult(false);
    }

    protected override void OnClosed(EventArgs e)
    {
        SettingsPersistenceService.OnSettingsError -= HandleSettingsError;
        _automaticBoost.Cancel();
        _lifetime.Cancel();
        _automaticBoost.Dispose();
        _lifetime.Dispose();
        SaveCurrentUserSettings();
        GameBoostService.RestoreAllGamePriorities();
        MemoryService.RestoreBackgroundProcesses();
        _telemetryTimer.Stop();
        _clockTimer.Stop();
        _ramCooldownTimer?.Stop();
        ViewStorage.StopAutoScan();
        _trayService?.Dispose();
        _trayService = null;
        if (_hudWindow != null && _hudWindow.IsLoaded)
        {
            _hudWindow.Close();
        }
        base.OnClosed(e);
    }
}
