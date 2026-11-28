using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace CoreSpectre.Services;

public class UserSettingsModel
{
    public string Language { get; set; } = "EN";
    public string Theme { get; set; } = "Dark";
    public bool Use12HourTime { get; set; } = true;
    public double TelemetryInterval { get; set; } = 1.0;
    public double DiskWarningThresholdGb { get; set; } = 7.0;
    public double CpuAlertThreshold { get; set; } = 90.0;
    public double RamAlertThreshold { get; set; } = 85.0;
    public double HudOpacity { get; set; } = 90.0;
    public bool AutoRamPurge { get; set; } = true;
    public bool AutoGamePriority { get; set; } = true;
    public string WatchedDriveLetter { get; set; } = "C";
    public bool IncludeLaunchers { get; set; } = false;
    public string PurgeMode { get; set; } = "RAM";
    public List<string> ActiveTabs { get; set; } = new() { "overview", "ram", "storage", "games", "settings" };
    public List<string> VaultTabs { get; set; } = new();

    // Storage cleanup customization options
    public bool CleanTempFiles { get; set; } = true;
    public bool CleanSoftwareDistribution { get; set; } = true;
    public bool CleanLogsAndReports { get; set; } = true;
    public bool CleanDnsCache { get; set; } = true;
    public bool CleanRecycleBin { get; set; } = false;
    public bool CleanGpuShaderCache { get; set; } = false;
    public bool CleanBrowserCache { get; set; } = false;
    public bool CleanDismComponentCleanup { get; set; } = true;
    public bool CleanDismResetBase { get; set; } = false;
    public string StorageSortMode { get; set; } = "SizeDesc";
    public int TempMinAgeDays { get; set; } = 0;
    public List<string> DisabledCleanupTargets { get; set; } = new();

    // Extended usability, safety and automation features
    public bool RunAtStartup { get; set; } = false;
    public bool EnableGlobalHotkeys { get; set; } = true;
    public bool EnableThresholdAlerts { get; set; } = true;
    public double HudLeft { get; set; } = 20.0;
    public double HudTop { get; set; } = 20.0;
    public string HudDriveLetter { get; set; } = "";
    public List<string> ProcessWhitelist { get; set; } = new();
    public List<string> CustomGameProcesses { get; set; } = new();
}

public record CoreSpectreBackupEnvelope(
    string Format,
    int Version,
    string AppVersion,
    DateTime ExportedAtUtc,
    string Checksum,
    UserSettingsModel Settings
);

