using System.Globalization;
using Sysora.Core.Alerts;
using Sysora.Core.Analysis;
using Sysora.Core.Diagnosis;
using Sysora.Core.Formatting;
using Sysora.Core.History;
using Sysora.Core.Models;
using Sysora.Core.Settings;

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
    public string ImpactText => !IsScored ? "Not counted" : Penalty == 0 ? "No impact" : $"−{Penalty} {(Penalty == 1 ? "point" : "points")}";
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
        Headline = "PC Health: collecting data…",
        Summary = "Sysora needs a couple of minutes of measurements before it gives a score.",
        Components = [],
    };

    /// <summary>Text of a grade.</summary>
    public static string GradeText(PcHealthGrade grade) => grade switch
    {
        PcHealthGrade.Good => "Good",
        PcHealthGrade.Fair => "Fair",
        PcHealthGrade.NeedsAttention => "Needs attention",
        PcHealthGrade.Poor => "Poor",
        _ => "Collecting data",
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

    public const string MethodText =
        "The score starts at 100. Each area takes off points according to what was measured, with fixed thresholds shown next to it. " +
        "CPU, memory, disk activity and GPU use averages over the last 15 minutes, so a short spike barely counts. " +
        "Areas that cannot be measured on this PC are not counted and are listed as not available: nothing is estimated.";

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
            .Select(c => $"{c.Name}: {c.Summary}")
            .ToArray();
        var cpuData = SnapshotStatistics.Summarize(recent, s => s.CpuPercent);
        if (cpuData is not { } data || data.Duration < MinimumData)
        {
            var have = cpuData is { } partial ? MetricFormatter.DurationCompact(partial.Duration) : "no measurement yet";
            return PcHealthReport.Empty with
            {
                Timestamp = input.Now,
                Summary = $"Sysora needs {MetricFormatter.DurationCompact(MinimumData)} of measurements before it gives a score ({have} so far).",
                Components = components,
                NotAvailable = notAvailable,
            };
        }

        var score = Math.Clamp(100 - components.Where(c => c.IsScored).Sum(c => c.Penalty), 0, 100);
        var grade = score >= 85 ? PcHealthGrade.Good : score >= 70 ? PcHealthGrade.Fair : score >= 50 ? PcHealthGrade.NeedsAttention : PcHealthGrade.Poor;
        var lowering = components.Where(c => c.IsScored && c.Penalty > 0).OrderByDescending(c => c.Penalty).ToArray();
        var summary = lowering.Length == 0
            ? "Nothing lowers the score right now."
            : $"Lowered by {Join(lowering.Select(c => $"{c.Name} (−{c.Penalty})"))}.";

        return new PcHealthReport
        {
            Timestamp = input.Now,
            Score = score,
            Grade = grade,
            Headline = $"PC Health: {score}/100 · {PcHealthReport.GradeText(grade)}",
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
            return Missing(PcHealthArea.Cpu, "CPU", max, input.Snapshot.IsUnavailable(MetricKind.Cpu) ? "CPU usage is not available on this PC" : "No measurement yet", DiagnosisAction.Performance);
        }

        var status = cpu.Average >= 85 ? PcHealthStatus.Problem : cpu.Average >= 65 ? PcHealthStatus.Attention : PcHealthStatus.Good;
        var evidence = new List<AnalysisEvidence> { WindowEvidence("CPU usage", cpu, MetricSources.Cpu), Thresholds("Points taken off", "None below 50% on average, up to 15 at 100%") };
        AddBaseline(evidence, input.Baseline, HistoryMetric.Cpu, "CPU usage");
        return new PcHealthComponent
        {
            Area = PcHealthArea.Cpu,
            Name = "CPU",
            Status = status,
            StatusText = StatusText(status),
            Penalty = Scale(cpu.Average, 50, 100, max),
            MaxPenalty = max,
            Summary = Text($"Average {MetricFormatter.Percent(cpu.Average)} over {MetricFormatter.DurationCompact(cpu.Duration)} (peak {MetricFormatter.Percent(cpu.Peak)})"),
            Explanation = status switch
            {
                PcHealthStatus.Problem => "The processor was nearly saturated on average: applications may respond slowly.",
                PcHealthStatus.Attention => "The processor was busy most of the time; it still had some spare capacity.",
                _ => "The processor had spare capacity on average. Short peaks are normal and barely count.",
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
            return Missing(PcHealthArea.Memory, "Memory", max, input.Snapshot.IsUnavailable(MetricKind.Memory) ? "Memory usage is not available on this PC" : "No measurement yet", DiagnosisAction.AppImpact);
        }

        var penalty = Scale(memory.Average, 60, 95, max);
        var status = memory.Average >= 90 ? PcHealthStatus.Problem : memory.Average >= 80 ? PcHealthStatus.Attention : PcHealthStatus.Good;
        var evidence = new List<AnalysisEvidence> { WindowEvidence("Memory in use", memory, MetricSources.Memory), Thresholds("Points taken off", "None below 60% on average, up to 20 at 95%; 3 more when the commit charge stays above 90% of its limit") };
        var commitNote = string.Empty;
        if (SnapshotStatistics.Summarize(recent, s => s.CommitPercent) is { } commit)
        {
            evidence.Add(WindowEvidence("Commit charge (share of the limit)", commit, MetricSources.Commit));
            if (commit.Average >= 90)
            {
                penalty = Math.Min(max, penalty + 3);
                status = status == PcHealthStatus.Good ? PcHealthStatus.Attention : status;
                commitNote = " The commit charge is close to its limit: Windows may refuse new memory allocations.";
            }
        }

        AddBaseline(evidence, input.Baseline, HistoryMetric.Memory, "memory usage");
        var bytes = SnapshotStatistics.Summarize(recent, s => s.MemoryUsedBytes);
        var total = recent.LastOrDefault(s => s.MemoryTotalBytes is not null)?.MemoryTotalBytes;
        var amount = bytes is { } b && total is { } t ? $" ({MetricFormatter.Bytes(b.Average)} of {MetricFormatter.Bytes(t)})" : string.Empty;
        return new PcHealthComponent
        {
            Area = PcHealthArea.Memory,
            Name = "Memory",
            Status = status,
            StatusText = StatusText(status),
            Penalty = penalty,
            MaxPenalty = max,
            Summary = Text($"Average {MetricFormatter.Percent(memory.Average)} in use{amount} over {MetricFormatter.DurationCompact(memory.Duration)}, peak {MetricFormatter.Percent(memory.Peak)}"),
            Explanation = (status switch
            {
                PcHealthStatus.Problem => "Memory is nearly full: Windows has to move data to the disk, which slows everything down.",
                PcHealthStatus.Attention => "Memory is well used; opening more applications may start to slow the PC.",
                _ => "There is free memory for the applications in use.",
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
            return Missing(PcHealthArea.Storage, "Storage", max, snapshot.IsUnavailable(MetricKind.Storage) ? "Free space is not available on this PC" : "No measurement yet", DiagnosisAction.Storage);
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

            evidence.Add(new AnalysisEvidence($"{volume.Letter}{(volume.IsSystemDrive ? " (Windows)" : string.Empty)}", Text($"{MetricFormatter.Bytes(volume.FreeBytes)} free of {MetricFormatter.Bytes(volume.TotalBytes)} ({MetricFormatter.Percent(used)} used)"))
            {
                Reference = Text($"Warning at {warning:0}% used, critical at {critical:0}% (Settings › Health thresholds)"),
                Source = MetricSources.Storage,
            });
        }

        evidence.Add(Thresholds("Points taken off", "16 when the Windows volume is above the critical level, 8 above the warning level; 4 for each other volume above the critical level"));
        var system = volumes.FirstOrDefault(v => v.IsSystemDrive) ?? volumes[0];
        var others = volumes.Length - 1;
        return new PcHealthComponent
        {
            Area = PcHealthArea.Storage,
            Name = "Storage",
            Status = status,
            StatusText = StatusText(status),
            Penalty = Math.Min(max, penalty),
            MaxPenalty = max,
            Summary = $"{system.Letter} {MetricFormatter.Bytes(system.FreeBytes)} free of {MetricFormatter.Bytes(system.TotalBytes)}" + (others > 0 ? $" · {MetricFormatter.Plural(others, "other volume")}" : string.Empty),
            Explanation = status switch
            {
                PcHealthStatus.Problem => "The Windows volume is almost full: updates can fail and Windows has little room for its page file and temporary files.",
                PcHealthStatus.Attention => "A volume is getting full. Large Files can show what takes the most space (nothing is ever deleted by Sysora).",
                _ => "Every volume has room left.",
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
            return Missing(PcHealthArea.DiskActivity, "Disk activity", max, input.Snapshot.IsUnavailable(MetricKind.DiskActivity) ? "Disk activity is not available on this PC" : "No measurement yet", DiagnosisAction.Replay);
        }

        var status = disk.Average >= 75 ? PcHealthStatus.Problem : disk.Average >= 50 ? PcHealthStatus.Attention : PcHealthStatus.Good;
        var evidence = new List<AnalysisEvidence> { WindowEvidence("Active time of the busiest disk", disk, MetricSources.Disk), Thresholds("Points taken off", "None below 40% on average, up to 10 at 90%") };
        AddBaseline(evidence, input.Baseline, HistoryMetric.Disk, "disk activity");
        return new PcHealthComponent
        {
            Area = PcHealthArea.DiskActivity,
            Name = "Disk activity",
            Status = status,
            StatusText = StatusText(status),
            Penalty = Scale(disk.Average, 40, 90, max),
            MaxPenalty = max,
            Summary = Text($"Busiest disk active {MetricFormatter.Percent(disk.Average)} of the time on average (peak {MetricFormatter.Percent(disk.Peak)})"),
            Explanation = status switch
            {
                PcHealthStatus.Problem => "A disk was busy most of the time: opening files and applications waits for it.",
                PcHealthStatus.Attention => "A disk was often busy (updates, indexing, copies or an application reading a lot).",
                _ => "Disks were mostly idle.",
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
            return Missing(PcHealthArea.Gpu, "GPU", max, "GPU monitoring is turned off in Settings", DiagnosisAction.Performance);
        }

        var usage = SnapshotStatistics.Summarize(recent, s => s.GpuPercent);
        var fullest = snapshot.Gpus?
            .Where(g => g.DedicatedMemoryUsedBytes is not null && g.DedicatedMemoryTotalBytes is > 0)
            .MaxBy(g => g.DedicatedMemoryUsedBytes!.Value * 1.0 / g.DedicatedMemoryTotalBytes!.Value);
        if (usage is null && fullest is null)
        {
            return Missing(PcHealthArea.Gpu, "GPU", max, snapshot.IsUnavailable(MetricKind.Gpu) || snapshot.Gpus is { Count: 0 } ? "GPU usage is not available on this PC" : "No measurement yet", DiagnosisAction.Performance);
        }

        var evidence = new List<AnalysisEvidence>();
        if (usage is { } u)
        {
            evidence.Add(WindowEvidence("GPU usage (busiest adapter)", u, MetricSources.Gpu));
        }

        var status = PcHealthStatus.Good;
        var penalty = 0;
        string memoryText = string.Empty;
        if (fullest is { } gpu)
        {
            var share = gpu.DedicatedMemoryUsedBytes!.Value * 100.0 / gpu.DedicatedMemoryTotalBytes!.Value;
            memoryText = $"video memory {MetricFormatter.Bytes(gpu.DedicatedMemoryUsedBytes)} of {MetricFormatter.Bytes(gpu.DedicatedMemoryTotalBytes)}";
            evidence.Add(new AnalysisEvidence($"Dedicated video memory ({gpu.Name})", Text($"{MetricFormatter.Bytes(gpu.DedicatedMemoryUsedBytes)} of {MetricFormatter.Bytes(gpu.DedicatedMemoryTotalBytes)} ({share:0}%)"))
            {
                Reference = "5 points taken off at 95% or more",
                Source = "Windows performance counter \\GPU Adapter Memory(*)\\Dedicated Usage",
            });
            if (share >= 95)
            {
                status = PcHealthStatus.Attention;
                penalty = max;
            }
        }

        var parts = new[] { usage is { } average ? $"Usage {MetricFormatter.Percent(average.Average)} on average" : null, memoryText.Length > 0 ? memoryText : null }.Where(p => p is not null);
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
                ? "Video memory is nearly full: games and graphics applications may stutter while Windows moves data to system memory."
                : "A busy GPU is normal while gaming or playing video, so usage alone does not lower the score; only nearly full video memory does.",
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
            readings.Add(("CPU", cpu));
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
            return Missing(PcHealthArea.Temperatures, "Temperatures", max,
                "Not available: Windows has no documented way to read them without a kernel driver, which Sysora does not install",
                DiagnosisAction.None);
        }

        var hottest = readings.MaxBy(r => r.Celsius);
        var status = hottest.Celsius >= 95 ? PcHealthStatus.Problem : hottest.Celsius >= 85 ? PcHealthStatus.Attention : PcHealthStatus.Good;
        return new PcHealthComponent
        {
            Area = PcHealthArea.Temperatures,
            Name = "Temperatures",
            Status = status,
            StatusText = StatusText(status),
            Penalty = status == PcHealthStatus.Problem ? max : status == PcHealthStatus.Attention ? 5 : 0,
            MaxPenalty = max,
            Summary = string.Join(" · ", readings.Select(r => $"{r.Name} {MetricFormatter.Temperature(r.Celsius)}")),
            Explanation = status == PcHealthStatus.Good ? "Temperatures reported by the drivers are in a normal range." : "A component is running hot; it may slow itself down to cool off.",
            Evidence = readings.Select(r => new AnalysisEvidence($"{r.Name} temperature", MetricFormatter.Temperature(r.Celsius)) { Reference = "5 points at 85 °C, 10 at 95 °C", Source = "Graphics driver" }).ToArray(),
        };
    }

    private static PcHealthComponent Stability(PcHealthInput input)
    {
        const int max = 12;
        var recurring = input.Recurring;
        if (!recurring.HasEnoughHistory)
        {
            return Missing(PcHealthArea.Stability, "Stability", max,
                recurring.Time == DateTimeOffset.MinValue ? "Not analyzed yet" : recurring.Summary,
                DiagnosisAction.PcHealth);
        }

        var count = recurring.Problems.Count;
        var status = count == 0 ? PcHealthStatus.Good : count >= 3 ? PcHealthStatus.Problem : PcHealthStatus.Attention;
        return new PcHealthComponent
        {
            Area = PcHealthArea.Stability,
            Name = "Stability",
            Status = status,
            StatusText = count == 0 ? "Good" : StatusText(status),
            Penalty = Math.Min(max, count * 4),
            MaxPenalty = max,
            Summary = count == 0
                ? $"No recurring problem in the last {MetricFormatter.Plural((int)RecurringProblemDetector.Window.TotalDays, "day")}"
                : $"{MetricFormatter.Plural(count, "recurring problem")}: {Join(recurring.Problems.Take(3).Select(p => $"{p.Title} ({p.Occurrences}×)"))}",
            Explanation = count == 0
                ? "No problem came back repeatedly over the last days."
                : "These problems came back on several days. A problem that repeats usually has a cause worth looking for.",
            Evidence =
            [
                new AnalysisEvidence("Recurring problems", count.ToString(CultureInfo.CurrentCulture))
                {
                    Reference = $"4 points each (at most {max}). A problem is recurring after {RecurringProblemDetector.MinimumOccurrences} episodes on {RecurringProblemDetector.MinimumDays} different days.",
                    Source = "Alerts and events of the local history",
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
            Name = "Recent anomalies",
            Status = status,
            StatusText = recent.Length == 0 ? "None" : recent.Length.ToString(CultureInfo.CurrentCulture),
            Penalty = penalty,
            MaxPenalty = max,
            Summary = recent.Length == 0
                ? "No alert in the last 24 hours"
                : $"{MetricFormatter.Plural(recent.Length, "alert")} in the last 24 hours" + (active > 0 ? $" ({active} still active)" : string.Empty),
            Explanation = recent.Length == 0
                ? "No lasting or unusual problem was detected recently."
                : "Alerts are raised only for problems that lasted or were unusual for this PC. Alerts marked as usual for this PC take nothing off.",
            Evidence = recent
                .OrderByDescending(a => a.RaisedAt)
                .Take(5)
                .Select(a => new AnalysisEvidence(a.Title, a.Value) { From = a.RaisedAt, To = a.ResolvedAt ?? a.UpdatedAt, Reference = $"{a.Severity}: {(a.Severity == AlertSeverity.Critical ? 4 : a.Severity == AlertSeverity.Warning ? 2 : 0)} points" })
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
            return Missing(PcHealthArea.UsualBehavior, "Usual behavior", max, baseline.Description, DiagnosisAction.Diagnosis);
        }

        var unusual = new List<string>();
        var evidence = new List<AnalysisEvidence>();
        foreach (var (metric, name, margin) in new[] { (HistoryMetric.Cpu, "CPU", 20.0), (HistoryMetric.Memory, "Memory", 10.0), (HistoryMetric.Disk, "Disk activity", 20.0) })
        {
            if (baseline.Get(metric) is not { } usual || SnapshotStatistics.Summarize(recent, s => s.Get(metric)) is not { } now)
            {
                continue;
            }

            var limit = Math.Max(usual.P95, usual.Median + margin);
            evidence.Add(new AnalysisEvidence(name, Text($"{MetricFormatter.Percent(now.Average)} now (15-minute average)"))
            {
                Reference = Text($"Usually {usual.UsualRange} (median {usual.Median:0}%); unusual above {limit:0}%"),
                From = baseline.From,
                To = baseline.To,
                SampleCount = usual.Minutes,
                Source = "Per-minute averages of the local history (last 7 days)",
            });
            if (now.Average > limit)
            {
                unusual.Add(Text($"{name} ({MetricFormatter.Percent(now.Average)} vs usually {usual.UsualRange})"));
            }
        }

        var status = unusual.Count > 0 ? PcHealthStatus.Attention : PcHealthStatus.Good;
        return new PcHealthComponent
        {
            Area = PcHealthArea.UsualBehavior,
            Name = "Usual behavior",
            Status = status,
            StatusText = unusual.Count > 0 ? "Unusual" : "Usual",
            Penalty = Math.Min(max, unusual.Count * 3),
            MaxPenalty = max,
            Summary = unusual.Count > 0 ? $"Above your usual range: {Join(unusual)}" : "Within your usual range",
            Explanation = unusual.Count > 0
                ? "This PC is busier than it usually is. It may be expected (a game, a build) or a sign that something runs in the background."
                : "Activity matches what this PC usually does.",
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
        Explanation = "Not counted in the score: Sysora never replaces a missing measurement with an estimate.",
        Action = action,
    };

    private static string StatusText(PcHealthStatus status) => status switch
    {
        PcHealthStatus.Good => "Normal",
        PcHealthStatus.Attention => "Attention",
        PcHealthStatus.Problem => "Problem",
        _ => MetricFormatter.NotAvailable,
    };

    private static PcHealthStatus Worst(PcHealthStatus a, PcHealthStatus b) => a >= b ? a : b;

    private static AnalysisEvidence WindowEvidence(string metric, WindowSummary summary, string source) =>
        new(metric, Text($"Average {MetricFormatter.Percent(summary.Average)}, peak {MetricFormatter.Percent(summary.Peak)}"))
        {
            From = summary.From,
            To = summary.To,
            SampleCount = summary.Count,
            Source = source,
        };

    private static AnalysisEvidence Thresholds(string metric, string text) => new(metric, text) { Source = "Sysora's fixed scoring thresholds" };

    private static void AddBaseline(List<AnalysisEvidence> evidence, UsageBaseline baseline, HistoryMetric metric, string name)
    {
        if (baseline.Get(metric) is { } usual)
        {
            evidence.Add(new AnalysisEvidence($"Usual {name}", Text($"{usual.UsualRange} (median {usual.Median:0}%)"))
            {
                From = baseline.From,
                To = baseline.To,
                SampleCount = usual.Minutes,
                Source = "Per-minute averages of the local history (last 7 days)",
            });
        }
    }

    private static string Join(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        return list.Count switch
        {
            0 => string.Empty,
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }

    private static string Text(FormattableString text) => text.ToString(CultureInfo.CurrentCulture);
}
