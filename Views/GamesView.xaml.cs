using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CoreSpectre.Services;

namespace CoreSpectre.Views;

public partial class GamesView : UserControl
{
    private string _gameDriveFilter = "ALL";
    private bool _includeLaunchers = false;
    private bool _isScanningGames = false;
    private bool _hasScannedGames;
    private string? _scanError;
    private List<InstalledGameInfo> _installedGames = new();

    public event Action? OnBoostPriorityRequested;
    public event Action? OnRefreshActiveGamesRequested;
    public event Action<FrameworkElement>? OnGameDriveFilterRequested;
    public event Action<bool>? OnIncludeLaunchersChanged;
    public event Action<string>? OnStatusChanged;

    public bool IncludeLaunchers => _includeLaunchers;
    public string GameDriveFilter => _gameDriveFilter;

    public GamesView()
    {
        InitializeComponent();
        var settings = SettingsPersistenceService.Load();
        _includeLaunchers = settings.IncludeLaunchers;
        UpdateLauncherToggleVisual();
    }

    public void ApplyLanguage(string language)
    {
        bool isEn = language == "EN";
        GamesTabHeading.Text = LocalizationService.Get("Games.Title");
        GamesTabSub.Text = LocalizationService.Get("Games.Subtitle");
        GamesBannerTitle.Text = LocalizationService.Get("Games.BannerTitle");
        GamesBannerSub.Text = LocalizationService.Get("Games.BannerSub");
        BtnGamesBoostText.Text = LocalizationService.Get("Games.BoostBtn");
        ActiveGamesSectionTitle.Text = LocalizationService.Get("Games.ActiveTitle");
        InstalledGamesSectionTitle.Text = LocalizationService.Get("Games.InstalledTitle");
        InstalledGamesSubtitle.Text = LocalizationService.Get("Games.InstalledSub");
        BtnScanGamesText.Text = LocalizationService.Get("Games.ScanDrivesBtn");
        TxtNoActiveGames.Text = LocalizationService.Get("Games.NoActive");
        UpdateScanStatus();
        InstalledGamesFooterInfo.Text = isEn
            ? "Scans Steam, Xbox, Epic, AppData (.minecraft, .tlauncher), and custom drive game folders."
            : "Сканирует библиотеки Steam, Xbox, Epic, AppData (.minecraft, .tlauncher) и папки Games на дисках.";

        UpdateDriveFilterVisual();
        UpdateLauncherToggleVisual();
    }

    public void UpdateDriveFilter(string filter)
    {
        if (_gameDriveFilter == filter || _isScanningGames) return;
        _gameDriveFilter = filter;
        _hasScannedGames = false;
        _scanError = null;
        _installedGames = new();
        ListInstalledGames.ItemsSource = null;
        UpdateDriveFilterVisual();
        UpdateScanStatus();
    }

    private void UpdateDriveFilterVisual()
    {
        bool isEn = LocalizationService.IsEnglish;
        TxtGameDriveFilter.Text = _gameDriveFilter == "ALL"
            ? (isEn ? "All Drives" : "Все диски")
            : (isEn ? $"Drive {_gameDriveFilter}:" : $"Диск {_gameDriveFilter}:");
    }

    public void SetIncludeLaunchers(bool include)
    {
        _includeLaunchers = include;
        UpdateLauncherToggleVisual();
    }

    public void UpdateLauncherToggleVisual()
    {
        bool isEn = LocalizationService.IsEnglish;
        if (_includeLaunchers)
        {
            IconToggleLaunchers.Fill = (SolidColorBrush)FindResource("BrushTextPrimary");
            BtnToggleLaunchers.ToolTip = isEn
                ? "Minecraft launchers: INCLUDED (Click to ignore)"
                : "Лаунчеры Minecraft: ВКЛЮЧЕНЫ (Нажмите, чтобы скрыть)";
        }
        else
        {
            IconToggleLaunchers.Fill = (SolidColorBrush)FindResource("BrushTextSecondary");
            BtnToggleLaunchers.ToolTip = isEn
                ? "Minecraft launchers: EXCLUDED (Click to include)"
                : "Лаунчеры Minecraft: СКРЫТЫ (Нажмите, чтобы включить)";
        }
    }

