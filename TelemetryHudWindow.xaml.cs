using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CoreSpectre.Services;

namespace CoreSpectre;

public partial class TelemetryHudWindow : Window
{
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly DispatcherTimer _timer;
    private bool _isClickThrough = false;
    private bool _isSampling = false;
    private string _hudDriveLetter = "";
    private DateTime _lastPushedSnapshotUtc = DateTime.MinValue;

    public bool IsClickThrough => _isClickThrough;
    public event Action<double, double>? OnPositionChanged;
    public event Action<string>? OnDriveChanged;

    public TelemetryHudWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _timer.Tick += async (_, _) => await RefreshMetricsAsync();

        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true)
            {
                ApplyLocalizationAndTheme();
                _timer.Start();
                await RefreshMetricsAsync();
            }
            else
            {
                _timer.Stop();
            }
        };

        ApplyLocalizationAndTheme();
    }

    public void RestoreState(double left, double top, string? driveLetter)
    {
        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;

        if (left >= vLeft - 50 && left <= vRight - 60) Left = left;
        if (top >= vTop - 20 && top <= vBottom - 30) Top = top;
        if (!string.IsNullOrWhiteSpace(driveLetter))
        {
            _hudDriveLetter = StorageService.ValidateDriveLetter(driveLetter);
        }
    }

    public void ApplyLocalizationAndTheme()
    {
        bool isEn = LocalizationService.IsEnglish;
        BtnLockHud.ToolTip = _isClickThrough
            ? (isEn ? "Unlock HUD Click-Through (Ctrl+Shift+L)" : "Отключить сквозные клики (Ctrl+Shift+L)")
            : (isEn ? "Enable HUD Click-Through (Ctrl+Shift+L)" : "Включить сквозные клики (Ctrl+Shift+L)");
        BtnCloseHud.ToolTip = isEn ? "Hide HUD (Ctrl+Shift+H)" : "Скрыть HUD (Ctrl+Shift+H)";
        HudDiskPanel.ToolTip = isEn ? "Click to switch monitored drive" : "Нажмите для смены диска в HUD";
    }

    public void ApplySnapshot(TelemetrySnapshot sample)
    {
        if (!IsVisible) return;
        string targetDrive = string.IsNullOrWhiteSpace(_hudDriveLetter)
            ? sample.SystemDriveLetter
            : _hudDriveLetter;

        double freeGb;
        if (targetDrive.Equals(sample.SystemDriveLetter, StringComparison.OrdinalIgnoreCase))
        {
            if (sample.DiskCTotalGb <= 0) return;
            freeGb = sample.DiskCFreeGb;
        }
        else if (sample.HasSecondaryDataDrive && targetDrive.Equals(sample.DataDriveLetter, StringComparison.OrdinalIgnoreCase))
        {
            freeGb = sample.DiskDFreeGb;
        }
        else return; // This drive is sampled by the HUD's worker, not by the UI thread.

        _lastPushedSnapshotUtc = DateTime.UtcNow;
        RenderSnapshot(sample, targetDrive, freeGb);
    }

    private void RenderSnapshot(TelemetrySnapshot sample, string targetDrive, double? freeGb)
    {
        bool isEn = LocalizationService.IsEnglish;

        HudCpuText.Text = $"{sample.CpuLoadPercent:0}%";
        HudRamText.Text = sample.RamTotalBytes > 0 ? $"{sample.RamUsedPercent:0}%" : "N/A";
        HudDiskLabel.Text = $"{targetDrive}: ";
        HudDiskText.Text = freeGb == null ? "—" : isEn ? $"{freeGb:0.#}G" : $"{freeGb:0.#}Г";

        var gpuParts = new List<string>(2);
        if (sample.GpuLoadPercent.HasValue) gpuParts.Add($"{sample.GpuLoadPercent.Value:0}%");
        if (sample.GpuTempCelsius.HasValue) gpuParts.Add($"{sample.GpuTempCelsius.Value:0}°C");

        if (gpuParts.Count > 0)
        {
            HudGpuPanel.Visibility = Visibility.Visible;
            HudGpuText.Text = string.Join(" · ", gpuParts);
        }
        else
        {
            HudGpuPanel.Visibility = Visibility.Collapsed;
        }

        if (sample.IsGameRunning)
        {
            HudStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "BrushTextPrimary");
            HudStatusDot.ToolTip = isEn
                ? $"Game Mode active: {sample.ActiveGameName}"
                : $"Игровой режим активен: {sample.ActiveGameName}";
        }
        else
        {
            HudStatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "BrushTextMuted");
            HudStatusDot.ToolTip = isEn ? "System nominal" : "Система в норме";
        }
    }

    private async Task RefreshMetricsAsync(bool forceRefresh = false)
    {
        if (_isSampling || !IsVisible) return;
        if (!forceRefresh && (DateTime.UtcNow - _lastPushedSnapshotUtc).TotalSeconds < 2.5)
        {
            return;
        }

        _isSampling = true;

        try
        {
            string selectedDrive = _hudDriveLetter;
            DateTime lastPush = _lastPushedSnapshotUtc;
            string targetDrive = string.IsNullOrWhiteSpace(selectedDrive)
                ? HardwareTelemetry.GetSystemDriveLetter()
                : selectedDrive;

            var (sample, driveInfo) = await Task.Run(() =>
                (HardwareTelemetry.Sample(includeSensors: true), StorageService.GetDriveInfo(targetDrive)));
            if (IsVisible && selectedDrive == _hudDriveLetter && lastPush == _lastPushedSnapshotUtc)
                RenderSnapshot(sample, targetDrive, driveInfo?.FreeBytes / (1024.0 * 1024 * 1024));
        }
        catch (Exception ex)
        {
            Logger.Debug($"HUD refresh error: {ex.Message}");
        }
        finally
        {
            _isSampling = false;
        }
    }

    public void ToggleClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if (!_isClickThrough)
        {
            if (SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT) == 0 && Marshal.GetLastPInvokeError() != 0) return;
            _isClickThrough = true;
            HudLockIcon.Data = (Geometry)FindResource("IconLock");
            HudLockIcon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "BrushTextPrimary");
        }
        else
        {
            if (SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle & ~WS_EX_TRANSPARENT) == 0 && Marshal.GetLastPInvokeError() != 0) return;
            _isClickThrough = false;
            HudLockIcon.Data = (Geometry)FindResource("IconLockOpen");
            HudLockIcon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "BrushTextMuted");
        }
        ApplyLocalizationAndTheme();
    }

    private void HudDiskPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isClickThrough) return;
        e.Handled = true;
        try
        {
            var fixedDrives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .Select(d => d.Name.TrimEnd('\\', ':'))
                .ToList();
            if (fixedDrives.Count == 0) return;

            string current = string.IsNullOrWhiteSpace(_hudDriveLetter) ? HardwareTelemetry.GetSystemDriveLetter() : _hudDriveLetter;
            int idx = fixedDrives.FindIndex(d => d.Equals(current, StringComparison.OrdinalIgnoreCase));
            _hudDriveLetter = fixedDrives[(idx + 1) % fixedDrives.Count];
            _lastPushedSnapshotUtc = DateTime.MinValue;
            HudDiskLabel.Text = $"{_hudDriveLetter}: ";
            HudDiskText.Text = "—";
            OnDriveChanged?.Invoke(_hudDriveLetter);
            _ = RefreshMetricsAsync(forceRefresh: true);
        }
        catch { }
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isClickThrough && e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
                OnPositionChanged?.Invoke(Left, Top);
            }
            catch { }
        }
    }

    private void BtnLockHud_Click(object sender, RoutedEventArgs e) => ToggleClickThrough();

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
