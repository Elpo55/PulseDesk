using Sysora.Core.Analysis;
using Sysora.Core.Diagnosis;
using Sysora.Core.Diagnosis.Rules;
using Sysora.Core.Formatting;
using Sysora.Core.History;
using Sysora.Core.Metrics;

namespace Sysora.Core.Alerts;

/// <summary>
/// An alert rule reports the conditions currently met. It never alerts on an instant value: every rule
/// requires a duration, a trend, or a comparison with the PC's usual behavior.
/// </summary>
public abstract class AlertRule
{
    public abstract string Id { get; }

    /// <summary>
    /// True for conditions describing a lasting state of the PC (low disk space) rather than an activity. An alert of such
    /// a rule still active when Sysora closed continues on the next start if the condition is still met, instead of
    /// being raised again as a new alert every time Sysora starts.
    /// </summary>
    public virtual bool IsPersistentState => false;

    /// <summary>Conditions currently met (empty when everything is fine).</summary>
    public abstract IEnumerable<AlertCondition> Evaluate(AlertContext context);

    protected static string Percent(double value) => MetricFormatter.Percent(value);

    protected static string Duration(TimeSpan duration) => MetricFormatter.DurationPrecise(duration);

    /// <summary>"Top application: chrome.exe (62%)" from the latest snapshot.</summary>
    protected static string? TopCpuApp(AlertContext context) =>
        context.Latest?.TopApps.MaxBy(a => a.CpuPercent) is { CpuPercent: > 1 } app
            ? $"Top application: {app.Name} ({MetricFormatter.Percent(app.CpuPercent)})"
            : null;

    protected static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>Points below the threshold at which an active condition still counts as ongoing.</summary>
    protected const double Hysteresis = 5;

    /// <summary>
    /// The period during which <paramref name="value"/> met the condition. A new condition needs
    /// <paramref name="threshold"/> for <paramref name="minimum"/>; an active one only needs to stay above
    /// <c>threshold − Hysteresis</c>.
    /// </summary>
    protected static SustainedSpan? Condition(AlertContext context, string key, Func<MetricSnapshot, double?> value, double threshold, TimeSpan minimum)
    {
        var active = context.IsActive(key);
        var span = SnapshotStatistics.Sustained(context.Recent, value, active ? threshold - Hysteresis : threshold);
        return span is { } s && (active || s.Duration >= minimum) ? s : null;
    }
}

/// <summary>CPU high for several minutes; more important when it is unusual for this PC.</summary>
public sealed class SustainedCpuAlertRule : AlertRule
{
    public override string Id => "cpu.sustained";

    public override IEnumerable<AlertCondition> Evaluate(AlertContext context)
    {
        var settings = context.Settings;
        if (Condition(context, Id, s => s.CpuPercent, settings.CpuPercent, TimeSpan.FromMinutes(settings.CpuMinutes)) is not { } span)
        {
            yield break;
        }

        var usual = context.Baseline.Get(HistoryMetric.Cpu);
        var unusual = usual is not null && span.Average > usual.P95 && span.Average - usual.Median >= 30;
        var usualForThisPc = usual is not null && span.Average <= usual.P95;
        var severity = unusual ? AlertSeverity.Critical : usualForThisPc ? AlertSeverity.Info : AlertSeverity.Warning;

        yield return new AlertCondition
        {
            Key = Id,
            Severity = severity,
            Title = unusual ? "CPU usage unusually high" : usualForThisPc ? "High CPU usage (usual for this PC)" : "Sustained high CPU usage",
            Metric = "CPU usage",
            Value = $"{Percent(span.Average)} on average (peak {Percent(span.Peak)})",
            Duration = span.Duration,
            Context = Join(usual is null ? "Usual level not known yet" : $"Usual level {usual.UsualRange}", TopCpuApp(context)),
            Explanation = unusual
                ? $"The CPU has stayed above {Percent(settings.CpuPercent)} for {Duration(span.Duration)}, far above this PC's usual {usual!.UsualRange}."
                : usualForThisPc
                    ? $"The CPU has stayed above {Percent(settings.CpuPercent)} for {Duration(span.Duration)}. This level is common on this PC, so it is reported for information."
                    : $"The CPU has stayed above {Percent(settings.CpuPercent)} for {Duration(span.Duration)}. Short spikes are ignored; this one lasted.",
            Recommendation = "Check App Impact to see which applications use the processor.",
            Evidence = [Evidence("CPU usage", span, settings.CpuPercent, MetricSources.Cpu)],
            Action = DiagnosisAction.AppImpact,
        };
    }

