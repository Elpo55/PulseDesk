using Sysora.Core.Analysis;
using Sysora.Core.Formatting;
using Sysora.Core.History;
using Sysora.Core.Models;
using Sysora.Localization;

using static Sysora.Core.Diagnosis.Rules.ConnectivityText;
using static Sysora.Core.Diagnosis.Rules.VolumeText;

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

        var drive = latest.DiskActiveDrive ?? Strings.Diag_Disk_Unnamed;
        var isSystem = context.Snapshot.SystemDrive is { } system && string.Equals(system.Letter, drive, StringComparison.OrdinalIgnoreCase);
        var span = SnapshotStatistics.Sustained(context.Recent, s => s.DiskActivePercent, BusyPercent);
        var throughput = Text.Format(Strings.Diag_Disk_Throughput, MetricFormatter.BytesPerSecond(latest.DiskReadBytesPerSecond), MetricFormatter.BytesPerSecond(latest.DiskWriteBytesPerSecond));

        if (span is { } busy && busy.Duration >= MinimumDuration)
        {
            var topIo = latest.TopApps.Where(a => a.IoBytesPerSecond > 0).MaxBy(a => a.IoBytesPerSecond);
            var memoryHigh = latest.MemoryPercent >= context.Thresholds.MemoryWarningPercent;
            var evidence = new List<AnalysisEvidence>
            {
                SpanEvidence(Text.Format(Strings.Diag_Disk_ActiveTimeOf, drive), busy, BusyPercent, MetricSources.Disk),
                new(Strings.Diag_Disk_ThroughputAll, throughput) { Source = MetricSources.Disk, To = latest.Timestamp },
            };
            if (topIo is not null)
            {
                evidence.Add(new AnalysisEvidence(Strings.Diag_Disk_MostIo, Text.Format(Strings.Common_NameValue, topIo.Name, MetricFormatter.BytesPerSecond(topIo.IoBytesPerSecond)))
                {
                    Reference = Strings.Diag_Disk_IoReference,
                    Source = MetricSources.ProcessIo,
                });
            }

            var explanation = Strings.Diag_Disk_Explanation;
            if (memoryHigh)
            {
                explanation += " " + Strings.Diag_Disk_ExplanationPaging;
            }

            yield return new DiagnosisResult
            {
                RuleId = Id,
                Category = DiagnosisCategory.Disk,
                Severity = busy.Average >= 98 && busy.Duration >= TimeSpan.FromMinutes(2) ? DiagnosisSeverity.Critical : DiagnosisSeverity.Warning,
                Title = isSystem
                    ? Strings.Diag_Disk_SystemHeavyTitle
                    : Text.Format(Strings.Diag_Disk_HeavyTitle, drive),
                Description = Text.Format(Strings.Diag_Disk_HeavyDescription, drive, Percent(busy.Average), Duration(busy.Duration), throughput),
                Metric = Strings.Diag_Metric_DiskActive,
                ObservedValue = Percent(busy.Average),
                ReferenceValue = Threshold(Percent(BusyPercent)),
                Duration = busy.Duration,
                Timestamp = latest.Timestamp,
                Explanation = explanation,
                Recommendation = topIo is null
                    ? Strings.Diag_Disk_Recommendation
                    : Text.Format(Strings.Diag_Disk_RecommendationApp, topIo.Name),
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
            Title = Strings.Diag_Disk_NormalTitle,
            Description = Text.Format(Strings.Diag_Disk_NormalDescription, drive, Percent(minute.Average)),
            Metric = Strings.Diag_Metric_DiskActive,
            ObservedValue = Percent(minute.Average),
            ReferenceValue = UsualText(context.Baseline.Get(HistoryMetric.Disk)) ?? Threshold(Percent(BusyPercent)),
            Timestamp = latest.Timestamp,
            Explanation = Strings.Diag_Disk_NormalExplanation,
            Confidence = ConfidenceLevel.High,
            Evidence = [WindowEvidence(Strings.Diag_Disk_BusiestLastMinute, minute, MetricSources.Disk)],
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
        var evidence = new AnalysisEvidence(Volume(drive.Letter), Text.Format(Strings.Diag_Space_FreeOfUsed, MetricFormatter.Bytes(drive.FreeBytes), MetricFormatter.Bytes(drive.TotalBytes), Percent(drive.UsedPercent)))
        {
            Reference = Text.Format(Strings.Diag_Space_Reference, Percent(thresholds.DiskWarningPercent), Percent(thresholds.DiskCriticalPercent), MetricFormatter.Bytes(MinimumFreeBytes)),
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
                DiagnosisSeverity.Critical => Strings.Diag_Space_FullTitle,
                DiagnosisSeverity.Warning => Strings.Diag_Space_LowTitle,
                _ => Strings.Diag_Space_EnoughTitle,
            },
            Description = Text.Format(Strings.Diag_Space_FreeOn, MetricFormatter.Bytes(drive.FreeBytes), drive.Letter, Percent(drive.UsedPercent)),
            Metric = Strings.Diag_Metric_SystemDiskSpace,
            ObservedValue = Percent(drive.UsedPercent),
            ReferenceValue = Warning(Percent(thresholds.DiskWarningPercent)),
            Timestamp = context.Snapshot.Timestamp,
            Explanation = severity == DiagnosisSeverity.Normal
                ? Strings.Diag_Space_EnoughExplanation
                : Strings.Diag_Space_LowExplanation,
            Recommendation = severity == DiagnosisSeverity.Normal
                ? null
                : Strings.Diag_Space_Recommendation,
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
                Title = Text.Format(Strings.Diag_Space_VolumeFullTitle, other.Letter, Percent(other.UsedPercent)),
                Description = Text.Format(Strings.Diag_Space_FreeOn, MetricFormatter.Bytes(other.FreeBytes), other.Letter, Percent(other.UsedPercent)),
                Metric = Strings.Diag_Metric_VolumeSpace,
                ObservedValue = Percent(other.UsedPercent),
                ReferenceValue = Warning(Percent(thresholds.DiskWarningPercent)),
                Timestamp = context.Snapshot.Timestamp,
                Explanation = Strings.Diag_Space_VolumeExplanation,
                Confidence = ConfidenceLevel.High,
                Evidence = [new AnalysisEvidence(Volume(other.Letter), Text.Format(Strings.Diag_Space_FreeOf, MetricFormatter.Bytes(other.FreeBytes), MetricFormatter.Bytes(other.TotalBytes))) { Source = MetricSources.Storage, To = context.Snapshot.Timestamp }],
                Action = DiagnosisAction.Storage,
            };
        }
    }
}

