using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace CoreSpectre.Services;

public record StartupEntry(
    string Name,
    string Command,
    string Source,
    bool IsEnabled,
    RegistryHive Hive = RegistryHive.CurrentUser,
    string RunSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
    string ApprovedSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
    bool IsScheduledTask = false,
    string TaskFullPath = ""
)
{
    public string EnabledText => LocalizationService.IsEnglish
        ? (IsEnabled ? "On" : "Off")
        : (IsEnabled ? "Вкл" : "Выкл");

    public string RemoveTooltip => LocalizationService.IsEnglish ? "Remove from startup" : "Удалить из автозагрузки";
}

public record StartupOperationResult(bool Success, string Message);

public static class StartupService
{
    private readonly record struct StartupSourceDef(
        string RunSubKey,
        string ApprovedSubKey,
        RegistryHive Hive,
        string Label
    );

    private static readonly StartupSourceDef[] Sources =
    [
        new(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
            RegistryHive.CurrentUser,
            @"HKCU\Run"),
        new(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
            RegistryHive.LocalMachine,
            @"HKLM\Run"),
        new(
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
            RegistryHive.LocalMachine,
            @"HKLM\Run32")
    ];

    private static RegistryKey GetRootKey(RegistryHive hive) =>
        hive == RegistryHive.CurrentUser ? Registry.CurrentUser : Registry.LocalMachine;

    public static List<StartupEntry> GetAllEntries()
    {
        var entries = new List<StartupEntry>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var src in Sources)
        {
            try
            {
                var rootKey = GetRootKey(src.Hive);
                var disabledNames = GetDisabledNamesForScope(rootKey, src.ApprovedSubKey);

                using var key = rootKey.OpenSubKey(src.RunSubKey, writable: false);
                if (key == null) continue;

                foreach (var name in key.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    string dedupeKey = $"{src.Hive}|{src.RunSubKey}|{name}";
                    if (!seenKeys.Add(dedupeKey)) continue;

                    string cmd = key.GetValue(name)?.ToString() ?? "";
                    bool enabled = !disabledNames.Contains(name);
                    entries.Add(new StartupEntry(
                        Name: name,
                        Command: cmd,
                        Source: src.Label,
                        IsEnabled: enabled,
                        Hive: src.Hive,
                        RunSubKey: src.RunSubKey,
                        ApprovedSubKey: src.ApprovedSubKey
                    ));
                }
            }
            catch (Exception ex) { Logger.Debug("StartupService.cs: " + ex.Message); }
        }

        // Also include non-Microsoft Logon/Boot Scheduled Tasks
        try
        {
            entries.AddRange(GetNonMicrosoftScheduledTasks());
        }
        catch (Exception ex) { Logger.Debug($"StartupService tasks: {ex.Message}"); }

