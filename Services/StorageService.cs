using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoreSpectre.Services;

public record DiskInfo(
    string Name,
    string VolumeLabel,
    long TotalBytes,
    long FreeBytes,
    long UsedBytes,
    double FreePercent,
    double UsedPercent
);

public record CleanableTarget(
    string Name,
    string Path,
    long Bytes,
    int FileCount,
    bool RequiresAdmin = false,
    bool IsSelected = true,
    string Key = ""
);

public record CleanResult(
    long BytesFreed,
    int FilesRemoved,
    int FilesSkipped,
    int AdminSkippedStages = 0,
    bool HasErrors = false,
    string SummaryMessage = ""
);

public record StorageCleanupOptions(
    bool CleanTemp = true,
    bool CleanSoftwareDistribution = true,
    bool CleanLogsAndReports = true,
    bool CleanDnsCache = true,
    bool CleanRecycleBin = false,
    bool CleanGpuShaderCache = false,
    bool CleanBrowserCache = false,
    bool CleanDismComponentCleanup = true,
    bool CleanDismResetBase = false,
    HashSet<string>? DisabledTargetKeys = null,
    int TempMinAgeDays = 0
);

public record LargeDirectory(
    string Path,
    long SizeBytes,
    string FormattedSize,
    bool IsBackupNote = false
);

public record TargetFilePreview(
    string Name,
    string FullPath,
    string FormattedSize,
    string LastModified
);

public record ElevatedCleanupJob(
    bool IsEnglish,
    bool CleanTemp,
    bool CleanSoftwareDistribution,
    bool CleanLogsAndReports,
    bool CleanGpuShaderCache,
    bool CleanBrowserCache,
    bool CleanDismComponentCleanup,
    bool CleanDismResetBase,
    List<string> DisabledTargetKeys,
    string Action = "cleanup",
    int TempMinAgeDays = 0
);

public record ElevatedWorkerRunResult(
    bool ElevationCancelled,
    CleanResult? Result,
    string? ErrorMessage
);

internal sealed record ProcessWatchdogResult(
    int ExitCode,
    bool TimedOut,
    bool Cancelled
);

public static partial class StorageService
{
    internal static readonly TimeSpan LongSystemOperationInactivityTimeout = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan ElevatedWorkerInactivityTimeout = TimeSpan.FromMinutes(16);

