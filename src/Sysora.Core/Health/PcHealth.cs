using System.Globalization;
using Sysora.Core.Alerts;
using Sysora.Core.Analysis;
using Sysora.Core.Diagnosis;
using Sysora.Core.Formatting;
using Sysora.Core.History;
using Sysora.Core.Models;
using Sysora.Core.Settings;
using Sysora.Localization;

namespace Sysora.Core.Health;

/// <summary>Areas that make up the PC Health score.</summary>
public enum PcHealthArea
{
    Cpu,
    Memory,
    Storage,
    DiskActivity,
    Gpu,
    Temperatures,
    Stability,
    RecentAnomalies,
    UsualBehavior,
}

/// <summary>State of one area.</summary>
public enum PcHealthStatus
{
    /// <summary>Not measurable on this PC (or not yet): the area is left out of the score.</summary>
    NotAvailable,

    Good,

    Attention,

    Problem,
}

/// <summary>Overall grade of the score.</summary>
public enum PcHealthGrade
{
    /// <summary>Not enough data yet: no score is given.</summary>
    Unknown,

    Good,

    Fair,

    NeedsAttention,

    Poor,
}

/// <summary>
/// One area of the PC Health score: its state, the points it takes off the score and why, with the measurements
/// behind it. An area that cannot be measured is <see cref="PcHealthStatus.NotAvailable"/> and takes nothing off.
/// </summary>
public sealed record PcHealthComponent
{
    public required PcHealthArea Area { get; init; }

    /// <summary>Display name, e.g. "Memory".</summary>
    public required string Name { get; init; }

    public required PcHealthStatus Status { get; init; }

    /// <summary>Short state, e.g. "Normal", "Attention", "Not available", or a count for anomalies.</summary>
    public required string StatusText { get; init; }

    /// <summary>Points taken off the score (0 when fine or not available).</summary>
    public int Penalty { get; init; }

    /// <summary>Most points this area can take off.</summary>
    public required int MaxPenalty { get; init; }

    /// <summary>What was measured, e.g. "Average 72% in use over 15 min (peak 81%)".</summary>
    public required string Summary { get; init; }

    /// <summary>Why the area has this impact, in plain words.</summary>
    public required string Explanation { get; init; }

    /// <summary>Data behind the area.</summary>
    public IReadOnlyList<AnalysisEvidence> Evidence { get; init; } = [];

    /// <summary>Where to look further.</summary>
    public DiagnosisAction Action { get; init; }

    /// <summary>True when the area counts in the score.</summary>
    public bool IsScored => Status != PcHealthStatus.NotAvailable;

    /// <summary>"−8 points", "No impact" or "Not counted".</summary>
    public string ImpactText => !IsScored
        ? Strings.Health_NotCounted
        : Penalty == 0
            ? Strings.Health_NoImpact
            : "−" + Text.Plural(Penalty, Strings.Count_Point_One, Strings.Count_Point_Other);
}

/// <summary>The overall state of the PC as a score out of 100, explained area by area.</summary>
public sealed record PcHealthReport
{
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Score out of 100, or null while there is not enough data.</summary>
    public int? Score { get; init; }

    public required PcHealthGrade Grade { get; init; }

    /// <summary>"PC Health: 87/100 · Good".</summary>
    public required string Headline { get; init; }

    /// <summary>What lowers the score, in one sentence.</summary>
    public required string Summary { get; init; }

    public required IReadOnlyList<PcHealthComponent> Components { get; init; }

    /// <summary>Start of the measurements used.</summary>
    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public int SampleCount { get; init; }

    /// <summary>How the score is computed, shown to the user.</summary>
    public string Method { get; init; } = PcHealthScorer.MethodText;

    /// <summary>Areas not available on this PC, with the reason.</summary>
    public IReadOnlyList<string> NotAvailable { get; init; } = [];

    public static PcHealthReport Empty { get; } = new()
    {
        Timestamp = DateTimeOffset.MinValue,
        Grade = PcHealthGrade.Unknown,
        Headline = Strings.Health_Collecting_Headline,
        Summary = Strings.Health_Collecting_Summary,
        Components = [],
    };