        return entries;
    }

    private static List<StartupEntry> GetNonMicrosoftScheduledTasks()
    {
        var list = new List<StartupEntry>();
        try
        {
            string psPath = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            string script = "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); " +
                "Get-ScheduledTask -ErrorAction Stop | Where-Object { $_.TaskPath -notlike '\\Microsoft\\*' -and ($_.Triggers | Where-Object { $_.CimClass.CimClassName -match 'Logon|Boot' }) } | Select-Object TaskName,TaskPath,State,@{N='Exec';E={($_.Actions | Select-Object -First 1).Execute}} | ConvertTo-Csv -NoTypeInformation";
            var psi = new ProcessStartInfo(psPath, $"-NoProfile -NonInteractive -Command \"{script}\"")
            {
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            var lines = new List<string>();
            var res = StorageService.RunProcessWithWatchdogAsync(psi, TimeSpan.FromSeconds(8), lines.Add, resetOnOutput: false)
                .GetAwaiter().GetResult();
            if (res.ExitCode != 0)
            {
                Logger.Warn(res.TimedOut ? "Scheduled task enumeration timed out." : $"Scheduled task enumeration exited with code {res.ExitCode}.");
                return list;
            }

            for (int i = 1; i < lines.Count; i++)
            {
                var cols = SplitCsvLine(lines[i]);
                if (cols.Count < 4) continue;

                string name = cols[0];
                string taskPath = cols[1];
                string state = cols[2];
                string exec = cols[3];
                if (string.IsNullOrWhiteSpace(name)) continue;

                string fullTask = taskPath.TrimEnd('\\') + "\\" + name;
                bool enabled = !string.Equals(state, "Disabled", StringComparison.OrdinalIgnoreCase);

                list.Add(new StartupEntry(
                    Name: name,
                    Command: string.IsNullOrWhiteSpace(exec) ? taskPath : exec,
                    Source: "Task",
                    IsEnabled: enabled,
                    IsScheduledTask: true,
                    TaskFullPath: fullTask
                ));
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"GetNonMicrosoftScheduledTasks failed: {ex.Message}");
        }
        return list;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var res = new List<string>();
        bool inQuotes = false;
        var cur = new System.Text.StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes && c == '"' && i + 1 < line.Length && line[i + 1] == '"')
            {
                cur.Append('"');
                i++;
                continue;
            }
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ',' && !inQuotes) { res.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(c);
        }
        res.Add(cur.ToString());
        return res;
    }

    private static HashSet<string> GetDisabledNamesForScope(RegistryKey root, string approvedSubKey)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = root.OpenSubKey(approvedSubKey, writable: false);
            if (key == null) return set;

            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is byte[] data && data.Length > 0 && data[0] == 0x03)
                {
                    set.Add(name);
                }
            }
        }
        catch (Exception ex) { Logger.Debug("StartupService.cs: " + ex.Message); }
        return set;
    }

    public static StartupOperationResult SetEntryEnabled(StartupEntry entry, bool enable, bool isEn = false)
    {
        if (entry.IsScheduledTask)
        {
            try
            {
                string flag = enable ? "/enable" : "/disable";
                var res = ChangeScheduledTask(entry, "/change", flag);
                if (res.TimedOut) return ScheduledTaskTimedOut(isEn);
                if (res.ExitCode == 0)
                    return new StartupOperationResult(true, isEn ? "Scheduled task updated." : "Задача планировщика обновлена.");
                return new StartupOperationResult(false, isEn ? "Administrator privileges may be required for this task." : "Возможно, требуются права администратора для этой задачи.");
            }
            catch (Exception ex)
            {
                return new StartupOperationResult(false, ex.Message);
            }
        }

        if (entry.Hive == RegistryHive.LocalMachine && !StorageService.IsAdministrator())
        {
            string reason = isEn
                ? "Skipped: administrator privileges required."
                : "Пропущено: нужны права администратора.";
            return new StartupOperationResult(false, reason);
        }

        try
        {
            var root = GetRootKey(entry.Hive);
            using var approvedKey = root.CreateSubKey(entry.ApprovedSubKey, writable: true);
            if (approvedKey == null)
            {
                return new StartupOperationResult(false, isEn ? "Could not open StartupApproved key." : "Не удалось открыть раздел StartupApproved.");
            }

            var existing = approvedKey.GetValue(entry.Name) as byte[];
            byte[] stateBytes = existing != null && existing.Length >= 12 ? (byte[])existing.Clone() : new byte[12];
            stateBytes[0] = enable ? (byte)0x02 : (byte)0x03;

            approvedKey.SetValue(entry.Name, stateBytes, RegistryValueKind.Binary);
            return new StartupOperationResult(true, isEn ? "Startup entry updated." : "Элемент автозагрузки обновлен.");
        }
        catch (Exception ex)
        {
            return new StartupOperationResult(false, ex.Message);
        }
    }

    public static StartupOperationResult DeleteEntry(StartupEntry entry, bool isEn = false)
    {
        if (entry.IsScheduledTask)
        {
            try
            {
                var res = ChangeScheduledTask(entry, "/delete", "/f");
                if (res.TimedOut) return ScheduledTaskTimedOut(isEn);
                if (res.ExitCode == 0)
                    return new StartupOperationResult(true, isEn ? $"Removed task '{entry.Name}'." : $"Задача '{entry.Name}' удалена.");
                return new StartupOperationResult(false, isEn ? "Failed to delete scheduled task (admin required?)." : "Не удалось удалить задачу (возможно, нужны права админа).");
            }
            catch (Exception ex)
            {
                return new StartupOperationResult(false, ex.Message);
            }
        }

        if (entry.Hive == RegistryHive.LocalMachine && !StorageService.IsAdministrator())
        {
            string reason = isEn
                ? "Skipped: administrator privileges required."
                : "Пропущено: нужны права администратора.";
            return new StartupOperationResult(false, reason);
        }

        try
        {
            var root = GetRootKey(entry.Hive);
            using (var runKey = root.OpenSubKey(entry.RunSubKey, writable: true))
            {
                runKey?.DeleteValue(entry.Name, throwOnMissingValue: false);
            }

            using (var approvedKey = root.OpenSubKey(entry.ApprovedSubKey, writable: true))
            {
                approvedKey?.DeleteValue(entry.Name, throwOnMissingValue: false);
            }

            return new StartupOperationResult(true, isEn ? $"Removed '{entry.Name}' from startup." : $"'{entry.Name}' удален из автозапуска.");
        }
        catch (Exception ex)
        {
            return new StartupOperationResult(false, ex.Message);
        }
    }

    private static ProcessWatchdogResult ChangeScheduledTask(StartupEntry entry, string action, string flag)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"));
        psi.ArgumentList.Add(action);
        psi.ArgumentList.Add("/tn");
        psi.ArgumentList.Add(entry.TaskFullPath);
        psi.ArgumentList.Add(flag);
        return StorageService.RunProcessWithWatchdogAsync(psi, TimeSpan.FromSeconds(8), resetOnOutput: false)
            .GetAwaiter().GetResult();
    }

    private static StartupOperationResult ScheduledTaskTimedOut(bool isEn) => new(false, isEn
        ? "Scheduled task change timed out; refresh to check its current state."
        : "Истекло время изменения задачи; обнови список, чтобы проверить её состояние.");
}
