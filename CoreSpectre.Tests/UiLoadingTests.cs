using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CoreSpectre.Services;
using CoreSpectre.Views;
using Xunit;

namespace CoreSpectre.Tests;

[Collection("Settings files")]
public class UiLoadingTests
{
    [Fact]
    public void App_Resources_And_Windows_Load_Without_Exceptions()
    {
        string isolatedSettingsPath = Path.Combine(AppContext.BaseDirectory, $"test_settings_{Guid.NewGuid():N}.json");
        SettingsPersistenceService.SettingsPathOverride = isolatedSettingsPath;
        string? failureDetails = null;

        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current == null)
                {
                    var app = new App();
                    app.InitializeComponent();
                }

                var mainWindow = new MainWindow();

                // Test all tabs
                for (int i = 0; i < 5; i++)
                {
                    mainWindow.HighlightTab(i);
                }

                // Test instantiation of all standalone views
                _ = new OverviewView();
                _ = new PurgeView();
                _ = new StorageView();
                _ = new GamesView();
                _ = new SettingsView();
                var hud = new TelemetryHudWindow();
                hud.ApplyLocalizationAndTheme();
                hud.ApplySnapshot(new TelemetrySnapshot(
                    CpuLoadPercent: 12.5,
                    RamUsedPercent: 45.0,
                    RamUsedBytes: 4L * 1024 * 1024 * 1024,
                    RamTotalBytes: 16L * 1024 * 1024 * 1024,
                    SystemDriveLetter: "C",
                    DiskCFreeGb: 50,
                    DiskCTotalGb: 100,
                    DataDriveLetter: "D",
                    DiskDFreeGb: 100,
                    DiskDTotalGb: 200,
                    HasSecondaryDataDrive: true,
                    IsGameRunning: false,
                    ActiveGameName: "",
                    GpuLoadPercent: 20,
                    GpuTempCelsius: 55,
                    CpuHistory: new[] { 12.5 },
                    RamHistory: new[] { 45.0 }));
                hud.Close();

                // Test theme switching (Light and Dark)
                var currentApp = Application.Current;
                Assert.NotNull(currentApp);
                foreach (bool isLight in new[] { true, false })
                {
                    mainWindow.SetTheme(isLight);
                    var scrollbar = new ScrollBar
                    {
                        Style = (Style)currentApp.FindResource(typeof(ScrollBar)),
                        Orientation = Orientation.Horizontal
                    };
                    scrollbar.ApplyTemplate();
                    var track = Assert.IsType<Track>(scrollbar.Template.FindName("PART_Track", scrollbar));
                    Assert.False(track.IsDirectionReversed);
                    Assert.Equal(4, scrollbar.Height);
                    Assert.Same(ScrollBar.PageLeftCommand, track.DecreaseRepeatButton.Command);
                    Assert.Same(ScrollBar.PageRightCommand, track.IncreaseRepeatButton.Command);
                }

                // Test language switching and CPU/RAM mode toggle
                mainWindow.SetLanguage("RU");
                Assert.EndsWith(" сек", mainWindow.ViewSettings.TxtIntervalCurrent.Text);
                Assert.EndsWith(" ГБ", mainWindow.ViewSettings.TxtThresholdCurrent.Text);
                mainWindow.SetPurgeTabMode(isCpu: true, saveSettings: false);
                mainWindow.SetPurgeTabMode(isCpu: false, saveSettings: false);
                mainWindow.SetLanguage("EN");
                Assert.EndsWith("s", mainWindow.ViewSettings.TxtIntervalCurrent.Text);
                Assert.EndsWith(" GB", mainWindow.ViewSettings.TxtThresholdCurrent.Text);

                // Verify required navigation icons exist in active theme
                Assert.NotNull(currentApp.TryFindResource("IconSquaresFour"));
                Assert.NotNull(currentApp.TryFindResource("IconCpu"));
                Assert.NotNull(currentApp.TryFindResource("IconRam"));

                // Verify footer clock updates cleanly for 12h and 24h formats and displays UTC offset
                var clockTb = mainWindow.FindName("FooterClockText") as System.Windows.Controls.TextBlock;
                Assert.NotNull(clockTb);
                mainWindow.SetTimeFormat(false);
                Assert.Contains("(UTC", clockTb.Text);
                Assert.Matches(@"^\d{2}:\d{2}:\d{2} \(UTC[+-]\d+(?::\d{2})?\)$", clockTb.Text);
                mainWindow.SetTimeFormat(true);
                Assert.Contains("(UTC", clockTb.Text);
                Assert.Matches(@"^\d{2}:\d{2}:\d{2} (AM|PM) \(UTC[+-]\d+(?::\d{2})?\)$", clockTb.Text);