public static class SettingsPersistenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private static readonly object _fileLock = new();

    /// <summary>
    /// Optional override path used strictly for isolated automated tests so user settings on disk are never touched.
    /// </summary>
    internal static string? SettingsPathOverride { get; set; }

    public static event Action<string>? OnSettingsError;
    public static string? LastError { get; private set; }

    public static string NormalizeDriveLetter(string? driveLetter, string fallback = "C")
    {
        if (!string.IsNullOrWhiteSpace(driveLetter))
        {
            string trimmed = driveLetter.Trim().TrimEnd('\\', ':');
            if (trimmed.Length == 1 && char.IsAsciiLetter(trimmed[0]))
            {
                return char.ToUpperInvariant(trimmed[0]).ToString();
            }
        }
        return fallback;
    }

    /// <summary>
    /// Checks whether portable mode is enabled via the existence of 'portable.flag' in the app directory.
    /// </summary>
    public static bool IsPortableMode()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return File.Exists(Path.Combine(baseDir, "portable.flag"));
        }
        catch (Exception ex)
        {
            Logger.Debug($"Failed to check portable.flag: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets the target path to user_settings.json:
    /// - If SettingsPathOverride is set: uses the override (test isolation).
    /// - If portable.flag exists: in app directory (portable mode).
    /// - Otherwise: in %APPDATA%\CoreSpectre\user_settings.json.
    /// </summary>
    public static string GetSettingsPath()
    {
        if (!string.IsNullOrWhiteSpace(SettingsPathOverride))
        {
            return SettingsPathOverride;
        }

        if (IsPortableMode())
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "user_settings.json");
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (!Path.IsPathFullyQualified(appData)) throw new IOException("Windows did not provide an AppData path.");
        string coreSpectreDir = Path.Combine(appData, "CoreSpectre");
        return Path.Combine(coreSpectreDir, "user_settings.json");
    }

    public static void EnsureMigration()
    {
        if (!string.IsNullOrWhiteSpace(SettingsPathOverride) || IsPortableMode()) return;

        try
        {
            string localLegacyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "user_settings.json");
            string appDataPath = GetSettingsPath();

            if (!File.Exists(appDataPath) && File.Exists(localLegacyPath))
            {
                string? targetDir = Path.GetDirectoryName(appDataPath);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                File.Copy(localLegacyPath, appDataPath, overwrite: false);
                Logger.Info($"[Settings] Successfully migrated existing settings from '{localLegacyPath}' to '{appDataPath}'");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Settings] Migration of existing settings failed: {ex.Message}");
        }
    }

    private static void SanitizeModel(UserSettingsModel model)
    {
        model.Language = string.Equals(model.Language, "RU", StringComparison.OrdinalIgnoreCase) ? "RU" : "EN";
        model.Theme = string.Equals(model.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        model.TelemetryInterval = model.TelemetryInterval > 0 ? Math.Clamp(model.TelemetryInterval, 0.5, 30.0) : 1.0;
        model.DiskWarningThresholdGb = model.DiskWarningThresholdGb > 0 ? Math.Clamp(model.DiskWarningThresholdGb, 1.0, 500.0) : 7.0;
        model.CpuAlertThreshold = model.CpuAlertThreshold > 0 ? Math.Clamp(model.CpuAlertThreshold, 10.0, 100.0) : 90.0;
        model.RamAlertThreshold = model.RamAlertThreshold > 0 ? Math.Clamp(model.RamAlertThreshold, 10.0, 100.0) : 85.0;
        model.HudOpacity = model.HudOpacity > 0 ? Math.Clamp(model.HudOpacity, 20.0, 100.0) : 90.0;
        model.TempMinAgeDays = Math.Clamp(model.TempMinAgeDays, 0, 90);
        model.WatchedDriveLetter = NormalizeDriveLetter(model.WatchedDriveLetter);
        model.HudDriveLetter = string.IsNullOrWhiteSpace(model.HudDriveLetter) ? "" : NormalizeDriveLetter(model.HudDriveLetter, "");
        model.ActiveTabs ??= new List<string> { "overview", "ram", "storage", "games", "settings" };
        model.VaultTabs ??= new List<string>();
        model.DisabledCleanupTargets ??= new List<string>();
        model.ProcessWhitelist ??= new List<string>();
        model.CustomGameProcesses ??= new List<string>();
        model.ActiveTabs.RemoveAll(string.IsNullOrWhiteSpace);
        model.VaultTabs.RemoveAll(string.IsNullOrWhiteSpace);
        model.DisabledCleanupTargets.RemoveAll(string.IsNullOrWhiteSpace);
        model.ProcessWhitelist.RemoveAll(string.IsNullOrWhiteSpace);
        model.CustomGameProcesses.RemoveAll(string.IsNullOrWhiteSpace);

        if (!model.ActiveTabs.Contains("settings"))
        {
            model.ActiveTabs.Add("settings");
        }
    }

    public static UserSettingsModel Load()
    {
        lock (_fileLock)
        {
            try
            {
                EnsureMigration();
                string path = GetSettingsPath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    var model = JsonSerializer.Deserialize<UserSettingsModel>(json, JsonOptions);
                    if (model != null)
                    {
                        SanitizeModel(model);
                        LastError = null;
                        return model;
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Logger.Error($"Failed to load user settings: {ex.Message}", ex);
                OnSettingsError?.Invoke($"Settings read error: {ex.Message}");
            }

            return new UserSettingsModel();
        }
    }

    public static bool Save(UserSettingsModel model)
    {
        lock (_fileLock)
        {
            try
            {
                SanitizeModel(model);
                string path = GetSettingsPath();
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(model, JsonOptions);
                WriteAtomic(path, json);

                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Logger.Error($"Failed to save user settings: {ex.Message}", ex);
                OnSettingsError?.Invoke($"Settings save error: {ex.Message}");
                return false;
            }
        }
    }

    public static (bool Success, string Message) ExportBackup(string filePath, UserSettingsModel model, bool isEn = false)
    {
        try
        {
            SanitizeModel(model);
            string payloadJson = JsonSerializer.Serialize(model, CanonicalJsonOptions);
            string checksum = ComputeSha256Hex(payloadJson);

            var envelope = new CoreSpectreBackupEnvelope(
                Format: "CoreSpectreBackup",
                Version: 1,
                AppVersion: "1.2.0",
                ExportedAtUtc: DateTime.UtcNow,
                Checksum: checksum,
                Settings: model
            );

            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string outJson = JsonSerializer.Serialize(envelope, JsonOptions);
            WriteAtomic(filePath, outJson);

            return (true, isEn
                ? $"Backup saved to {Path.GetFileName(filePath)}."
                : $"Резервная копия сохранена в {Path.GetFileName(filePath)}.");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to export .csb backup", ex);
            return (false, isEn ? $"Backup export failed: {ex.Message}" : $"Ошибка экспорта .csb: {ex.Message}");
        }
    }

    private static void WriteAtomic(string path, string json)
    {
        string tempPath = path + $".tmp.{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); }
            catch (Exception ex) { Logger.Debug($"Could not remove temporary settings file: {ex.Message}"); }
            throw;
        }
    }

    public static (bool Success, UserSettingsModel? Model, string Message) ImportBackup(string filePath, bool isEn = false)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return (false, null, isEn ? "Backup file not found." : "Файл резервной копии не найден.");
            }

            string raw = File.ReadAllText(filePath, Encoding.UTF8);
            var envelope = JsonSerializer.Deserialize<CoreSpectreBackupEnvelope>(raw, JsonOptions);
            if (envelope == null ||
                !string.Equals(envelope.Format, "CoreSpectreBackup", StringComparison.Ordinal) ||
                envelope.Version < 1 ||
                envelope.Settings == null)
            {
                return (false, null, isEn ? "Invalid .csb backup format." : "Неверный формат файла резервной копии .csb.");
            }

            using var doc = JsonDocument.Parse(raw);
            var settingsProperties = doc.RootElement.EnumerateObject()
                .Where(p => p.Name.Equals("Settings", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (settingsProperties.Length != 1)
                return (false, null, isEn ? "Invalid .csb backup format." : "Неверный формат файла резервной копии .csb.");

            string expectedChecksum = ComputeSha256Hex(JsonSerializer.Serialize(settingsProperties[0].Value, CanonicalJsonOptions));
            if (!string.Equals(expectedChecksum, envelope.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                return (false, null, isEn
                    ? "Backup checksum verification failed (file may be corrupted)."
                    : "Контрольная сумма .csb не совпала (файл повреждён или изменён).");
            }

            SanitizeModel(envelope.Settings);
            return (true, envelope.Settings, isEn
                ? $"Loaded backup from {Path.GetFileName(filePath)}."
                : $"Настройки загружены из {Path.GetFileName(filePath)}.");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to import .csb backup", ex);
            return (false, null, isEn ? $"Backup import failed: {ex.Message}" : $"Ошибка чтения .csb: {ex.Message}");
        }
    }

    private static string ComputeSha256Hex(string text)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>
    /// Registers per-user HKCU file association for .csb backups so double-clicking opens CoreSpectre cleanly without admin rights.
    /// </summary>
    public static void EnsureCsbFileAssociation()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) ||
                !File.Exists(exePath) ||
                !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(exePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string expectedCommand = $"\"{exePath}\" \"%1\"";
            string expectedIcon = $"\"{exePath}\",0";
            bool changed = false;

            using (var extKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.csb", writable: true))
            {
                if (!string.Equals(extKey?.GetValue(null) as string, "CoreSpectre.Backup", StringComparison.Ordinal))
                {
                    extKey?.SetValue(null, "CoreSpectre.Backup", RegistryValueKind.String);
                    changed = true;
                }
            }

            using (var progIdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CoreSpectre.Backup", writable: true))
            {
                if (!string.Equals(progIdKey?.GetValue(null) as string, "CoreSpectre Settings Backup", StringComparison.Ordinal))
                {
                    progIdKey?.SetValue(null, "CoreSpectre Settings Backup", RegistryValueKind.String);
                    changed = true;
                }
            }

            using (var iconKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CoreSpectre.Backup\DefaultIcon", writable: true))
            {
                if (!string.Equals(iconKey?.GetValue(null) as string, expectedIcon, StringComparison.OrdinalIgnoreCase))
                {
                    iconKey?.SetValue(null, expectedIcon, RegistryValueKind.String);
                    changed = true;
                }
            }

            using (var cmdKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CoreSpectre.Backup\shell\open\command", writable: true))
            {
                if (!string.Equals(cmdKey?.GetValue(null) as string, expectedCommand, StringComparison.OrdinalIgnoreCase))
                {
                    cmdKey?.SetValue(null, expectedCommand, RegistryValueKind.String);
                    changed = true;
                }
            }

            if (changed)
            {
                SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
                Logger.Info($"[Settings] Registered HKCU .csb file association -> {exePath}");
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Failed to register .csb association: {ex.Message}");
        }
    }

    public static bool SetRunAtStartup(bool enable)
    {
        try
        {
            const string runKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            using var runKey = enable
                ? Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true)
                : Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
            if (runKey == null) return !enable;

            if (enable)
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;
                using var approved = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", writable: true);
                approved?.DeleteValue("CoreSpectre", throwOnMissingValue: false);
                runKey.SetValue("CoreSpectre", $"\"{exePath}\" --minimized", RegistryValueKind.String);
            }
            else
            {
                runKey.DeleteValue("CoreSpectre", throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"SetRunAtStartup failed: {ex.Message}");
            return false;
        }
    }
}