    /// <summary>Text of a grade.</summary>
    public static string GradeText(PcHealthGrade grade) => grade switch
    {
        PcHealthGrade.Good => Strings.Health_Grade_Good,
        PcHealthGrade.Fair => Strings.Health_Grade_Fair,
        PcHealthGrade.NeedsAttention => Strings.Health_Grade_NeedsAttention,
        PcHealthGrade.Poor => Strings.Health_Grade_Poor,
        _ => Strings.Health_Grade_Collecting,
    };
}

/// <summary>Everything the score looks at. Built once per computation.</summary>
public sealed record PcHealthInput
{
    public required DateTimeOffset Now { get; init; }

    /// <summary>Latest full snapshot from the monitor (free space, video memory, temperatures).</summary>
    public required SystemSnapshot Snapshot { get; init; }

    /// <summary>Recent history snapshots, oldest first (typically the last 15 minutes).</summary>
    public required IReadOnlyList<MetricSnapshot> Recent { get; init; }

    public UsageBaseline Baseline { get; init; } = UsageBaseline.Empty;

    /// <summary>Recent alerts, any status (at least the last 24 hours).</summary>
    public IReadOnlyList<Alert> Alerts { get; init; } = [];

    public RecurringProblemReport Recurring { get; init; } = RecurringProblemReport.Unknown;

    /// <summary>The user's health thresholds (disk space warning and critical levels).</summary>
    public AlertSettings Thresholds { get; init; } = new();

    public bool GpuMonitoringEnabled { get; init; } = true;
}

/// <summary>
/// Computes the PC Health score (pure, deterministic). The score starts at 100 and each area takes off points from
/// documented thresholds. Time-based areas use averages over the analysis window, so a short spike barely moves the
/// score; areas that cannot be measured are left out (never guessed).
/// </summary>
public static class PcHealthScorer
{
    /// <summary>Measurements averaged by the time-based areas.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>Measurements needed before a score is given.</summary>
    public static readonly TimeSpan MinimumData = TimeSpan.FromMinutes(2);

    /// <summary>Alerts counted as recent anomalies.</summary>
    public static readonly TimeSpan AnomalyWindow = TimeSpan.FromHours(24);

    public static string MethodText => Strings.Health_Method;

    public static PcHealthReport Compute(PcHealthInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var recent = input.Recent.Where(s => s.Timestamp >= input.Now - Window - TimeSpan.FromMinutes(1)).ToArray();
        var components = new List<PcHealthComponent>
        {
            Cpu(input, recent),
            Memory(input, recent),
            Storage(input),
            DiskActivity(input, recent),
            Gpu(input, recent),
            Temperatures(input),
            Stability(input),
            RecentAnomalies(input),
            UsualBehavior(input, recent),
        };

        var notAvailable = components
            .Where(c => !c.IsScored)
            .Select(c => Text.Format(Strings.Common_NameValue, c.Name, c.Summary))
            .ToArray();
        var cpuData = SnapshotStatistics.Summarize(recent, s => s.CpuPercent);
        if (cpuData is not { } data || data.Duration < MinimumData)
        {
            var have = cpuData is { } partial ? MetricFormatter.DurationCompact(partial.Duration) : Strings.Health_NoMeasurementYetLower;
            return PcHealthReport.Empty with
            {
                Timestamp = input.Now,
                Summary = Text.Format(Strings.Health_NeedsData, MetricFormatter.DurationCompact(MinimumData), have),
                Components = components,
                NotAvailable = notAvailable,
            };
        }

        var score = Math.Clamp(100 - components.Where(c => c.IsScored).Sum(c => c.Penalty), 0, 100);
        var grade = score >= 85 ? PcHealthGrade.Good : score >= 70 ? PcHealthGrade.Fair : score >= 50 ? PcHealthGrade.NeedsAttention : PcHealthGrade.Poor;
        var lowering = components.Where(c => c.IsScored && c.Penalty > 0).OrderByDescending(c => c.Penalty).ToArray();
        var summary = lowering.Length == 0
            ? Strings.Health_NothingLowers
            : Text.Format(Strings.Health_LoweredBy, Text.List(lowering.Select(c => $"{c.Name} (−{c.Penalty})")));

        return new PcHealthReport
        {
            Timestamp = input.Now,
            Score = score,
            Grade = grade,
            Headline = Text.Format(Strings.Health_Headline, score, PcHealthReport.GradeText(grade)),
            Summary = summary,
            Components = components,
            From = data.From,
            To = data.To,
            SampleCount = data.Count,
            NotAvailable = notAvailable,
        };
    }

