using System.Globalization;
using Sysora.Core.Formatting;
using Sysora.Core.Models;
using Sysora.Core.Settings;
using Sysora.Localization;

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
            indicators.Add(new(HealthCategory.Cpu, HealthStatus.Unknown, Strings.Indicator_CpuNotAvailable));
        }
        else if (snapshot.Cpu is not null)
        {
            indicators.Add(Sustained(
                HealthCategory.Cpu, _cpuCritical, _cpuWarning, _settings.CpuSustainSeconds,
                normal: Strings.Indicator_CpuNormal, elevated: Strings.Diag_CpuLoad_HighTitle));
        }
    }

    private void AddMemory(List<HealthIndicator> indicators, SystemSnapshot snapshot)
    {
        if (snapshot.IsUnavailable(MetricKind.Memory))
        {
            indicators.Add(new(HealthCategory.Memory, HealthStatus.Unknown, Strings.Indicator_MemoryNotAvailable));
        }
        else if (snapshot.Memory is not null)
        {
            indicators.Add(Sustained(
                HealthCategory.Memory, _memoryCritical, _memoryWarning, _settings.MemorySustainSeconds,
                normal: Strings.Indicator_MemoryNormal, elevated: Strings.Recurring_HighMemory));
        }
    }

    private void AddStorage(List<HealthIndicator> indicators, SystemSnapshot snapshot)
    {
        if (snapshot.Storage is not { } drives)
        {
            if (snapshot.IsUnavailable(MetricKind.Storage))
            {
                indicators.Add(new(HealthCategory.Storage, HealthStatus.Unknown, Strings.Indicator_StorageNotAvailable));
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
                Text.Format(Strings.Indicator_Storage, drive.Letter, MetricFormatter.Percent(drive.UsedPercent)),
                Text.Format(
                    status == HealthStatus.Critical
                        ? Strings.Indicator_AboveCritical
                        : Strings.Indicator_AboveWarning,
                    MetricFormatter.Percent(threshold))));
            reported = true;
        }

        if (!reported)
        {
            indicators.Add(new(HealthCategory.Storage, HealthStatus.Normal, Strings.Indicator_StorageNormal));
        }
    }

    private static void AddNetwork(List<HealthIndicator> indicators, SystemSnapshot snapshot)
    {
        if (snapshot.Network is not { } network)
        {
            if (snapshot.IsUnavailable(MetricKind.Network))
            {
                indicators.Add(new(HealthCategory.Network, HealthStatus.Unknown, Strings.Indicator_NetworkNotAvailable));
            }

            return;
        }

        indicators.Add(network.Connectivity switch
        {
            NetworkConnectivity.InternetAccess => new(HealthCategory.Network, HealthStatus.Normal, Strings.Indicator_NetworkConnected),
            NetworkConnectivity.ConstrainedInternetAccess => new(HealthCategory.Network, HealthStatus.Warning, Strings.Diag_Net_LimitedTitle, Strings.Indicator_NetworkRestricted),
            NetworkConnectivity.LocalAccess => new(HealthCategory.Network, HealthStatus.Warning, Strings.Indicator_NetworkNoInternet),
            NetworkConnectivity.None => new(HealthCategory.Network, HealthStatus.Warning, Strings.Diag_Net_NoneTitle),
            _ => new(HealthCategory.Network, HealthStatus.Unknown, Strings.Indicator_NetworkUnknown),
        });
    }

    private void AddProcesses(List<HealthIndicator> indicators, SystemSnapshot snapshot)
    {
        if (snapshot.Processes is null)
        {
            if (snapshot.IsUnavailable(MetricKind.Processes))
            {
                indicators.Add(new(HealthCategory.Processes, HealthStatus.Unknown, Strings.Indicator_ProcessesNotAvailable));
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
                    Text.Format(Strings.Indicator_AppMemory, heaviest.Name, MetricFormatter.Percent(share)),
                    Text.Format(Strings.Indicator_AppMemoryThreshold, MetricFormatter.Percent(_settings.ProcessMemoryWarningPercent))));
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
                Text.Format(Strings.Indicator_AppCpu, busiest.Name),
                AboveFor(_settings.ProcessCpuWarningPercent, _settings.CpuSustainSeconds)));
            reported = true;
        }

        if (!reported)
        {
            indicators.Add(new(HealthCategory.Processes, HealthStatus.Normal, Strings.Indicator_NoAbnormalProcess));
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

    /// <summary>"Above 80% for more than 30 s".</summary>
    private static string AboveFor(double threshold, int seconds) =>
        Text.Format(Strings.Indicator_AboveFor, MetricFormatter.Percent(threshold), seconds);

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
            return new(category, HealthStatus.Critical, elevated, AboveFor(critical.Threshold, seconds));
        }

        return warning.IsActive
            ? new(category, HealthStatus.Warning, elevated, AboveFor(warning.Threshold, seconds))
            : new(category, HealthStatus.Normal, normal);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
