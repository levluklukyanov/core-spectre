using System;
using System.IO;
using System.Text.Json;
using CoreSpectre.Services;
using Xunit;

namespace CoreSpectre.Tests;

[Collection("Settings files")]
public class SettingsPersistenceTests
{
    [Fact]
    public void UserSettingsModel_DefaultValues_AdhereToStandards()
    {
        var model = new UserSettingsModel();

        Assert.Equal("EN", model.Language);
        Assert.Equal("Dark", model.Theme);
        Assert.True(model.Use12HourTime);
        Assert.Equal(1.0, model.TelemetryInterval);
        Assert.Equal(7.0, model.DiskWarningThresholdGb);
        Assert.Equal(90.0, model.CpuAlertThreshold);
        Assert.Equal(85.0, model.RamAlertThreshold);
        Assert.True(model.AutoRamPurge);
        Assert.True(model.AutoGamePriority);
        Assert.Equal("C", model.WatchedDriveLetter);
        Assert.False(model.IncludeLaunchers);
        Assert.Equal("RAM", model.PurgeMode);
        Assert.True(model.EnableGlobalHotkeys);
        Assert.True(model.EnableThresholdAlerts);

        Assert.Contains("overview", model.ActiveTabs);
        Assert.Contains("ram", model.ActiveTabs);
        Assert.Contains("storage", model.ActiveTabs);
        Assert.Contains("games", model.ActiveTabs);
        Assert.Contains("settings", model.ActiveTabs);

        Assert.True(model.CleanTempFiles);
        Assert.True(model.CleanSoftwareDistribution);
        Assert.True(model.CleanLogsAndReports);
        Assert.True(model.CleanDnsCache);
        Assert.False(model.CleanRecycleBin);
        Assert.False(model.CleanGpuShaderCache);
        Assert.False(model.CleanBrowserCache);
        Assert.True(model.CleanDismComponentCleanup);
        Assert.False(model.CleanDismResetBase);
        Assert.Equal("SizeDesc", model.StorageSortMode);
        Assert.False(model.RunAtStartup);
        Assert.Equal(0, model.TempMinAgeDays);
    }