    internal static AnalysisEvidence Evidence(string metric, SustainedSpan span, double threshold, string source) =>
        new(metric, $"At or above {MetricFormatter.Percent(threshold)} for {MetricFormatter.DurationPrecise(span.Duration)} (average {MetricFormatter.Percent(span.Average)}, peak {MetricFormatter.Percent(span.Peak)})")
        {
            From = span.Since,
            To = span.Until,
            SampleCount = span.Samples,
            Source = source,
        };
}

/// <summary>Memory nearly full for several minutes.</summary>
public sealed class SustainedMemoryAlertRule : AlertRule
{
    public override string Id => "memory.sustained";

    public override IEnumerable<AlertCondition> Evaluate(AlertContext context)
    {
        var settings = context.Settings;
        if (Condition(context, Id, s => s.MemoryPercent, settings.MemoryPercent, TimeSpan.FromMinutes(settings.MemoryMinutes)) is not { } span)
        {
            yield break;
        }

        // Severity follows the current level (the last few minutes), not the average since the start.
        var latest = context.Latest!;
        var level = SnapshotStatistics.Summarize(
            context.Recent.Where(s => s.Timestamp >= latest.Timestamp - TimeSpan.FromMinutes(settings.MemoryMinutes)).ToArray(),
            s => s.MemoryPercent)?.Average ?? span.Average;
        var largest = latest.TopApps.MaxBy(a => a.MemoryBytes);
        yield return new AlertCondition
        {
            Key = Id,
            Severity = level >= 95 ? AlertSeverity.Critical : AlertSeverity.Warning,
            Title = "Memory nearly full",
            Metric = "Memory usage",
            Value = $"{Percent(span.Average)} on average (peak {Percent(span.Peak)})",
            Duration = span.Duration,
            Context = Join(
                context.Baseline.Get(HistoryMetric.Memory) is { } usual ? $"Usual level {usual.UsualRange}" : null,
                largest is null ? null : $"Largest application: {largest.Name} ({MetricFormatter.Bytes(largest.MemoryBytes)})"),
            Explanation = $"Memory usage has stayed above {Percent(settings.MemoryPercent)} for {Duration(span.Duration)}. Windows then moves data to the disk, which slows applications down.",
            Recommendation = "Close applications you are not using.",
            Evidence = [SustainedCpuAlertRule.Evidence("Memory usage", span, settings.MemoryPercent, MetricSources.Memory)],
            AppKey = largest?.Key,
            Action = DiagnosisAction.AppImpact,
        };
    }
}

/// <summary>Memory usage rising steadily for a long time (possible leak).</summary>
public sealed class MemoryGrowthAlertRule : AlertRule
{
    public override string Id => "memory.growth";

    public override IEnumerable<AlertCondition> Evaluate(AlertContext context)
    {
        var settings = context.Settings;
        var latest = context.Latest;
        if (latest is null)
        {
            yield break;
        }

        var window = context.Recent.Where(s => s.Timestamp >= latest.Timestamp - TimeSpan.FromMinutes(settings.MemoryGrowthMinutes)).ToArray();
        if (SnapshotStatistics.Summarize(window, s => s.MemoryPercent) is not { } summary
            || summary.Duration < TimeSpan.FromMinutes(settings.MemoryGrowthMinutes * 0.9))
        {
            yield break;
        }

        var required = context.IsActive(Id) ? settings.MemoryGrowthPoints / 2 : settings.MemoryGrowthPoints;
        var trend = SnapshotStatistics.Trend(window, s => s.MemoryPercent, required);
        var rise = trend.ChangePerMinute * summary.Duration.TotalMinutes;
        if (trend.Direction != TrendDirection.Rising || rise < required)
        {
            yield break;
        }

        var (app, growth) = MemoryGrowthRule.LargestGrowth(window);
        yield return new AlertCondition
        {
            Key = Id,
            Severity = AlertSeverity.Info,
            Title = "Memory usage rising steadily",
            Metric = "Memory usage",
            Value = $"+{rise:0} points ({Percent(summary.Minimum)} → {Percent(latest.MemoryPercent ?? summary.Peak)})",
            Duration = summary.Duration,
            Context = app is null ? "No single application stands out" : $"{app.Name} grew by {MetricFormatter.Bytes(growth)}",
            Explanation = $"Memory usage has been rising for {Duration(summary.Duration)}. This can be normal (data being loaded) or an application not releasing memory.",
            Recommendation = app is null ? null : $"If {app.Name} keeps growing, restarting it frees the memory.",
            Evidence =
            [
                new AnalysisEvidence("Memory usage trend", $"+{rise:0.#} points in {Duration(summary.Duration)}")
                {
                    Reference = "Robust trend (Theil–Sen), insensitive to isolated spikes",
                    From = summary.From,
                    To = summary.To,
                    SampleCount = summary.Count,
                    Source = MetricSources.Memory,
                },
            ],
            AppKey = app?.Key,
            Action = app is null ? DiagnosisAction.Replay : DiagnosisAction.AppImpact,
        };
    }
}

