using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CoreSpectre.Services;

public record MemoryStatus(
    long TotalPhysicalBytes,
    long AvailablePhysicalBytes,
    long UsedPhysicalBytes,
    double UsedPercent
);

public record ExtendedMemoryInfo(
    long CommitTotalBytes,
    long CommitLimitBytes,
    long SystemCacheBytes,
    long KernelPagedBytes,
    long KernelNonpagedBytes
);

public record PurgeResult(
    long BytesFreed,
    int ProcessesOptimized,
    double UsedPercentBefore,
    double UsedPercentAfter
);

public static class MemoryService
{
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint SYNCHRONIZE = 0x100000;
    private const uint PROCESS_SET_QUOTA = 0x0100;
    private const uint PROCESS_SET_INFORMATION = 0x0200;

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const int SystemMemoryListInformation = 80;
    private const int MemoryFlushModifiedList = 3;
    private const int MemoryPurgeStandbyList = 4;
    private const int MemoryPurgeLowPriorityStandbyList = 5;

    private const int ProcessPowerThrottling = 4;
    private const int ProcessMemoryPriority = 0;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    private const uint MEMORY_PRIORITY_LOW = 2;
    private readonly record struct ThrottledProcess(long CreatedAt, ProcessPriorityClass Priority,
        PROCESS_POWER_THROTTLING_STATE? Power, MEMORY_PRIORITY_INFORMATION? Memory);

    private static readonly object _throttleLock = new();
    private static readonly object _standbyLock = new();
    private static readonly ConcurrentDictionary<int, ThrottledProcess> _throttledOriginals = new();