                // Verify CompactOS Enabling state survives MainWindow.SetLanguage even while Storage tab is collapsed
                mainWindow.HighlightTab(2);
                mainWindow.ViewStorage.CompactOsState = CompactOsState.Enabling;
                mainWindow.HighlightTab(4);
                mainWindow.SetLanguage("RU");
                Assert.Equal(CompactOsState.Enabling, mainWindow.ViewStorage.CompactOsState);
                Assert.Equal("CompactOS: Включение...", mainWindow.ViewStorage.BtnCompactOsText.Text);
                Assert.False(mainWindow.ViewStorage.BtnCompactOs.IsEnabled);
                mainWindow.SetLanguage("EN");
                Assert.Equal(CompactOsState.Enabling, mainWindow.ViewStorage.CompactOsState);
                Assert.Equal("CompactOS: Enabling...", mainWindow.ViewStorage.BtnCompactOsText.Text);

                // Verify closing MainWindow actually closes the app instead of minimizing to tray
                bool wasClosed = false;
                mainWindow.Closed += (_, _) => wasClosed = true;
                mainWindow.Close();
                Assert.True(wasClosed);
            }
            catch (Exception ex)
            {
                var cur = ex;
                var sb = new System.Text.StringBuilder();
                while (cur != null)
                {
                    sb.AppendLine($"[{cur.GetType().FullName}] {cur.Message}");
                    cur = cur.InnerException;
                }
                failureDetails = sb.ToString();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        try
        {
            SettingsPersistenceService.SettingsPathOverride = null;
            if (File.Exists(isolatedSettingsPath)) File.Delete(isolatedSettingsPath);
        }
        catch { }

        if (failureDetails != null)
        {
            Assert.Fail(failureDetails);
        }
    }

    [Fact]
    public void StorageView_CompactOsState_TranslatesOnLanguageSwitchAndBlocksReentry()
    {
        string isolatedSettingsPath = Path.Combine(AppContext.BaseDirectory, $"test_settings_{Guid.NewGuid():N}.json");
        SettingsPersistenceService.SettingsPathOverride = isolatedSettingsPath;
        string? failureDetails = null;

        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current == null)
                {
                    var app = new App();
                    app.InitializeComponent();
                }

                LocalizationService.CurrentLanguage = "EN";
                var storageView = new StorageView();
                storageView.CompactOsState = CompactOsState.Enabling;
                Assert.False(storageView.CanExecuteCompactOs);
                Assert.False(storageView.BtnCompactOs.IsEnabled);
                Assert.Equal("CompactOS: Enabling...", storageView.CompactOsButtonText);
                Assert.Equal("CompactOS: Enabling...", storageView.BtnCompactOsText.Text);

                // Switching language while Enabling must translate the active state, not reset to Off
                storageView.ApplyLanguage("RU");
                Assert.Equal(CompactOsState.Enabling, storageView.CompactOsState);
                Assert.False(storageView.CanExecuteCompactOs);
                Assert.False(storageView.BtnCompactOs.IsEnabled);
                Assert.Equal("CompactOS: Включение...", storageView.CompactOsButtonText);
                Assert.Equal("CompactOS: Включение...", storageView.BtnCompactOsText.Text);

                storageView.CompactOsState = CompactOsState.Disabling;
                Assert.False(storageView.CanExecuteCompactOs);
                Assert.False(storageView.BtnCompactOs.IsEnabled);
                Assert.Equal("CompactOS: Отключение...", storageView.CompactOsButtonText);
                Assert.Equal("CompactOS: Отключение...", storageView.BtnCompactOsText.Text);

                storageView.ApplyLanguage("EN");
                Assert.Equal(CompactOsState.Disabling, storageView.CompactOsState);
                Assert.Equal("CompactOS: Disabling...", storageView.CompactOsButtonText);
                Assert.Equal("CompactOS: Disabling...", storageView.BtnCompactOsText.Text);

                storageView.CompactOsState = CompactOsState.On;
                Assert.True(storageView.CanExecuteCompactOs);
                Assert.True(storageView.BtnCompactOs.IsEnabled);
                Assert.Equal("CompactOS: On", storageView.CompactOsButtonText);

                storageView.StopAutoScan();
            }
            catch (Exception ex)
            {
                var cur = ex;
                var sb = new System.Text.StringBuilder();
                while (cur != null)
                {
                    sb.AppendLine($"[{cur.GetType().FullName}] {cur.Message}");
                    cur = cur.InnerException;
                }
                failureDetails = sb.ToString();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        try
        {
            LocalizationService.CurrentLanguage = "EN";
            SettingsPersistenceService.SettingsPathOverride = null;
            if (File.Exists(isolatedSettingsPath)) File.Delete(isolatedSettingsPath);
        }
        catch { }

        if (failureDetails != null)
        {
            Assert.Fail(failureDetails);
        }
    }
}
