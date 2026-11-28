using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CoreSpectre.Services;

public record TelemetrySnapshot(
    double CpuLoadPercent,
    double RamUsedPercent,
    long RamUsedBytes,
    long RamTotalBytes,
    string SystemDriveLetter,
    double DiskCFreeGb,
    double DiskCTotalGb,
    string DataDriveLetter,
    double DiskDFreeGb,
    double DiskDTotalGb,
    bool HasSecondaryDataDrive,
    bool IsGameRunning,
    string ActiveGameName,
    double? GpuLoadPercent,
    double? GpuTempCelsius,
    double[] CpuHistory,
    double[] RamHistory
);

public static class HardwareTelemetry
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        public ulong ToULong() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    private static ulong _prevIdleTime;
    private static ulong _prevKernelTime;
    private static ulong _prevUserTime;
    private static bool _initialized = false;
    private static double _lastCpuUsage = 0.0;
    private static readonly object _syncLock = new();
    private static readonly object _historyLock = new();
    private static readonly object _sensorLock = new();
    private static readonly Dictionary<string, PerformanceCounter> _gpuEngineCounters = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _gpuCountersLastRefreshUtc = DateTime.MinValue;
    private static readonly Queue<double> _cpuHistory = new();
    private static readonly Queue<double> _ramHistory = new();
    private static string? _cachedCpuName = null;
    private static int? _cachedPhysicalCores = null;
    private static string? _nvidiaSmiPath = null;
    private static bool _nvidiaSmiProbed = false;
    private static DateTime _lastSensorSampleUtc = DateTime.MinValue;
    private sealed record GpuReading(double? Load, double? Temperature);
    private static volatile GpuReading _cachedGpu = new(null, null);

    public static bool IsRussianLanguage { get; set; }

    public static int GetPhysicalCoreCount()
    {
        if (_cachedPhysicalCores.HasValue)
            return _cachedPhysicalCores.Value;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor");
            using var results = searcher.Get();
            int totalCores = 0;
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    totalCores += Convert.ToInt32(item["NumberOfCores"] ?? 0);
                }
            }
            if (totalCores > 0)
            {
                _cachedPhysicalCores = totalCores;
                return totalCores;
            }
        }
        catch (Exception ex) { Logger.Debug("HardwareTelemetry.cs: " + ex.Message); }

        _cachedPhysicalCores = 0;
        return 0;
    }

    public static string GetProcessorName()
    {
        if (_cachedCpuName != null)
            return _cachedCpuName;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            string? name = key?.GetValue("ProcessorNameString") as string;
            if (!string.IsNullOrWhiteSpace(name))
            {
                _cachedCpuName = string.Join(" ", name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                return _cachedCpuName;
            }
        }
        catch (Exception ex) { Logger.Debug("HardwareTelemetry.cs: " + ex.Message); }

        string? envCpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        _cachedCpuName = !string.IsNullOrWhiteSpace(envCpu) ? envCpu : "System CPU";
        return _cachedCpuName;
    }

    public static string GetSystemDriveLetter()
    {
        try
        {
            string? root = Path.GetPathRoot(Environment.SystemDirectory);
            if (!string.IsNullOrEmpty(root))
            {
                return root.TrimEnd('\\', ':');
            }
        }
        catch (Exception ex) { Logger.Debug("HardwareTelemetry.cs: " + ex.Message); }
        return "C";
    }

    public static TelemetrySnapshot Sample(
        bool includeSensors = true,
        IReadOnlyCollection<string>? customGames = null,
        IReadOnlyCollection<string>? excludedGames = null,
        (bool IsGame, string Name)? knownActiveGame = null)
    {
        double cpu = GetCpuUsage();
        var mem = MemoryService.GetCurrentStatus();

        string sysLetter = GetSystemDriveLetter();
        double diskCFree = 0.0, diskCTotal = 0.0;
        string dataLetter = "D";
        double diskDFree = 0.0, diskDTotal = 0.0;
        bool hasSecondary = false;

        try
        {
            var drives = DriveInfo.GetDrives();
            foreach (var d in drives)
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                string letter = d.Name.TrimEnd('\\', ':');
                if (string.Equals(letter, sysLetter, StringComparison.OrdinalIgnoreCase))
                {
                    diskCFree = (double)d.AvailableFreeSpace / (1024 * 1024 * 1024);
                    diskCTotal = (double)d.TotalSize / (1024 * 1024 * 1024);
                }
                else if (!hasSecondary)
                {
                    hasSecondary = true;
                    dataLetter = letter;
                    diskDFree = (double)d.AvailableFreeSpace / (1024 * 1024 * 1024);
                    diskDTotal = (double)d.TotalSize / (1024 * 1024 * 1024);
                }
            }
        }
        catch (Exception ex) { Logger.Debug("HardwareTelemetry.cs drives: " + ex.Message); }

        var (isGame, gameName) = knownActiveGame ?? CheckActiveGame(customGames, excludedGames);

        if (includeSensors)
        {
            bool shouldPoll = false;
            lock (_sensorLock)
            {
                if ((DateTime.UtcNow - _lastSensorSampleUtc).TotalSeconds >= 3)
                {
                    _lastSensorSampleUtc = DateTime.UtcNow;
                    shouldPoll = true;
                }
            }
            if (shouldPoll)
            {
                PollGpuSensors();
            }
        }

        double cpuRounded = Math.Round(cpu, 1);
        double ramRounded = mem.UsedPercent;
        double[] cpuHist, ramHist;

        lock (_historyLock)
        {
            const int MaxHistorySamples = 25;
            _cpuHistory.Enqueue(cpuRounded);
            while (_cpuHistory.Count > MaxHistorySamples) _cpuHistory.Dequeue();
            _ramHistory.Enqueue(ramRounded);
            while (_ramHistory.Count > MaxHistorySamples) _ramHistory.Dequeue();

            cpuHist = _cpuHistory.ToArray();
            ramHist = _ramHistory.ToArray();
        }

        var gpu = _cachedGpu;
        return new TelemetrySnapshot(
            cpuRounded,
            ramRounded,
            mem.UsedPhysicalBytes,
            mem.TotalPhysicalBytes,
            sysLetter,
            Math.Round(diskCFree, 2),
            Math.Round(diskCTotal, 2),
            dataLetter,
            Math.Round(diskDFree, 2),
            Math.Round(diskDTotal, 2),
            hasSecondary,
            isGame,
            gameName,
            gpu.Load,
            gpu.Temperature,
            cpuHist,
            ramHist
        );
    }

    private static void PollGpuSensors()
    {
        lock (_sensorLock)
        {
            double? nvidiaUtil = null;
            double? nvidiaTemp = null;

            try
            {
                if (!_nvidiaSmiProbed)
                {
                    _nvidiaSmiProbed = true;
                    string[] candidates =
                    [
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
                        Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe")
                    ];
                    _nvidiaSmiPath = candidates.FirstOrDefault(path => Path.IsPathFullyQualified(path) && File.Exists(path));
                }

                if (!string.IsNullOrEmpty(_nvidiaSmiPath))
                {
                    var psi = new ProcessStartInfo(_nvidiaSmiPath, "--query-gpu=utilization.gpu,temperature.gpu --format=csv,noheader,nounits")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using var p = Process.Start(psi);
                    if (p != null)
                    {
                        using var reader = p.StandardOutput;
                        using var readCts = new CancellationTokenSource();
                        var readTask = reader.ReadToEndAsync(readCts.Token);
                        if (!p.WaitForExit(800))
                        {
                            try { p.Kill(entireProcessTree: true); } catch { }
                        }
                        string output = "";
                        if (readTask.Wait(200))
                        {
                            output = readTask.GetAwaiter().GetResult();
                        }
                        else
                        {
                            readCts.Cancel();
                            _ = readTask.ContinueWith(static t => { _ = t.Exception; }, CancellationToken.None,
                                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        }
                        string line = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                        var parts = line.Split(',');
                        if (parts.Length >= 2)
                        {
                            if (double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double util))
                                nvidiaUtil = Math.Clamp(util, 0, 100);
                            if (double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double temp))
                                nvidiaTemp = temp;
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Debug("HardwareTelemetry nvidia-smi: " + ex.Message); }

            if (!nvidiaUtil.HasValue)
            {
                nvidiaUtil = TryPollWindowsGpuEngineUtil();
            }

            _cachedGpu = new GpuReading(nvidiaUtil, nvidiaTemp);
        }
    }

    private static double? TryPollWindowsGpuEngineUtil()
    {
        try
        {
            var now = DateTime.UtcNow;
            if (_gpuEngineCounters.Count == 0 || (now - _gpuCountersLastRefreshUtc).TotalSeconds >= 15)
            {
                _gpuCountersLastRefreshUtc = now;
                if (!PerformanceCounterCategory.Exists("GPU Engine"))
                    return null;

                var cat = new PerformanceCounterCategory("GPU Engine");
                var instances = cat.GetInstanceNames()
                    .Where(inst => inst.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var stale in _gpuEngineCounters.Keys.Where(k => !instances.Contains(k)).ToList())
                {
                    if (_gpuEngineCounters.Remove(stale, out var oldCounter))
                    {
                        try { oldCounter.Dispose(); } catch { }
                    }
                }

                foreach (var inst in instances)
                {
                    if (!_gpuEngineCounters.ContainsKey(inst))
                    {
                        PerformanceCounter? pc = null;
                        try
                        {
                            pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, readOnly: true);
                            _ = pc.NextValue(); // Warm up baseline sample
                            _gpuEngineCounters[inst] = pc;
                            pc = null; // Ownership transferred to the cache.
                        }
                        catch { }
                        finally { pc?.Dispose(); }
                    }
                }
            }

            if (_gpuEngineCounters.Count == 0) return null;

            double sum = 0;
            int sampled = 0;
            foreach (var kv in _gpuEngineCounters.ToList())
            {
                try
                {
                    sum += kv.Value.NextValue();
                    sampled++;
                }
                catch
                {
                    _gpuEngineCounters.Remove(kv.Key);
                    try { kv.Value.Dispose(); } catch { }
                }
            }

            if (sampled > 0)
            {
                return Math.Clamp(Math.Round(sum, 1), 0, 100);
            }
        }
        catch (Exception ex) { Logger.Debug("HardwareTelemetry GPU Engine: " + ex.Message); }
        return null;
    }

    private static double GetCpuUsage()
    {
        lock (_syncLock)
        {
            if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
                return _lastCpuUsage;

            ulong idle = idleTime.ToULong();
            ulong kernel = kernelTime.ToULong();
            ulong user = userTime.ToULong();

            if (!_initialized)
            {
                _prevIdleTime = idle;
                _prevKernelTime = kernel;
                _prevUserTime = user;
                _initialized = true;
                return 0.0;
            }

            ulong usrDiff = user - _prevUserTime;
            ulong kerDiff = kernel - _prevKernelTime;
            ulong idlDiff = idle - _prevIdleTime;

            _prevIdleTime = idle;
            _prevKernelTime = kernel;
            _prevUserTime = user;

            ulong totalSys = usrDiff + kerDiff;
            if (totalSys == 0) return _lastCpuUsage;

            ulong nonIdle = totalSys > idlDiff ? totalSys - idlDiff : 0;
            double cpu = (double)nonIdle * 100.0 / totalSys;

            _lastCpuUsage = Math.Clamp(cpu, 0.0, 100.0);
            return _lastCpuUsage;
        }
    }

    private static (bool IsRunning, string GameName) CheckActiveGame(
        IReadOnlyCollection<string>? customGames = null,
        IReadOnlyCollection<string>? excludedGames = null)
    {
        try
        {
            var processes = Process.GetProcesses();
            foreach (var proc in processes)
            {
                try
                {
                    string name = proc.ProcessName;
                    string title = "";
                    try { title = proc.MainWindowTitle; } catch { }

                    var (isGame, displayName) = GameBoostService.ResolveGameIdentity(name, title, IsRussianLanguage, customGames, excludedGames);
                    if (isGame)
                    {
                        return (true, displayName);
                    }
                }
                catch
                {
                    // Access denied
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
            // Process scan failure
        }

        return (false, string.Empty);
    }
}
