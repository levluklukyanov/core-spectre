using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CoreSpectre.Services;

public record ProcessItemInfo
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string WindowTitle { get; init; } = "";
    public long WorkingSetBytes { get; init; }
    public string FormattedRam { get; init; } = "";
    public double CpuPercent { get; init; }
    public string FormattedCpu { get; init; } = "";
    public int ThreadCount { get; init; }
    public string Priority { get; init; } = "Обычный";
    public bool IsCritical { get; init; }
    public bool HasWindow { get; init; }
}

public enum ProcessSortMode
{
    Cpu,
    Ram
}

public static class ProcessTrackerService
{
    private static readonly Dictionary<int, (TimeSpan TotalTime, long SampleTime, double CpuPercent)> _previousTimes = new();
    private static readonly object _lock = new();

    private static readonly HashSet<string> CriticalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "services",
        "lsass", "winlogon", "fontdrvhost", "dwm", "explorer", "corespectre",
        "svchost", "sihost", "audiodg", "spoolsv"
    };

    public static List<ProcessItemInfo> SampleProcesses(ProcessSortMode sortMode, string filter = "")
    {
        int processorCount = Math.Max(1, Environment.ProcessorCount);
        List<ProcessItemInfo> list;

        lock (_lock)
        {
            long now = Stopwatch.GetTimestamp();
            var processes = Process.GetProcesses();
            list = new List<ProcessItemInfo>(processes.Length);
            var activePids = new HashSet<int>(processes.Length);
            foreach (var proc in processes)
            {
                try
                {
                    int pid = proc.Id;
                    activePids.Add(pid);
                    string name = proc.ProcessName;
                    string windowTitle = "";
                    long workingSet = 0;
                    int threadCount = 1;
                    string priority = "Обычный";
                    bool isCritical = pid <= 4 || CriticalProcessNames.Contains(name);
                    double cpuPercent = 0.0;

                    try
                    {
                        workingSet = proc.WorkingSet64;
                        threadCount = proc.Threads.Count;
                        windowTitle = proc.MainWindowTitle;
                        priority = MapPriority(proc.BasePriority);

                        var totalTime = proc.TotalProcessorTime;
                        if (_previousTimes.TryGetValue(pid, out var prev) && totalTime >= prev.TotalTime)
                        {
                            double timeDiffMs = Stopwatch.GetElapsedTime(prev.SampleTime, now).TotalMilliseconds;
                            if (timeDiffMs > 100)
                            {
                                double cpuDiffMs = (totalTime - prev.TotalTime).TotalMilliseconds;
                                cpuPercent = Math.Clamp((cpuDiffMs / (timeDiffMs * processorCount)) * 100.0, 0.0, 100.0);
                                _previousTimes[pid] = (totalTime, now, cpuPercent);
                            }
                            else cpuPercent = prev.CpuPercent;
                        }
                        else _previousTimes[pid] = (totalTime, now, 0);
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Process sample skipped for {name} ({pid}): {ex.Message}");
                    }

                    if (!string.IsNullOrWhiteSpace(filter))
                    {
                        bool matchName = name.Contains(filter, StringComparison.OrdinalIgnoreCase);
                        bool matchPid = pid.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase);
                        bool matchTitle = !string.IsNullOrEmpty(windowTitle) && windowTitle.Contains(filter, StringComparison.OrdinalIgnoreCase);
                        if (!matchName && !matchPid && !matchTitle)
                            continue;
                    }

                    list.Add(new ProcessItemInfo
                    {
                        Pid = pid,
                        Name = name,
                        DisplayName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.exe",
                        WindowTitle = string.IsNullOrWhiteSpace(windowTitle) ? "-" : windowTitle,
                        WorkingSetBytes = workingSet,
                        FormattedRam = StorageService.FormatBytes(workingSet),
                        CpuPercent = Math.Round(cpuPercent, 1),
                        FormattedCpu = $"{cpuPercent:0.0}%",
                        ThreadCount = threadCount,
                        Priority = priority,
                        IsCritical = isCritical,
                        HasWindow = !string.IsNullOrWhiteSpace(windowTitle)
                    });
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                finally
                {
                    proc.Dispose();
                }
            }

            var deadPids = _previousTimes.Keys.Where(k => !activePids.Contains(k)).ToList();
            foreach (var d in deadPids)
            {
                _previousTimes.Remove(d);
            }
        }

        if (sortMode == ProcessSortMode.Cpu)
        {
            list.Sort((a, b) =>
            {
                int c = b.CpuPercent.CompareTo(a.CpuPercent);
                return c != 0 ? c : b.WorkingSetBytes.CompareTo(a.WorkingSetBytes);
            });
        }
        else
        {
            list.Sort((a, b) =>
            {
                int c = b.WorkingSetBytes.CompareTo(a.WorkingSetBytes);
                return c != 0 ? c : b.CpuPercent.CompareTo(a.CpuPercent);
            });
        }

        return list;
    }

    private static string MapPriority(int basePriority) => basePriority switch
    {
        >= 13 => "Высокий",
        >= 8 => "Обычный",
        >= 4 => "Низкий",
        _ => "Фоновый"
    };
}
