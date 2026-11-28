using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CoreSpectre.Services;

namespace CoreSpectre;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private Mutex? _instanceMutex;
    private CancellationTokenSource? _pipeCts;

    private static string GetUserScopeToken()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return (id.User?.Value ?? Environment.UserName).Replace("-", "");
        }
        catch
        {
            return Environment.UserName;
        }
    }

    private static string? ExtractCsbArg(string[] args)
    {
        foreach (var arg in args)
        {
            if (!string.IsNullOrWhiteSpace(arg) &&
                arg.EndsWith(".csb", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(arg))
            {
                return Path.GetFullPath(arg);
            }
        }
        return null;
    }

    private static async Task ForwardActivationToPrimaryAsync(string pipeName, string? csbPath)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(cts.Token);
            string message = string.IsNullOrEmpty(csbPath) ? "ACTIVATE\n" : $"OPEN:{csbPath}\n";
            await client.WriteAsync(Encoding.UTF8.GetBytes(message), cts.Token);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Single-instance forward failed: {ex.Message}");
        }
    }

    private void StartActivationPipeListener(string pipeName, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var sec = new PipeSecurity();
                    using var id = WindowsIdentity.GetCurrent();
                    if (id.User != null)
                    {
                        sec.AddAccessRule(new PipeAccessRule(id.User, PipeAccessRights.FullControl, AccessControlType.Allow));
                    }
                    try
                    {
                        // Allow Medium-IL secondary instance (.csb open) to signal an elevated primary instance
                        sec.SetSecurityDescriptorSddlForm("S:(ML;;NW;;;ME)", AccessControlSections.Audit);
                    }
                    catch (Exception sddlEx)
                    {
                        Logger.Debug($"Pipe SACL setup skipped: {sddlEx.Message}");
                    }

                    using var server = NamedPipeServerStreamAcl.Create(
                        pipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        inBufferSize: 4096,
                        outBufferSize: 1024,
                        sec);

                    await server.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(TimeSpan.FromSeconds(2));
                    string? msg = await reader.ReadLineAsync(readCts.Token);
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        string? csbPath = msg.StartsWith("OPEN:", StringComparison.Ordinal) ? msg[5..] : null;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (MainWindow is MainWindow mw)
                            {
                                mw.HandleExternalCsbOpen(csbPath);
                            }
                        }, System.Windows.Threading.DispatcherPriority.Normal, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    Logger.Debug("Activation pipe client read timed out.");
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Activation pipe loop warning: {ex.Message}");
                    try { await Task.Delay(250, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                }
            }
        }, ct);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try { await StartCoreSpectreAsync(e.Args); }
        catch (Exception ex)
        {
            Logger.LogCrash("CoreSpectre startup failed", ex);
            Shutdown(1);
        }
    }

    private async Task StartCoreSpectreAsync(string[] args)
    {
        int workerIdx = Array.FindIndex(args, a => string.Equals(a, "--elevated-worker", StringComparison.OrdinalIgnoreCase));
        if (workerIdx >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (workerIdx + 1 >= args.Length || !StorageService.IsValidPipeName(args[workerIdx + 1]))
            {
                Shutdown(2);
                return;
            }
            string pipeName = args[workerIdx + 1];
            int code = 0;
            try
            {
                Logger.Info($"CoreSpectre elevated cleanup worker started for pipe: {pipeName}");
                code = await Task.Run(() => StorageService.RunElevatedWorkerAsync(pipeName));
            }
            catch (Exception ex)
            {
                Logger.Error("Elevated cleanup worker failed", ex);
                code = 1;
            }
            Shutdown(code);
            return;
        }

        string? startupCsbPath = ExtractCsbArg(args);
        bool startMinimized = args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        string scope = GetUserScopeToken();
        string mutexName = $"Local\\CoreSpectre_SingleInstance_{scope}";
        string activationPipeName = $"CoreSpectre_Activation_{scope}";

        _instanceMutex = new Mutex(initiallyOwned: true, mutexName, out bool isPrimary);
        if (!isPrimary)
        {
            await ForwardActivationToPrimaryAsync(activationPipeName, startupCsbPath);
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown(0);
            return;
        }

        // Инициализируем шрифты и ассоциацию .csb до создания главного окна
        SettingsPersistenceService.EnsureCsbFileAssociation();
        FontService.Initialize();
        Logger.Info("CoreSpectre application startup initialized.");

        DispatcherUnhandledException += (s, args) =>
        {
            var ex = args.Exception;
            Logger.Error($"Dispatcher unhandled exception: {ex.GetType().FullName}", ex);

            // Handle only well-known benign WPF clipboard/drag COM & Win32 races
            bool isBenign =
                (ex is System.Runtime.InteropServices.COMException comEx &&
                 (unchecked((uint)comEx.HResult) == 0x800401D0u || unchecked((uint)comEx.HResult) == 0x800401D3u)) ||
                (ex is System.ComponentModel.Win32Exception w32 &&
                 (w32.NativeErrorCode == 1418 || w32.NativeErrorCode == 1400));

            if (isBenign)
            {
                Logger.Warn($"Non-fatal UI exception handled: {ex.Message}");
                args.Handled = true;
            }
            else
            {
                Logger.LogCrash("Fatal Dispatcher Unhandled Exception", ex);
                args.Handled = false;
            }
        };

        AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;

        _pipeCts = new CancellationTokenSource();
        StartActivationPipeListener(activationPipeName, _pipeCts.Token);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        if (!string.IsNullOrEmpty(startupCsbPath))
        {
            void OpenStartupBackup(object sender, RoutedEventArgs e)
            {
                mainWindow.Loaded -= OpenStartupBackup;
                mainWindow.HandleExternalCsbOpen(startupCsbPath);
            }
            mainWindow.Loaded += OpenStartupBackup;
        }
        if (startMinimized && string.IsNullOrEmpty(startupCsbPath))
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(mainWindow);
            helper.EnsureHandle();
            if (!mainWindow.CanRestoreFromTray) mainWindow.Show();
        }
        else
        {
            mainWindow.Show();
        }
    }

    private static void HandleUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception ex) Logger.LogCrash("Unhandled AppDomain Exception", ex);
        else Logger.LogCrash($"Unhandled AppDomain Object: {args.ExceptionObject}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException -= HandleUnhandledException;
        _pipeCts?.Cancel();
        _pipeCts?.Dispose();
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