    [Fact]
    public void SerializationRoundTrip_PreservesAllSettings()
    {
        var original = new UserSettingsModel
        {
            Language = "RU",
            Theme = "Light",
            Use12HourTime = false,
            TelemetryInterval = 2.5,
            DiskWarningThresholdGb = 15.0,
            CpuAlertThreshold = 95.0,
            RamAlertThreshold = 92.0,
            HudOpacity = 80.0,
            AutoRamPurge = false,
            AutoGamePriority = false,
            WatchedDriveLetter = "D",
            IncludeLaunchers = true,
            PurgeMode = "STANDBY",
            ActiveTabs = new List<string> { "overview", "storage" },
            VaultTabs = new List<string> { "games", "settings" },
            CleanTempFiles = false,
            CleanSoftwareDistribution = false,
            CleanLogsAndReports = false,
            CleanDnsCache = false,
            CleanRecycleBin = true,
            CleanGpuShaderCache = true,
            CleanBrowserCache = true,
            CleanDismComponentCleanup = false,
            CleanDismResetBase = true,
            StorageSortMode = "NameAsc",
            EnableGlobalHotkeys = false,
            EnableThresholdAlerts = false,
            ProcessWhitelist = new List<string> { "discord", "obs64" },
            CustomGameProcesses = new List<string> { "valorant" }
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        string json = JsonSerializer.Serialize(original, options);
        var restored = JsonSerializer.Deserialize<UserSettingsModel>(json, options);

        Assert.NotNull(restored);
        Assert.Equal("RU", restored.Language);
        Assert.Equal("Light", restored.Theme);
        Assert.False(restored.Use12HourTime);
        Assert.Equal(2.5, restored.TelemetryInterval);
        Assert.Equal(15.0, restored.DiskWarningThresholdGb);
        Assert.Equal(95.0, restored.CpuAlertThreshold);
        Assert.Equal(92.0, restored.RamAlertThreshold);
        Assert.Equal(80.0, restored.HudOpacity);
        Assert.False(restored.AutoRamPurge);
        Assert.False(restored.AutoGamePriority);
        Assert.Equal("D", restored.WatchedDriveLetter);
        Assert.True(restored.IncludeLaunchers);
        Assert.Equal("STANDBY", restored.PurgeMode);
        Assert.Equal(2, restored.ActiveTabs.Count);
        Assert.Equal(2, restored.VaultTabs.Count);
        Assert.False(restored.CleanTempFiles);
        Assert.False(restored.CleanSoftwareDistribution);
        Assert.False(restored.CleanLogsAndReports);
        Assert.False(restored.CleanDnsCache);
        Assert.True(restored.CleanRecycleBin);
        Assert.True(restored.CleanGpuShaderCache);
        Assert.True(restored.CleanBrowserCache);
        Assert.False(restored.CleanDismComponentCleanup);
        Assert.True(restored.CleanDismResetBase);
        Assert.Equal("NameAsc", restored.StorageSortMode);
        Assert.Contains("discord", restored.ProcessWhitelist);
        Assert.Contains("valorant", restored.CustomGameProcesses);
    }

    [Fact]
    public void BackupCsb_ExportAndImport_PreservesSettingsAndRejectsTamperedFiles()
    {
        string backupPath = Path.Combine(AppContext.BaseDirectory, $"test_backup_{Guid.NewGuid():N}.csb");
        try
        {
            var settings = new UserSettingsModel
            {
                Language = "RU",
                Theme = "Light",
                WatchedDriveLetter = "D",
                ProcessWhitelist = new List<string> { "obs64" },
                CustomGameProcesses = new List<string> { "eldenring" }
            };

            var (okExport, _) = SettingsPersistenceService.ExportBackup(backupPath, settings, isEn: true);
            Assert.True(okExport);
            Assert.True(File.Exists(backupPath));

            var (okImport, imported, _) = SettingsPersistenceService.ImportBackup(backupPath, isEn: true);
            Assert.True(okImport);
            Assert.NotNull(imported);
            Assert.Equal("RU", imported.Language);
            Assert.Equal("Light", imported.Theme);
            Assert.Equal("D", imported.WatchedDriveLetter);
            Assert.Contains("obs64", imported.ProcessWhitelist);
            Assert.Contains("eldenring", imported.CustomGameProcesses);

            // Tamper file without updating SHA-256 checksum
            string tampered = File.ReadAllText(backupPath).Replace("\"Language\": \"RU\"", "\"Language\": \"EN\"");
            File.WriteAllText(backupPath, tampered);

            var (okTampered, _, _) = SettingsPersistenceService.ImportBackup(backupPath, isEn: true);
            Assert.False(okTampered);
        }
        finally
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }

    [Theory]
    [InlineData("d:", "D")]
    [InlineData("C", "C")]
    [InlineData("C:\\Windows\\System32", "C")]
    [InlineData("123", "C")]
    [InlineData("", "C")]
    public void NormalizeDriveLetter_ValidatesStrictAsciiDriveLetter(string input, string expected)
    {
        Assert.Equal(expected, SettingsPersistenceService.NormalizeDriveLetter(input, "C"));
    }

    [Fact]
    public void LoadAndSave_SanitizesOutOfRangeValuesAndPersistsCleanly()
    {
        string tempSettingsPath = Path.Combine(AppContext.BaseDirectory, $"test_persist_{Guid.NewGuid():N}.json");
        SettingsPersistenceService.SettingsPathOverride = tempSettingsPath;
        try
        {
            var dirty = new UserSettingsModel
            {
                Language = "FR",
                Theme = "InvalidTheme",
                TelemetryInterval = 0.01,
                DiskWarningThresholdGb = 5000.0,
                CpuAlertThreshold = 5.0,
                RamAlertThreshold = 150.0,
                HudOpacity = 5.0,
                WatchedDriveLetter = "INVALID",
                TempMinAgeDays = 99
            };

            Assert.True(SettingsPersistenceService.Save(dirty));
            var loaded = SettingsPersistenceService.Load();

            Assert.Equal("EN", loaded.Language);
            Assert.Equal("Dark", loaded.Theme);
            Assert.Equal(0.5, loaded.TelemetryInterval);
            Assert.Equal(500.0, loaded.DiskWarningThresholdGb);
            Assert.Equal(10.0, loaded.CpuAlertThreshold);
            Assert.Equal(100.0, loaded.RamAlertThreshold);
            Assert.Equal(20.0, loaded.HudOpacity);
            Assert.Equal("C", loaded.WatchedDriveLetter);
            Assert.Equal(90, loaded.TempMinAgeDays);
        }
        finally
        {
            SettingsPersistenceService.SettingsPathOverride = null;
            if (File.Exists(tempSettingsPath)) File.Delete(tempSettingsPath);
        }
    }

    [Fact]
    public void ImportBackup_RemainsValidForMinimalLegacyPayloadWhenChecksumMatches()
    {
        string backupPath = Path.Combine(AppContext.BaseDirectory, $"test_legacy_{Guid.NewGuid():N}.csb");
        try
        {
            var payload = new { Language = "RU", Theme = "Light" };
            string canonical = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
            string docJson = JsonSerializer.Serialize(new
            {
                Format = "CoreSpectreBackup",
                Version = 1,
                AppVersion = "1.1.0",
                ExportedAtUtc = DateTime.UtcNow,
                Checksum = sha,
                Settings = payload
            }, new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(backupPath, docJson);
            var (ok, model, _) = SettingsPersistenceService.ImportBackup(backupPath, isEn: true);
            Assert.True(ok);
            Assert.NotNull(model);
            Assert.Equal("RU", model.Language);
            Assert.Equal("Light", model.Theme);
        }
        finally
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
    }
}