    /// <summary>Points from 0 at <paramref name="start"/> to <paramref name="max"/> at <paramref name="end"/>, linearly.</summary>
    public static int Scale(double value, double start, double end, int max) =>
        (int)Math.Round(Math.Clamp((value - start) / (end - start), 0, 1) * max, MidpointRounding.AwayFromZero);

    private static PcHealthComponent Cpu(PcHealthInput input, IReadOnlyList<MetricSnapshot> recent)
    {
        const int max = 15;
        if (SnapshotStatistics.Summarize(recent, s => s.CpuPercent) is not { } cpu)
        {
            return Missing(PcHealthArea.Cpu, Strings.Health_Area_Cpu, max, input.Snapshot.IsUnavailable(MetricKind.Cpu) ? Strings.Health_Cpu_NotAvailable : Strings.Health_NoMeasurementYet, DiagnosisAction.Performance);
        }

        var status = cpu.Average >= 85 ? PcHealthStatus.Problem : cpu.Average >= 65 ? PcHealthStatus.Attention : PcHealthStatus.Good;
        var evidence = new List<AnalysisEvidence> { WindowEvidence(Strings.Diag_Metric_CpuUsage, cpu, MetricSources.Cpu), Thresholds(Strings.Health_PointsTakenOff, Strings.Health_Cpu_Thresholds) };
        AddBaseline(evidence, input.Baseline, HistoryMetric.Cpu, Strings.Diag_Metric_CpuUsage);
        return new PcHealthComponent
        {
            Area = PcHealthArea.Cpu,
            Name = Strings.Health_Area_Cpu,
            Status = status,
            StatusText = StatusText(status),
            Penalty = Scale(cpu.Average, 50, 100, max),
            MaxPenalty = max,
            Summary = Text.Format(Strings.Health_Cpu_Summary, MetricFormatter.Percent(cpu.Average), MetricFormatter.DurationCompact(cpu.Duration), MetricFormatter.Percent(cpu.Peak)),
            Explanation = status switch
            {
                PcHealthStatus.Problem => Strings.Health_Cpu_Problem,
                PcHealthStatus.Attention => Strings.Health_Cpu_Attention,
                _ => Strings.Health_Cpu_Good,
            },
            Evidence = evidence,
            Action = DiagnosisAction.Diagnosis,
        };
    }

    private static PcHealthComponent Memory(PcHealthInput input, IReadOnlyList<MetricSnapshot> recent)
    {
        const int max = 20;
        if (SnapshotStatistics.Summarize(recent, s => s.MemoryPercent) is not { } memory)
        {
            return Missing(PcHealthArea.Memory, Strings.Health_Area_Memory, max, input.Snapshot.IsUnavailable(MetricKind.Memory) ? Strings.Health_Memory_NotAvailable : Strings.Health_NoMeasurementYet, DiagnosisAction.AppImpact);
        }

        var penalty = Scale(memory.Average, 60, 95, max);
        var status = memory.Average >= 90 ? PcHealthStatus.Problem : memory.Average >= 80 ? PcHealthStatus.Attention : PcHealthStatus.Good;
        var evidence = new List<AnalysisEvidence> { WindowEvidence(Strings.State_Metric_MemoryUsed, memory, MetricSources.Memory), Thresholds(Strings.Health_PointsTakenOff, Strings.Health_Memory_Thresholds) };
        var commitNote = string.Empty;
        if (SnapshotStatistics.Summarize(recent, s => s.CommitPercent) is { } commit)
        {
            evidence.Add(WindowEvidence(Strings.Health_CommitShare, commit, MetricSources.Commit));
            if (commit.Average >= 90)
            {
                penalty = Math.Min(max, penalty + 3);
                status = status == PcHealthStatus.Good ? PcHealthStatus.Attention : status;
                commitNote = " " + Strings.Health_CommitNote;
            }
        }

        AddBaseline(evidence, input.Baseline, HistoryMetric.Memory, Strings.Diag_Metric_MemoryUsage);
        var bytes = SnapshotStatistics.Summarize(recent, s => s.MemoryUsedBytes);
        var total = recent.LastOrDefault(s => s.MemoryTotalBytes is not null)?.MemoryTotalBytes;
        var amount = bytes is { } b && total is { } t ? " " + Text.Format(Strings.Health_Memory_Amount, MetricFormatter.Bytes(b.Average), MetricFormatter.Bytes(t)) : string.Empty;
        return new PcHealthComponent
        {
            Area = PcHealthArea.Memory,
            Name = Strings.Health_Area_Memory,
            Status = status,
            StatusText = StatusText(status),
            Penalty = penalty,
            MaxPenalty = max,
            Summary = Text.Format(Strings.Health_Memory_Summary, MetricFormatter.Percent(memory.Average), amount, MetricFormatter.DurationCompact(memory.Duration), MetricFormatter.Percent(memory.Peak)),
            Explanation = (status switch
            {
                PcHealthStatus.Problem => Strings.Health_Memory_Problem,
                PcHealthStatus.Attention => Strings.Health_Memory_Attention,
                _ => Strings.Health_Memory_Good,
            }) + commitNote,
            Evidence = evidence,
            Action = DiagnosisAction.AppImpact,
        };
    }

