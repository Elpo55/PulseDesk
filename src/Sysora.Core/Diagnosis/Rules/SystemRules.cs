using Sysora.Core.Analysis;
using Sysora.Core.Formatting;
using Sysora.Core.History;
using Sysora.Core.Models;

namespace Sysora.Core.Diagnosis.Rules;

/// <summary>Is a disk busy almost all the time?</summary>
public sealed class DiskActivityRule : DiagnosisRule
{
    /// <summary>Active time at or above which a disk is considered saturated.</summary>
    public const double BusyPercent = 90;

    /// <summary>How long a disk must stay busy before it is reported.</summary>
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(30);

    public override string Id => "disk.activity";

    public override IEnumerable<DiagnosisResult> Evaluate(DiagnosisContext context)
    {
        if (context.Latest is not { DiskActivePercent: not null } latest
            || SnapshotStatistics.Summarize(context.Last(TimeSpan.FromMinutes(1)), s => s.DiskActivePercent) is not { } minute)
        {
            yield break;
        }

        var drive = latest.DiskActiveDrive ?? "disk";
        var isSystem = context.Snapshot.SystemDrive is { } system && string.Equals(system.Letter, drive, StringComparison.OrdinalIgnoreCase);
        var span = SnapshotStatistics.Sustained(context.Recent, s => s.DiskActivePercent, BusyPercent);
        var throughput = $"read {MetricFormatter.BytesPerSecond(latest.DiskReadBytesPerSecond)}, write {MetricFormatter.BytesPerSecond(latest.DiskWriteBytesPerSecond)}";

        if (span is { } busy && busy.Duration >= MinimumDuration)
        {
            var topIo = latest.TopApps.Where(a => a.IoBytesPerSecond > 0).MaxBy(a => a.IoBytesPerSecond);
            var memoryHigh = latest.MemoryPercent >= context.Thresholds.MemoryWarningPercent;
            var evidence = new List<AnalysisEvidence>
            {
                SpanEvidence($"Disk {drive} active time", busy, BusyPercent, MetricSources.Disk),
                new("Throughput (all volumes)", throughput) { Source = MetricSources.Disk, To = latest.Timestamp },
            };
            if (topIo is not null)
            {
                evidence.Add(new AnalysisEvidence("Most I/O", $"{topIo.Name}: {MetricFormatter.BytesPerSecond(topIo.IoBytesPerSecond)}")
                {
                    Reference = "Per-process I/O includes files, devices and network; it cannot be split by disk.",
                    Source = MetricSources.ProcessIo,
                });
            }

            var explanation = "When a disk is busy almost all the time, applications that need to read or write files must wait.";
            if (memoryHigh)
            {
                explanation += " Memory is also high: part of this activity may be Windows paging memory to disk (a possibility, not confirmed).";
            }

            yield return new DiagnosisResult
            {
                RuleId = Id,
                Category = DiagnosisCategory.Disk,
                Severity = busy.Average >= 98 && busy.Duration >= TimeSpan.FromMinutes(2) ? DiagnosisSeverity.Critical : DiagnosisSeverity.Warning,
                Title = isSystem ? "The system disk shows heavy activity" : $"Disk {drive} shows heavy activity",
                Description = $"Disk {drive} has been busy {Percent(busy.Average)} of the time for {Duration(busy.Duration)} ({throughput}).",
                Metric = "Disk active time",
                ObservedValue = Percent(busy.Average),
                ReferenceValue = $"Threshold {Percent(BusyPercent)}",
                Duration = busy.Duration,
                Timestamp = latest.Timestamp,
                Explanation = explanation,
                Recommendation = topIo is null
                    ? "Updates, antivirus scans and file indexing often cause this temporarily. App Impact shows which applications read and write the most."
                    : $"{topIo.Name} currently has the most I/O. Updates, antivirus scans and file indexing also cause this temporarily.",
                Confidence = busy.Duration >= MinimumDuration * 4 ? ConfidenceLevel.High : ConfidenceLevel.Medium,
                Evidence = evidence,
                AppKey = topIo?.Key,
                Action = DiagnosisAction.AppImpact,
            };
            yield break;
        }

        yield return new DiagnosisResult
        {
            RuleId = Id,
            Category = DiagnosisCategory.Disk,
            Severity = DiagnosisSeverity.Normal,
            Title = "Disk activity normal",
            Description = $"The busiest disk ({drive}) was active {Percent(minute.Average)} of the time over the last minute.",
            Metric = "Disk active time",
            ObservedValue = Percent(minute.Average),
            ReferenceValue = UsualText(context.Baseline.Get(HistoryMetric.Disk)) ?? $"Threshold {Percent(BusyPercent)}",
            Timestamp = latest.Timestamp,
            Explanation = "Disks have spare capacity.",
            Confidence = ConfidenceLevel.High,
            Evidence = [WindowEvidence("Busiest disk active time (last minute)", minute, MetricSources.Disk)],
        };
    }
}

