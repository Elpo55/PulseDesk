using Sysora.Core.Analysis;
using Sysora.Core.Diagnosis;
using Sysora.Core.Diagnosis.Rules;
using Sysora.Core.Formatting;
using Sysora.Core.History;
using Sysora.Core.Metrics;
using Sysora.Localization;

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
            ? Text.Format(Strings.Alert_TopApp, app.Name, MetricFormatter.Percent(app.CpuPercent))
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
            Title = unusual
                ? Strings.Alert_Cpu_UnusualTitle
                : usualForThisPc ? Strings.Alert_Cpu_UsualTitle : Strings.Alert_Cpu_SustainedTitle,
            Metric = Strings.Diag_Metric_CpuUsage,
            Value = Text.Format(Strings.Alert_AveragePeak, Percent(span.Average), Percent(span.Peak)),
            Duration = span.Duration,
            Context = Join(usual is null ? Strings.Alert_UsualNotKnown : Text.Format(Strings.Alert_UsualLevel, usual.UsualRange), TopCpuApp(context)),
            Explanation = unusual
                ? Text.Format(Strings.Alert_Cpu_UnusualExplanation, Percent(settings.CpuPercent), Duration(span.Duration), usual!.UsualRange)
                : usualForThisPc
                    ? Text.Format(Strings.Alert_Cpu_UsualExplanation, Percent(settings.CpuPercent), Duration(span.Duration))
                    : Text.Format(Strings.Alert_Cpu_SustainedExplanation, Percent(settings.CpuPercent), Duration(span.Duration)),
            Recommendation = Strings.Alert_Cpu_Recommendation,
            Evidence = [Evidence(Strings.Diag_Metric_CpuUsage, span, settings.CpuPercent, MetricSources.Cpu)],
            Action = DiagnosisAction.AppImpact,
        };
    }

    internal static AnalysisEvidence Evidence(string metric, SustainedSpan span, double threshold, string source) =>
        new(metric, Text.Format(Strings.Diag_AtOrAboveFor, MetricFormatter.Percent(threshold), MetricFormatter.DurationPrecise(span.Duration), MetricFormatter.Percent(span.Average), MetricFormatter.Percent(span.Peak)))
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
            Title = Strings.Alert_Memory_Title,
            Metric = Strings.Diag_Metric_MemoryUsage,
            Value = Text.Format(Strings.Alert_AveragePeak, Percent(span.Average), Percent(span.Peak)),
            Duration = span.Duration,
            Context = Join(
                context.Baseline.Get(HistoryMetric.Memory) is { } usual ? Text.Format(Strings.Alert_UsualLevel, usual.UsualRange) : null,
                largest is null ? null : Text.Format(Strings.Alert_LargestApp, largest.Name, MetricFormatter.Bytes(largest.MemoryBytes))),
            Explanation = Text.Format(Strings.Alert_Memory_Explanation, Percent(settings.MemoryPercent), Duration(span.Duration)),
            Recommendation = Strings.Diag_Memory_CloseApps,
            Evidence = [SustainedCpuAlertRule.Evidence(Strings.Diag_Metric_MemoryUsage, span, settings.MemoryPercent, MetricSources.Memory)],
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
            Title = Strings.Alert_Growth_Title,
            Metric = Strings.Diag_Metric_MemoryUsage,
            Value = Text.Format(Strings.Alert_Growth_Value, rise, Percent(summary.Minimum), Percent(latest.MemoryPercent ?? summary.Peak)),
            Duration = summary.Duration,
            Context = app is null
                ? Strings.Alert_Growth_NoApp
                : Text.Format(Strings.Alert_Growth_AppGrew, app.Name, MetricFormatter.Bytes(growth)),
            Explanation = Text.Format(Strings.Alert_Growth_Explanation, Duration(summary.Duration)),
            Recommendation = app is null ? null : Text.Format(Strings.Alert_Growth_Recommendation, app.Name),
            Evidence =
            [
                new AnalysisEvidence(Strings.Diag_Growth_Trend, Text.Format(Strings.Alert_Growth_TrendValue, rise, Duration(summary.Duration)))
                {
                    Reference = Strings.Diag_Growth_TrendReference,
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
                Title = Text.Format(Strings.Alert_AppCpu_Title, app.Name, MetricFormatter.DurationCompact(span.Duration)),
                Metric = Strings.Diag_Metric_AppCpu,
                Value = Text.Format(Strings.Alert_AppCpu_Value, Percent(span.Average), Percent(span.Peak)),
                Duration = span.Duration,
                Context = Text.Format(Strings.Alert_AppCpu_TotalNow, MetricFormatter.Percent(latest.CpuPercent)),
                Explanation = Text.Format(Strings.Alert_AppCpu_Explanation, app.Name, Percent(settings.AppCpuPercent), Duration(span.Duration)),
                Recommendation = Text.Format(Strings.Alert_AppCpu_Recommendation, app.Name),
                Evidence = [SustainedCpuAlertRule.Evidence(Diagnosis.Rules.CpuHungryAppRule.AppCpu(app.Name), span, settings.AppCpuPercent, MetricSources.Processes)],
                AppKey = app.Key,
                AppName = app.Name,
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
            Title = latest.DiskActiveDrive is { } drive
                ? Text.Format(Strings.Alert_Disk_Title, drive, MetricFormatter.DurationCompact(span.Duration))
                : Text.Format(Strings.Alert_Disk_TitleUnnamed, MetricFormatter.DurationCompact(span.Duration)),
            Metric = Strings.Diag_Metric_DiskActive,
            Value = Text.Format(Strings.Alert_OnAverage, Percent(span.Average)),
            Duration = span.Duration,
            Context = Join(
                Text.Format(Strings.Alert_Disk_ReadWrite, MetricFormatter.BytesPerSecond(latest.DiskReadBytesPerSecond), MetricFormatter.BytesPerSecond(latest.DiskWriteBytesPerSecond)),
                topIo is null ? null : Text.Format(Strings.Alert_Disk_MostIo, topIo.Name)),
            Explanation = Text.Format(Strings.Alert_Disk_Explanation, Percent(settings.DiskActivePercent), Duration(span.Duration)),
            Recommendation = Strings.Alert_Disk_Recommendation,
            Evidence = [SustainedCpuAlertRule.Evidence(Strings.Diag_Metric_DiskActive, span, settings.DiskActivePercent, MetricSources.Disk)],
            AppKey = topIo?.Key,
            Action = DiagnosisAction.AppImpact,
        };
    }
}

