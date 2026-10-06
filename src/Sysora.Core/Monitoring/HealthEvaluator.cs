using System.Globalization;
using Sysora.Core.Models;
using Sysora.Core.Settings;

namespace Sysora.Core.Monitoring;

/// <summary>
/// Turns snapshots into health indicators using the user's thresholds. Stateful: CPU, memory and
/// per-application CPU conditions must hold for a configurable time before being reported.
/// </summary>
/// <remarks>Not thread-safe; feed it from one thread (see <see cref="HealthService"/>).</remarks>
public sealed class HealthEvaluator
{
    /// <summary>System components whose memory use is not attributable to an application.</summary>
    private static readonly HashSet<string> MemoryRuleExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Memory Compression", "Secure System",
    };

    private readonly Dictionary<string, SustainedThresholdDetector> _processCpu = new(StringComparer.OrdinalIgnoreCase);
    private AlertSettings _settings = new();
    private SustainedThresholdDetector _cpuWarning = null!;
    private SustainedThresholdDetector _cpuCritical = null!;
    private SustainedThresholdDetector _memoryWarning = null!;
    private SustainedThresholdDetector _memoryCritical = null!;
    private IReadOnlyList<ProcessGroup> _groups = [];

    public HealthEvaluator(AlertSettings settings) => Configure(settings);

    /// <summary>Applies new thresholds. Ongoing conditions restart from scratch.</summary>
    public void Configure(AlertSettings settings)
    {
        _settings = settings;
        var cpuDuration = TimeSpan.FromSeconds(settings.CpuSustainSeconds);
        var memoryDuration = TimeSpan.FromSeconds(settings.MemorySustainSeconds);
        _cpuWarning = new SustainedThresholdDetector(settings.CpuWarningPercent, cpuDuration);
        _cpuCritical = new SustainedThresholdDetector(settings.CpuCriticalPercent, cpuDuration);
        _memoryWarning = new SustainedThresholdDetector(settings.MemoryWarningPercent, memoryDuration, hysteresis: 2);
        _memoryCritical = new SustainedThresholdDetector(settings.MemoryCriticalPercent, memoryDuration, hysteresis: 2);
        _processCpu.Clear();
    }

    /// <summary>Updates detectors with the metrics refreshed in <paramref name="updated"/> and builds a report.</summary>
    public HealthReport Evaluate(SystemSnapshot snapshot, MetricKind updated)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var timestamp = snapshot.Timestamp;

        if ((updated & MetricKind.Cpu) != 0 && snapshot.Cpu is { } cpu)
        {
            _cpuWarning.Update(cpu.UsagePercent, timestamp);
            _cpuCritical.Update(cpu.UsagePercent, timestamp);
        }

        if ((updated & MetricKind.Memory) != 0 && snapshot.Memory is { } memory)
        {
            _memoryWarning.Update(memory.UsedPercent, timestamp);
            _memoryCritical.Update(memory.UsedPercent, timestamp);
        }

        if ((updated & MetricKind.Processes) != 0 && snapshot.Processes is { } processes)
        {
            _groups = ProcessAggregation.GroupByName(processes.Processes);
            UpdateProcessDetectors(timestamp);
        }

        var indicators = new List<HealthIndicator>(6);
        AddCpu(indicators, snapshot);
        AddMemory(indicators, snapshot);
        AddStorage(indicators, snapshot);
        AddNetwork(indicators, snapshot);
        AddProcesses(indicators, snapshot);
        return new HealthReport(timestamp, indicators);
    }

    private void AddCpu(List<HealthIndicator> indicators, SystemSnapshot snapshot)
    {
        if (snapshot.IsUnavailable(MetricKind.Cpu))
        {
            indicators.Add(new(HealthCategory.Cpu, HealthStatus.Unknown, "CPU usage not available"));
        }
        else if (snapshot.Cpu is not null)
        {
            indicators.Add(Sustained(
                HealthCategory.Cpu, _cpuCritical, _cpuWarning, _settings.CpuSustainSeconds,
                normal: "CPU normal", elevated: "High CPU usage"));
        }
    }

    private void AddMemory(List<HealthIndicator> indicators, SystemSnapshot snapshot)
    {
        if (snapshot.IsUnavailable(MetricKind.Memory))
        {
            indicators.Add(new(HealthCategory.Memory, HealthStatus.Unknown, "Memory usage not available"));
        }
        else if (snapshot.Memory is not null)
        {
            indicators.Add(Sustained(
                HealthCategory.Memory, _memoryCritical, _memoryWarning, _settings.MemorySustainSeconds,
                normal: "Memory normal", elevated: "High memory usage"));
        }
    }

    private void AddStorage(List<HealthIndicator> indicators, SystemSnapshot snapshot)
    {
        if (snapshot.Storage is not { } drives)
        {
            if (snapshot.IsUnavailable(MetricKind.Storage))
            {
                indicators.Add(new(HealthCategory.Storage, HealthStatus.Unknown, "Storage not available"));
            }

            return;
        }

        var reported = false;
        foreach (var drive in drives.Where(d => d.Kind == DriveKind.Fixed).OrderBy(d => d.Drive, StringComparer.OrdinalIgnoreCase))
        {
            // Compare at display precision so "85%" on screen and an 85% threshold agree.
            var used = Math.Round(drive.UsedPercent);
            var status = used >= _settings.DiskCriticalPercent ? HealthStatus.Critical
                : used >= _settings.DiskWarningPercent ? HealthStatus.Warning
                : HealthStatus.Normal;
            if (status == HealthStatus.Normal)
            {
                continue;
            }

            var threshold = status == HealthStatus.Critical ? _settings.DiskCriticalPercent : _settings.DiskWarningPercent;
            indicators.Add(new(
                HealthCategory.Storage,
                status,
                Invariant($"Storage {drive.Letter} {drive.UsedPercent:0}%"),
                Invariant($"Above the {threshold:0}% {(status == HealthStatus.Critical ? "critical" : "warning")} threshold")));
            reported = true;
        }

        if (!reported)
        {
            indicators.Add(new(HealthCategory.Storage, HealthStatus.Normal, "Storage normal"));
        }
    }

    private static void AddNetwork(List<HealthIndicator> indicators, SystemSnapshot snapshot)
    {
        if (snapshot.Network is not { } network)
        {
            if (snapshot.IsUnavailable(MetricKind.Network))
            {
                indicators.Add(new(HealthCategory.Network, HealthStatus.Unknown, "Network status not available"));
            }

            return;
        }

        indicators.Add(network.Connectivity switch
        {
            NetworkConnectivity.InternetAccess => new(HealthCategory.Network, HealthStatus.Normal, "Network connected"),
            NetworkConnectivity.ConstrainedInternetAccess => new(HealthCategory.Network, HealthStatus.Warning, "Limited Internet access", "Windows reports restricted access (for example a sign-in page)"),
            NetworkConnectivity.LocalAccess => new(HealthCategory.Network, HealthStatus.Warning, "Network available but Internet unavailable"),
            NetworkConnectivity.None => new(HealthCategory.Network, HealthStatus.Warning, "No network connection"),
            _ => new(HealthCategory.Network, HealthStatus.Unknown, "Network status unknown"),
        });
    }

    private void AddProcesses(List<HealthIndicator> indicators, SystemSnapshot snapshot)
    {
        if (snapshot.Processes is null)
        {
            if (snapshot.IsUnavailable(MetricKind.Processes))
            {
                indicators.Add(new(HealthCategory.Processes, HealthStatus.Unknown, "Process information not available"));
            }

            return;
        }

        var reported = false;

        if (snapshot.Memory is { TotalBytes: > 0 } memory)
        {
            var heaviest = _groups
                .Where(g => !MemoryRuleExclusions.Contains(g.Name))
                .MaxBy(g => g.PrivateWorkingSetBytes);
            var share = heaviest is null ? 0 : heaviest.PrivateWorkingSetBytes * 100.0 / memory.TotalBytes;
            if (heaviest is not null && share >= _settings.ProcessMemoryWarningPercent)
            {
                indicators.Add(new(
                    HealthCategory.Processes,
                    HealthStatus.Warning,
                    Invariant($"{heaviest.Name} uses {share:0}% of memory"),
                    Invariant($"Above the {_settings.ProcessMemoryWarningPercent:0}% per-application threshold")));
                reported = true;
            }
        }

        var busiest = _groups
            .Where(g => _processCpu.TryGetValue(g.Name, out var detector) && detector.IsActive)
            .MaxBy(g => g.CpuPercent);
        if (busiest is not null)
        {
            indicators.Add(new(
                HealthCategory.Processes,
                HealthStatus.Warning,
                Invariant($"{busiest.Name} uses a lot of CPU"),
                Invariant($"Above {_settings.ProcessCpuWarningPercent:0}% for more than {_settings.CpuSustainSeconds} s")));
            reported = true;
        }

        if (!reported)
        {
            indicators.Add(new(HealthCategory.Processes, HealthStatus.Normal, "No abnormal process detected"));
        }
    }

    private void UpdateProcessDetectors(DateTimeOffset timestamp)
    {
        var duration = TimeSpan.FromSeconds(_settings.CpuSustainSeconds);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _groups)
        {
            seen.Add(group.Name);
            if (!_processCpu.TryGetValue(group.Name, out var detector))
            {
                // Only track applications that are currently busy, to keep the dictionary small.
                if (group.CpuPercent < _settings.ProcessCpuWarningPercent)
                {
                    continue;
                }

                detector = new SustainedThresholdDetector(_settings.ProcessCpuWarningPercent, duration);
                _processCpu[group.Name] = detector;
            }

            detector.Update(group.CpuPercent, timestamp);
        }

        // Forget applications that exited or calmed down.
        foreach (var name in _processCpu.Keys.ToArray())
        {
            if (!seen.Contains(name) || _processCpu[name] is { IsActive: false, AboveSince: null })
            {
                _processCpu.Remove(name);
            }
        }
    }

    private static HealthIndicator Sustained(
        HealthCategory category,
        SustainedThresholdDetector critical,
        SustainedThresholdDetector warning,
        int seconds,
        string normal,
        string elevated)
    {
        if (critical.IsActive)
        {
            return new(category, HealthStatus.Critical, elevated, Invariant($"Above {critical.Threshold:0}% for more than {seconds} s"));
        }

        return warning.IsActive
            ? new(category, HealthStatus.Warning, elevated, Invariant($"Above {warning.Threshold:0}% for more than {seconds} s"))
            : new(category, HealthStatus.Normal, normal);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