/// <summary>Is the system disk running out of space?</summary>
public sealed class DiskSpaceRule : DiagnosisRule
{
    /// <summary>Below this free space, the system disk is reported whatever its size.</summary>
    public const ulong MinimumFreeBytes = 10UL * 1024 * 1024 * 1024;

    public override string Id => "storage.space";

    public override IEnumerable<DiagnosisResult> Evaluate(DiagnosisContext context)
    {
        if (context.Snapshot.SystemDrive is not { } drive)
        {
            yield break;
        }

        var thresholds = context.Thresholds;
        var used = Math.Round(drive.UsedPercent);
        var severity = used >= thresholds.DiskCriticalPercent ? DiagnosisSeverity.Critical
            : used >= thresholds.DiskWarningPercent || drive.FreeBytes < MinimumFreeBytes ? DiagnosisSeverity.Warning
            : DiagnosisSeverity.Normal;
        var evidence = new AnalysisEvidence($"Volume {drive.Letter}", $"{MetricFormatter.Bytes(drive.FreeBytes)} free of {MetricFormatter.Bytes(drive.TotalBytes)} ({Percent(drive.UsedPercent)} used)")
        {
            Reference = $"Warning {Percent(thresholds.DiskWarningPercent)}, critical {Percent(thresholds.DiskCriticalPercent)}, or less than {MetricFormatter.Bytes(MinimumFreeBytes)} free",
            Source = MetricSources.Storage,
            To = context.Snapshot.Timestamp,
        };

        yield return new DiagnosisResult
        {
            RuleId = Id,
            Category = DiagnosisCategory.Storage,
            Severity = severity,
            Title = severity switch
            {
                DiagnosisSeverity.Critical => "The system disk is almost full",
                DiagnosisSeverity.Warning => "Free space is low on the system disk",
                _ => "Enough free space on the system disk",
            },
            Description = $"{MetricFormatter.Bytes(drive.FreeBytes)} free on {drive.Letter} ({Percent(drive.UsedPercent)} used).",
            Metric = "System disk space",
            ObservedValue = Percent(drive.UsedPercent),
            ReferenceValue = $"Warning {Percent(thresholds.DiskWarningPercent)}",
            Timestamp = context.Snapshot.Timestamp,
            Explanation = severity == DiagnosisSeverity.Normal
                ? "Windows has room for updates, temporary files and the page file."
                : "Windows needs free space for updates, temporary files and the page file. A nearly full system disk can slow the PC down and make updates fail.",
            Recommendation = severity == DiagnosisSeverity.Normal
                ? null
                : "Remove files you no longer need, or use Windows Settings › System › Storage › Cleanup recommendations.",
            Confidence = ConfidenceLevel.High,
            Evidence = [evidence],
            Action = severity == DiagnosisSeverity.Normal ? DiagnosisAction.None : DiagnosisAction.Storage,
        };

        // Other fixed volumes do not slow Windows down when full, but are worth knowing about.
        foreach (var other in (context.Snapshot.Storage ?? [])
            .Where(v => v.Kind == Models.DriveKind.Fixed && !v.IsSystemDrive && Math.Round(v.UsedPercent) >= thresholds.DiskWarningPercent)
            .OrderBy(v => v.Letter, StringComparer.OrdinalIgnoreCase))
        {
            yield return new DiagnosisResult
            {
                RuleId = $"{Id}.{other.Letter}",
                Category = DiagnosisCategory.Storage,
                Severity = DiagnosisSeverity.Info,
                Title = $"Volume {other.Letter} is {Percent(other.UsedPercent)} full",
                Description = $"{MetricFormatter.Bytes(other.FreeBytes)} free on {other.Letter} ({Percent(other.UsedPercent)} used).",
                Metric = "Volume space",
                ObservedValue = Percent(other.UsedPercent),
                ReferenceValue = $"Warning {Percent(thresholds.DiskWarningPercent)}",
                Timestamp = context.Snapshot.Timestamp,
                Explanation = "This is not the system disk, so it does not slow Windows down, but applications saving data there may run out of space.",
                Confidence = ConfidenceLevel.High,
                Evidence = [new AnalysisEvidence($"Volume {other.Letter}", $"{MetricFormatter.Bytes(other.FreeBytes)} free of {MetricFormatter.Bytes(other.TotalBytes)}") { Source = MetricSources.Storage, To = context.Snapshot.Timestamp }],
                Action = DiagnosisAction.Storage,
            };
        }
    }
}