/// <summary>Is the current activity outside what this PC usually shows?</summary>
internal static class VolumeText
{
    public static string Volume(string letter) => Text.Format(Strings.Diag_Volume, letter);

    public static string Warning(string value) => Text.Format(Strings.Diag_Warning, value);
}

public sealed class UnusualActivityRule : DiagnosisRule
{
    /// <summary>Period compared with the baseline.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>Minimum recent data for a comparison.</summary>
    public static readonly TimeSpan MinimumData = TimeSpan.FromMinutes(5);

    private static readonly (HistoryMetric Metric, Func<string> Name, double Margin)[] Metrics =
    [
        (HistoryMetric.Cpu, () => Strings.Diag_Unusual_Cpu, 20),
        (HistoryMetric.Memory, () => Strings.Diag_Unusual_Memory, 15),
        (HistoryMetric.Disk, () => Strings.Diag_Unusual_Disk, 20),
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
        foreach (var (metric, nameOf, margin) in Metrics)
        {
            var name = nameOf();
            if (context.Baseline.Get(metric) is not { } usual
                || SnapshotStatistics.Summarize(window, s => s.Get(metric)) is not { } summary
                || summary.Duration < MinimumData)
            {
                continue;
            }

            evidence.Add(new AnalysisEvidence(name, Text.Format(Strings.Diag_Unusual_OverLast, Percent(summary.Average), Duration(summary.Duration)))
            {
                Reference = Text.Format(Strings.Diag_Unusual_Reference, usual.UsualRange, Percent(usual.P95)),
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
                Title = Strings.Diag_Unusual_NormalTitle,
                Description = Text.Format(Strings.Diag_Unusual_NormalDescription, MetricFormatter.DurationCompact(Window)),
                Metric = Strings.Diag_Metric_ActivityVsUsual,
                ObservedValue = Strings.Diag_Unusual_Usual,
                ReferenceValue = context.Baseline.Description,
                Timestamp = latest.Timestamp,
                Explanation = Strings.Diag_Unusual_NormalExplanation,
                Confidence = ConfidenceLevel.Medium,
                Evidence = evidence,
            };
            yield break;
        }

        var parts = unusual.Select(u => Text.Format(Strings.Diag_Unusual_Part, u.Name, Percent(u.Summary.Average), u.Usual.UsualRange));
        yield return new DiagnosisResult
        {
            RuleId = Id,
            Category = DiagnosisCategory.System,
            Severity = unusual.Count >= 2 ? DiagnosisSeverity.Warning : DiagnosisSeverity.Info,
            Title = Strings.Diag_Unusual_Title,
            Description = Text.Format(Strings.Diag_Unusual_Description, MetricFormatter.DurationCompact(Window), string.Join(Strings.List_Separator, parts)),
            Metric = Strings.Diag_Metric_ActivityVsUsual,
            ObservedValue = string.Join(Strings.List_Separator, unusual.Select(u => $"{u.Name} {Percent(u.Summary.Average)}")),
            ReferenceValue = string.Join(Strings.List_Separator, unusual.Select(u => Text.Format(Strings.Diag_Unusual_Usually, u.Usual.UsualRange))),
            Duration = unusual.Max(u => u.Summary.Duration),
            Timestamp = latest.Timestamp,
            Explanation = Strings.Diag_Unusual_Explanation,
            Recommendation = Strings.Diag_Unusual_Recommendation,
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
                Title = Strings.Diag_Gpu_FullTitle,
                Description = Text.Format(Strings.Diag_Gpu_FullDescription, Percent(BusyPercent), Duration(busy.Duration)),
                Metric = Strings.Diag_Metric_GpuUsage,
                ObservedValue = Percent(busy.Average),
                ReferenceValue = Threshold(Percent(BusyPercent)),
                Duration = busy.Duration,
                Timestamp = latest.Timestamp,
                Explanation = Strings.Diag_Gpu_FullExplanation,
                Confidence = ConfidenceLevel.Medium,
                Evidence = [SpanEvidence(Strings.Diag_Metric_GpuUsage, busy, BusyPercent, MetricSources.Gpu)],
                Action = DiagnosisAction.Performance,
            };
            yield break;
        }

        yield return new DiagnosisResult
        {
            RuleId = Id,
            Category = DiagnosisCategory.Gpu,
            Severity = DiagnosisSeverity.Normal,
            Title = Strings.Diag_Gpu_NormalTitle,
            Description = Text.Format(Strings.Diag_Gpu_NormalDescription, Percent(minute.Average)),
            Metric = Strings.Diag_Metric_GpuUsage,
            ObservedValue = Percent(minute.Average),
            ReferenceValue = Threshold(Percent(BusyPercent)),
            Timestamp = latest.Timestamp,
            Explanation = Strings.Diag_Gpu_NormalExplanation,
            Confidence = ConfidenceLevel.High,
            Evidence = [WindowEvidence(Strings.Diag_Gpu_LastMinute, minute, MetricSources.Gpu)],
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
            NetworkConnectivity.InternetAccess => (DiagnosisSeverity.Normal, Strings.Diag_Net_InternetTitle, Strings.Diag_Net_InternetDescription),
            NetworkConnectivity.ConstrainedInternetAccess => (DiagnosisSeverity.Info, Strings.Diag_Net_LimitedTitle, Strings.Diag_Net_LimitedDescription),
            NetworkConnectivity.LocalAccess => (DiagnosisSeverity.Warning, Strings.Diag_Net_LocalTitle, Strings.Diag_Net_LocalDescription),
            _ => (DiagnosisSeverity.Warning, Strings.Diag_Net_NoneTitle, Strings.Diag_Net_NoneDescription),
        };

        yield return new DiagnosisResult
        {
            RuleId = Id,
            Category = DiagnosisCategory.Network,
            Severity = severity,
            Title = title,
            Description = description,
            Metric = Strings.Diag_Metric_Connectivity,
            ObservedValue = Connectivity(network.Connectivity),
            Timestamp = context.Snapshot.Timestamp,
            Explanation = severity == DiagnosisSeverity.Normal
                ? Strings.Diag_Net_OkExplanation
                : Strings.Diag_Net_ProblemExplanation,
            Recommendation = severity == DiagnosisSeverity.Normal ? null : Strings.Diag_Net_Recommendation,
            Confidence = ConfidenceLevel.High,
            Evidence = [new AnalysisEvidence(Strings.Diag_Metric_Connectivity, Connectivity(network.Connectivity)) { Source = MetricSources.Connectivity, To = context.Snapshot.Timestamp }],
            Action = severity == DiagnosisSeverity.Normal ? DiagnosisAction.None : DiagnosisAction.Network,
        };
    }
}