    private static PcHealthComponent Storage(PcHealthInput input)
    {
        const int max = 20;
        var snapshot = input.Snapshot;
        var volumes = snapshot.Storage?.Where(v => v.Kind == DriveKind.Fixed || v.IsSystemDrive).ToArray();
        if (volumes is not { Length: > 0 })
        {
            return Missing(PcHealthArea.Storage, Strings.Health_Area_Storage, max, snapshot.IsUnavailable(MetricKind.Storage) ? Strings.Health_Storage_NotAvailable : Strings.Health_NoMeasurementYet, DiagnosisAction.Storage);
        }

        var warning = input.Thresholds.DiskWarningPercent;
        var critical = input.Thresholds.DiskCriticalPercent;
        var penalty = 0;
        var status = PcHealthStatus.Good;
        var evidence = new List<AnalysisEvidence>();
        foreach (var volume in volumes.OrderByDescending(v => v.IsSystemDrive).ThenBy(v => v.Drive, StringComparer.OrdinalIgnoreCase))
        {
            var used = volume.UsedPercent;
            if (volume.IsSystemDrive && used >= critical)
            {
                penalty += 16;
                status = PcHealthStatus.Problem;
            }
            else if (volume.IsSystemDrive && used >= warning)
            {
                penalty += 8;
                status = Worst(status, PcHealthStatus.Attention);
            }
            else if (used >= critical)
            {
                penalty += 4;
                status = Worst(status, PcHealthStatus.Attention);
            }

            evidence.Add(new AnalysisEvidence($"{volume.Letter}{(volume.IsSystemDrive ? " (Windows)" : string.Empty)}", Text.Format(Strings.Diag_Space_FreeOfUsed, MetricFormatter.Bytes(volume.FreeBytes), MetricFormatter.Bytes(volume.TotalBytes), MetricFormatter.Percent(used)))
            {
                Reference = Text.Format(Strings.Health_Storage_Reference, MetricFormatter.Percent(warning), MetricFormatter.Percent(critical)),
                Source = MetricSources.Storage,
            });
        }

        evidence.Add(Thresholds(Strings.Health_PointsTakenOff, Strings.Health_Storage_Thresholds));
        var system = volumes.FirstOrDefault(v => v.IsSystemDrive) ?? volumes[0];
        var others = volumes.Length - 1;
        return new PcHealthComponent
        {
            Area = PcHealthArea.Storage,
            Name = Strings.Health_Area_Storage,
            Status = status,
            StatusText = StatusText(status),
            Penalty = Math.Min(max, penalty),
            MaxPenalty = max,
            Summary = Text.Format(Strings.Health_Storage_Summary, system.Letter, MetricFormatter.Bytes(system.FreeBytes), MetricFormatter.Bytes(system.TotalBytes))
                + (others > 0 ? " · " + Text.Plural(others, Strings.Count_OtherVolume_One, Strings.Count_OtherVolume_Other) : string.Empty),
            Explanation = status switch
            {
                PcHealthStatus.Problem => Strings.Health_Storage_Problem,
                PcHealthStatus.Attention => Strings.Health_Storage_Attention,
                _ => Strings.Health_Storage_Good,
            },
            Evidence = evidence,
            Action = DiagnosisAction.LargeFiles,
        };
    }

