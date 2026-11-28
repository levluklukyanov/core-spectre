using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CoreSpectre.Services;

namespace CoreSpectre.Views;

public record TabVaultEntry(string Key, string Title, string IconKey);

public partial class SettingsView : UserControl
{
    private readonly Dictionary<Expander, bool> _accordionCollapsing = new();
    private int _animTokenSeq = 0;
    private bool _sysInfoLoaded = false;
    private bool _sysInfoLoading;
    private bool _isInitializing = false;
    private bool _isEditingTabs = false;
    private string _currentLanguage = "EN";
    private bool _isLightTheme = false;
    private bool _use12HourTime = true;
    private bool _autoRamEnabled = true;
    private bool _autoGamePriorityEnabled = true;
    private bool _runAtStartup = false;
    private bool _enableGlobalHotkeys = true;
    private bool _enableThresholdAlerts = true;
    private double _ramThresholdValue = 85.0;
    private double _intervalSeconds = 1.0;
    private double _diskThresholdGb = 7.0;
    private Expander? _stickyExpander;
    private bool _startupChanging;
    private int _startupRefreshVersion;
    private List<StartupEntry> _startupEntries = new();

    // Cached hardware specs
    private List<CpuInfo>? _cachedCpu;
    private List<RamSlot>? _cachedRam;
    private List<DiskDetail>? _cachedDisks;
    private List<GpuInfo>? _cachedGpus;
    private MotherboardInfo? _cachedMb;
    private BiosInfo? _cachedBios;
    private OsInfo? _cachedOs;
    private List<NetworkAdapterInfo>? _cachedNet;
    private string _cachedUptime = "";

    public event Action<string>? OnLanguageChanged;
    public event Action<bool>? OnThemeChanged;
    public event Action<bool>? OnTimeFormatChanged;
    public event Action<double>? OnHudOpacityChanged;
    public event Action<double>? OnCpuAlertChanged;
    public event Action<double>? OnRamAlertChanged;
    public event Action<bool>? OnAutoRamChanged;
    public event Action<bool>? OnAutoGamePriorityChanged;
    public event Action<bool>? OnRunAtStartupChanged;
    public event Action<bool>? OnGlobalHotkeysChanged;
    public event Action<bool>? OnThresholdAlertsChanged;
    public event Action<List<string>, List<string>>? OnProcessListsChanged;
    public event Action? OnExportBackupRequested;
    public event Action? OnImportBackupRequested;
    public event Action? OnExportReportRequested;
    public event Action? OnResetSettingsRequested;
    public event Action<FrameworkElement>? OnIntervalSelectorRequested;
    public event Action<FrameworkElement>? OnThresholdSelectorRequested;
    public event Action? OnToggleTabEditRequested;
    public event Action? OnTabEditShouldDisable;
    public event Action? OnOpenTabVaultRequested;
    public event Action<string>? OnRestoreTabRequested;
    public event Action<string>? OnStatusChanged;

    public bool IsVaultOpen => VaultDrawerPanel.Visibility == Visibility.Visible;

    public SettingsView()
    {
        InitializeComponent();
        ExpCustom.Collapsed += (s, e) => OnTabEditShouldDisable?.Invoke();
        Loaded += (_, _) => _ = RefreshStartupListAsync();
    }