/// <summary>One application using a large share of the CPU for several minutes.</summary>
public sealed class AppCpuAlertRule : AlertRule
{
    public override string Id => "app.cpu";

    public override IEnumerable<AlertCondition> Evaluate(AlertContext context)
    {
        if (context.Latest is not { } latest)
        {
            yield break;
        }

        var settings = context.Settings;
        foreach (var app in latest.TopApps.Where(a => a.CpuPercent >= settings.AppCpuPercent - Hysteresis))
        {
            var key = $"{Id}:{app.Key}";
            var active = context.IsActive(key);
            var series = SnapshotStatistics.AppSeries(context.Recent, app.Key);
            if (CpuHungryAppRule.SustainedApp(series, active ? settings.AppCpuPercent - Hysteresis : settings.AppCpuPercent) is not { } span
                || (!active && span.Duration < TimeSpan.FromMinutes(settings.AppCpuMinutes)))
            {
                continue;
            }

            yield return new AlertCondition
            {
                Key = key,
                Severity = AlertSeverity.Warning,
                Title = $"{app.Name} has used a lot of CPU for {MetricFormatter.DurationCompact(span.Duration)}",
                Metric = "Application CPU usage",
                Value = $"{Percent(span.Average)} of total CPU on average (peak {Percent(span.Peak)})",
                Duration = span.Duration,
                Context = $"Total CPU now {MetricFormatter.Percent(latest.CpuPercent)}",
                Explanation = $"{app.Name} has stayed above {Percent(settings.AppCpuPercent)} of total CPU capacity for {Duration(span.Duration)}. It may be working normally (a build, an export) or be stuck.",
                Recommendation = $"If you don't need {app.Name} right now, close it or wait for its task to finish.",
                Evidence = [SustainedCpuAlertRule.Evidence($"{app.Name} CPU", span, settings.AppCpuPercent, MetricSources.Processes)],
                AppKey = app.Key,
                Action = DiagnosisAction.AppImpact,
            };
        }
    }
}

/// <summary>A disk busy almost all the time for several minutes.</summary>
public sealed class DiskBusyAlertRule : AlertRule
{
    public override string Id => "disk.busy";

    public override IEnumerable<AlertCondition> Evaluate(AlertContext context)
    {
        var settings = context.Settings;
        if (Condition(context, Id, s => s.DiskActivePercent, settings.DiskActivePercent, TimeSpan.FromMinutes(settings.DiskMinutes)) is not { } span)
        {
            yield break;
        }

        var latest = context.Latest!;
        var topIo = latest.TopApps.Where(a => a.IoBytesPerSecond > 0).MaxBy(a => a.IoBytesPerSecond);
        yield return new AlertCondition
        {
            Key = Id,
            Severity = AlertSeverity.Warning,
            Title = $"Disk {latest.DiskActiveDrive ?? string.Empty} busy for {MetricFormatter.DurationCompact(span.Duration)}".Replace("  ", " ", StringComparison.Ordinal),
            Metric = "Disk active time",
            Value = $"{Percent(span.Average)} on average",
            Duration = span.Duration,
            Context = Join(
                $"Read {MetricFormatter.BytesPerSecond(latest.DiskReadBytesPerSecond)}, write {MetricFormatter.BytesPerSecond(latest.DiskWriteBytesPerSecond)}",
                topIo is null ? null : $"Most I/O: {topIo.Name}"),
            Explanation = $"The disk has been active more than {Percent(settings.DiskActivePercent)} of the time for {Duration(span.Duration)}: applications reading or writing files have to wait.",
            Recommendation = "Updates, antivirus scans and indexing often cause this temporarily.",
            Evidence = [SustainedCpuAlertRule.Evidence("Disk active time", span, settings.DiskActivePercent, MetricSources.Disk)],
            AppKey = topIo?.Key,
            Action = DiagnosisAction.AppImpact,
        };
    }
}