    private static PcHealthComponent DiskActivity(PcHealthInput input, IReadOnlyList<MetricSnapshot> recent)
    {
        const int max = 10;
        if (SnapshotStatistics.Summarize(recent, s => s.DiskActivePercent) is not { } disk)
        {
            return Missing(PcHealthArea.DiskActivity, Strings.Health_Area_Disk, max, input.Snapshot.IsUnavailable(MetricKind.DiskActivity) ? Strings.Health_Disk_NotAvailable : Strings.Health_NoMeasurementYet, DiagnosisAction.Replay);
        }

        var status = disk.Average >= 75 ? PcHealthStatus.Problem : disk.Average >= 50 ? PcHealthStatus.Attention : PcHealthStatus.Good;
        var evidence = new List<AnalysisEvidence> { WindowEvidence(Strings.Health_Disk_Busiest, disk, MetricSources.Disk), Thresholds(Strings.Health_PointsTakenOff, Strings.Health_Disk_Thresholds) };
        AddBaseline(evidence, input.Baseline, HistoryMetric.Disk, Strings.Health_Area_Disk);
        return new PcHealthComponent
        {
            Area = PcHealthArea.DiskActivity,
            Name = Strings.Health_Area_Disk,
            Status = status,
            StatusText = StatusText(status),
            Penalty = Scale(disk.Average, 40, 90, max),
            MaxPenalty = max,
            Summary = Text.Format(Strings.Health_Disk_Summary, MetricFormatter.Percent(disk.Average), MetricFormatter.Percent(disk.Peak)),
            Explanation = status switch
            {
                PcHealthStatus.Problem => Strings.Health_Disk_Problem,
                PcHealthStatus.Attention => Strings.Health_Disk_Attention,
                _ => Strings.Health_Disk_Good,
            },
            Evidence = evidence,
            Action = DiagnosisAction.Replay,
        };
    }

    private static PcHealthComponent Gpu(PcHealthInput input, IReadOnlyList<MetricSnapshot> recent)
    {
        const int max = 5;
        var snapshot = input.Snapshot;
        if (!input.GpuMonitoringEnabled)
        {
            return Missing(PcHealthArea.Gpu, "GPU", max, Strings.Health_Gpu_Off, DiagnosisAction.Performance);
        }

        var usage = SnapshotStatistics.Summarize(recent, s => s.GpuPercent);
        var fullest = snapshot.Gpus?
            .Where(g => g.DedicatedMemoryUsedBytes is not null && g.DedicatedMemoryTotalBytes is > 0)
            .MaxBy(g => g.DedicatedMemoryUsedBytes!.Value * 1.0 / g.DedicatedMemoryTotalBytes!.Value);
        if (usage is null && fullest is null)
        {
            return Missing(PcHealthArea.Gpu, "GPU", max, snapshot.IsUnavailable(MetricKind.Gpu) || snapshot.Gpus is { Count: 0 } ? Strings.Health_Gpu_NotAvailable : Strings.Health_NoMeasurementYet, DiagnosisAction.Performance);
        }

        var evidence = new List<AnalysisEvidence>();
        if (usage is { } u)
        {
            evidence.Add(WindowEvidence(Strings.Game_Ev_GpuBusiest, u, MetricSources.Gpu));
        }

        var status = PcHealthStatus.Good;
        var penalty = 0;
        string memoryText = string.Empty;
        if (fullest is { } gpu)
        {
            var share = gpu.DedicatedMemoryUsedBytes!.Value * 100.0 / gpu.DedicatedMemoryTotalBytes!.Value;
            memoryText = Text.Format(Strings.Health_Gpu_VideoMemory, MetricFormatter.Bytes(gpu.DedicatedMemoryUsedBytes), MetricFormatter.Bytes(gpu.DedicatedMemoryTotalBytes));
            evidence.Add(new AnalysisEvidence(Text.Format(Strings.Health_Gpu_Dedicated, gpu.Name), Text.Format(Strings.Health_Gpu_DedicatedValue, MetricFormatter.Bytes(gpu.DedicatedMemoryUsedBytes), MetricFormatter.Bytes(gpu.DedicatedMemoryTotalBytes), MetricFormatter.Percent(share)))
            {
                Reference = Strings.Health_Gpu_Reference,
                Source = Strings.Source_VideoMemory,
            });
            if (share >= 95)
            {
                status = PcHealthStatus.Attention;
                penalty = max;
            }
        }

        var parts = new[] { usage is { } average ? Text.Format(Strings.Health_Gpu_Usage, MetricFormatter.Percent(average.Average)) : null, memoryText.Length > 0 ? memoryText : null }.Where(p => p is not null);
        return new PcHealthComponent
        {
            Area = PcHealthArea.Gpu,
            Name = "GPU",
            Status = status,
            StatusText = StatusText(status),
            Penalty = penalty,
            MaxPenalty = max,
            Summary = string.Join(" · ", parts),
            Explanation = status == PcHealthStatus.Attention
                ? Strings.Health_Gpu_Attention
                : Strings.Health_Gpu_Good,
            Evidence = evidence,
            Action = DiagnosisAction.Performance,
        };
    }

