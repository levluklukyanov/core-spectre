using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CoreSpectre.Services;

namespace CoreSpectre.Views;

public partial class PurgeView : UserControl
{
    private bool _isCpuPurgeMode = false;
    private bool _isStandbyBusy;

    public event Action? OnPurgeRequested;
    public event Action? OnStandbyPurgeRequested;
    public event Action? OnRestoreCpuRequested;
    public event Action? OnSwitchPurgeModeRequested;
    public event Action? OnOpenTaskManagerRequested;

    public bool IsCpuPurgeMode => _isCpuPurgeMode;

    public PurgeView()
    {
        InitializeComponent();
    }

    public void SetPurgeMode(bool isCpuMode)
    {
        _isCpuPurgeMode = isCpuMode;
        UpdateVisuals();
    }

    public void SetStandbyPurgeBusy(bool isBusy)
    {
        _isStandbyBusy = isBusy;
        bool isEn = LocalizationService.IsEnglish;
        BtnPurgeStandby.IsEnabled = !isBusy;
        BtnPurgeStandbyText.Text = isBusy
            ? (isEn ? "Clearing..." : "Сброс...")
            : (isEn ? "Clear Standby" : "Сбросить Standby");
    }

    public void UpdateVisuals()
    {
        bool isEn = LocalizationService.IsEnglish;
        BtnSwitchPurgeMode.ToolTip = isEn ? "Switch between RAM and CPU modes" : "Переключить режим ОЗУ / ЦП";

        if (_isCpuPurgeMode)
        {
            RamTabHeading.Text = isEn ? "CPU Load Optimization" : "Оптимизация нагрузки ЦП";
            RamTabSub.Text = isEn ? "Normalizes background worker thread priority and throttles idle utilities" : "Нормализует приоритеты фоновых потоков и снижает нагрузку фоновых утилит";
            RamBannerTitle.Text = isEn ? "Background Thread Normalization" : "Нормализация фоновых потоков";
            RamBannerSub.Text = isEn ? "Applies EcoQoS and BelowNormal priority to background utilities, freeing CPU cycles for active apps." : "Применяет EcoQoS и пониженный приоритет к фоновым службам (обратимо).";
            BtnFullPurgeText.Text = isEn ? "Optimize CPU Now" : "Оптимизировать ЦП";
            RamDetailHeader.Text = isEn ? "PROCESSOR SPECIFICATION" : "СПЕЦИФИКАЦИЯ ПРОЦЕССОРА";
            RamTotalLabel.Text = isEn ? "Processor Model:" : "Модель процессора:";
            RamUsedLabel.Text = isEn ? "Cores / Threads:" : "Ядра / Потоки:";
            RamAvailLabel.Text = isEn ? "Current CPU Load:" : "Текущая нагрузка ЦП:";
            RamLogTitle.Text = isEn ? "CPU OPTIMIZATION LOG" : "ЖУРНАЛ ОПТИМИЗАЦИИ ЦП";

            ExtendedMemPanel.Visibility = Visibility.Collapsed;
            BtnPurgeStandby.Visibility = Visibility.Collapsed;
            BtnRestoreCpu.Visibility = MemoryService.ActiveThrottledProcessCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnRestoreCpuText.Text = isEn ? "Restore CPU" : "Откатить ЦП";

            RamHeroIcon.Data = (Geometry)FindResource("IconCpu");
            BtnFullPurgeIcon.Data = (Geometry)FindResource("IconLightning");

            RamTotalLabel.FontSize = 11;
            Grid.SetRow(RamDetailTotal, 1);
            Grid.SetColumn(RamDetailTotal, 0);
            Grid.SetColumnSpan(RamDetailTotal, 2);
            RamDetailTotal.HorizontalAlignment = HorizontalAlignment.Left;
            RamDetailTotal.Margin = new Thickness(0, 3, 0, 0);
        }
        else
        {
            RamTabHeading.Text = isEn ? "Physical Memory Optimization" : "Оптимизация оперативной памяти";
            RamTabSub.Text = isEn ? "Working set trimming and Windows Standby List management" : "Сброс рабочих наборов и управление Standby List Windows";
            RamBannerTitle.Text = isEn ? "Working Set Memory Trimming" : "Сброс рабочих наборов памяти";
            RamBannerSub.Text = isEn ? "Safely returns unused pages from background processes to physical memory, preventing pagefile inflation." : "Очищает рабочие наборы процессов через EmptyWorkingSet, кратковременно высвобождая ОЗУ.";
            BtnFullPurgeText.Text = isEn ? "Purge Memory Now" : "Очистить память";
            RamDetailHeader.Text = isEn ? "MEMORY SPECIFICATION" : "СПЕЦИФИКАЦИЯ ОЗУ";
            RamTotalLabel.Text = isEn ? "Total RAM:" : "Всего ОЗУ:";
            RamUsedLabel.Text = isEn ? "In Use:" : "Занято:";
            RamAvailLabel.Text = isEn ? "Available:" : "Доступно:";
            RamCommitLabel.Text = isEn ? "Commit Charge:" : "Выделено (Commit):";
            RamCacheLabel.Text = isEn ? "System Cache:" : "Системный кэш:";
            RamLogTitle.Text = isEn ? "PURGE ACTIVITY LOG" : "ЖУРНАЛ ОЧИСТКИ ПАМЯТИ";

            ExtendedMemPanel.Visibility = Visibility.Visible;
            BtnPurgeStandby.Visibility = Visibility.Visible;
            SetStandbyPurgeBusy(_isStandbyBusy);
            BtnRestoreCpu.Visibility = Visibility.Collapsed;

            RamHeroIcon.Data = (Geometry)FindResource("IconRam");
            BtnFullPurgeIcon.Data = (Geometry)FindResource("IconBroom");

            RamTotalLabel.FontSize = 12.5;
            Grid.SetRow(RamDetailTotal, 0);
            Grid.SetColumn(RamDetailTotal, 1);
            Grid.SetColumnSpan(RamDetailTotal, 1);
            RamDetailTotal.HorizontalAlignment = HorizontalAlignment.Right;
            RamDetailTotal.Margin = new Thickness(0);
        }

        BtnRamOpenInspectorText.Text = isEn ? "Open Task Manager" : "Открыть диспетчер задач";
    }