/// <summary>CPU, memory or disk above the PC's usual range for several minutes (needs the baseline).</summary>
public sealed class UnusualActivityAlertRule : AlertRule
{
    private static readonly (HistoryMetric Metric, string Name, double Margin)[] Metrics =
    [
        (HistoryMetric.Cpu, "CPU usage", 20),
        (HistoryMetric.Memory, "Memory usage", 10),
        (HistoryMetric.Disk, "Disk activity", 20),
    ];

    public override string Id => "unusual";

    public override IEnumerable<AlertCondition> Evaluate(AlertContext context)
    {
        var settings = context.Settings;
        if (!settings.UnusualActivity || !context.Baseline.IsReady)
        {
            yield break;
        }

        foreach (var (metric, name, margin) in Metrics)
        {
            if (context.Baseline.Get(metric) is not { } usual)
            {
                continue;
            }

            // "Above the baseline" means above what 95% of the usual minutes show, with a margin so that a
            // value barely above the usual range is not reported.
            var threshold = Math.Max(usual.P95, usual.Median + margin);
            var key = $"{Id}.{metric.ToString().ToLowerInvariant()}";
            if (Condition(context, key, s => s.Get(metric), threshold, TimeSpan.FromMinutes(settings.UnusualMinutes)) is not { } span)
            {
                continue;
            }

            // Above the absolute threshold, the sustained rule of the same metric already reports it (and
            // escalates it when unusual): one condition, one alert.
            var absolute = metric switch
            {
                HistoryMetric.Cpu => settings.CpuPercent,
                HistoryMetric.Memory => settings.MemoryPercent,
                _ => settings.DiskActivePercent,
            };
            if (span.Average >= absolute)
            {
                continue;
            }

            yield return new AlertCondition
            {
                Key = key,
                Severity = span.Average - usual.Median >= 2 * margin ? AlertSeverity.Warning : AlertSeverity.Info,
                Title = $"{name} unusually high",
                Metric = name,
                Value = $"{Percent(span.Average)} on average",
                Duration = span.Duration,
                Context = $"Usual level {usual.UsualRange} (median {Percent(usual.Median)})",
                Explanation = $"{name} has remained above your recent baseline for {Duration(span.Duration)}.",
                Recommendation = "Use Replay to see when it started.",
                Evidence =
                [
                    SustainedCpuAlertRule.Evidence(name, span, threshold, MetricSources.For(metric)),
                    new AnalysisEvidence("Baseline", $"Usual {usual.UsualRange}, 95% of minutes below {Percent(usual.P95)}")
                    {
                        From = context.Baseline.From,
                        To = context.Baseline.To,
                        SampleCount = usual.Minutes,
                        Source = "Per-minute averages of the local history (last 7 days)",
                    },
                ],
                Action = DiagnosisAction.Replay,
            };
        }
    }
}

/// <summary>Little free space left on the system disk.</summary>
public sealed class LowDiskSpaceAlertRule : AlertRule
{
    public override string Id => "storage.low";

    public override bool IsPersistentState => true;

    public override IEnumerable<AlertCondition> Evaluate(AlertContext context)
    {
        if (context.Snapshot.SystemDrive is not { TotalBytes: > 0 } drive)
        {
            yield break;
        }

        var freePercent = drive.FreeBytes * 100.0 / drive.TotalBytes;
        var limit = context.Settings.LowDiskFreePercent;
        var key = $"{Id}:{drive.Letter}";
        if (freePercent >= (context.IsActive(key) ? limit + 1 : limit))
        {
            yield break;
        }

        yield return new AlertCondition
        {
            Key = key,
            Severity = freePercent < limit / 2 ? AlertSeverity.Critical : AlertSeverity.Warning,
            Title = $"Low free space on {drive.Letter}",
            Metric = "Free space",
            Value = $"{MetricFormatter.Bytes(drive.FreeBytes)} free ({freePercent:0.#}%)",
            Context = $"{MetricFormatter.Bytes(drive.TotalBytes)} volume",
            Explanation = "Windows needs free space on the system disk for updates, temporary files and the page file.",
            Recommendation = "Remove files you no longer need, or use Windows Settings › System › Storage.",
            Evidence = [new AnalysisEvidence($"Volume {drive.Letter}", $"{MetricFormatter.Bytes(drive.FreeBytes)} free of {MetricFormatter.Bytes(drive.TotalBytes)}") { Source = MetricSources.Storage, Reference = $"Alert below {limit:0.#}% free" }],
            Action = DiagnosisAction.Storage,
        };
    }
}