/// <summary>Is the current activity outside what this PC usually shows?</summary>
public sealed class UnusualActivityRule : DiagnosisRule
{
    /// <summary>Period compared with the baseline.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>Minimum recent data for a comparison.</summary>
    public static readonly TimeSpan MinimumData = TimeSpan.FromMinutes(5);

    private static readonly (HistoryMetric Metric, string Name, double Margin)[] Metrics =
    [
        (HistoryMetric.Cpu, "CPU", 20),
        (HistoryMetric.Memory, "Memory", 15),
        (HistoryMetric.Disk, "Disk activity", 20),
    ];

    public override string Id => "activity.unusual";

    public override IEnumerable<DiagnosisResult> Evaluate(DiagnosisContext context)
    {
        // Without enough history, Sysora does not claim to know what is usual.
        if (!context.Baseline.IsReady)
        {
            yield break;
        }

        var window = context.Last(Window);
        var unusual = new List<(string Name, WindowSummary Summary, MetricBaseline Usual)>();
        var evidence = new List<AnalysisEvidence>();
        foreach (var (metric, name, margin) in Metrics)
        {
            if (context.Baseline.Get(metric) is not { } usual
                || SnapshotStatistics.Summarize(window, s => s.Get(metric)) is not { } summary
                || summary.Duration < MinimumData)
            {
                continue;
            }

            evidence.Add(new AnalysisEvidence(name, $"{Percent(summary.Average)} over the last {Duration(summary.Duration)}")
            {
                Reference = Format($"Usual {usual.UsualRange}, 95% of minutes below {usual.P95:0}%"),
                From = summary.From,
                To = summary.To,
                SampleCount = summary.Count,
                Source = MetricSources.For(metric),
            });
            if (summary.Average > usual.P95 && summary.Average - usual.Median >= margin)
            {
                unusual.Add((name, summary, usual));
            }
        }

        if (evidence.Count == 0)
        {
            yield break;
        }

        var latest = context.Latest!;
        if (unusual.Count == 0)
        {
            yield return new DiagnosisResult
            {
                RuleId = Id,
                Category = DiagnosisCategory.System,
                Severity = DiagnosisSeverity.Normal,
                Title = "Activity within your usual range",
                Description = $"The last {MetricFormatter.DurationCompact(Window)} look like this PC's usual activity.",
                Metric = "Activity compared with usual",
                ObservedValue = "Usual",
                ReferenceValue = context.Baseline.Description,
                Timestamp = latest.Timestamp,
                Explanation = "Compared with per-minute averages of the last 7 days.",
                Confidence = ConfidenceLevel.Medium,
                Evidence = evidence,
            };
            yield break;
        }

        var parts = unusual.Select(u => $"{u.Name} {Percent(u.Summary.Average)} (usually {u.Usual.UsualRange})");
        yield return new DiagnosisResult
        {
            RuleId = Id,
            Category = DiagnosisCategory.System,
            Severity = unusual.Count >= 2 ? DiagnosisSeverity.Warning : DiagnosisSeverity.Info,
            Title = "Activity is unusual compared with your recent usage",
            Description = $"Over the last {MetricFormatter.DurationCompact(Window)}: {string.Join(", ", parts)}.",
            Metric = "Activity compared with usual",
            ObservedValue = string.Join(", ", unusual.Select(u => $"{u.Name} {Percent(u.Summary.Average)}")),
            ReferenceValue = string.Join(", ", unusual.Select(u => $"usually {u.Usual.UsualRange}")),
            Duration = unusual.Max(u => u.Summary.Duration),
            Timestamp = latest.Timestamp,
            Explanation = "These values are above what this PC shows in 95% of the minutes of the last 7 days: something different from usual is running.",
            Recommendation = "Use Replay to see when it started, and App Impact to see which applications are involved.",
            Confidence = ConfidenceLevel.Medium,
            Evidence = evidence,
            Action = DiagnosisAction.Replay,
        };
    }
}