    private static PcHealthComponent Temperatures(PcHealthInput input)
    {
        const int max = 10;
        var snapshot = input.Snapshot;
        var readings = new List<(string Name, double Celsius)>();
        if (snapshot.Cpu?.TemperatureCelsius is { } cpu && double.IsFinite(cpu))
        {
            readings.Add((Strings.Health_Area_Cpu, cpu));
        }

        foreach (var gpu in snapshot.Gpus ?? [])
        {
            if (gpu.TemperatureCelsius is { } celsius && double.IsFinite(celsius))
            {
                readings.Add((gpu.Name, celsius));
            }
        }

        if (readings.Count == 0)
        {
            return Missing(PcHealthArea.Temperatures, Strings.Health_Area_Temperatures, max,
                Strings.Health_Temperatures_NotAvailable,
                DiagnosisAction.None);
        }

        var hottest = readings.MaxBy(r => r.Celsius);
        var status = hottest.Celsius >= 95 ? PcHealthStatus.Problem : hottest.Celsius >= 85 ? PcHealthStatus.Attention : PcHealthStatus.Good;
        return new PcHealthComponent
        {
            Area = PcHealthArea.Temperatures,
            Name = Strings.Health_Area_Temperatures,
            Status = status,
            StatusText = StatusText(status),
            Penalty = status == PcHealthStatus.Problem ? max : status == PcHealthStatus.Attention ? 5 : 0,
            MaxPenalty = max,
            Summary = string.Join(" · ", readings.Select(r => $"{r.Name} {MetricFormatter.Temperature(r.Celsius)}")),
            Explanation = status == PcHealthStatus.Good
                ? Strings.Health_Temperatures_Good
                : Strings.Health_Temperatures_Hot,
            Evidence = readings.Select(r => new AnalysisEvidence(Text.Format(Strings.Health_Temperature_Of, r.Name), MetricFormatter.Temperature(r.Celsius)) { Reference = Strings.Health_Temperature_Reference, Source = Strings.Health_Temperature_Source }).ToArray(),
        };
    }