    internal static async Task<ProcessWatchdogResult> RunProcessWithWatchdogAsync(
        ProcessStartInfo psi,
        TimeSpan inactivityTimeout,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default,
        bool resetOnOutput = true)
    {
        if (cancellationToken.IsCancellationRequested)
            return new ProcessWatchdogResult(-1, TimedOut: false, Cancelled: true);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding outputEncoding = Encoding.UTF8;
        try
        {
            outputEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch { }

        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.WindowStyle = ProcessWindowStyle.Hidden;
        psi.StandardOutputEncoding ??= outputEncoding;
        psi.StandardErrorEncoding ??= outputEncoding;

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process '{psi.FileName}'.");
        using var stdout = proc.StandardOutput;
        using var stderr = proc.StandardError;

        using var watchdogCts = new CancellationTokenSource();
        watchdogCts.CancelAfter(inactivityTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, watchdogCts.Token);

        var outputLock = new object();

        async Task DrainStreamAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync(linkedCts.Token).ConfigureAwait(false) is { } line)
            {
                try
                {
                    if (linkedCts.IsCancellationRequested) break;
                    if (resetOnOutput && !watchdogCts.IsCancellationRequested)
                        watchdogCts.CancelAfter(inactivityTimeout);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                string trimmed = line.Trim();
                if (onOutputLine != null && trimmed.Length > 0)
                {
                    lock (outputLock) onOutputLine(trimmed);
                }
            }
        }

        Task drainTask = Task.WhenAll(DrainStreamAsync(stdout), DrainStreamAsync(stderr));
        bool completed = false;

        try
        {
            await proc.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            await drainTask.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            linkedCts.Token.ThrowIfCancellationRequested();
            completed = true;
            return new ProcessWatchdogResult(proc.ExitCode, TimedOut: false, Cancelled: false);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            bool cancelled = cancellationToken.IsCancellationRequested;
            return new ProcessWatchdogResult(-1, TimedOut: !cancelled, Cancelled: cancelled);
        }
        finally
        {
            if (!completed)
            {
                linkedCts.Cancel();
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch (Exception ex) { Logger.Debug($"Process tree kill failed for {psi.FileName}: {ex.Message}"); }

                try { await drainTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    _ = drainTask.ContinueWith(static t => { _ = t.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
                catch (Exception ex) { Logger.Debug($"Process output drain failed for {psi.FileName}: {ex.Message}"); }
            }
        }
    }
    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI   = 0x00000002;
    private const uint SHERB_NOSOUND        = 0x00000004;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile
    );

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        IntPtr hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint GENERIC_DELETE = 0x00010000;
    private const uint FILE_READ_ATTRIBUTES = 0x00000080;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    private static readonly Regex ValidPipeRegex = new(@"\ACoreSpectre_Elev_[0-9a-fA-F]{32}\z", RegexOptions.Compiled);

    public static bool IsValidPipeName(string? pipeName) =>
        !string.IsNullOrWhiteSpace(pipeName) && ValidPipeRegex.IsMatch(pipeName);

    public static NamedPipeServerStream CreateSecurePipeServer(string pipeName)
    {
        if (!IsValidPipeName(pipeName))
        {
            throw new ArgumentException("Invalid elevated pipe name.", nameof(pipeName));
        }

        var pipeSecurity = new PipeSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User != null)
        {
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                identity.User,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            adminsSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 8192,
            outBufferSize: 8192,
            pipeSecurity);
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static string ValidateDriveLetter(string? driveLetter, string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(driveLetter))
        {
            string trimmed = driveLetter.Trim().TrimEnd('\\', ':');
            if (trimmed.Length == 1 && char.IsAsciiLetter(trimmed[0]))
            {
                return char.ToUpperInvariant(trimmed[0]).ToString();
            }
        }
        return !string.IsNullOrWhiteSpace(fallback) ? fallback : HardwareTelemetry.GetSystemDriveLetter();
    }

    public static bool CanDeleteFile(FileInfo file)
    {
        if (string.Equals(file.Name, "CBS.log", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            IntPtr hFile = CreateFileW(
                file.FullName,
                GENERIC_DELETE,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OPEN_REPARSE_POINT,
                IntPtr.Zero
            );

            if (hFile == IntPtr.Zero || hFile == new IntPtr(-1))
            {
                return false;
            }

            CloseHandle(hFile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFinalPathWithinRoot(string path, string normalizedAllowedRoot, bool isDirectory)
    {
        string? resolved = ResolveFinalPath(path, isDirectory, followLinks: false);
        return resolved != null && (resolved.Equals(normalizedAllowedRoot, StringComparison.OrdinalIgnoreCase) ||
            resolved.StartsWith(normalizedAllowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    internal static string? ResolveDirectoryPath(string path) => Path.IsPathFullyQualified(path)
        ? ResolveFinalPath(path, isDirectory: true, followLinks: true) : null;

    private static string? ResolveFinalPath(string path, bool isDirectory, bool followLinks)
    {
        try
        {
            uint flags = (followLinks ? 0u : FILE_FLAG_OPEN_REPARSE_POINT) | (isDirectory ? FILE_FLAG_BACKUP_SEMANTICS : 0u);
            IntPtr handle = CreateFileW(
                path,
                FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                flags,
                IntPtr.Zero);

            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                return null;

            try
            {
                var sb = new StringBuilder(1024);
                uint len = GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, 0);
                if (len == 0) return null;
                if (len >= sb.Capacity)
                {
                    sb = new StringBuilder((int)len + 2);
                    len = GetFinalPathNameByHandleW(handle, sb, (uint)sb.Capacity, 0);
                    if (len == 0 || len >= sb.Capacity) return null;
                }

                string resolved = sb.ToString();
                if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    resolved = @"\\" + resolved[8..];
                else if (resolved.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                    resolved = resolved[4..];

                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved));
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch
        {
            return null;
        }
    }

    public static DiskInfo? GetDriveInfo(string driveLetter = "C")
    {
        try
        {
            string clean = ValidateDriveLetter(driveLetter, "C");
            var drive = new DriveInfo($"{clean}:\\");
            if (!drive.IsReady) return null;

            long total = drive.TotalSize;
            long free = drive.AvailableFreeSpace;
            long used = Math.Max(0, total - free);
            double freePct = total > 0 ? (double)free / total * 100.0 : 0.0;
            double usedPct = total > 0 ? (double)used / total * 100.0 : 0.0;

            string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? (clean == "C" ? "System" : "Data") : drive.VolumeLabel;

            return new DiskInfo(
                Name: clean,
                VolumeLabel: label,
                TotalBytes: total,
                FreeBytes: free,
                UsedBytes: used,
                FreePercent: freePct,
                UsedPercent: usedPct
            );
        }
        catch
        {
            return null;
        }
    }

    private static bool IsProtectedSelfExtractionPath(string fullPath)
    {
        try
        {
            string dotNetTemp = Path.GetFullPath(Path.Combine(Path.GetTempPath(), ".net")).TrimEnd(Path.DirectorySeparatorChar);
            string normalized = Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar);
            return normalized.Equals(dotNetTemp, StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith(dotNetTemp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> GetGpuShaderCacheDirectories()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string localLow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
        return new[]
        {
            Path.Combine(localAppData, "D3DSCache"),
            Path.Combine(localAppData, "NVIDIA", "DXCache"),
            Path.Combine(localAppData, "NVIDIA", "GLCache"),
            Path.Combine(localLow, "NVIDIA", "PerDriverVersion", "DXCache"),
            Path.Combine(localAppData, "AMD", "DxCache"),
            Path.Combine(localAppData, "AMD", "GLCache")
        };
    }

    private static IEnumerable<string> GetBrowserCacheDirectories()
    {
        var dirs = new List<string>();
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(localAppData)) return dirs;

        string[] chromiumRoots =
        [
            Path.Combine(localAppData, "Google", "Chrome", "User Data"),
            Path.Combine(localAppData, "Microsoft", "Edge", "User Data"),
            Path.Combine(localAppData, "Yandex", "YandexBrowser", "User Data"),
            Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data")
        ];

        foreach (var root in chromiumRoots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var profile in Directory.EnumerateDirectories(root))
                {
                    string name = Path.GetFileName(profile);
                    if (!name.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string c1 = Path.Combine(profile, "Cache", "Cache_Data");
                    string c2 = Path.Combine(profile, "Code Cache");
                    if (Directory.Exists(c1)) dirs.Add(c1);
                    if (Directory.Exists(c2)) dirs.Add(c2);
                }
            }
            catch { }
        }

        string ffProfiles = Path.Combine(localAppData, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(ffProfiles))
        {
            try
            {
                foreach (var prof in Directory.EnumerateDirectories(ffProfiles))
                {
                    string c2 = Path.Combine(prof, "cache2", "entries");
                    if (Directory.Exists(c2)) dirs.Add(c2);
                }
            }
            catch { }
        }

        return dirs;
    }

    public static List<CleanableTarget> ScanTempLocations(bool isEn = true, UserSettingsModel? settings = null)
    {
        var targets = new List<CleanableTarget>();
        bool isAdmin = IsAdministrator();
        settings ??= SettingsPersistenceService.Load();
        var disabled = new HashSet<string>(settings.DisabledCleanupTargets ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        bool IsSelectedFor(string key, bool groupFlag) => groupFlag && !disabled.Contains(key);

        // 1. User Temp (%TEMP%)
        string userTemp = Path.GetTempPath();
        if (TryGetCleanupRoot(userTemp, out _))
        {
            var (bytes, count) = GetDirectorySizeSafe(userTemp, minAgeDays: settings.TempMinAgeDays);
            string name = isEn ? "User Temporary Files (%TEMP%)" : "Пользовательский кэш (%TEMP%)";
            targets.Add(new CleanableTarget(name, userTemp, bytes, count, RequiresAdmin: false, IsSelected: IsSelectedFor("user_temp", settings.CleanTempFiles), Key: "user_temp"));
        }

        // 2. Windows Temp
        string winTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        if (Directory.Exists(winTemp))
        {
            if (isAdmin)
            {
                var (bytes, count) = GetDirectorySizeSafe(winTemp, minAgeDays: settings.TempMinAgeDays);
                string name = isEn ? "Windows System Temp Cache" : "Системный кэш Windows Temp";
                targets.Add(new CleanableTarget(name, winTemp, bytes, count, RequiresAdmin: false, IsSelected: IsSelectedFor("win_temp", settings.CleanTempFiles), Key: "win_temp"));
            }
            else
            {
                var (totalBytes, count) = GetDirectorySizeSafe(winTemp, cleanableOnly: false, minAgeDays: settings.TempMinAgeDays);
                string name = isEn ? "Windows System Temp (Admin Required)" : "Системный кэш Windows Temp (Нужны права админа)";
                targets.Add(new CleanableTarget(name, winTemp, totalBytes, count, RequiresAdmin: true, IsSelected: IsSelectedFor("win_temp", settings.CleanTempFiles), Key: "win_temp"));
            }
        }

        // 3. Windows Update Download Cache
        string swDist = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download");
        if (Directory.Exists(swDist))
        {
            if (isAdmin)
            {
                var (bytes, count) = GetDirectorySizeSafe(swDist, cleanableOnly: true);
                string name = isEn ? "Windows Update Download Cache" : "Кэш обновлений Windows (SoftwareDistribution)";
                targets.Add(new CleanableTarget(name, swDist, bytes, count, RequiresAdmin: false, IsSelected: IsSelectedFor("sw_dist", settings.CleanSoftwareDistribution), Key: "sw_dist"));
            }
            else
            {
                var (totalBytes, count) = GetDirectorySizeSafe(swDist, cleanableOnly: false);
                string name = totalBytes > 0
                    ? (isEn ? "Windows Update Cache (Admin Required)" : "Кэш обновлений Windows (Нужны права админа)")
                    : (isEn ? "Windows Update Download Cache" : "Кэш обновлений Windows (SoftwareDistribution)");
                targets.Add(new CleanableTarget(name, swDist, totalBytes, count, RequiresAdmin: totalBytes > 0, IsSelected: IsSelectedFor("sw_dist", settings.CleanSoftwareDistribution), Key: "sw_dist"));
            }
        }

        // 4. Delivery Optimization Cache
        string delivOpt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "DeliveryOptimization");
        if (Directory.Exists(delivOpt))
        {
            if (isAdmin)
            {
                var (bytes, count) = GetDirectorySizeSafe(delivOpt, cleanableOnly: true);
                string name = isEn ? "Delivery Optimization Peer Cache" : "Кэш доставки обновлений (DeliveryOptimization)";
                targets.Add(new CleanableTarget(name, delivOpt, bytes, count, RequiresAdmin: false, IsSelected: IsSelectedFor("deliv_opt", settings.CleanSoftwareDistribution), Key: "deliv_opt"));
            }
            else
            {
                var (totalBytes, count) = GetDirectorySizeSafe(delivOpt, cleanableOnly: false);
                if (totalBytes > 0)
                {
                    string name = isEn ? "Delivery Optimization (Admin Required)" : "Кэш доставки обновлений (Нужны права админа)";
                    targets.Add(new CleanableTarget(name, delivOpt, totalBytes, count, RequiresAdmin: true, IsSelected: IsSelectedFor("deliv_opt", settings.CleanSoftwareDistribution), Key: "deliv_opt"));
                }
            }
        }

        // 5. Crash Dumps
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string crashDumps = Path.Combine(localAppData, "CrashDumps");
        if (Directory.Exists(crashDumps))
        {
            var (bytes, count) = GetDirectorySizeSafe(crashDumps, cleanableOnly: true);
            targets.Add(new CleanableTarget(isEn ? "System Crash Dumps (CrashDumps)" : "Дампы системных сбоев (CrashDumps)", crashDumps, bytes, count, RequiresAdmin: false, IsSelected: IsSelectedFor("crash_dumps", settings.CleanLogsAndReports), Key: "crash_dumps"));
        }

        // 6. Windows Error Reporting (WER)
        string wer = Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportArchive");
        if (Directory.Exists(wer))
        {
            var (bytes, count) = GetDirectorySizeSafe(wer, cleanableOnly: true);
            targets.Add(new CleanableTarget(isEn ? "Error Reporting Archive (WER)" : "Архив отчетов об ошибках (WER)", wer, bytes, count, RequiresAdmin: false, IsSelected: IsSelectedFor("wer", settings.CleanLogsAndReports), Key: "wer"));
        }

        // 7. System Diagnostic Logs (CBS - live CBS.log excluded)
        string cbsLogs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "CBS");
        if (Directory.Exists(cbsLogs))
        {
            if (isAdmin)
            {
                var (bytes, count) = GetDirectorySizeSafe(cbsLogs, cleanableOnly: true);
                string name = isEn ? "Windows CBS Diagnostic Logs" : "Системные логи Windows (CBS)";
                targets.Add(new CleanableTarget(name, cbsLogs, bytes, count, RequiresAdmin: false, IsSelected: IsSelectedFor("cbs_logs", settings.CleanLogsAndReports), Key: "cbs_logs"));
            }
            else
            {
                var (totalBytes, count) = GetDirectorySizeSafe(cbsLogs, cleanableOnly: false);
                string name = isEn ? "Windows CBS Logs (Admin Required)" : "Системные логи Windows (CBS) (Нужны права админа)";
                targets.Add(new CleanableTarget(name, cbsLogs, totalBytes, count, RequiresAdmin: true, IsSelected: IsSelectedFor("cbs_logs", settings.CleanLogsAndReports), Key: "cbs_logs"));
            }
        }

        // 8. GPU & DirectX Shader Caches
        long gpuBytes = 0;
        int gpuFiles = 0;
        foreach (var dir in GetGpuShaderCacheDirectories())
        {
            var (b, c) = GetDirectorySizeSafe(dir, cleanableOnly: true);
            gpuBytes += b;
            gpuFiles += c;
        }
        targets.Add(new CleanableTarget(
            isEn ? "GPU & DirectX Shader Caches" : "Кэш шейдеров GPU и DirectX",
            "D3DSCache / NVIDIA / AMD",
            gpuBytes,
            gpuFiles,
            RequiresAdmin: false,
            IsSelected: IsSelectedFor("gpu_shader", settings.CleanGpuShaderCache),
            Key: "gpu_shader"));

        // 9. Browser HTTP Caches (Chrome, Edge, Yandex, Brave)
        long browserBytes = 0;
        int browserFiles = 0;
        foreach (var dir in GetBrowserCacheDirectories())
        {
            var (b, c) = GetDirectorySizeSafe(dir, cleanableOnly: true);
            browserBytes += b;
            browserFiles += c;
        }
        targets.Add(new CleanableTarget(
            isEn ? "Browser HTTP Caches (Chrome/Edge/Yandex)" : "Кэш браузеров (Chrome/Edge/Yandex)",
            "User Data\\Default\\Cache",
            browserBytes,
            browserFiles,
            RequiresAdmin: false,
            IsSelected: IsSelectedFor("browser_cache", settings.CleanBrowserCache),
            Key: "browser_cache"));

        // 10. Recycle Bin
        var (binBytes, binCount) = GetRecycleBinSize();
        targets.Add(new CleanableTarget(isEn ? "Windows Recycle Bin (All Drives)" : "Корзина Windows (Все диски)", "$Recycle.Bin", binBytes, binCount, RequiresAdmin: false, IsSelected: IsSelectedFor("recycle_bin", settings.CleanRecycleBin), Key: "recycle_bin"));

        return targets;
    }

    private static (long bytes, int count) GetRecycleBinSize(CancellationToken cancellationToken = default)
    {
        long binBytes = 0;
        int binCount = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                if (!drive.IsReady) continue;
                string rbPath = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                if (Directory.Exists(rbPath))
                {
                    var (b, c) = GetDirectorySizeSafe(rbPath, cancellationToken: cancellationToken);
                    binBytes += b;
                    binCount += c;
                }
            }
            catch (Exception ex) { Logger.Debug("StorageService.cs: " + ex.Message); }
        }
        return (binBytes, binCount);
    }

    public static CleanResult CleanDirectoryContents(string path, CancellationToken cancellationToken = default, int minAgeDays = 0)
    {
        long freed = 0;
        int count = 0;
        int skipped = 0;
        DateTime? cutoffUtc = minAgeDays > 0 ? DateTime.UtcNow.AddDays(-minAgeDays) : null;

        if (cancellationToken.IsCancellationRequested) return new CleanResult(0, 0, 0);
        if (!TryGetCleanupRoot(path, out string allowedRoot))
            return new CleanResult(0, 0, 0, HasErrors: true);
        if (!Directory.Exists(allowedRoot)) return new CleanResult(0, 0, 0);

        if (!IsFinalPathWithinRoot(allowedRoot, allowedRoot, isDirectory: true))
        {
            Logger.Warn($"[StorageService] Skipped cleanup root (reparse/path mismatch): {allowedRoot}");
            return new CleanResult(0, 0, 0, HasErrors: true);
        }

        var dirStack = new Stack<string>();
        var dirsBottomUp = new List<string>();
        dirStack.Push(allowedRoot);

        while (dirStack.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested) break;
            string currentDir = dirStack.Pop();

            try
            {
                var currentInfo = new DirectoryInfo(currentDir);
                if ((currentInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    IsProtectedSelfExtractionPath(currentDir) ||
                    !IsFinalPathWithinRoot(currentDir, allowedRoot, isDirectory: true))
                {
                    continue;
                }

                if (!string.Equals(currentDir, allowedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    dirsBottomUp.Add(currentDir);
                }

                foreach (var file in currentInfo.EnumerateFiles())
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    try
                    {
                        if ((file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                            IsProtectedSelfExtractionPath(file.FullName))
                        {
                            continue;
                        }

                        if (string.Equals(file.Name, "CBS.log", StringComparison.OrdinalIgnoreCase))
                        {
                            skipped++;
                            continue;
                        }

                        if (!IsFinalPathWithinRoot(file.FullName, allowedRoot, isDirectory: false))
                        {
                            skipped++;
                            continue;
                        }

                        if (cutoffUtc.HasValue && file.LastWriteTimeUtc > cutoffUtc.Value)
                        {
                            continue;
                        }

                        if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                        {
                            file.Attributes &= ~FileAttributes.ReadOnly;
                        }

                        long sz = file.Length;
                        file.Delete();
                        freed += sz;
                        count++;
                    }
                    catch
                    {
                        skipped++;
                    }
                }

                foreach (var sub in currentInfo.EnumerateDirectories())
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    try
                    {
                        if ((sub.Attributes & FileAttributes.ReparsePoint) == 0 &&
                            !IsProtectedSelfExtractionPath(sub.FullName) &&
                            IsFinalPathWithinRoot(sub.FullName, allowedRoot, isDirectory: true))
                        {
                            dirStack.Push(sub.FullName);
                        }
                    }
                    catch (Exception ex) { Logger.Debug("StorageService.cs: " + ex.Message); }
                }
            }
            catch (Exception ex) { Logger.Debug("StorageService.cs: " + ex.Message); }
        }

        // Remove emptied subdirectories bottom-up using non-recursive deletion (no junction traversal)
        for (int i = dirsBottomUp.Count - 1; i >= 0; i--)
        {
            if (cancellationToken.IsCancellationRequested) break;
            string subPath = dirsBottomUp[i];
            try
            {
                if (Directory.Exists(subPath) && IsFinalPathWithinRoot(subPath, allowedRoot, isDirectory: true))
                {
                    var di = new DirectoryInfo(subPath);
                    if ((di.Attributes & FileAttributes.ReparsePoint) == 0 &&
                        !di.EnumerateFileSystemInfos().Any())
                    {
                        Directory.Delete(subPath, recursive: false);
                    }
                }
            }
            catch (Exception ex) { Logger.Debug("StorageService.cs: " + ex.Message); }
        }

        return new CleanResult(freed, count, skipped);
    }

    internal static bool TryGetCleanupRoot(string path, out string root)
    {
        root = "";
        if (!Path.IsPathFullyQualified(path)) return false;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return !string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (IOException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    public static async Task<CleanResult> ExecuteDeepCleanupAsync(
        bool isEn,
        Action<string> logCallback,
        StorageCleanupOptions? options = null,
        bool adminStagesOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (options == null)
        {
            var loadedSettings = SettingsPersistenceService.Load();
            options = new StorageCleanupOptions(
                loadedSettings.CleanTempFiles,
                loadedSettings.CleanSoftwareDistribution,
                loadedSettings.CleanLogsAndReports,
                loadedSettings.CleanDnsCache,
                loadedSettings.CleanRecycleBin,
                loadedSettings.CleanGpuShaderCache,
                loadedSettings.CleanBrowserCache,
                loadedSettings.CleanDismComponentCleanup,
                loadedSettings.CleanDismResetBase,
                new HashSet<string>(loadedSettings.DisabledCleanupTargets, StringComparer.OrdinalIgnoreCase),
                loadedSettings.TempMinAgeDays
            );
        }

        var disabled = options.DisabledTargetKeys ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        long totalFreed = 0;
        int totalRemoved = 0;
        int totalSkipped = 0;
        int adminSkippedStages = 0;
        bool hasErrors = false;
        bool isAdmin = IsAdministrator();

        // Stage 0: Restore Point creation
        if (!cancellationToken.IsCancellationRequested)
        {
            if (isAdmin)
            {
                logCallback(isEn
                    ? "[0/7] Creating Windows System Restore Point for safe rollback..."
                    : "[0/7] Создание системной точки восстановления Windows для безопасного отката...");
                var (created, reason) = await CreateRestorePointDetailedAsync("CoreSpectre Before Deep Cleanup", cancellationToken);
                if (created)
                {
                    logCallback(isEn
                        ? "[Restore Point] Successfully created 'CoreSpectre Before Deep Cleanup'."
                        : "[Точка восстановления] Успешно создана: 'CoreSpectre Before Deep Cleanup'.");
                }
                else
                {
                    string detail = string.IsNullOrWhiteSpace(reason)
                        ? (isEn ? "System Protection disabled or 24h checkpoint limit reached" : "защита системы отключена или сработал суточный лимит Windows")
                        : reason;
                    logCallback(isEn
                        ? $"[Restore Point] Skipped ({detail})."
                        : $"[Точка восстановления] Пропущено ({detail}).");
                }
            }
            else
            {
                logCallback(isEn
                    ? "[0/7] System Restore Point: skipped (administrator privileges required)."
                    : "[0/7] Точка восстановления: пропущено (для автоматического создания точки перед очисткой нужны права администратора).");
            }
        }

        // Stage 1: Temp Folders
        bool cleanUserTemp = !adminStagesOnly && options.CleanTemp && !disabled.Contains("user_temp");
        bool cleanWinTemp = options.CleanTemp && !disabled.Contains("win_temp");
        if (!cancellationToken.IsCancellationRequested && (cleanUserTemp || cleanWinTemp))
        {
            if (!adminStagesOnly)
            {
                logCallback(isEn
                    ? "[1/7] Cleaning temporary folders (%TEMP% and Windows Temp)..."
                    : "[1/7] Очистка временных папок (%TEMP% и Windows Temp)...");
            }
            else if (cleanWinTemp)
            {
                logCallback(isEn
                    ? "[1/7] Cleaning protected Windows Temp folder..."
                    : "[1/7] Очистка системной папки Windows Temp...");
            }

            if (cleanUserTemp)
            {
                var r1 = CleanDirectoryContents(Path.GetTempPath(), cancellationToken, options.TempMinAgeDays);
                totalFreed += r1.BytesFreed;
                totalRemoved += r1.FilesRemoved;
                totalSkipped += r1.FilesSkipped;
                hasErrors |= r1.HasErrors;
            }

            if (cleanWinTemp && !cancellationToken.IsCancellationRequested)
            {
                string winTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
                if (isAdmin)
                {
                    var r2 = CleanDirectoryContents(winTemp, cancellationToken, options.TempMinAgeDays);
                    totalFreed += r2.BytesFreed;
                    totalRemoved += r2.FilesRemoved;
                    totalSkipped += r2.FilesSkipped;
                    hasErrors |= r2.HasErrors;
                }
                else
                {
                    logCallback(isEn
                        ? "[Windows Temp] Skipped: administrator privileges required."
                        : "[Windows Temp] Пропущено: нужны права администратора.");
                    adminSkippedStages++;
                }
            }
        }
        else if (!adminStagesOnly && !cancellationToken.IsCancellationRequested)
        {
            logCallback(isEn
                ? "[1/7] Temporary folders: Skipped by user configuration."
                : "[1/7] Временные папки: Пропущено по настройке пользователя.");
        }

        // Stage 2: Windows Update & Delivery Optimization
        bool cleanSwDist = options.CleanSoftwareDistribution && !disabled.Contains("sw_dist");
        bool cleanDelivOpt = options.CleanSoftwareDistribution && !disabled.Contains("deliv_opt");
        if (!cancellationToken.IsCancellationRequested && (cleanSwDist || cleanDelivOpt))
        {
            string swDist = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download");
            string delivOpt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "DeliveryOptimization");

            if (isAdmin)
            {
                logCallback(isEn
                    ? "[2/7] Cleaning Windows Update and Delivery Optimization caches..."
                    : "[2/7] Очистка кэша обновлений Windows и Delivery Optimization...");

                if (cleanSwDist && !cancellationToken.IsCancellationRequested)
                {
                    var r3 = await CleanStoppedServiceCacheAsync("wuauserv", swDist, logCallback, isEn, cancellationToken);
                    totalFreed += r3.BytesFreed;
                    totalRemoved += r3.FilesRemoved;
                    totalSkipped += r3.FilesSkipped;
                    hasErrors |= r3.HasErrors;
                }
                if (cleanDelivOpt && !cancellationToken.IsCancellationRequested)
                {
                    var r4 = await CleanStoppedServiceCacheAsync("dosvc", delivOpt, logCallback, isEn, cancellationToken);
                    totalFreed += r4.BytesFreed;
                    totalRemoved += r4.FilesRemoved;
                    totalSkipped += r4.FilesSkipped;
                    hasErrors |= r4.HasErrors;
                }
            }
            else
            {
                logCallback(isEn
                    ? "[SoftwareDistribution] Skipped: administrator privileges required."
                    : "[SoftwareDistribution] Пропущено: нужны права администратора.");
                adminSkippedStages++;
            }
        }
        else if (!adminStagesOnly && !cancellationToken.IsCancellationRequested)
        {
            logCallback(isEn
                ? "[2/7] Windows Update cache: Skipped by user configuration."
                : "[2/7] Кэш обновлений Windows: Пропущено по настройке пользователя.");
        }

        // Stage 3: Error Reports and Diagnostic Logs (excluding active CBS.log)
        bool cleanCrashDumps = !adminStagesOnly && options.CleanLogsAndReports && !disabled.Contains("crash_dumps");
        bool cleanWer = options.CleanLogsAndReports && !disabled.Contains("wer");
        bool cleanCbs = options.CleanLogsAndReports && !disabled.Contains("cbs_logs");
        if (!cancellationToken.IsCancellationRequested && (cleanCrashDumps || cleanWer || cleanCbs))
        {
            logCallback(isEn
                ? "[3/7] Clearing error reports and system diagnostic logs..."
                : "[3/7] Очистка отчетов об ошибках и логов...");

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            if (cleanCrashDumps)
            {
                var r5 = CleanDirectoryContents(Path.Combine(localAppData, "CrashDumps"), cancellationToken);
                totalFreed += r5.BytesFreed;
                totalRemoved += r5.FilesRemoved;
                totalSkipped += r5.FilesSkipped;
                hasErrors |= r5.HasErrors;
            }

            if (cleanWer && !cancellationToken.IsCancellationRequested)
            {
                if (!adminStagesOnly)
                {
                    var r6 = CleanDirectoryContents(Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportArchive"), cancellationToken);
                    var r7 = CleanDirectoryContents(Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportQueue"), cancellationToken);
                    totalFreed += r6.BytesFreed + r7.BytesFreed;
                    totalRemoved += r6.FilesRemoved + r7.FilesRemoved;
                    totalSkipped += r6.FilesSkipped + r7.FilesSkipped;
                    hasErrors |= r6.HasErrors || r7.HasErrors;
                }

                if (isAdmin)
                {
                    var r8 = CleanDirectoryContents(Path.Combine(progData, "Microsoft", "Windows", "WER", "ReportArchive"), cancellationToken);
                    var r9 = CleanDirectoryContents(Path.Combine(progData, "Microsoft", "Windows", "WER", "ReportQueue"), cancellationToken);
                    totalFreed += r8.BytesFreed + r9.BytesFreed;
                    totalRemoved += r8.FilesRemoved + r9.FilesRemoved;
                    totalSkipped += r8.FilesSkipped + r9.FilesSkipped;
                    hasErrors |= r8.HasErrors || r9.HasErrors;
                }
                else
                {
                    logCallback(isEn
                        ? "[System WER Archive] Skipped: administrator privileges required."
                        : "[Системные отчёты WER] Пропущено: нужны права администратора.");
                    adminSkippedStages++;
                }
            }

            if (cleanCbs && !cancellationToken.IsCancellationRequested)
            {
                string cbsLogs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "CBS");
                if (isAdmin)
                {
                    var r10 = CleanDirectoryContents(cbsLogs, cancellationToken);
                    totalFreed += r10.BytesFreed;
                    totalRemoved += r10.FilesRemoved;
                    totalSkipped += r10.FilesSkipped;
                    hasErrors |= r10.HasErrors;
                }
                else
                {
                    logCallback(isEn
                        ? "[CBS Logs] Skipped: administrator privileges required."
                        : "[CBS Logs] Пропущено: нужны права администратора.");
                    adminSkippedStages++;
                }
            }
        }
        else if (!adminStagesOnly && !cancellationToken.IsCancellationRequested)
        {
            logCallback(isEn
                ? "[3/7] Error reports and CBS logs: Skipped by user configuration."
                : "[3/7] Отчеты об ошибках и логи CBS: Пропущено по настройке пользователя.");
        }

        // Stage 4: GPU Shader & Browser HTTP Caches
        if (!adminStagesOnly && !cancellationToken.IsCancellationRequested)
        {
            bool cleanGpu = options.CleanGpuShaderCache && !disabled.Contains("gpu_shader");
            bool cleanBrowser = options.CleanBrowserCache && !disabled.Contains("browser_cache");
            if (cleanGpu || cleanBrowser)
            {
                logCallback(isEn
                    ? "[4/7] Cleaning GPU shader and browser caches..."
                    : "[4/7] Очистка кэша шейдеров GPU и браузеров...");

                if (cleanGpu)
                {
                    foreach (var dir in GetGpuShaderCacheDirectories())
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        var r = CleanDirectoryContents(dir, cancellationToken);
                        totalFreed += r.BytesFreed;
                        totalRemoved += r.FilesRemoved;
                        totalSkipped += r.FilesSkipped;
                        hasErrors |= r.HasErrors;
                    }
                }

                if (cleanBrowser)
                {
                    foreach (var dir in GetBrowserCacheDirectories())
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        var r = CleanDirectoryContents(dir, cancellationToken);
                        totalFreed += r.BytesFreed;
                        totalRemoved += r.FilesRemoved;
                        totalSkipped += r.FilesSkipped;
                        hasErrors |= r.HasErrors;
                    }
                }
            }
            else
            {
                logCallback(isEn
                    ? "[4/7] GPU & browser caches: Skipped by user configuration."
                    : "[4/7] Кэш GPU и браузеров: Пропущено по настройке пользователя.");
            }
        }

        // Stage 5: DNS Cache Flush
        if (!adminStagesOnly && !cancellationToken.IsCancellationRequested)
        {
            if (options.CleanDnsCache && !disabled.Contains("dns_cache"))
            {
                logCallback(isEn
                    ? "[5/7] Flushing network DNS resolver cache..."
                    : "[5/7] Сброс сетевого DNS-кэша...");

                try
                {
                    string ipconfigPath = Path.Combine(Environment.SystemDirectory, "ipconfig.exe");
                    var res = await RunProcessWithWatchdogAsync(new ProcessStartInfo(ipconfigPath, "/flushdns"),
                        TimeSpan.FromSeconds(15), logCallback, cancellationToken, resetOnOutput: false);
                    hasErrors |= !res.Cancelled && res.ExitCode != 0;
                }
                catch (Exception ex) { hasErrors = true; Logger.Debug("StorageService.cs: " + ex.Message); }
            }
            else
            {
                logCallback(isEn
                    ? "[5/7] DNS resolver cache: Skipped by user configuration."
                    : "[5/7] Сетевой DNS-кэш: Пропущено по настройке пользователя.");
            }
        }

        // Stage 6: Empty Recycle Bin via native Shell32 API
        if (!adminStagesOnly && !cancellationToken.IsCancellationRequested)
        {
            if (options.CleanRecycleBin && !disabled.Contains("recycle_bin"))
            {
                logCallback(isEn
                    ? "[6/7] Emptying Windows Recycle Bin across all drives..."
                    : "[6/7] Очистка Корзины на всех накопителях...");

                var (binBytesBefore, binCountBefore) = GetRecycleBinSize(cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        uint hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                        if (hr != 0 && hr != 0x8000FFFFu)
                            throw new COMException($"SHEmptyRecycleBin returned 0x{hr:X8}", unchecked((int)hr));
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"SHEmptyRecycleBin fallback triggered: {ex.Message}");
                        try
                        {
                            string psPath = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                            var psi = new ProcessStartInfo(psPath, "-NoProfile -NonInteractive -Command \"Clear-RecycleBin -Confirm:$false -ErrorAction Stop\"");
                            var res = await RunProcessWithWatchdogAsync(psi, TimeSpan.FromSeconds(60), logCallback,
                                cancellationToken, resetOnOutput: false);
                            hasErrors |= !res.Cancelled && res.ExitCode != 0;
                        }
                        catch (Exception psEx) { hasErrors = true; Logger.Debug("StorageService.cs: " + psEx.Message); }
                    }
                }
                if (!cancellationToken.IsCancellationRequested)
                {
                    var (binBytesAfter, binCountAfter) = GetRecycleBinSize(cancellationToken);
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        totalFreed += Math.Max(0, binBytesBefore - binBytesAfter);
                        totalRemoved += Math.Max(0, binCountBefore - binCountAfter);
                    }
                }
            }
            else
            {
                logCallback(isEn
                    ? "[6/7] Recycle Bin: Skipped by user configuration (default: off)."
                    : "[6/7] Корзина: Пропущено по настройке пользователя (по умолчанию выкл).");
            }
        }

        // Stage 7: Windows Component Store Cleanup (DISM)
        if (!cancellationToken.IsCancellationRequested && options.CleanDismComponentCleanup && !disabled.Contains("dism_cleanup"))
        {
            bool useResetBase = options.CleanDismResetBase && !disabled.Contains("dism_resetbase");
            string dismArgs = useResetBase
                ? "/Online /Cleanup-Image /StartComponentCleanup /ResetBase"
                : "/Online /Cleanup-Image /StartComponentCleanup";

            logCallback(isEn
                ? $"[7/7] Cleaning Windows Component Store (DISM{(useResetBase ? " with /ResetBase" : "")})..."
                : $"[7/7] Очистка хранилища компонентов Windows (DISM{(useResetBase ? " с /ResetBase" : "")})...");

            if (isAdmin)
            {
                try
                {
                    string dismPath = Path.Combine(Environment.SystemDirectory, "dism.exe");
                    var psi = new ProcessStartInfo(dismPath, dismArgs);

                    DateTime lastProgressLogUtc = DateTime.MinValue;
                    string lastLoggedLine = string.Empty;
                    var dismResult = await RunProcessWithWatchdogAsync(
                        psi,
                        LongSystemOperationInactivityTimeout,
                        line =>
                        {
                            bool isProgressBar = line.Contains('%') && (line.StartsWith('[') || line.Contains("=="));
                            DateTime now = DateTime.UtcNow;
                            if (isProgressBar)
                            {
                                if (line == lastLoggedLine || ((now - lastProgressLogUtc).TotalSeconds < 3 && !line.Contains("100.0%")))
                                    return;
                                lastProgressLogUtc = now;
                            }
                            lastLoggedLine = line;
                            logCallback($"[DISM] {line}");
                        },
                        cancellationToken);

                    if (dismResult.Cancelled)
                    {
                        logCallback(isEn ? "[DISM] Cancelled by user." : "[DISM] Отменено пользователем.");
                    }
                    else if (dismResult.TimedOut)
                    {
                        hasErrors = true;
                        logCallback(isEn
                            ? "[DISM] Operation timed out (no progress for 15 minutes)."
                            : "[DISM] Операция прервана по тайм-ауту (нет отклика более 15 минут).");
                    }
                    else if (dismResult.ExitCode != 0)
                    {
                        hasErrors = true;
                        logCallback(isEn
                            ? $"[DISM] Failed with ExitCode {dismResult.ExitCode}."
                            : $"[DISM] Завершился с ошибкой (код выхода: {dismResult.ExitCode}).");
                    }
                    else
                    {
                        logCallback(isEn
                            ? "[DISM] Component cleanup completed successfully."
                            : "[DISM] Очистка хранилища компонентов успешно завершена.");
                    }
                }
                catch (Exception ex)
                {
                    hasErrors = true;
                    logCallback(isEn
                        ? $"[DISM] Execution error: {ex.Message}"
                        : $"[DISM] Ошибка запуска DISM: {ex.Message}");
                }
            }
            else
            {
                logCallback(isEn
                    ? "[DISM] Skipped: administrator privileges required."
                    : "[DISM] Пропущено: нужны права администратора.");
                adminSkippedStages++;
            }
        }
        else if (!adminStagesOnly && !cancellationToken.IsCancellationRequested)
        {
            logCallback(isEn
                ? "[7/7] DISM Component Cleanup: Skipped by user configuration."
                : "[7/7] Очистка компонентов DISM: Пропущено по настройке пользователя.");
        }

        string summaryMessage;
        if (cancellationToken.IsCancellationRequested)
        {
            summaryMessage = isEn
                ? $"Cleanup cancelled. Freed before stop: {FormatBytes(totalFreed)}, removed {totalRemoved} files."
                : $"Очистка прервана пользователем. Успело освободиться: {FormatBytes(totalFreed)}, удалено файлов: {totalRemoved}.";
        }
        else if (adminSkippedStages > 0 || totalSkipped > 0 || hasErrors)
        {
            summaryMessage = isEn
                ? $"Cleanup completed with limitations. Freed: {FormatBytes(totalFreed)}, removed {totalRemoved} files, locked/in-use: {totalSkipped}, skipped due to permissions: {adminSkippedStages} step(s)."
                : $"Очистка выполнена с ограничениями. Освобождено: {FormatBytes(totalFreed)}, удалено файлов: {totalRemoved}, занято/заблокировано: {totalSkipped}, пропущено по правам: {adminSkippedStages} шагов.";
        }
        else
        {
            summaryMessage = isEn
                ? $"Cleanup completed successfully. Freed: {FormatBytes(totalFreed)}, removed {totalRemoved} files."
                : $"Очистка успешно завершена. Освобождено: {FormatBytes(totalFreed)}, удалено файлов: {totalRemoved}.";
        }

        logCallback(summaryMessage);

        return new CleanResult(totalFreed, totalRemoved, totalSkipped, adminSkippedStages, hasErrors, summaryMessage);
    }

    public static async Task<ElevatedWorkerRunResult> ExecuteViaElevatedWorkerAsync(
        ElevatedCleanupJob job,
        Action<string> logCallback,
        CancellationToken cancellationToken = default)
    {
        string pipeName = $"CoreSpectre_Elev_{Guid.NewGuid():N}";
        bool isEn = job.IsEnglish;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var pipeServer = CreateSecurePipeServer(pipeName);

            string exePath = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                exePath = Path.Combine(AppContext.BaseDirectory, "CoreSpectre.exe");
            }

            var psi = new ProcessStartInfo(exePath)
            {
                Arguments = $"--elevated-worker {pipeName}",
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = await Task.Run(() => Process.Start(psi), cancellationToken);
            if (proc == null)
            {
                string msg = isEn ? "Could not start elevated worker process." : "Не удалось запустить фоновый процесс с правами администратора.";
                return new ElevatedWorkerRunResult(false, null, msg);
            }

            using var workerWatchdogCts = new CancellationTokenSource(ElevatedWorkerInactivityTimeout);
            using var execCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, workerWatchdogCts.Token);
            try
            {
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(execCts.Token))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(25));
                    Task exitMonitor = proc.WaitForExitAsync(connectCts.Token);
                    Task connectTask = pipeServer.WaitForConnectionAsync(connectCts.Token);
                    try
                    {
                        Task first = await Task.WhenAny(connectTask, exitMonitor);
                        await first;
                        if (!connectTask.IsCompletedSuccessfully)
                            return new ElevatedWorkerRunResult(false, null, isEn
                                ? "Elevated worker exited before connecting."
                                : "Повышенный процесс завершился до подключения.");
                    }
                    finally
                    {
                        connectCts.Cancel();
                        await Task.WhenAll(connectTask, exitMonitor).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                    }
                }