/// <summary>Is the graphics processor fully used?</summary>
public sealed class GpuLoadRule : DiagnosisRule
{
    public const double BusyPercent = 90;

    public static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(30);

    public override string Id => "gpu.load";

    public override IEnumerable<DiagnosisResult> Evaluate(DiagnosisContext context)
    {
        if (context.Latest is not { GpuPercent: not null } latest
            || SnapshotStatistics.Summarize(context.Last(TimeSpan.FromMinutes(1)), s => s.GpuPercent) is not { } minute)
        {
            yield break;
        }

        var span = SnapshotStatistics.Sustained(context.Recent, s => s.GpuPercent, BusyPercent);
        if (span is { } busy && busy.Duration >= MinimumDuration)
        {
            yield return new DiagnosisResult
            {
                RuleId = Id,
                Category = DiagnosisCategory.Gpu,
                Severity = DiagnosisSeverity.Info,
                Title = "The GPU is fully used",
                Description = $"The busiest graphics engine has been above {Percent(BusyPercent)} for {Duration(busy.Duration)}.",
                Metric = "GPU usage",
                ObservedValue = Percent(busy.Average),
                ReferenceValue = $"Threshold {Percent(BusyPercent)}",
                Duration = busy.Duration,
                Timestamp = latest.Timestamp,
                Explanation = "Expected while gaming, rendering or playing high-resolution video. Otherwise an application is using the GPU heavily, which can make the display feel sluggish.",
                Confidence = ConfidenceLevel.Medium,
                Evidence = [SpanEvidence("GPU usage", busy, BusyPercent, MetricSources.Gpu)],
                Action = DiagnosisAction.Performance,
            };
            yield break;
        }

        yield return new DiagnosisResult
        {
            RuleId = Id,
            Category = DiagnosisCategory.Gpu,
            Severity = DiagnosisSeverity.Normal,
            Title = "GPU usage normal",
            Description = $"The GPU averaged {Percent(minute.Average)} over the last minute.",
            Metric = "GPU usage",
            ObservedValue = Percent(minute.Average),
            ReferenceValue = $"Threshold {Percent(BusyPercent)}",
            Timestamp = latest.Timestamp,
            Explanation = "The graphics processor has spare capacity.",
            Confidence = ConfidenceLevel.High,
            Evidence = [WindowEvidence("GPU usage (last minute)", minute, MetricSources.Gpu)],
        };
    }
}