    private static PcHealthComponent Stability(PcHealthInput input)
    {
        const int max = 12;
        var recurring = input.Recurring;
        if (!recurring.HasEnoughHistory)
        {
            return Missing(PcHealthArea.Stability, Strings.Health_Area_Stability, max,
                recurring.Time == DateTimeOffset.MinValue ? Strings.Health_NotAnalyzedYet : recurring.Summary,
                DiagnosisAction.PcHealth);
        }

        var count = recurring.Problems.Count;
        var status = count == 0 ? PcHealthStatus.Good : count >= 3 ? PcHealthStatus.Problem : PcHealthStatus.Attention;
        return new PcHealthComponent
        {
            Area = PcHealthArea.Stability,
            Name = Strings.Health_Area_Stability,
            Status = status,
            StatusText = count == 0 ? Strings.Health_Grade_Good : StatusText(status),
            Penalty = Math.Min(max, count * 4),
            MaxPenalty = max,
            Summary = count == 0
                ? Text.Format(Strings.Health_Stability_None, (int)RecurringProblemDetector.Window.TotalDays)
                : Text.Format(
                    Strings.Common_NameValue,
                    Text.Plural(count, Strings.Count_RecurringProblem_One, Strings.Count_RecurringProblem_Other),
                    Text.List(recurring.Problems.Take(3).Select(p => $"{p.Title} ({p.Occurrences}×)"))),
            Explanation = count == 0
                ? Strings.Health_Stability_Good
                : Strings.Health_Stability_Problems,
            Evidence =
            [
                new AnalysisEvidence(Strings.Health_RecurringProblems, count.ToString(CultureInfo.CurrentCulture))
                {
                    Reference = Text.Format(Strings.Health_Stability_Reference, max, RecurringProblemDetector.MinimumOccurrences, RecurringProblemDetector.MinimumDays),
                    Source = Strings.Health_Stability_Source,
                    From = input.Now - RecurringProblemDetector.Window,
                    To = input.Now,
                },
            ],
            Action = DiagnosisAction.PcHealth,
        };
    }

    private static PcHealthComponent RecentAnomalies(PcHealthInput input)
    {
        const int max = 10;
        var recent = input.Alerts.Where(a => a.RaisedAt >= input.Now - AnomalyWindow && a.RaisedAt <= input.Now).ToArray();
        var penalty = Math.Min(max, recent.Sum(a => a.Severity switch
        {
            AlertSeverity.Critical => 4,
            AlertSeverity.Warning => 2,
            _ => 0,
        }));
        var active = recent.Count(a => a.IsActive);
        var status = recent.Any(a => a.IsActive && a.Severity == AlertSeverity.Critical) ? PcHealthStatus.Problem
            : recent.Any(a => a.Severity >= AlertSeverity.Warning) ? PcHealthStatus.Attention
            : PcHealthStatus.Good;
        return new PcHealthComponent
        {
            Area = PcHealthArea.RecentAnomalies,
            Name = Strings.Health_Area_Anomalies,
            Status = status,
            StatusText = recent.Length == 0 ? Strings.Health_None : recent.Length.ToString(CultureInfo.CurrentCulture),
            Penalty = penalty,
            MaxPenalty = max,
            Summary = recent.Length == 0
                ? Strings.Health_Anomalies_None
                : Text.Format(Strings.Health_Anomalies_Count, Text.Plural(recent.Length, Strings.Count_Alert_One, Strings.Count_Alert_Other))
                    + (active > 0 ? " " + Text.Format(Strings.Health_Anomalies_Active, active) : string.Empty),
            Explanation = recent.Length == 0
                ? Strings.Health_Anomalies_Good
                : Strings.Health_Anomalies_Explanation,
            Evidence = recent
                .OrderByDescending(a => a.RaisedAt)
                .Take(5)
                .Select(a => new AnalysisEvidence(a.Title, a.Value) { From = a.RaisedAt, To = a.ResolvedAt ?? a.UpdatedAt, Reference = Text.Format(Strings.Common_NameValue, AlertSeverityText.Label(a.Severity), Text.Plural(a.Severity == AlertSeverity.Critical ? 4 : a.Severity == AlertSeverity.Warning ? 2 : 0, Strings.Count_Point_One, Strings.Count_Point_Other)) })
                .ToArray(),
            Action = DiagnosisAction.Alerts,
        };
    }