                await pipeServer.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(job) + "\n"), execCts.Token);
                using var reader = new StreamReader(pipeServer, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                while (await reader.ReadLineAsync(execCts.Token) is { } frame)
                {
                    workerWatchdogCts.CancelAfter(ElevatedWorkerInactivityTimeout);

                    if (frame.StartsWith("LOG:", StringComparison.Ordinal))
                    {
                        string logLine = Encoding.UTF8.GetString(Convert.FromBase64String(frame[4..]));
                        logCallback(logLine);
                    }
                    else if (frame.StartsWith("RES:", StringComparison.Ordinal))
                    {
                        string resJson = Encoding.UTF8.GetString(Convert.FromBase64String(frame[4..]));
                        if (JsonSerializer.Deserialize<CleanResult>(resJson) is { } result)
                            return new ElevatedWorkerRunResult(false, result, null);
                    }
                }
                return new ElevatedWorkerRunResult(false, null, isEn
                    ? "Elevated worker disconnected without reporting a result."
                    : "Повышенный процесс отключился, не передав результат.");
            }
            catch (OperationCanceledException)
            {
                // EOF cancels the job, allowing the worker to restore any stopped service.
                pipeServer.Dispose();
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                try { await proc.WaitForExitAsync(stopCts.Token); }
                catch (OperationCanceledException) when (stopCts.IsCancellationRequested)
                {
                    return new ElevatedWorkerRunResult(false, null, isEn
                        ? "Stop requested; elevated worker exit has not been confirmed."
                        : "Отмена запрошена; завершение повышенного процесса не подтверждено.");
                }
                return new ElevatedWorkerRunResult(false, null, cancellationToken.IsCancellationRequested
                    ? (isEn ? "Elevated operation cancelled by user." : "Операция с правами администратора отменена пользователем.")
                    : (isEn ? "Elevated worker timed out." : "Превышено время ожидания операции администратора."));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ElevatedWorkerRunResult(false, null, isEn ? "Elevated operation cancelled." : "Операция с правами администратора отменена.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ElevatedWorkerRunResult(true, null, isEn ? "Administrator elevation cancelled." : "Предоставление прав администратора отменено.");
        }
        catch (Exception ex)
        {
            Logger.Error("Elevated worker failed", ex);
            return new ElevatedWorkerRunResult(false, null, ex.Message);
        }
    }

    public static async Task<int> RunElevatedWorkerAsync(string pipeName)
    {
        if (!IsValidPipeName(pipeName)) return 2;

        using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await pipeClient.ConnectAsync(connectCts.Token);

        using var reader = new StreamReader(pipeClient, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        using var jobCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string? jobJson = await reader.ReadLineAsync(jobCts.Token);
        if (string.IsNullOrWhiteSpace(jobJson)) return 3;

        var job = JsonSerializer.Deserialize<ElevatedCleanupJob>(jobJson);
        if (job == null || job.TempMinAgeDays is < 0 or > 90) return 4;
        string action = (job.Action ?? "").Trim().ToLowerInvariant();
        if (action is not ("cleanup" or "standby" or "compact_on" or "compact_off")) return 4;

        using var workCts = new CancellationTokenSource();
        using var monitorCts = new CancellationTokenSource();
        async Task MonitorDisconnectionAsync()
        {
            try { await reader.ReadAsync(new char[1].AsMemory(), monitorCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (monitorCts.IsCancellationRequested) { return; }
            catch (IOException) { }
            workCts.Cancel();
        }
        Task monitor = MonitorDisconnectionAsync();

        var pipeLock = new object();
        void SendFrame(string prefix, string payload)
        {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
            lock (pipeLock)
            {
                if (workCts.IsCancellationRequested) return;
                using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(workCts.Token);
                writeCts.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    byte[] frame = Encoding.UTF8.GetBytes($"{prefix}:{encoded}\n");
                    pipeClient.WriteAsync(frame.AsMemory(), writeCts.Token).AsTask().GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { workCts.Cancel(); }
            }
        }

        try
        {
            if (workCts.IsCancellationRequested) return 1;
            CleanResult result;
            if (action == "standby")
            {
                var (ok, freed, msg) = MemoryService.PurgeStandbyList(job.IsEnglish);
                SendFrame("LOG", msg);
                result = new CleanResult(freed, 0, 0, 0, HasErrors: !ok, SummaryMessage: msg);
            }
            else if (action is "compact_on" or "compact_off")
            {
                bool enable = action == "compact_on";
                var (ok, msg) = await SetCompactOsAsync(enable, job.IsEnglish,
                    line => SendFrame("LOG", $"[CompactOS] {line}"), workCts.Token);
                SendFrame("LOG", $"[CompactOS] {msg}");
                result = new CleanResult(0, 0, 0, 0, HasErrors: !ok, SummaryMessage: msg);
            }
            else
            {
                var opts = new StorageCleanupOptions(
                    job.CleanTemp,
                    job.CleanSoftwareDistribution,
                    job.CleanLogsAndReports,
                    CleanDnsCache: false,
                    CleanRecycleBin: false,
                    CleanGpuShaderCache: false,
                    CleanBrowserCache: false,
                    job.CleanDismComponentCleanup,
                    job.CleanDismResetBase,
                    new HashSet<string>(job.DisabledTargetKeys ?? new List<string>(), StringComparer.OrdinalIgnoreCase),
                    TempMinAgeDays: job.TempMinAgeDays
                );

                result = await ExecuteDeepCleanupAsync(job.IsEnglish, line => SendFrame("LOG", line), opts,
                    adminStagesOnly: true, cancellationToken: workCts.Token);
            }

            SendFrame("RES", JsonSerializer.Serialize(result));
            return workCts.IsCancellationRequested ? 1 : 0;
        }
        finally
        {
            monitorCts.Cancel();
            await monitor;
        }
    }

    public static async Task<(bool Created, string? Reason)> CreateRestorePointDetailedAsync(string description, CancellationToken cancellationToken = default)
    {
        if (!IsAdministrator()) return (false, "Admin required");

        try
        {
            string safeDesc = new string((description ?? "CoreSpectre Restore Point")
                .Where(c => !char.IsControl(c))
                .ToArray());
            if (string.IsNullOrWhiteSpace(safeDesc)) safeDesc = "CoreSpectre Before Deep Cleanup";

            string descData = Convert.ToBase64String(Encoding.Unicode.GetBytes(safeDesc));
            string psScript = $"$description = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{descData}')); " +
                "Checkpoint-Computer -Description $description -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop";
            string encodedCmd = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));

            string psPath = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            var psi = new ProcessStartInfo(psPath, $"-NoProfile -NonInteractive -EncodedCommand {encodedCmd}");
            string? firstLine = null;
            var res = await RunProcessWithWatchdogAsync(psi, TimeSpan.FromSeconds(30),
                line => firstLine ??= line, cancellationToken, resetOnOutput: false);
            if (res.Cancelled) return (false, "Cancelled");
            if (res.TimedOut) return (false, "Restore point creation was not confirmed within 30 seconds");
            return res.ExitCode == 0 ? (true, null) : (false, firstLine ?? $"ExitCode {res.ExitCode}");
        }
        catch (Exception ex) { Logger.Debug("StorageService.cs: " + ex.Message); return (false, ex.Message); }
    }

    public static bool LaunchSystemRestore()
    {
        try
        {
            string rstruiPath = Path.Combine(Environment.SystemDirectory, "rstrui.exe");
            if (File.Exists(rstruiPath))
            {
                using var process = Process.Start(new ProcessStartInfo(rstruiPath) { UseShellExecute = true });
                return true;
            }
        }
        catch (Exception ex) { Logger.Debug("StorageService.cs: " + ex.Message); }
        return false;
    }

    public static List<LargeDirectory> ScanLargeDirectories(string? driveLetter = null, int maxResults = 8, CancellationToken cancellationToken = default)
    {
        var results = new List<LargeDirectory>();
        string drive = ValidateDriveLetter(driveLetter);
        string root = $"{drive}:\\";

        try
        {
            var rootDir = new DirectoryInfo(root);
            foreach (var dir in rootDir.EnumerateDirectories())
            {
                if (cancellationToken.IsCancellationRequested) break;
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((dir.Attributes & FileAttributes.System) != 0 && dir.Name.StartsWith("$", StringComparison.Ordinal))
                    continue;

                try
                {
                    long size = GetTopLevelSize(dir, cancellationToken);
                    if (size > 50L * 1024 * 1024)
                    {
                        results.Add(new LargeDirectory(dir.FullName, size, FormatBytes(size)));
                    }
                }
                catch
                {
                    // Skip inaccessible folders
                }
            }
        }
        catch
        {
            // Root enumeration failed
        }

        return results.OrderByDescending(x => x.SizeBytes).Take(maxResults).ToList();
    }

    private static (long totalBytes, int fileCount) GetDirectorySizeSafe(string path, bool cleanableOnly = true,
        int minAgeDays = 0, CancellationToken cancellationToken = default)
    {
        long total = 0;
        int count = 0;

        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return (0, 0);
        DateTime cutoffUtc = minAgeDays > 0 ? DateTime.UtcNow.AddDays(-minAgeDays) : DateTime.MaxValue;

        try
        {
            var enumOpts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            var dir = new DirectoryInfo(path);
            foreach (var file in dir.EnumerateFiles("*", enumOpts))
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    if (IsProtectedSelfExtractionPath(file.FullName) || file.LastWriteTimeUtc > cutoffUtc)
                        continue;

                    if (cleanableOnly && (file.Name.Equals("CBS.log", StringComparison.OrdinalIgnoreCase) || !CanDeleteFile(file)))
                        continue;

                    total += file.Length;
                    count++;
                }
                catch
                {
                    // Skip inaccessible individual file
                }
            }
        }
        catch
        {
            // Directory unreadable
        }

        return (total, count);
    }

    private static long GetTopLevelSize(DirectoryInfo dir, CancellationToken cancellationToken, int maxFiles = 200_000)
    {
        long total = 0;
        int inspected = 0;

        try
        {
            var enumOpts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var file in dir.EnumerateFiles("*", enumOpts))
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    total += file.Length;
                    inspected++;
                    if (inspected >= maxFiles) break;
                }
                catch
                {
                    // Skip locked
                }
            }
        }
        catch
        {
            // Subfolder access denied
        }

        return total;
    }

    public static List<LargeDirectory> ScanLargestFiles(string? driveLetter = null, long minBytes = 100L * 1024 * 1024,
        int maxResults = 15, CancellationToken cancellationToken = default)
    {
        var list = new List<LargeDirectory>();
        string drive = ValidateDriveLetter(driveLetter);
        string root = $"{drive}:\\";

        try
        {
            var enumOpts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", enumOpts))
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    if (f.Length >= minBytes)
                    {
                        list.Add(new LargeDirectory(f.FullName, f.Length, FormatBytes(f.Length)));
                    }
                }
                catch { }
            }
        }
        catch { }

        return list.OrderByDescending(x => x.SizeBytes).Take(maxResults).ToList();
    }

    public static List<LargeDirectory> FindBackupAndArchiveAudits(CancellationToken cancellationToken = default)
    {
        var audits = new List<LargeDirectory>();
        var backupDirs = new[]
        {
            @"D:\.minecraft\backup",
            @"D:\.minecraft\saves_backups",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "backup"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "saves_backups")
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in backupDirs)
        {
            if (cancellationToken.IsCancellationRequested) return audits;
            if (!Path.IsPathFullyQualified(dir) || !Directory.Exists(dir)) continue;
            try
            {
                var di = new DirectoryInfo(dir);
                string resolved = (di.Attributes & FileAttributes.ReparsePoint) != 0
                    ? di.ResolveLinkTarget(true)?.FullName ?? dir
                    : dir;
                if (!seen.Add(resolved)) continue;
                long sz = GetTopLevelSize(new DirectoryInfo(resolved), cancellationToken);
                if (sz > 10L * 1024 * 1024)
                    audits.Add(new LargeDirectory(resolved, sz, FormatBytes(sz), IsBackupNote: true));
            }
            catch { }
        }

        foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Fixed))
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                foreach (var zip in d.RootDirectory.EnumerateFiles("*.zip"))
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    if (zip.Length >= 50L * 1024 * 1024)
                    {
                        audits.Add(new LargeDirectory(zip.FullName, zip.Length, FormatBytes(zip.Length), IsBackupNote: true));
                    }
                }
            }
            catch { }
        }

        return audits.OrderByDescending(x => x.SizeBytes).ToList();
    }

    public static List<TargetFilePreview> PreviewTargetFiles(string key, int maxItems = 25)
    {
        var dirs = new List<string>();
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        switch (key)
        {
            case "user_temp": dirs.Add(Path.GetTempPath()); break;
            case "win_temp": dirs.Add(Path.Combine(winDir, "Temp")); break;
            case "sw_dist": dirs.Add(Path.Combine(winDir, "SoftwareDistribution", "Download")); break;
            case "deliv_opt": dirs.Add(Path.Combine(winDir, "SoftwareDistribution", "DeliveryOptimization")); break;
            case "crash_dumps": dirs.Add(Path.Combine(localAppData, "CrashDumps")); break;
            case "wer": dirs.Add(Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportArchive")); break;
            case "cbs_logs": dirs.Add(Path.Combine(winDir, "Logs", "CBS")); break;
            case "gpu_shader": dirs.AddRange(GetGpuShaderCacheDirectories()); break;
            case "browser_cache": dirs.AddRange(GetBrowserCacheDirectories()); break;
        }

        var files = new List<FileInfo>();
        var enumOpts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var d in dirs)
        {
            if (!Path.IsPathFullyQualified(d) || !Directory.Exists(d)) continue;
            try
            {
                foreach (var f in new DirectoryInfo(d).EnumerateFiles("*", enumOpts))
                {
                    if (!IsProtectedSelfExtractionPath(f.FullName) && !string.Equals(f.Name, "CBS.log", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(f);
                    }
                }
            }
            catch { }
        }

        return files
            .OrderByDescending(f => f.Length)
            .Take(maxItems)
            .Select(f => new TargetFilePreview(
                f.Name,
                f.FullName,
                FormatBytes(f.Length),
                f.LastWriteTime.ToString("dd.MM.yyyy HH:mm")))
            .ToList();
    }

    public static async Task<bool?> IsCompactOsEnabledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string compactExe = Path.Combine(Environment.SystemDirectory, "compact.exe");
            var output = new StringBuilder();
            var result = await RunProcessWithWatchdogAsync(new ProcessStartInfo(compactExe, "/CompactOS:query"),
                TimeSpan.FromSeconds(5), line => output.AppendLine(line), cancellationToken, resetOnOutput: false)
                .ConfigureAwait(false);
            return result is { TimedOut: false, Cancelled: false }
                ? ParseCompactOsState(output.ToString())
                : null;
        }
        catch (Exception ex)
        {
            Logger.Debug($"IsCompactOsEnabledAsync failed: {ex.Message}");
            return null;
        }
    }

    internal static bool? ParseCompactOsState(string output)
    {
        if (output.Contains("not in the Compact", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("не находится в сжатом", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("не находится в компактном", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("не сжатом", StringComparison.OrdinalIgnoreCase))
            return false;

        if (output.Contains("in the Compact state", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("находится в сжатом состоянии", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("находится в компактном состоянии", StringComparison.OrdinalIgnoreCase))
            return true;

        return null;
    }

    public static async Task<(bool Success, string Message)> SetCompactOsAsync(
        bool enable,
        bool isEn = false,
        Action<string>? logCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAdministrator())
        {
            return (false, isEn ? "Administrator privileges required for CompactOS." : "Для настройки CompactOS требуются права администратора.");
        }

        try
        {
            string compactExe = Path.Combine(Environment.SystemDirectory, "compact.exe");
            string arg = enable ? "/CompactOS:always" : "/CompactOS:never";
            var psi = new ProcessStartInfo(compactExe, arg);

            DateTime lastLogUtc = DateTime.MinValue;
            var res = await RunProcessWithWatchdogAsync(
                psi,
                LongSystemOperationInactivityTimeout,
                line =>
                {
                    if (logCallback == null) return;
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastLogUtc).TotalMilliseconds >= 500 || !line.Contains("..."))
                    {
                        lastLogUtc = now;
                        logCallback(line);
                    }
                },
                cancellationToken);

            if (res.Cancelled)
            {
                return (false, isEn ? "CompactOS operation cancelled by user." : "Операция CompactOS отменена пользователем.");
            }
            if (res.TimedOut)
            {
                return (false, isEn
                    ? "CompactOS operation timed out (no progress for 15 minutes)."
                    : "Операция CompactOS превысила лимит ожидания бездействия (15 минут).");
            }
            if (res.ExitCode == 0)
            {
                return (true, enable
                    ? (isEn ? "CompactOS compression enabled (system files compressed)." : "CompactOS включён (системные файлы сжаты для экономии места).")
                    : (isEn ? "CompactOS compression disabled." : "CompactOS отключён."));
            }
            return (false, $"compact.exe ExitCode {res.ExitCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unitIndex = 0;

        while (size >= 1024.0 && unitIndex < units.Length - 1)
        {
            size /= 1024.0;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{size.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} {units[unitIndex]}"
            : $"{size.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }
}