    public void UpdateActiveGames(List<ActiveGameInfo> games)
    {
        ListActiveGames.ItemsSource = games;
        TxtNoActiveGames.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public async Task ScanGamesAsync()
    {
        if (_isScanningGames) return;
        _isScanningGames = true;
        BtnScanGames.IsEnabled = false;
        BtnGameDriveFilter.IsEnabled = false;
        BtnToggleLaunchers.IsEnabled = false;
        _scanError = null;
        UpdateScanStatus();

        try
        {
            _installedGames = await GameBoostService.ScanInstalledGamesAsync(_gameDriveFilter, LocalizationService.IsRussian, _includeLaunchers);
            ListInstalledGames.ItemsSource = _installedGames;
            _hasScannedGames = true;
            OnStatusChanged?.Invoke(LocalizationService.IsEnglish
                ? $"Game library scan complete: {_installedGames.Count} items." 
                : $"Сканирование каталога игр завершено: найдено {_installedGames.Count}.");
        }
        catch (Exception ex)
        {
            Logger.Error("Game scan failed", ex);
            _scanError = ex.Message;
        }
        finally
        {
            _isScanningGames = false;
            BtnScanGames.IsEnabled = true;
            BtnGameDriveFilter.IsEnabled = true;
            BtnToggleLaunchers.IsEnabled = true;
            UpdateScanStatus();
        }
    }

    private void UpdateScanStatus()
    {
        bool isEn = LocalizationService.IsEnglish;
        InstalledScanCountText.Text = "";
        TxtScanPlaceholder.Visibility = !_isScanningGames && _scanError == null && _installedGames.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        TxtScanPlaceholder.Text = _hasScannedGames
            ? (isEn ? "No games found on the selected drives." : "На выбранных дисках игры не найдены.")
            : LocalizationService.Get("Games.NoScan");

        if (_isScanningGames)
            InstalledScanStatusText.Text = isEn ? $"Scanning storage ({_gameDriveFilter})..." : $"Сканирование накопителей ({_gameDriveFilter})...";
        else if (_scanError != null)
            InstalledScanStatusText.Text = isEn ? $"Scan failed: {_scanError}" : $"Ошибка сканирования: {_scanError}";
        else if (_hasScannedGames)
        {
            int count = _installedGames.Count;
            string size = StorageService.FormatBytes(_installedGames.Sum(g => g.SizeBytes));
            InstalledScanStatusText.Text = isEn ? $"Scan completed: {count} game directories found." : $"Сканирование завершено: найдено каталогов: {count}.";
            InstalledScanCountText.Text = isEn ? $"{count} games ({size})" : $"{count} игр ({size})";
        }
        else
            InstalledScanStatusText.Text = isEn ? "Click 'Scan Drives' to discover games on your PC." : "Нажмите «Сканировать диски» для поиска игр на ПК.";
    }

    private async void BtnScanGames_Click(object sender, RoutedEventArgs e)
    {
        await ScanGamesAsync();
    }

    private void BtnBoostGamePriority_Click(object sender, RoutedEventArgs e) => OnBoostPriorityRequested?.Invoke();
    private void BtnRefreshActiveGames_Click(object sender, RoutedEventArgs e) => OnRefreshActiveGamesRequested?.Invoke();

    private void BtnGameDriveFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) OnGameDriveFilterRequested?.Invoke(fe);
    }

    private void BtnToggleLaunchers_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        bool isEn = LocalizationService.IsEnglish;

        var cm = new ContextMenu
        {
            PlacementTarget = btn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        var miToggle = new MenuItem
        {
            Header = isEn ? "Scan Minecraft Launchers (TLauncher, etc.)" : "Искать лаунчеры Майнкрафт (TLauncher и др.)",
            IsCheckable = true,
            IsChecked = _includeLaunchers
        };
        miToggle.Click += (s, ev) =>
        {
            _includeLaunchers = miToggle.IsChecked;
            UpdateLauncherToggleVisual();
            OnIncludeLaunchersChanged?.Invoke(_includeLaunchers);
            _ = ScanGamesAsync();
        };

        cm.Items.Add(miToggle);
        cm.IsOpen = true;
    }

    private void BtnOpenGameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string path && Directory.Exists(path))
        {
            try
            {
                string safePath = Path.GetFullPath(path);
                string explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                using var process = Process.Start(new ProcessStartInfo(explorerPath, $"\"{Path.TrimEndingDirectorySeparator(safePath)}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to open game folder: {path}", ex);
            }
        }
    }
}