    private static PcHealthComponent UsualBehavior(PcHealthInput input, IReadOnlyList<MetricSnapshot> recent)
    {
        const int max = 6;
        var baseline = input.Baseline;
        if (!baseline.IsReady)
        {
            return Missing(PcHealthArea.UsualBehavior, Strings.Health_Area_Usual, max, baseline.Description, DiagnosisAction.Diagnosis);
        }

        var unusual = new List<string>();
        var evidence = new List<AnalysisEvidence>();
        foreach (var (metric, name, margin) in new[] { (HistoryMetric.Cpu, Strings.Health_Area_Cpu, 20.0), (HistoryMetric.Memory, Strings.Health_Area_Memory, 10.0), (HistoryMetric.Disk, Strings.Health_Area_Disk, 20.0) })
        {
            if (baseline.Get(metric) is not { } usual || SnapshotStatistics.Summarize(recent, s => s.Get(metric)) is not { } now)
            {
                continue;
            }

            var limit = Math.Max(usual.P95, usual.Median + margin);
            evidence.Add(new AnalysisEvidence(name, Text.Format(Strings.Health_Usual_Now, MetricFormatter.Percent(now.Average)))
            {
                Reference = Text.Format(Strings.Health_Usual_Reference, usual.UsualRange, MetricFormatter.Percent(usual.Median), MetricFormatter.Percent(limit)),
                From = baseline.From,
                To = baseline.To,
                SampleCount = usual.Minutes,
                Source = Strings.Source_HistoryMinutes7Days,
            });
            if (now.Average > limit)
            {
                unusual.Add(Text.Format(Strings.Health_Usual_Item, name, MetricFormatter.Percent(now.Average), usual.UsualRange));
            }
        }

        var status = unusual.Count > 0 ? PcHealthStatus.Attention : PcHealthStatus.Good;
        return new PcHealthComponent
        {
            Area = PcHealthArea.UsualBehavior,
            Name = Strings.Health_Area_Usual,
            Status = status,
            StatusText = unusual.Count > 0 ? Strings.Health_Unusual : Strings.Health_Usual,
            Penalty = Math.Min(max, unusual.Count * 3),
            MaxPenalty = max,
            Summary = unusual.Count > 0
                ? Text.Format(Strings.Health_Usual_Above, Text.List(unusual))
                : Strings.Health_Usual_Within,
            Explanation = unusual.Count > 0
                ? Strings.Health_Usual_Busier
                : Strings.Health_Usual_Matches,
            Evidence = evidence,
            Action = DiagnosisAction.Diagnosis,
        };
    }

    private static PcHealthComponent Missing(PcHealthArea area, string name, int max, string reason, DiagnosisAction action) => new()
    {
        Area = area,
        Name = name,
        Status = PcHealthStatus.NotAvailable,
        StatusText = MetricFormatter.NotAvailable,
        MaxPenalty = max,
        Summary = reason,
        Explanation = Strings.Health_Missing_Explanation,
        Action = action,
    };

    private static string StatusText(PcHealthStatus status) => status switch
    {
        PcHealthStatus.Good => Strings.Health_Status_Normal,
        PcHealthStatus.Attention => Strings.Health_Status_Attention,
        PcHealthStatus.Problem => Strings.Health_Status_Problem,
        _ => MetricFormatter.NotAvailable,
    };

    private static PcHealthStatus Worst(PcHealthStatus a, PcHealthStatus b) => a >= b ? a : b;

    private static AnalysisEvidence WindowEvidence(string metric, WindowSummary summary, string source) =>
        new(metric, Text.Format(Strings.Diag_AveragePeak, MetricFormatter.Percent(summary.Average), MetricFormatter.Percent(summary.Peak)))
        {
            From = summary.From,
            To = summary.To,
            SampleCount = summary.Count,
            Source = source,
        };

    private static AnalysisEvidence Thresholds(string metric, string text) => new(metric, text) { Source = Strings.Health_ThresholdsSource };

    private static void AddBaseline(List<AnalysisEvidence> evidence, UsageBaseline baseline, HistoryMetric metric, string name)
    {
        if (baseline.Get(metric) is { } usual)
        {
            evidence.Add(new AnalysisEvidence(Text.Format(Strings.Diag_UsualOf, name), Text.Format(Strings.Health_UsualMedian, usual.UsualRange, MetricFormatter.Percent(usual.Median)))
            {
                From = baseline.From,
                To = baseline.To,
                SampleCount = usual.Minutes,
                Source = Strings.Source_HistoryMinutes7Days,
            });
        }
    }
}