    public void ApplyLanguage(string language)
    {
        _currentLanguage = language == "RU" ? "RU" : "EN";
        bool isEn = _currentLanguage == "EN";
        SettingsHeadingTitle.Text = LocalizationService.Get("Settings.Title");
        SettingsHeadingSub.Text = LocalizationService.Get("Settings.Subtitle");
        TxtExportBackup.Text = isEn ? "Save .csb" : "Сохранить .csb";
        TxtImportBackup.Text = isEn ? "Load .csb" : "Загрузить .csb";
        TxtExportReport.Text = isEn ? "Report" : "Отчёт";
        BtnExportBackup.ToolTip = isEn ? "Save settings backup to .csb file" : "Сохранить резервную копию настроек в файл .csb";
        BtnImportBackup.ToolTip = isEn ? "Restore settings from .csb backup file" : "Восстановить настройки из файла .csb";
        BtnExportReport.ToolTip = isEn ? "Export system hardware & diagnostics report (.txt)" : "Экспортировать диагностический отчёт по ПК (.txt)";
        TxtResetSettings.Text = LocalizationService.Get("Settings.ResetBtn");

        // Accordion 1
        ExpCustom.Header = LocalizationService.Get("Settings.Customization");
        LangSettingTitle.Text = LocalizationService.Get("Settings.Language");
        LangSettingDesc.Text = isEn ? "Default is English. Switchable instantly." : "По умолчанию — English. Переключается на лету.";
        ThemeSettingTitle.Text = LocalizationService.Get("Settings.Theme");
        ThemeSettingDesc.Text = isEn ? "Dark or light theme for the entire workspace" : "Тёмная или светлая тема для всего интерфейса";
        ThemeDarkText.Text = LocalizationService.Get("Settings.ThemeDark");
        ThemeLightText.Text = LocalizationService.Get("Settings.ThemeLight");
        TimeFormatSettingTitle.Text = LocalizationService.Get("Settings.TimeFormat");
        TimeFormatSettingDesc.Text = isEn ? "12-hour (AM/PM) or 24-hour clock display" : "12-часовой (AM/PM) или 24-часовой формат времени";
        BtnTime12hText.Text = isEn ? "12-hour" : "12-часовой";
        BtnTime24hText.Text = isEn ? "24-hour" : "24-часовой";
        HudOpacityTitle.Text = LocalizationService.Get("Settings.HudOpacity");
        TabManagerTitle.Text = LocalizationService.Get("Settings.Tabs");
        TabManagerDesc.Text = isEn ? "Manage navigation tabs, order and removal" : "Управление списком вкладок навигации, удаление и сортировка";
        TxtToggleTabEdit.Text = _isEditingTabs ? (isEn ? "Done" : "Готово") : (isEn ? "Edit" : "Редактировать");
        TabVaultTitle.Text = LocalizationService.Get("Settings.TabStorage");
        TabVaultDesc.Text = isEn ? "Inactive tabs not added to navigation" : "Вкладки, не добавленные в навигацию. Нажмите Открыть для добавления";
        TxtBtnOpenTabVault.Text = VaultDrawerPanel.Visibility == Visibility.Visible
            ? (isEn ? "Close" : "Закрыть")
            : (isEn ? "Open" : "Открыть");

        // Accordion 2
        ExpMonitor.Header = isEn ? "Monitoring & Tools" : "Мониторинг и инструменты";
        TelemetrySectionTitle.Text = isEn ? "TELEMETRY & SAMPLING" : "ТЕЛЕМЕТРИЯ И ОПРОС";
        IntervalTitle.Text = isEn ? "Metrics sampling interval:" : "Интервал опроса телеметрии:";
        IntervalDesc.Text = isEn ? "Determines update frequency for CPU, RAM and storage metrics" : "Определяет частоту обновления метрик ЦП, ОЗУ и дисков";
        UpdateAutoRamHeading();
        AutoRamDesc.Text = isEn ? "Background working set trim when RAM is near capacity" : "Фоновая очистка рабочих наборов при критическом заполнении памяти";
        AutoGamePriorityTitle.Text = isEn ? "Automatic Game Priority" : "Автоматический игровой приоритет";
        AutoGamePriorityDesc.Text = isEn ? "Auto-boost games on launch and restore priority on exit" : "Повышает приоритет игр при запуске и возвращает при выходе";

        TraySectionTitle.Text = isEn ? "TRAY, HOTKEYS & ALERTS" : "ТРЕЙ, ГОРЯЧИЕ КЛАВИШИ И УВЕДОМЛЕНИЯ";
        RunStartupTitle.Text = isEn ? "Run CoreSpectre at Windows Startup" : "Запускать CoreSpectre вместе с Windows";
        RunStartupDesc.Text = isEn ? "Launch minimized to system tray when you sign in to Windows" : "Автозапуск свёрнутым в системный трей при входе в Windows";
        HotkeysTitle.Text = isEn ? "Global Hotkeys (Ctrl+Shift+H / L / P)" : "Глобальные клавиши (Ctrl+Shift+H / L / P)";
        HotkeysDesc.Text = isEn ? "H: toggle HUD · L: Click-Through · P: quick RAM Purge" : "H: скрыть/показать HUD · L: Click-Through · P: очистка ОЗУ";
        TrayAlertsTitle.Text = isEn ? "Threshold Tray Notifications" : "Оповещения в системный трей";
        TrayAlertsDesc.Text = isEn ? "Send Windows tray warnings on high CPU/RAM or low disk space" : "Уведомления при превышении порогов ЦП/ОЗУ или нехватке места";

        ListsSectionTitle.Text = isEn ? "PROCESS EXCEPTIONS & CUSTOM GAMES" : "ИСКЛЮЧЕНИЯ ПРОЦЕССОВ И СВОИ ИГРЫ";
        WhitelistTitle.Text = isEn ? "Memory & Priority Whitelist" : "Белый список процессов";
        WhitelistDesc.Text = isEn ? "Comma-separated processes skipped by RAM purge and auto-boost (e.g. discord, obs64)" : "Имена процессов через запятую, которые не сжимаются и не бустятся (discord, obs64)";
        CustomGamesTitle.Text = isEn ? "Custom Game Executables" : "Свои игровые процессы";
        CustomGamesDesc.Text = isEn ? "Comma-separated extra game .exe names to detect (e.g. valorant, eldenring)" : "Дополнительные .exe игр через запятую (например: valorant, eldenring)";
        TxtSaveProcessLists.Text = isEn ? "Apply" : "Применить";

        StorageSectionTitle.Text = isEn ? "STORAGE & THRESHOLDS" : "ХРАНИЛИЩЕ И ПОРОГИ";
        DiskThresholdTitle.Text = isEn ? "Drive warning threshold:" : "Порог предупреждения диска:";
        DiskThresholdDesc.Text = isEn ? "Warning status triggers when free space drops below threshold" : "Предупреждение срабатывает при остатке места меньше заданного";
        AlertSectionTitle.Text = isEn ? "ALERT THRESHOLDS" : "ПОРОГИ ОПОВЕЩЕНИЙ";
        CpuAlertTitle.Text = isEn ? "CPU Alert Threshold (%):" : "Порог нагрузки ЦП (%):";
        RamAlertTitle.Text = isEn ? "RAM Alert Threshold (%):" : "Порог нагрузки ОЗУ (%):";
        StartupSectionTitle.Text = isEn ? "STARTUP MANAGER" : "ДИСПЕТЧЕР АВТОЗАГРУЗКИ";
        BtnRefreshStartupText.Text = isEn ? "Refresh" : "Обновить";
        UpdateThresholdLabels();
        StartupList.Items.Refresh();

        UpdateToggleButtonStyles();

        // Accordion 3
        ExpSysInfo.Header = isEn ? "System Specifications" : "Характеристики системы";
        if (_sysInfoLoaded)
        {
            _sysInfoLoaded = false;
            if (ExpSysInfo.IsExpanded)
            {
                _ = LoadSysInfoAsync();
            }
            else if (_cachedOs != null)
            {
                PopulateSpecsUI();
            }
        }
        else
        {
            SysInfoLoadingText.Text = isEn ? "Scanning system hardware specifications..." : "Поиск характеристик системы...";
        }

        UpdateStickyHeader();
    }