    private static readonly HashSet<string> ProtectedSystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "services",
        "lsass", "winlogon", "fontdrvhost", "dwm", "explorer", "corespectre",
        "svchost", "sihost", "audiodg", "spoolsv", "msmpeng", "securityhealthservice",
        "securityhealthsystray", "nissrv", "searchindexer", "ctfmon", "taskhostw",
        "runtimebroker", "shellexperiencehost", "startmenuexperiencehost"
    };

    public static bool IsProtectedFromWorkingSetTrim(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) &&
        ProtectedSystemProcesses.Contains(processName.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase));

    public static int ActiveThrottledProcessCount => _throttledOriginals.Count;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public static MEMORYSTATUSEX Create()
        {
            var result = new MEMORYSTATUSEX();
            result.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            return result;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal;
        public UIntPtr CommitLimit;
        public UIntPtr CommitPeak;
        public UIntPtr PhysicalTotal;
        public UIntPtr PhysicalAvailable;
        public UIntPtr SystemCache;
        public UIntPtr KernelTotal;
        public UIntPtr KernelPaged;
        public UIntPtr KernelNonpaged;
        public UIntPtr PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_PRIORITY_INFORMATION
    {
        public uint MemoryPriority;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static extern SafeProcessHandle OpenPriorityProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ProcessPriorityClass GetPriorityClass(SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(SafeProcessHandle process, ProcessPriorityClass priority);

    [DllImport("kernel32.dll", EntryPoint = "GetProcessInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPowerThrottling(SafeProcessHandle process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE state, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetProcessInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMemoryPriority(SafeProcessHandle process, int infoClass, out MEMORY_PRIORITY_INFORMATION state, uint size);

    [DllImport("kernel32.dll", EntryPoint = "SetProcessInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPowerThrottling(SafeProcessHandle process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE state, uint size);

    [DllImport("kernel32.dll", EntryPoint = "SetProcessInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMemoryPriority(SafeProcessHandle process, int infoClass, ref MEMORY_PRIORITY_INFORMATION state, uint size);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int SystemInformationClass, ref int SystemInformation, int SystemInformationLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableTokenPrivilege(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TOKEN_PRIVILEGES desired, uint length, out TOKEN_PRIVILEGES previous, out uint returnedLength);

    public static MemoryStatus GetCurrentStatus()
    {
        var memStatus = MEMORYSTATUSEX.Create();
        if (GlobalMemoryStatusEx(ref memStatus))
        {
            long total = (long)memStatus.ullTotalPhys;
            long avail = (long)memStatus.ullAvailPhys;
            long used = total - avail;
            double percent = total > 0 ? (double)used / total * 100.0 : 0.0;
            return new MemoryStatus(total, avail, used, Math.Round(percent, 1));
        }

        return new MemoryStatus(0L, 0L, 0L, 0.0);
    }

    public static ExtendedMemoryInfo? GetPerformanceDetails()
    {
        try
        {
            uint cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>();
            if (GetPerformanceInfo(out var info, cb))
            {
                long pageSize = (long)info.PageSize;
                return new ExtendedMemoryInfo(
                    CommitTotalBytes: (long)info.CommitTotal * pageSize,
                    CommitLimitBytes: (long)info.CommitLimit * pageSize,
                    SystemCacheBytes: (long)info.SystemCache * pageSize,
                    KernelPagedBytes: (long)info.KernelPaged * pageSize,
                    KernelNonpagedBytes: (long)info.KernelNonpaged * pageSize
                );
            }
        }
        catch (Exception ex) { Logger.Debug($"GetPerformanceDetails failed: {ex.Message}"); }
        return null;
    }

    private static int? PurgeStandbyWithPrivilege(bool includeModified, out bool partial)
    {
        partial = false;
        lock (_standbyLock)
        {
            using var current = Process.GetCurrentProcess();
            if (!OpenProcessToken(current.Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
                return null;

            try
            {
                if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out LUID luid))
                    return null;

                var tp = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED
                };

                if (!EnableTokenPrivilege(token, false, ref tp, (uint)Marshal.SizeOf<TOKEN_PRIVILEGES>(), out var previous, out _) ||
                    Marshal.GetLastPInvokeError() != 0)
                    return null;

                try
                {
                    int cmdLow = MemoryPurgeLowPriorityStandbyList;
                    int cmdStandby = MemoryPurgeStandbyList;
                    int low = NtSetSystemInformation(SystemMemoryListInformation, ref cmdLow, sizeof(int));
                    int full = NtSetSystemInformation(SystemMemoryListInformation, ref cmdStandby, sizeof(int));
                    partial = full != 0 && low == 0;
                    if (includeModified)
                    {
                        int cmdMod = MemoryFlushModifiedList;
                        partial |= NtSetSystemInformation(SystemMemoryListInformation, ref cmdMod, sizeof(int)) != 0;
                    }
                    return full != 0 && low != 0 ? full : 0;
                }
                finally
                {
                    if (previous.PrivilegeCount > 0 &&
                        (!AdjustTokenPrivileges(token, false, ref previous, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastPInvokeError() != 0))
                        Logger.Warn("Could not restore the Standby List privilege state.");
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
    }

    public static (bool Success, long BytesFreed, string Message) PurgeStandbyList(bool isEn = false, bool includeModified = false)
    {
        if (!StorageService.IsAdministrator())
        {
            return (false, 0, isEn ? "Administrator privileges required for Standby List purge." : "Для очистки Standby List требуются права администратора.");
        }

        var before = GetCurrentStatus();
        int? status = PurgeStandbyWithPrivilege(includeModified, out bool partial);
        if (!status.HasValue)
        {
            return (false, 0, isEn ? "Could not enable SeProfileSingleProcessPrivilege." : "Не удалось активировать привилегию SeProfileSingleProcessPrivilege.");
        }

        if (status.Value != 0)
        {
            return (false, 0, isEn ? $"NtSetSystemInformation failed (NTSTATUS 0x{status.Value:X8})." : $"Ошибка сброса Standby List (NTSTATUS 0x{status.Value:X8}).");
        }

        var after = GetCurrentStatus();
        long freed = Math.Max(0, after.AvailablePhysicalBytes - before.AvailablePhysicalBytes);
        if (partial)
            return (false, freed, isEn ? "Standby List purge completed only partially." : "Очистка Standby List выполнена частично.");
        string msg = isEn
            ? $"Standby List purged (available RAM: {StorageService.FormatBytes(after.AvailablePhysicalBytes)})."
            : $"Standby List очищен (доступно ОЗУ: {StorageService.FormatBytes(after.AvailablePhysicalBytes)}).";
        return (true, freed, msg);
    }

    public static PurgeResult PurgeMemory(IReadOnlyCollection<string>? processWhitelist = null)
    {
        var before = GetCurrentStatus();
        int optimizedCount = 0;

        HashSet<string>? excluded = null;
        if (processWhitelist != null && processWhitelist.Count > 0)
        {
            excluded = new HashSet<string>(
                processWhitelist
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);
        }

        var processes = Process.GetProcesses();
        int currentPid = Environment.ProcessId;

        foreach (var proc in processes)
        {
            try
            {
                if (proc.Id <= 4 || proc.Id == currentPid)
                    continue;

                if (IsProtectedFromWorkingSetTrim(proc.ProcessName))
                    continue;

                if (excluded != null && excluded.Contains(proc.ProcessName))
                    continue;

                IntPtr hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, proc.Id);
                if (hProcess != IntPtr.Zero)
                {
                    try
                    {
                        if (EmptyWorkingSet(hProcess))
                        {
                            optimizedCount++;
                        }
                    }
                    finally
                    {
                        CloseHandle(hProcess);
                    }
                }
            }
            catch
            {
                // Access denied or process terminated, skip safely
            }
            finally
            {
                proc.Dispose();
            }
        }

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();

        var after = GetCurrentStatus();
        long freed = Math.Max(0, after.AvailablePhysicalBytes - before.AvailablePhysicalBytes);

        return new PurgeResult(
            freed,
            optimizedCount,
            before.UsedPercent,
            after.UsedPercent
        );
    }

    public static bool ShouldThrottlePriority(ProcessPriorityClass priority) =>
        priority is ProcessPriorityClass.Normal or ProcessPriorityClass.AboveNormal or ProcessPriorityClass.High;

    public static int OptimizeBackgroundProcesses(IReadOnlyCollection<string>? processWhitelist = null,
        CancellationToken cancellationToken = default)
    {
        PruneExitedBackgroundProcesses();
        string[] targetPatterns =
        [
            "onedrive", "dropbox", "googledrivesync", "searchindexer",
            "compattelrunner", "mscorsvw", "adobeupdateservice", "armsvc", "epicwebhelper"
        ];

        var excluded = new HashSet<string>(
            (processWhitelist ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

        int optimized = 0;
        var procs = Process.GetProcesses();
        foreach (var p in procs)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested || p.Id <= 4) continue;
                string pName = p.ProcessName;
                if (excluded.Contains(pName)) continue;

                string lower = pName.ToLowerInvariant();
                if (!targetPatterns.Any(t => lower.Contains(t))) continue;

                if (TryThrottleBackgroundProcess(p.Id, p.StartTime.ToFileTimeUtc(), cancellationToken))
                    optimized++;
            }
            catch (Exception ex) { Logger.Debug($"Background CPU opt skipped: {ex.Message}"); }
            finally { p.Dispose(); }
        }
        return optimized;
    }

    internal static bool TryThrottleBackgroundProcess(int pid, long expectedCreationTime, CancellationToken cancellationToken = default)
    {
        lock (_throttleLock)
        {
            if (cancellationToken.IsCancellationRequested) return false;
            using var process = OpenPriorityProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process.IsInvalid || !GetProcessTimes(process, out long created, out _, out _, out _) ||
                created != expectedCreationTime)
                return false;

            if (_throttledOriginals.TryGetValue(pid, out var existing) && existing.CreatedAt == created)
                return false;

            var priority = GetPriorityClass(process);
            if (!ShouldThrottlePriority(priority) || !SetPriorityClass(process, ProcessPriorityClass.BelowNormal))
                return false;

            var power = new PROCESS_POWER_THROTTLING_STATE { Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION };
            uint powerSize = (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>();
            uint memorySize = (uint)Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>();
            PROCESS_POWER_THROTTLING_STATE? originalPower = null;
            MEMORY_PRIORITY_INFORMATION? originalMemory = null;
            if (GetPowerThrottling(process, ProcessPowerThrottling, ref power, powerSize))
            {
                var eco = power;
                eco.ControlMask |= PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
                eco.StateMask |= PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
                if (SetPowerThrottling(process, ProcessPowerThrottling, ref eco, powerSize)) originalPower = power;
            }
            if (GetMemoryPriority(process, ProcessMemoryPriority, out var memory, memorySize))
            {
                var low = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = MEMORY_PRIORITY_LOW };
                if (SetMemoryPriority(process, ProcessMemoryPriority, ref low, memorySize)) originalMemory = memory;
            }
            _throttledOriginals[pid] = new ThrottledProcess(created, priority, originalPower, originalMemory);
            return true;
        }
    }

    private static bool IsGoneOrReplaced(SafeProcessHandle process, long expectedCreationTime) =>
        process.IsInvalid ? Marshal.GetLastWin32Error() == 87 /* ERROR_INVALID_PARAMETER */ :
        WaitForSingleObject(process, 0) == 0 ||
        (GetProcessTimes(process, out long created, out _, out _, out _) && created != expectedCreationTime);

    internal static void PruneExitedBackgroundProcesses()
    {
        if (_throttledOriginals.IsEmpty) return;
        lock (_throttleLock)
        {
            foreach (var (pid, original) in _throttledOriginals)
            {
                using var process = OpenPriorityProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, pid);
                if (IsGoneOrReplaced(process, original.CreatedAt)) _throttledOriginals.TryRemove(pid, out _);
            }
        }
    }

    public static int RestoreBackgroundProcesses()
    {
        int restored = 0;
        lock (_throttleLock)
        {
            PruneExitedBackgroundProcesses();
            foreach (var (pid, original) in _throttledOriginals)
            {
                using var process = OpenPriorityProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (process.IsInvalid || !GetProcessTimes(process, out long created, out _, out _, out _) || created != original.CreatedAt)
                    continue;

                bool ok = SetPriorityClass(process, original.Priority);
                if (original.Power is { } power)
                    ok &= SetPowerThrottling(process, ProcessPowerThrottling, ref power, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
                if (original.Memory is { } memory)
                    ok &= SetMemoryPriority(process, ProcessMemoryPriority, ref memory, (uint)Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>());

                if (ok)
                {
                    _throttledOriginals.TryRemove(pid, out _);
                    restored++;
                }
            }
        }
        return restored;
    }
}