/// <summary>CPU, memory or disk above the PC's usual range for several minutes (needs the baseline).</summary>
public sealed class UnusualActivityAlertRule : AlertRule
{
    private static readonly (HistoryMetric Metric, Func<string> Name, double Margin)[] Metrics =
    [
        (HistoryMetric.Cpu, () => Strings.Diag_Metric_CpuUsage, 20),
        (HistoryMetric.Memory, () => Strings.Diag_Metric_MemoryUsage, 10),
        (HistoryMetric.Disk, () => Strings.Alert_Unusual_Disk, 20),
    ];

    public override string Id => "unusual";

    public override IEnumerable<AlertCondition> Evaluate(AlertContext context)
    {
        var settings = context.Settings;
        if (!settings.UnusualActivity || !context.Baseline.IsReady)
        {
            yield break;
        }

        foreach (var (metric, nameOf, margin) in Metrics)
        {
            var name = nameOf();
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
                Title = Text.Format(Strings.Alert_Unusual_Title, name),
                Metric = name,
                Value = Text.Format(Strings.Alert_OnAverage, Percent(span.Average)),
                Duration = span.Duration,
                Context = Text.Format(Strings.Alert_Unusual_Context, usual.UsualRange, Percent(usual.Median)),
                Explanation = Text.Format(Strings.Alert_Unusual_Explanation, name, Duration(span.Duration)),
                Recommendation = Strings.Alert_Unusual_Recommendation,
                Evidence =
                [
                    SustainedCpuAlertRule.Evidence(name, span, threshold, MetricSources.For(metric)),
                    new AnalysisEvidence(Strings.Alert_Baseline, Text.Format(Strings.Diag_Unusual_Reference, usual.UsualRange, Percent(usual.P95)))
                    {
                        From = context.Baseline.From,
                        To = context.Baseline.To,
                        SampleCount = usual.Minutes,
                        Source = Strings.Source_HistoryMinutes7Days,
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
            Title = Text.Format(Strings.Alert_Space_Title, drive.Letter),
            Metric = Strings.Alert_Space_Metric,
            Value = Text.Format(Strings.Alert_Space_Value, MetricFormatter.Bytes(drive.FreeBytes), MetricFormatter.Percent(freePercent, 1)),
            Context = Text.Format(Strings.Alert_Space_Context, MetricFormatter.Bytes(drive.TotalBytes)),
            Explanation = Strings.Alert_Space_Explanation,
            Recommendation = Strings.Alert_Space_Recommendation,
            Evidence = [new AnalysisEvidence(Text.Format(Strings.Diag_Volume, drive.Letter), Text.Format(Strings.Diag_Space_FreeOf, MetricFormatter.Bytes(drive.FreeBytes), MetricFormatter.Bytes(drive.TotalBytes))) { Source = MetricSources.Storage, Reference = Text.Format(Strings.Alert_Space_Reference, MetricFormatter.Percent(limit, 1)) }],
            Action = DiagnosisAction.Storage,
        };
    }
}