/// <summary>Has Windows been running for a long time without a restart?</summary>
/// <summary>Connectivity levels in words.</summary>
internal static class ConnectivityText
{
    public static string Connectivity(NetworkConnectivity connectivity) => connectivity switch
    {
        NetworkConnectivity.InternetAccess => Strings.Connectivity_Internet,
        NetworkConnectivity.ConstrainedInternetAccess => Strings.Connectivity_Constrained,
        NetworkConnectivity.LocalAccess => Strings.Connectivity_Local,
        NetworkConnectivity.None => Strings.Connectivity_None,
        _ => Strings.Connectivity_Unknown,
    };
}

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

        var evidence = new AnalysisEvidence(Strings.Diag_Uptime_Since, MetricFormatter.DurationLong(system.Uptime))
        {
            Reference = Strings.Diag_Uptime_Reference,
            Source = MetricSources.Uptime,
        };
        var isLong = system.Uptime >= LongUptime;
        yield return new DiagnosisResult
        {
            RuleId = Id,
            Category = DiagnosisCategory.System,
            Severity = isLong ? DiagnosisSeverity.Info : DiagnosisSeverity.Normal,
            Title = isLong
                ? Text.Format(Strings.Diag_Uptime_LongTitle, (int)system.Uptime.TotalDays)
                : Strings.Diag_Uptime_RecentTitle,
            Description = Text.Format(Strings.Diag_Uptime_Description, MetricFormatter.DurationCompact(system.Uptime)),
            Metric = Strings.Diag_Metric_Uptime,
            ObservedValue = MetricFormatter.DurationCompact(system.Uptime),
            ReferenceValue = Text.Format(Strings.Diag_Uptime_Reference2, LongUptime.TotalDays),
            Timestamp = context.Snapshot.Timestamp,
            Explanation = isLong
                ? Strings.Diag_Uptime_LongExplanation
                : Strings.Diag_Uptime_RecentExplanation,
            Recommendation = isLong ? Strings.Diag_Uptime_Recommendation : null,
            Confidence = ConfidenceLevel.Low,
            Evidence = [evidence],
        };
    }
}