    private void UpdateAutoRamHeading()
    {
        bool isEn = _currentLanguage == "EN";
        AutoRamTitle.Text = isEn
            ? $"Auto-purge on memory spike (> {_ramThresholdValue:0}%)"
            : $"Авто-очистка при пиковой нагрузке (> {_ramThresholdValue:0}%)";
    }

    public void InitSettings(UserSettingsModel settings, bool isLightTheme)
    {
        _isInitializing = true;
        try
        {
            _currentLanguage = settings.Language == "RU" ? "RU" : "EN";
            _isLightTheme = isLightTheme;
            _use12HourTime = settings.Use12HourTime;
            _autoRamEnabled = settings.AutoRamPurge;
            _autoGamePriorityEnabled = settings.AutoGamePriority;
            _runAtStartup = settings.RunAtStartup;
            _enableGlobalHotkeys = settings.EnableGlobalHotkeys;
            _enableThresholdAlerts = settings.EnableThresholdAlerts;
            _ramThresholdValue = settings.RamAlertThreshold;
            _intervalSeconds = settings.TelemetryInterval;
            _diskThresholdGb = settings.DiskWarningThresholdGb;

            SliderHudOpacity.Value = settings.HudOpacity;
            TxtHudOpacityVal.Text = $"{settings.HudOpacity:0}%";

            SliderCpuAlert.Value = settings.CpuAlertThreshold;
            TxtCpuAlertVal.Text = $"{settings.CpuAlertThreshold:0}%";

            SliderRamAlert.Value = settings.RamAlertThreshold;
            TxtRamAlertVal.Text = $"{settings.RamAlertThreshold:0}%";

            UpdateThresholdLabels();

            TxtProcessWhitelist.Text = string.Join(", ", settings.ProcessWhitelist ?? new List<string>());
            TxtCustomGames.Text = string.Join(", ", settings.CustomGameProcesses ?? new List<string>());

            UpdateAutoRamHeading();
            UpdateToggleButtonStyles();
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void UpdateThresholdLabels()
    {
        bool isRu = _currentLanguage == "RU";
        TxtIntervalCurrent.Text = isRu ? $"{_intervalSeconds:0.#} сек" : $"{_intervalSeconds:0.#}s";
        TxtThresholdCurrent.Text = isRu ? $"{_diskThresholdGb:0.#} ГБ" : $"{_diskThresholdGb:0.#} GB";
    }

    public void UpdateToggleButtonStyles()
    {
        var primaryStyle = (Style)FindResource("BtnPrimaryWhite");
        var pillStyle = (Style)FindResource("BtnPill");
        bool isRu = _currentLanguage == "RU";
        bool isEn = !isRu;

        BtnLangRu.Style = isRu ? primaryStyle : pillStyle;
        BtnLangEn.Style = !isRu ? primaryStyle : pillStyle;

        BtnThemeDark.Style = !_isLightTheme ? primaryStyle : pillStyle;
        BtnThemeLight.Style = _isLightTheme ? primaryStyle : pillStyle;

        BtnTime12h.Style = _use12HourTime ? primaryStyle : pillStyle;
        BtnTime24h.Style = !_use12HourTime ? primaryStyle : pillStyle;

        TxtAutoRamStatus.Text = _autoRamEnabled ? (isEn ? "Enabled" : "Включено") : (isEn ? "Disabled" : "Выключено");
        BtnToggleAutoRam.Style = _autoRamEnabled ? primaryStyle : pillStyle;

        TxtAutoGamePriorityStatus.Text = _autoGamePriorityEnabled ? (isEn ? "Enabled" : "Включено") : (isEn ? "Disabled" : "Выключено");
        BtnToggleAutoGamePriority.Style = _autoGamePriorityEnabled ? primaryStyle : pillStyle;

        TxtRunAtStartupStatus.Text = _runAtStartup ? (isEn ? "Enabled" : "Включено") : (isEn ? "Disabled" : "Выключено");
        BtnToggleRunAtStartup.Style = _runAtStartup ? primaryStyle : pillStyle;

        TxtGlobalHotkeysStatus.Text = _enableGlobalHotkeys ? (isEn ? "Enabled" : "Включено") : (isEn ? "Disabled" : "Выключено");
        BtnToggleGlobalHotkeys.Style = _enableGlobalHotkeys ? primaryStyle : pillStyle;

        TxtThresholdAlertsStatus.Text = _enableThresholdAlerts ? (isEn ? "Enabled" : "Включено") : (isEn ? "Disabled" : "Выключено");
        BtnToggleThresholdAlerts.Style = _enableThresholdAlerts ? primaryStyle : pillStyle;

        BtnToggleTabEdit.Style = _isEditingTabs ? primaryStyle : pillStyle;
        TxtToggleTabEdit.Text = _isEditingTabs ? (isEn ? "Done" : "Готово") : (isEn ? "Edit" : "Редактировать");
    }

    public void RefreshThemeState(bool isLightTheme)
    {
        _isLightTheme = isLightTheme;
        UpdateToggleButtonStyles();
    }

    public void SetTabEditState(bool isEditing)
    {
        _isEditingTabs = isEditing;
        UpdateToggleButtonStyles();
    }

    public void SetVaultDrawerOpen(bool isOpen)
    {
        bool isEn = LocalizationService.IsEnglish;
        VaultDrawerPanel.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        TxtBtnOpenTabVault.Text = isOpen ? (isEn ? "Close" : "Закрыть") : (isEn ? "Open" : "Открыть");

        if (IconVaultChevron.RenderTransform is not RotateTransform)
        {
            IconVaultChevron.RenderTransform = new RotateTransform(0);
        }
        ((RotateTransform)IconVaultChevron.RenderTransform).Angle = isOpen ? 180 : 0;
    }

    public void RefreshVaultItems(IEnumerable<TabVaultEntry> vaultTabs)
    {
        bool isEn = LocalizationService.IsEnglish;
        VaultItemsContainer.Children.Clear();
        var list = vaultTabs.ToList();

        if (list.Count == 0)
        {
            TxtVaultEmpty.Visibility = Visibility.Visible;
            TxtVaultEmpty.Text = isEn
                ? "All available tabs are active in navigation."
                : "Все доступные вкладки активны в навигации.";
            return;
        }

        TxtVaultEmpty.Visibility = Visibility.Collapsed;

        foreach (var tab in list)
        {
            var rowBorder = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6)
            };
            rowBorder.SetResourceReference(Border.BackgroundProperty, "BrushBgBase");
            rowBorder.SetResourceReference(Border.BorderBrushProperty, "BrushBorderCard");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var btnAdd = new Button
            {
                Style = (Style)FindResource("BtnPill"),
                Tag = tab.Key,
                Padding = new Thickness(7, 5, 7, 5),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = isEn ? "Add back to navigation (at the end)" : "Добавить в навигацию (в конец списка)"
            };
            var addIcon = new System.Windows.Shapes.Path
            {
                Data = (Geometry)FindResource("IconPlus"),
                Width = 10,
                Height = 10,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            addIcon.SetResourceReference(System.Windows.Shapes.Path.FillProperty, "BrushTextPrimary");
            btnAdd.Content = addIcon;
            btnAdd.Click += (s, ev) => OnRestoreTabRequested?.Invoke(tab.Key);
            Grid.SetColumn(btnAdd, 0);
            grid.Children.Add(btnAdd);

            var infoSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (TryFindResource(tab.IconKey) is Geometry geom)
            {
                var tabIcon = new System.Windows.Shapes.Path
                {
                    Data = geom,
                    Width = 14,
                    Height = 14,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                tabIcon.SetResourceReference(System.Windows.Shapes.Path.FillProperty, "BrushTextPrimary");
                infoSp.Children.Add(tabIcon);
            }
            var titleTb = new TextBlock
            {
                Text = tab.Title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextPrimary");
            infoSp.Children.Add(titleTb);
            Grid.SetColumn(infoSp, 1);
            grid.Children.Add(infoSp);

            var hintTb = new TextBlock
            {
                Text = isEn ? "Restore to sidebar" : "Вернуть в меню",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            hintTb.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextMuted");
            Grid.SetColumn(hintTb, 2);
            grid.Children.Add(hintTb);

            rowBorder.Child = grid;
            VaultItemsContainer.Children.Add(rowBorder);
        }
    }

    public void CollapseAllAccordions()
    {
        foreach (var exp in new[] { ExpCustom, ExpMonitor, ExpSysInfo })
        {
            if (exp != null && exp.IsExpanded && exp.Content is FrameworkElement content)
            {
                AnimateCollapse(exp, content);
            }
        }
        if (StickyAccordionBar != null) StickyAccordionBar.Visibility = Visibility.Collapsed;
        SettingsScrollViewer?.ScrollToTop();
    }

    private void Expander_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Expander exp && exp.Content is FrameworkElement content)
        {
            var hit = e.OriginalSource as DependencyObject;
            while (hit != null && hit != exp)
            {
                if (hit is System.Windows.Controls.Primitives.ToggleButton) break;
                hit = VisualTreeHelper.GetParent(hit);
            }
            if (hit == null || hit == exp) return;

            if (_accordionCollapsing.TryGetValue(exp, out bool isCollapsing) && isCollapsing)
            {
                e.Handled = true;
                _accordionCollapsing[exp] = false;
                AnimateExpand(exp, content);
                return;
            }

            if (exp.IsExpanded)
            {
                e.Handled = true;
                AnimateCollapse(exp, content);
            }
        }
    }

    private void Expander_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander exp && exp.Content is FrameworkElement content)
        {
            if (_accordionCollapsing.TryGetValue(exp, out bool isCol) && isCol) return;
            AnimateExpand(exp, content);
            UpdateStickyHeader();
        }
    }

