using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CoreSpectre.Services;

public static partial class StorageService
{
    private const uint ServiceStopped = 1;
    private const uint ServiceRunning = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, State, ControlsAccepted, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint;
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr service);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(ServiceHandle service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(ServiceHandle service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(ServiceHandle service, uint argumentCount, IntPtr arguments);

    private static async Task<bool> WaitForServiceStateAsync(ServiceHandle service, uint state, CancellationToken ct = default)
    {
        var start = Stopwatch.GetTimestamp();
        while (!ct.IsCancellationRequested && QueryServiceStatus(service, out var status))
        {
            if (status.State == state) return true;
            if (Stopwatch.GetElapsedTime(start) >= TimeSpan.FromSeconds(10)) return false;
            try { await Task.Delay(100, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        }
        return false;
    }

    private static async Task<CleanResult> CleanStoppedServiceCacheAsync(string serviceName, string path, Action<string> log, bool isEn, CancellationToken ct)
    {
        if (!TryGetCleanupRoot(path, out var root)) return new CleanResult(0, 0, 0, HasErrors: true);
        if (ct.IsCancellationRequested || !Directory.Exists(root)) return new CleanResult(0, 0, 0);

        using var manager = OpenSCManager(null, null, 0x0001); // SC_MANAGER_CONNECT
        if (manager.IsInvalid) return new CleanResult(0, 0, 0, HasErrors: true);
        using var service = OpenService(manager, serviceName, 0x0004 | 0x0010 | 0x0020); // QUERY_STATUS | START | STOP
        if (service.IsInvalid || !QueryServiceStatus(service, out var initial))
            return new CleanResult(0, 0, 0, HasErrors: true);

        if (ct.IsCancellationRequested) return new CleanResult(0, 0, 0);
        if (initial.State == ServiceStopped) return CleanDirectoryContents(root, ct);

        if (initial.State != ServiceRunning || !ControlService(service, 1 /* SERVICE_CONTROL_STOP */, out _))
        {
            log($"[{serviceName}] {(isEn ? "Could not stop service; cache skipped." : "Не удалось остановить службу; кэш пропущен.")}");
            return new CleanResult(0, 0, 0, HasErrors: true);
        }

        CleanResult result = new(0, 0, 0);
        bool restored;
        try
        {
            if (await WaitForServiceStateAsync(service, ServiceStopped, ct).ConfigureAwait(false))
                result = CleanDirectoryContents(root, ct);
            else if (!ct.IsCancellationRequested)
                result = result with { HasErrors = true };
        }
        finally
        {
            // A stop request may still be pending after cancellation. Restore only a service we stopped.
            if (await WaitForServiceStateAsync(service, ServiceStopped).ConfigureAwait(false))
                _ = StartService(service, 0, IntPtr.Zero);
            restored = await WaitForServiceStateAsync(service, ServiceRunning).ConfigureAwait(false);
            if (!restored)
                log($"[{serviceName}] {(isEn ? "Service restart was not confirmed." : "Перезапуск службы не подтверждён.")}");
        }
        return result with { HasErrors = result.HasErrors || !restored };
    }
}