/// <summary>Does Windows report a working Internet connection?</summary>
public sealed class ConnectivityRule : DiagnosisRule
{
    public override string Id => "network.connectivity";

    public override IEnumerable<DiagnosisResult> Evaluate(DiagnosisContext context)
    {
        if (context.Snapshot.Network is not { } network || network.Connectivity == NetworkConnectivity.Unknown)
        {
            yield break;
        }

        var (severity, title, description) = network.Connectivity switch
        {
            NetworkConnectivity.InternetAccess => (DiagnosisSeverity.Normal, "Internet access available", "Windows reports a working Internet connection."),
            NetworkConnectivity.ConstrainedInternetAccess => (DiagnosisSeverity.Info, "Limited Internet access", "Windows reports restricted Internet access (for example a sign-in page)."),
            NetworkConnectivity.LocalAccess => (DiagnosisSeverity.Warning, "No Internet access", "Windows reports a local network connection without Internet access."),
            _ => (DiagnosisSeverity.Warning, "No network connection", "Windows reports no network connection."),
        };

        yield return new DiagnosisResult
        {
            RuleId = Id,
            Category = DiagnosisCategory.Network,
            Severity = severity,
            Title = title,
            Description = description,
            Metric = "Connectivity",
            ObservedValue = network.Connectivity.ToString(),
            Timestamp = context.Snapshot.Timestamp,
            Explanation = severity == DiagnosisSeverity.Normal
                ? "Web pages and online applications can reach the Internet."
                : "Web pages and online applications will be slow or fail. This is a connection problem, not a performance problem of the PC.",
            Recommendation = severity == DiagnosisSeverity.Normal ? null : "Check the Wi-Fi or cable connection, or the router.",
            Confidence = ConfidenceLevel.High,
            Evidence = [new AnalysisEvidence("Connectivity", network.Connectivity.ToString()) { Source = MetricSources.Connectivity, To = context.Snapshot.Timestamp }],
            Action = severity == DiagnosisSeverity.Normal ? DiagnosisAction.None : DiagnosisAction.Network,
        };
    }
}

/// <summary>Has Windows been running for a long time without a restart?</summary>
public sealed class UptimeRule : DiagnosisRule
{
    /// <summary>Uptime from which a restart is suggested.</summary>
    public static readonly TimeSpan LongUptime = TimeSpan.FromDays(14);

    public override string Id => "system.uptime";

    public override IEnumerable<DiagnosisResult> Evaluate(DiagnosisContext context)
    {
        if (context.Snapshot.System is not { } system)
        {
            yield break;
        }

        var evidence = new AnalysisEvidence("Time since Windows started", MetricFormatter.DurationLong(system.Uptime))
        {
            Reference = "Includes time asleep. With Fast Startup, \"Shut down\" does not reset it; \"Restart\" does.",
            Source = MetricSources.Uptime,
        };
        var isLong = system.Uptime >= LongUptime;
        yield return new DiagnosisResult
        {
            RuleId = Id,
            Category = DiagnosisCategory.System,
            Severity = isLong ? DiagnosisSeverity.Info : DiagnosisSeverity.Normal,
            Title = isLong ? $"Windows has not restarted for {(int)system.Uptime.TotalDays} days" : "Recent restart",
            Description = $"Windows started {MetricFormatter.DurationCompact(system.Uptime)} ago.",
            Metric = "Uptime",
            ObservedValue = MetricFormatter.DurationCompact(system.Uptime),
            ReferenceValue = Format($"Suggested from {LongUptime.TotalDays:0} days"),
            Timestamp = context.Snapshot.Timestamp,
            Explanation = isLong
                ? "Some problems build up over time (drivers, applications slowly using more memory). A restart clears them and applies pending updates. This is a general suggestion, not a measured cause."
                : "No long-running session to worry about.",
            Recommendation = isLong ? "Restart Windows (use Restart, not Shut down) when convenient." : null,
            Confidence = ConfidenceLevel.Low,
            Evidence = [evidence],
        };
    }
}