    private async void ExpSysInfo_Expanded(object sender, RoutedEventArgs e)
    {
        Expander_Expanded(sender, e);
        if (!_sysInfoLoaded)
        {
            await LoadSysInfoAsync();
        }
    }

    private void AnimateExpand(Expander exp, FrameworkElement content)
    {
        _accordionCollapsing[exp] = false;
        int animToken = Interlocked.Increment(ref _animTokenSeq);
        exp.Tag = animToken;

        content.BeginAnimation(FrameworkElement.HeightProperty, null);
        content.BeginAnimation(UIElement.OpacityProperty, null);

        content.Height = double.NaN;
        double availWidth = exp.ActualWidth > 40 ? exp.ActualWidth - 40 : 800;
        content.Measure(new Size(availWidth, double.PositiveInfinity));
        double target = Math.Max(32, content.DesiredSize.Height);

        double startH = 0;
        if (content.ActualHeight > 0 && content.ActualHeight < target - 5)
        {
            startH = content.ActualHeight;
        }

        content.Height = startH;
        content.Opacity = startH > 0 ? Math.Max(0.1, content.Opacity) : 0;

        var animH = new DoubleAnimation(startH, target, new Duration(TimeSpan.FromMilliseconds(260)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var animO = new DoubleAnimation(content.Opacity, 1.0, new Duration(TimeSpan.FromMilliseconds(220)));

        animH.Completed += (s, ev) =>
        {
            if (exp.Tag is int token && token == animToken)
            {
                content.BeginAnimation(FrameworkElement.HeightProperty, null);
                content.BeginAnimation(UIElement.OpacityProperty, null);
                content.Height = double.NaN;
                content.Opacity = 1;
                UpdateStickyHeader();
            }
        };

        content.BeginAnimation(FrameworkElement.HeightProperty, animH);
        content.BeginAnimation(UIElement.OpacityProperty, animO);
    }

    private void AnimateCollapse(Expander exp, FrameworkElement content)
    {
        if (exp == ExpCustom)
        {
            OnTabEditShouldDisable?.Invoke();
        }

        _accordionCollapsing[exp] = true;
        int animToken = Interlocked.Increment(ref _animTokenSeq);
        exp.Tag = animToken;

        content.BeginAnimation(FrameworkElement.HeightProperty, null);
        content.BeginAnimation(UIElement.OpacityProperty, null);

        double startH = content.ActualHeight;
        if (startH <= 1)
        {
            content.Height = double.NaN;
            content.Opacity = 1;
            exp.IsExpanded = false;
            _accordionCollapsing[exp] = false;
            UpdateStickyHeader();
            return;
        }

        content.Height = startH;
        var animH = new DoubleAnimation(startH, 0, new Duration(TimeSpan.FromMilliseconds(240)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        var animO = new DoubleAnimation(Math.Max(0, content.Opacity), 0, new Duration(TimeSpan.FromMilliseconds(180)));

        animH.Completed += (s, ev) =>
        {
            if (exp.Tag is int token && token == animToken)
            {
                content.BeginAnimation(FrameworkElement.HeightProperty, null);
                content.BeginAnimation(UIElement.OpacityProperty, null);
                content.Height = double.NaN;
                content.Opacity = 1;
                exp.IsExpanded = false;
                _accordionCollapsing[exp] = false;
                UpdateStickyHeader();
            }
        };

        content.BeginAnimation(FrameworkElement.HeightProperty, animH);
        content.BeginAnimation(UIElement.OpacityProperty, animO);
    }

    private void SettingsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateStickyHeader();
    private void SettingsScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            SettingsScrollViewer.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 14, 14);
        }
        UpdateStickyHeader();
    }

    private void UpdateStickyHeader()
    {
        if (StickyAccordionBar == null || SettingsScrollViewer == null) return;

        _stickyExpander = null;
        foreach (var exp in new[] { ExpCustom, ExpMonitor, ExpSysInfo })
        {
            if (exp?.IsExpanded != true || !SettingsScrollViewer.IsAncestorOf(exp)) continue;
            if (_accordionCollapsing.TryGetValue(exp, out bool collapsing) && collapsing) continue;
            double top = exp.TransformToAncestor(SettingsScrollViewer).Transform(new Point(0, 0)).Y;
            if (top + 46 <= 0 && top + exp.ActualHeight > 80)
            {
                _stickyExpander = exp;
                StickyAccordionTitle.Text = exp.Header?.ToString() ?? "Settings";
                break;
            }
        }
        StickyAccordionBar.Visibility = _stickyExpander == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BtnCollapseSticky_Click(object sender, MouseButtonEventArgs e)
    {
        StickyAccordionBar.Visibility = Visibility.Collapsed;
        if (_stickyExpander is { IsExpanded: true, Content: FrameworkElement content } exp)
        {
            var offset = exp.TransformToAncestor(SettingsScrollViewer).Transform(new Point(0, 0)).Y;
            AnimateCollapse(exp, content);
            SettingsScrollViewer.ScrollToVerticalOffset(SettingsScrollViewer.VerticalOffset + offset);
        }
    }

    private void StickyAccordionBar_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SettingsScrollViewer?.ScrollToVerticalOffset(SettingsScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    // Hardware Specs
    private async Task LoadSysInfoAsync()
    {
        if (_sysInfoLoading) return;
        _sysInfoLoading = true;
        bool languageChanged = false;
        try
        {
            SysInfoLoadingText.Visibility = Visibility.Visible;
            bool isEn = LocalizationService.IsEnglish;

            await Task.Run(() =>
            {
                _cachedCpu = SystemInfoService.GetCpuDetails(isEn);
                _cachedRam = SystemInfoService.GetRamDetails(isEn);
                _cachedDisks = SystemInfoService.GetAllDiskDetails(isEn);
                _cachedGpus = SystemInfoService.GetGpus();
                _cachedMb = SystemInfoService.GetMotherboard();
                _cachedBios = SystemInfoService.GetBios();
                _cachedOs = SystemInfoService.GetOsInfo();
                _cachedNet = SystemInfoService.GetNetworkAdapters(isEn);
                _cachedUptime = SystemInfoService.GetUptime(isEn);
            });

            languageChanged = isEn != LocalizationService.IsEnglish;
            if (languageChanged) return;

            _sysInfoLoaded = true;
            PopulateSpecsUI();
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load sysinfo", ex);
            SysInfoLoadingText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _sysInfoLoading = false;
            if (languageChanged && ExpSysInfo.IsExpanded) _ = LoadSysInfoAsync();
        }
    }

    private void PopulateSpecsUI()
    {
        SysInfoPanel.Children.Clear();
        bool isEn = LocalizationService.IsEnglish;
        bool isRu = !isEn;

        Border CreateSection(string title, IEnumerable<KeyValuePair<string, string>> items)
        {
            var border = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 10)
            };
            border.SetResourceReference(Border.BackgroundProperty, "BrushBgInner");
            border.SetResourceReference(Border.BorderBrushProperty, "BrushBorderCard");

            var sp = new StackPanel();
            var headerTb = new TextBlock
            {
                Text = title,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            headerTb.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextMuted");
            sp.Children.Add(headerTb);

            bool first = true;
            foreach (var kv in items)
            {
                if (!first)
                {
                    var sep = new Separator { Margin = new Thickness(0, 6, 0, 6) };
                    sep.SetResourceReference(Separator.BackgroundProperty, "BrushBorderSubtle");
                    sp.Children.Add(sep);
                }
                first = false;

                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var lbl = new TextBlock
                {
                    Text = kv.Key,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 18, 0)
                };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
                Grid.SetColumn(lbl, 0);

                var val = new TextBlock
                {
                    Text = string.IsNullOrEmpty(kv.Value) ? "--" : kv.Value,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    TextAlignment = TextAlignment.Right,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                };
                val.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextPrimary");
                Grid.SetColumn(val, 1);

                g.Children.Add(lbl);
                g.Children.Add(val);
                sp.Children.Add(g);
            }

            border.Child = sp;
            return border;
        }

        // OS
        if (_cachedOs != null)
        {
            string osCaption = _cachedOs.Caption;
            string osArch = _cachedOs.Architecture;
            if (isEn)
            {
                osCaption = osCaption
                    .Replace("Майкрософт", "Microsoft")
                    .Replace("Домашняя", "Home")
                    .Replace("Профессиональная", "Pro")
                    .Replace("Корпоративная", "Enterprise")
                    .Replace("для образовательных учреждений", "Education");

                if (osArch.Contains("64")) osArch = "64-bit";
                else if (osArch.Contains("32")) osArch = "32-bit";
            }
            else
            {
                if (osArch.Contains("64") && !osArch.Contains("разряд")) osArch = "64-разрядная";
                else if (osArch.Contains("32") && !osArch.Contains("разряд")) osArch = "32-разрядная";
            }

            SysInfoPanel.Children.Add(CreateSection(isRu ? "ОПЕРАЦИОННАЯ СИСТЕМА И ПК" : "OPERATING SYSTEM & PC", new[]
            {
                new KeyValuePair<string, string>(isRu ? "Имя компьютера" : "Computer Name", _cachedOs.ComputerName),
                new KeyValuePair<string, string>(isRu ? "Пользователь" : "Current User", _cachedOs.RegisteredUser),
                new KeyValuePair<string, string>(isRu ? "ОС" : "OS", $"{osCaption} ({osArch})"),
                new KeyValuePair<string, string>(isRu ? "Версия / Сборка" : "Version / Build", $"{_cachedOs.Version} (Build {_cachedOs.BuildNumber})"),
                new KeyValuePair<string, string>(isRu ? "Время работы (Uptime)" : "System Uptime", _cachedUptime),
                new KeyValuePair<string, string>(isRu ? "Дата установки" : "Install Date", _cachedOs.InstallDate)
            }));
        }

        // CPU
        if (_cachedCpu != null && _cachedCpu.Count > 0)
        {
            foreach (var cpu in _cachedCpu)
            {
                SysInfoPanel.Children.Add(CreateSection(isRu ? $"ПРОЦЕССОР — {cpu.Name}" : $"PROCESSOR — {cpu.Name}", new[]
                {
                    new KeyValuePair<string, string>(isRu ? "Физических ядер" : "Physical Cores", cpu.Cores.ToString()),
                    new KeyValuePair<string, string>(isRu ? "Логических процессоров" : "Logical Processors", cpu.LogicalProcessors.ToString()),
                    new KeyValuePair<string, string>(isRu ? "Базовая частота" : "Base Clock", cpu.MaxClockMhz),
                    new KeyValuePair<string, string>(isRu ? "Архитектура" : "Architecture", cpu.Architecture),
                    new KeyValuePair<string, string>(isRu ? "Сокет" : "Socket", cpu.SocketDesignation)
                }));
            }
        }

        // RAM Slots
        if (_cachedRam != null && _cachedRam.Count > 0)
        {
            long totalRamBytes = _cachedRam.Sum(r => r.CapacityBytes);
            var list = new List<KeyValuePair<string, string>>
            {
                new(isRu ? "Общий объем" : "Total Installed", StorageService.FormatBytes(totalRamBytes)),
                new(isRu ? "Слотов занято" : "Slots Occupied", _cachedRam.Count.ToString())
            };
            foreach (var slot in _cachedRam)
            {
                list.Add(new KeyValuePair<string, string>(
                    isRu ? $"Слот {slot.BankLabel}" : $"Slot {slot.BankLabel}",
                    $"{slot.FormattedSize} · {slot.Speed} · {slot.Manufacturer} ({slot.MemoryType})"
                ));
            }
            SysInfoPanel.Children.Add(CreateSection(isRu ? "ОПЕРАТИВНАЯ ПАМЯТЬ (RAM)" : "PHYSICAL MEMORY (RAM)", list));
        }

        // GPU
        if (_cachedGpus != null && _cachedGpus.Count > 0)
        {
            foreach (var g in _cachedGpus)
            {
                SysInfoPanel.Children.Add(CreateSection(isRu ? $"ВИДЕОКАРТА — {g.Name}" : $"GRAPHICS ADAPTER — {g.Name}", new[]
                {
                    new KeyValuePair<string, string>(isRu ? "Видеопамять (VRAM)" : "Video Memory (VRAM)", g.FormattedRam),
                    new KeyValuePair<string, string>(isRu ? "Версия драйвера" : "Driver Version", g.DriverVersion)
                }));
            }
        }

        // Motherboard & BIOS
        if (_cachedMb != null && _cachedBios != null)
        {
            SysInfoPanel.Children.Add(CreateSection(isRu ? "МАТЕРИНСКАЯ ПЛАТА И BIOS" : "MOTHERBOARD & BIOS", new[]
            {
                new KeyValuePair<string, string>(isRu ? "Производитель платы" : "Motherboard Vendor", _cachedMb.Manufacturer),
                new KeyValuePair<string, string>(isRu ? "Модель платы" : "Motherboard Model", _cachedMb.Product),
                new KeyValuePair<string, string>(isRu ? "Серийный номер" : "Serial Number", _cachedMb.SerialNumber),
                new KeyValuePair<string, string>(isRu ? "Производитель BIOS" : "BIOS Vendor", _cachedBios.Manufacturer),
                new KeyValuePair<string, string>(isRu ? "Версия BIOS" : "BIOS Version", _cachedBios.Version),
                new KeyValuePair<string, string>(isRu ? "Дата выпуска BIOS" : "BIOS Release Date", _cachedBios.ReleaseDate)
            }));
        }

        // Storage
        if (_cachedDisks != null && _cachedDisks.Count > 0)
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (var d in _cachedDisks)
            {
                string freeOfTotal = isEn ? $"{d.FormattedFree} free of {d.FormattedTotal}" : $"{d.FormattedFree} свободно из {d.FormattedTotal}";
                string diskLabel = d.Label;
                if (string.IsNullOrWhiteSpace(diskLabel) ||
                    diskLabel.Equals("Локальный диск", StringComparison.OrdinalIgnoreCase) ||
                    diskLabel.Equals("Local Disk", StringComparison.OrdinalIgnoreCase))
                {
                    diskLabel = isEn ? "Local Disk" : "Локальный диск";
                }
                string driveTypeDesc = d.DriveType switch
                {
                    "Fixed" => isEn ? "Fixed Disk" : "Локальный диск",
                    "Removable" => isEn ? "Removable USB" : "Съемный диск",
                    "Network" => isEn ? "Network Drive" : "Сетевой диск",
                    "CDRom" => isEn ? "CD/DVD Drive" : "Дисковод",
                    _ => d.DriveType
                };
                list.Add(new KeyValuePair<string, string>(
                    isEn ? $"Drive {d.DriveLetter}: [{diskLabel}]" : $"Диск {d.DriveLetter}: [{diskLabel}]",
                    $"{freeOfTotal} ({d.FileSystem}, {driveTypeDesc})"
                ));
            }
            SysInfoPanel.Children.Add(CreateSection(isRu ? "ФИЗИЧЕСКИЕ И ЛОГИЧЕСКИЕ НАКОПИТЕЛИ" : "PHYSICAL & LOGICAL DRIVES", list));
        }

        // Network Adapters Summary with inline Ping button
        if (_cachedNet != null && _cachedNet.Count > 0)
        {
            var netItems = _cachedNet.Select(n => new KeyValuePair<string, string>(
                n.Name,
                $"{n.StatusText} · {n.IpAndMac}"
            ));
            var netBorder = CreateSection(isRu ? "СЕТЕВЫЕ ИНТЕРФЕЙСЫ" : "NETWORK INTERFACES", netItems);
            if (netBorder.Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock titleTb)
            {
                sp.Children.RemoveAt(0);
                var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                titleTb.Margin = new Thickness(0);
                titleTb.VerticalAlignment = VerticalAlignment.Center;

                var pingLabel = new TextBlock
                {
                    Text = isEn ? "Ping (8.8.8.8)" : "Пинг (8.8.8.8)",
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var pingContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var pingIcon = new System.Windows.Shapes.Path
                {
                    Data = (Geometry)FindResource("IconSearch"),
                    Width = 11,
                    Height = 11,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                pingIcon.SetResourceReference(System.Windows.Shapes.Path.FillProperty, "BrushTextPrimary");
                pingContent.Children.Add(pingIcon);
                pingContent.Children.Add(pingLabel);

                var btnPing = new Button
                {
                    Style = (Style)FindResource("BtnPill"),
                    Padding = new Thickness(10, 4, 10, 4),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Content = pingContent
                };
                btnPing.Click += async (_, _) => await RunPingIntoLabelAsync(pingLabel);

                headerGrid.Children.Add(titleTb);
                headerGrid.Children.Add(btnPing);
                sp.Children.Insert(0, headerGrid);
            }
            SysInfoPanel.Children.Add(netBorder);
        }
    }

    // Async Startup & Network refresh
    public async Task RefreshStartupListAsync()
    {
        if (_startupChanging) return;
        int version = ++_startupRefreshVersion;
        try
        {
            var items = await Task.Run(() => StartupService.GetAllEntries());
            if (version != _startupRefreshVersion) return;
            _startupEntries = items;
            StartupList.ItemsSource = _startupEntries;
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to enumerate startup items", ex);
        }
    }

    private void BtnStartupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is StartupEntry entry)
            _ = ChangeStartupEntryAsync(entry, remove: false);
    }