    public void UpdateMetrics(
        double ramUsedGb,
        double ramTotalGb,
        double cpuPercent,
        string cpuName,
        int physicalCores,
        int logicalCores,
        ExtendedMemoryInfo? extMem = null)
    {
        bool isEn = LocalizationService.IsEnglish;
        if (_isCpuPurgeMode)
        {
            RamDetailTotal.Text = string.IsNullOrWhiteSpace(cpuName) ? (isEn ? "Detecting..." : "Определение...") : cpuName;
            RamDetailUsed.Text = physicalCores > 0 ? $"{physicalCores} / {logicalCores}" : $"{logicalCores}";
            RamDetailAvail.Text = $"{cpuPercent:0.#}%";
            BtnRestoreCpu.Visibility = MemoryService.ActiveThrottledProcessCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            if (ramTotalGb > 0)
            {
                string unit = isEn ? "GB" : "ГБ";
                double availGb = Math.Max(0, ramTotalGb - ramUsedGb);
                RamDetailTotal.Text = $"{ramTotalGb:0.00} {unit}";
                RamDetailUsed.Text = $"{ramUsedGb:0.00} {unit}";
                RamDetailAvail.Text = $"{availGb:0.00} {unit}";
            }
            else
            {
                RamDetailTotal.Text = "N/A";
                RamDetailUsed.Text = "N/A";
                RamDetailAvail.Text = "N/A";
            }

            if (extMem != null)
            {
                RamDetailCommit.Text = $"{StorageService.FormatBytes(extMem.CommitTotalBytes)} / {StorageService.FormatBytes(extMem.CommitLimitBytes)}";
                RamDetailCache.Text = StorageService.FormatBytes(extMem.SystemCacheBytes);
            }
        }
    }

    public void Log(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => Log(line));
            return;
        }

        const int MaxLines = 250;
        var existing = RamLogTextBox.Text;
        if (string.IsNullOrEmpty(existing))
        {
            RamLogTextBox.Text = line;
        }
        else
        {
            var lines = existing.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length >= MaxLines)
            {
                RamLogTextBox.Text = string.Join(Environment.NewLine, lines.Skip(lines.Length - MaxLines + 1)) + Environment.NewLine + line;
            }
            else
            {
                RamLogTextBox.AppendText(Environment.NewLine + line);
            }
        }
        RamLogTextBox.ScrollToEnd();
    }

    private void BtnFullPurge_Click(object sender, RoutedEventArgs e) => OnPurgeRequested?.Invoke();
    private void BtnPurgeStandby_Click(object sender, RoutedEventArgs e) => OnStandbyPurgeRequested?.Invoke();
    private void BtnRestoreCpu_Click(object sender, RoutedEventArgs e) => OnRestoreCpuRequested?.Invoke();
    private void BtnSwitchPurgeMode_Click(object sender, RoutedEventArgs e) => OnSwitchPurgeModeRequested?.Invoke();
    private void BtnOpenTaskManager_Click(object sender, RoutedEventArgs e) => OnOpenTaskManagerRequested?.Invoke();
}