    private void BtnStartupDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is StartupEntry entry)
            _ = ChangeStartupEntryAsync(entry, remove: true);
    }

    private async Task ChangeStartupEntryAsync(StartupEntry entry, bool remove)
    {
        if (_startupChanging) return;
        _startupChanging = true;
        _startupRefreshVersion++;
        StartupList.IsEnabled = false;
        try
        {
            bool isEn = LocalizationService.IsEnglish;
            var result = await Task.Run(() => remove ? StartupService.DeleteEntry(entry, isEn) : StartupService.SetEntryEnabled(entry, !entry.IsEnabled, isEn));
            if (!result.Success) OnStatusChanged?.Invoke(result.Message);
        }
        catch (Exception ex) { Logger.Error("Startup entry change failed", ex); }
        finally
        {
            _startupChanging = false;
            await RefreshStartupListAsync();
            StartupList.IsEnabled = true;
        }
    }

    private void BtnRefreshStartup_Click(object sender, RoutedEventArgs e) => _ = RefreshStartupListAsync();

    private void StartupList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox lb || SettingsScrollViewer == null) return;
        var sv = FindDescendant<ScrollViewer>(lb);
        if (sv == null || sv.ScrollableHeight <= 0 ||
            (e.Delta > 0 && sv.VerticalOffset <= 0) ||
            (e.Delta < 0 && sv.VerticalOffset >= sv.ScrollableHeight - 0.5))
        {
            SettingsScrollViewer.ScrollToVerticalOffset(SettingsScrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var res = FindDescendant<T>(child);
            if (res != null) return res;
        }
        return null;
    }

    private static async Task RunPingIntoLabelAsync(TextBlock targetLabel)
    {
        targetLabel.Text = "...";
        bool isEn = LocalizationService.IsEnglish;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync("8.8.8.8", 2000);
            if (reply.Status == IPStatus.Success)
            {
                targetLabel.Text = isEn ? $"{reply.RoundtripTime} ms" : $"{reply.RoundtripTime} мс";
            }
            else
            {
                targetLabel.Text = isEn ? "Timeout" : "Нет ответа";
            }
        }
        catch
        {
            targetLabel.Text = isEn ? "Error" : "Ошибка";
        }
    }

    // Handlers
    private void BtnLang_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string lang)
        {
            _currentLanguage = lang == "RU" ? "RU" : "EN";
            UpdateToggleButtonStyles();
            OnLanguageChanged?.Invoke(_currentLanguage);
        }
    }

    private void BtnTheme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string theme)
        {
            bool wantLight = theme == "Light";
            _isLightTheme = wantLight;
            UpdateToggleButtonStyles();
            OnThemeChanged?.Invoke(wantLight);
        }
    }

    private void BtnTimeFormat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string mode)
        {
            bool is12h = mode == "12";
            _use12HourTime = is12h;
            UpdateToggleButtonStyles();
            OnTimeFormatChanged?.Invoke(is12h);
        }
    }

    private void SliderHudOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtHudOpacityVal != null) TxtHudOpacityVal.Text = $"{e.NewValue:0}%";
        if (!_isInitializing) OnHudOpacityChanged?.Invoke(e.NewValue);
    }

    private void SliderCpuAlert_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtCpuAlertVal != null) TxtCpuAlertVal.Text = $"{e.NewValue:0}%";
        if (!_isInitializing) OnCpuAlertChanged?.Invoke(e.NewValue);
    }

    private void SliderRamAlert_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _ramThresholdValue = e.NewValue;
        if (TxtRamAlertVal != null) TxtRamAlertVal.Text = $"{e.NewValue:0}%";
        UpdateAutoRamHeading();
        if (!_isInitializing) OnRamAlertChanged?.Invoke(e.NewValue);
    }

    private void BtnIntervalSelector_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) OnIntervalSelectorRequested?.Invoke(fe);
    }

    private void BtnThresholdSelector_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) OnThresholdSelectorRequested?.Invoke(fe);
    }

    private void BtnToggleAutoRam_Click(object sender, RoutedEventArgs e)
    {
        _autoRamEnabled = !_autoRamEnabled;
        UpdateToggleButtonStyles();
        OnAutoRamChanged?.Invoke(_autoRamEnabled);
    }

    private void BtnToggleAutoGamePriority_Click(object sender, RoutedEventArgs e)
    {
        _autoGamePriorityEnabled = !_autoGamePriorityEnabled;
        UpdateToggleButtonStyles();
        OnAutoGamePriorityChanged?.Invoke(_autoGamePriorityEnabled);
    }

    private void BtnToggleRunAtStartup_Click(object sender, RoutedEventArgs e)
    {
        _runAtStartup = !_runAtStartup;
        UpdateToggleButtonStyles();
        OnRunAtStartupChanged?.Invoke(_runAtStartup);
    }

    private void BtnToggleGlobalHotkeys_Click(object sender, RoutedEventArgs e)
    {
        _enableGlobalHotkeys = !_enableGlobalHotkeys;
        UpdateToggleButtonStyles();
        OnGlobalHotkeysChanged?.Invoke(_enableGlobalHotkeys);
    }

    private void BtnToggleThresholdAlerts_Click(object sender, RoutedEventArgs e)
    {
        _enableThresholdAlerts = !_enableThresholdAlerts;
        UpdateToggleButtonStyles();
        OnThresholdAlertsChanged?.Invoke(_enableThresholdAlerts);
    }

    private static List<string> ParseProcessCsv(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? new List<string>()
            : text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => s.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase))
                  .Where(s => !string.IsNullOrWhiteSpace(s))
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .ToList();

    private void ApplyProcessLists()
    {
        if (_isInitializing) return;
        OnProcessListsChanged?.Invoke(ParseProcessCsv(TxtProcessWhitelist.Text), ParseProcessCsv(TxtCustomGames.Text));
    }

    private void TxtLists_LostFocus(object sender, RoutedEventArgs e) => ApplyProcessLists();
    private void BtnSaveProcessLists_Click(object sender, RoutedEventArgs e) => ApplyProcessLists();

    private void BtnExportBackup_Click(object sender, RoutedEventArgs e) => OnExportBackupRequested?.Invoke();
    private void BtnImportBackup_Click(object sender, RoutedEventArgs e) => OnImportBackupRequested?.Invoke();
    private void BtnExportReport_Click(object sender, RoutedEventArgs e) => OnExportReportRequested?.Invoke();
    private void BtnResetSettings_Click(object sender, RoutedEventArgs e) => OnResetSettingsRequested?.Invoke();
    private void BtnToggleTabEdit_Click(object sender, RoutedEventArgs e) => OnToggleTabEditRequested?.Invoke();
    private void BtnOpenTabVault_Click(object sender, RoutedEventArgs e) => OnOpenTabVaultRequested?.Invoke();
}
