using System.Globalization;
using PulseDesk.Core.Analysis;
using PulseDesk.Core.Diagnosis;
using PulseDesk.Core.Formatting;

namespace PulseDesk.Core.Gaming;

/// <summary>Whether a finding reports a limit reached during the game, or interprets the measurements.</summary>
public enum GameFindingKind
{
    /// <summary>A resource limit measured during the session.</summary>
    Anomaly,

    /// <summary>An interpretation of the measurements, worded as a hypothesis.</summary>
    Analysis,

    /// <summary>A difference with the previous sessions of the same game.</summary>
    Comparison,
}

/// <summary>One conclusion of a recap, with the measurements behind it.</summary>
public sealed record GameFinding
{
    public required GameFindingKind Kind { get; init; }

    public required DiagnosisSeverity Severity { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    /// <summary>How the conclusion should be read: "Likely limiting factor", "Possible cause", "Potential contributor"…</summary>
    public string? Qualifier { get; init; }

    public string? Recommendation { get; init; }

    public required ConfidenceLevel Confidence { get; init; }

    public IReadOnlyList<AnalysisEvidence> Evidence { get; init; } = [];
}

/// <summary>Average and maximum of one metric, formatted, or "Not available".</summary>
/// <param name="Name">Metric.</param>
/// <param name="Average">Average over the session.</param>
/// <param name="Maximum">Highest value.</param>
/// <param name="Note">Scope or reason it is not available.</param>
public sealed record GameRecapMetric(string Name, string Average, string Maximum, string? Note = null)
{
    public bool IsAvailable => Average != GameRecapBuilder.NotAvailable;
}

/// <summary>This session compared with the previous sessions of the same game.</summary>
/// <param name="Metric">Metric.</param>
/// <param name="ThisSession">Value in this session.</param>
/// <param name="Previous">Average of the previous sessions.</param>
/// <param name="Difference">Difference, formatted ("+12 points", "−400 MB").</param>
/// <param name="IsNotable">True when the difference is large enough to mention.</param>
public sealed record GameComparisonItem(string Metric, string ThisSession, string Previous, string Difference, bool IsNotable);

/// <summary>The recap shown when a game closes: what was measured, what went wrong, and how it compares.</summary>
public sealed record GameRecap
{
    public required GameSession Session { get; init; }

    /// <summary>"No problem observed" or "2 points to watch".</summary>
    public required string Headline { get; init; }

    /// <summary>Two or three sentences summarizing the session.</summary>
    public required string Summary { get; init; }

    /// <summary>Most serious finding level (Normal when nothing was observed).</summary>
    public required DiagnosisSeverity Severity { get; init; }

    public required IReadOnlyList<GameRecapMetric> Metrics { get; init; }

    /// <summary>Anomalies, analysis and notable differences, most important first.</summary>
    public required IReadOnlyList<GameFinding> Findings { get; init; }

    public IReadOnlyList<GameComparisonItem> Comparison { get; init; } = [];

    /// <summary>What the comparison is based on, or why there is none.</summary>
    public required string ComparisonNote { get; init; }

    /// <summary>Number of previous sessions of the same game used for the comparison.</summary>
    public int PreviousSessions { get; init; }

    /// <summary>What the measurements cover (late start, gaps).</summary>
    public required string Coverage { get; init; }

    /// <summary>What PulseDesk could not measure, and why.</summary>
    public required IReadOnlyList<string> NotAvailable { get; init; }
}

/// <summary>Builds the recap of a gaming session from its measurements and the previous sessions (pure, deterministic).</summary>
public static class GameRecapBuilder
{
    public const string NotAvailable = "Not available";

    /// <summary>Why PulseDesk shows no frame rate.</summary>
    public const string FpsNotAvailable =
        "FPS: Not available. Windows gives other applications no reliable frame-rate source: measuring it requires " +
        "administrator-level event tracing or hooking into the game, and PulseDesk does neither.";

    /// <summary>Previous sessions used at most for the comparison.</summary>
    public const int MaxComparedSessions = 10;

    /// <summary>GPU average from which the GPU is described as the likely limiting factor.</summary>
    public const double GpuBoundPercent = 85;

    /// <summary>CPU average from which the processor is described as heavily used.</summary>
    public const double CpuHeavyPercent = 80;

    /// <summary>Average share of total CPU from which a background application is mentioned.</summary>
    public const double BackgroundAppPercent = 5;

    /// <summary>Difference in percentage points that is worth mentioning in a comparison.</summary>
    public const double NotablePoints = 10;

    /// <summary>Relative difference in game memory worth mentioning (also needs <see cref="NotableMemoryBytes"/>).</summary>
    public const double NotableMemoryRatio = 0.2;

    public const double NotableMemoryBytes = 500.0 * 1024 * 1024;

    /// <summary>Measurements needed before an average is interpreted.</summary>
    private const int MinimumSamples = 10;

    private const string CpuSource = "Windows performance counter \\Processor Information(_Total)\\% Processor Utility";

    public static GameRecap Build(GameSession session, IReadOnlyList<GameSession> history)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(history);

        var previous = history
            .Where(s => s.GameKey == session.GameKey && s.Id != session.Id && s.End <= session.Start)
            .OrderByDescending(s => s.Start)
            .Take(MaxComparedSessions)
            .ToArray();

        var findings = new List<GameFinding>();
        findings.AddRange(Conditions(session));
        findings.AddRange(Analysis(session));
        var (comparison, note, comparisonFindings) = Compare(session, previous);
        findings.AddRange(comparisonFindings);

        var ordered = findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Kind)
            .ThenByDescending(f => f.Confidence)
            .ToList();
        if (!ordered.Any(f => f.Severity >= DiagnosisSeverity.Info && f.Kind == GameFindingKind.Anomaly))
        {
            ordered.Insert(ordered.Count(f => f.Severity >= DiagnosisSeverity.Info), new GameFinding
            {
                Kind = GameFindingKind.Anomaly,
                Severity = DiagnosisSeverity.Normal,
                Title = "No resource limit reached",
                Description = "Memory, processor, video memory and disks never stayed at their limit during the measured time.",
                Confidence = session.MonitoredSeconds >= 60 ? ConfidenceLevel.High : ConfidenceLevel.Low,
                Evidence = [Coverage(session)],
            });
        }

        var problems = ordered.Count(f => f.Severity >= DiagnosisSeverity.Warning);
        var severity = ordered.Count > 0 ? ordered.Max(f => f.Severity) : DiagnosisSeverity.Normal;
        return new GameRecap
        {
            Session = session,
            Headline = problems switch
            {
                0 => "No problem observed",
                1 => "1 point to watch",
                _ => $"{problems.ToString(CultureInfo.CurrentCulture)} points to watch",
            },
            Summary = Summary(session, ordered),
            Severity = severity,
            Metrics = Metrics(session),
            Findings = ordered,
            Comparison = comparison,
            ComparisonNote = note,
            PreviousSessions = previous.Length,
            Coverage = CoverageText(session),
            NotAvailable = Missing(session),
        };
    }

    private static string Summary(GameSession session, IReadOnlyList<GameFinding> findings)
    {
        var parts = new List<string> { $"{MetricFormatter.DurationPrecise(session.Duration)} of {session.Name}." };
        var averages = new List<string>();
        if (session.Cpu is { } cpu)
        {
            averages.Add($"CPU {MetricFormatter.Percent(cpu.Average)}");
        }

        if (session.Gpu is { } gpu)
        {
            averages.Add($"GPU {MetricFormatter.Percent(gpu.Average)}");
        }

        if (session.Memory is { } memory)
        {
            averages.Add($"memory {MetricFormatter.Percent(memory.Average)}");
        }

        if (averages.Count > 0)
        {
            parts.Add($"On average: {string.Join(", ", averages)}.");
        }

        if (findings.FirstOrDefault(f => f.Severity >= DiagnosisSeverity.Info) is { } first)
        {
            parts.Add(first.Qualifier is { } qualifier ? $"{qualifier}: {Lower(first.Title)}." : $"{first.Title}.");
        }
        else
        {
            parts.Add("No resource limit was reached.");
        }

        return string.Join(" ", parts);
    }

    private static IReadOnlyList<GameRecapMetric> Metrics(GameSession session)
    {
        var memoryTotal = session.MemoryTotalBytes is { } total ? $"of {MetricFormatter.Bytes(total)}" : null;
        var videoTotal = session.VideoMemoryTotalBytes is { } video ? $"of {MetricFormatter.Bytes(video)}" : null;
        return
        [
            new("FPS", NotAvailable, NotAvailable, "No reliable source in Windows"),
            Percent("CPU (whole PC)", session.Cpu),
            Percent("CPU used by the game", session.GameCpu, "Share of total processor capacity"),
            Percent("GPU (busiest adapter)", session.Gpu, session.GpuName),
            session.GameGpu is null
                ? new GameRecapMetric("GPU used by the game", NotAvailable, NotAvailable, "Windows did not report GPU usage per process")
                : Percent("GPU used by the game", session.GameGpu, "Busiest GPU engine used by the game"),
            Bytes("Video memory in use", session.VideoMemoryBytes, Join(videoTotal, "dedicated memory, all applications")),
            Percent("Memory (whole PC)", session.Memory, memoryTotal),
            Bytes("Memory used by the game", session.GameMemoryBytes, "Private working set of the game's processes"),
            Percent("Busiest disk active time", session.Disk),
            Rate("Network received (whole PC)", session.NetworkReceive),
            Rate("Network sent (whole PC)", session.NetworkSend),
            Percent("PulseDesk's own CPU", session.SelfCpu, "Measured during the session"),
        ];
    }

    private static IEnumerable<GameFinding> Conditions(GameSession session)
    {
        foreach (var condition in session.Conditions)
        {
            var total = MetricFormatter.DurationPrecise(TimeSpan.FromSeconds(condition.TotalSeconds));
            var periods = condition.Periods > 1 ? $" over {condition.Periods.ToString(CultureInfo.CurrentCulture)} periods" : string.Empty;
            var evidence = new AnalysisEvidence(ConditionMetric(condition.Kind), $"At or above {MetricFormatter.Percent(condition.Threshold)} for {total}{periods} (peak {MetricFormatter.Percent(condition.Peak)})")
            {
                Reference = $"Counted when it lasts at least {MetricFormatter.DurationCompact(ConditionMinimum(condition.Kind))}",
                From = condition.FirstAt,
                To = session.End,
                Source = ConditionSource(condition.Kind),
            };
            var longEnough = condition.TotalSeconds >= 120;
            yield return condition.Kind switch
            {
                GameConditionKind.MemoryNearlyFull => new GameFinding
                {
                    Kind = GameFindingKind.Anomaly,
                    Severity = DiagnosisSeverity.Warning,
                    Title = "Memory was nearly full",
                    Description = $"Physical memory stayed above {MetricFormatter.Percent(condition.Threshold)} for {total}{periods}.",
                    Qualifier = "Possible cause of stutters",
                    Recommendation = BiggestBackgroundMemory(session) is { } app
                        ? $"Close applications you don't need before playing. The largest other application was {app.Name} ({MetricFormatter.Bytes(app.MemoryMaximumBytes)})."
                        : "Close applications you don't need before playing.",
                    Confidence = longEnough ? ConfidenceLevel.High : ConfidenceLevel.Medium,
                    Evidence = [evidence],
                },
                GameConditionKind.CpuSaturated => new GameFinding
                {
                    Kind = GameFindingKind.Anomaly,
                    Severity = DiagnosisSeverity.Warning,
                    Title = "The processor was saturated",
                    Description = $"Total CPU usage stayed above {MetricFormatter.Percent(condition.Threshold)} for {total}{periods}.",
                    Qualifier = "Likely contributor to slowdowns",
                    Recommendation = TopBackgroundApp(session) is { } app
                        ? $"The busiest other application was {app.Name} ({MetricFormatter.Percent(app.CpuAverage, 1)} of CPU on average): close it while playing if you don't need it."
                        : "Lowering CPU-heavy settings (view distance, crowd density, physics) reduces the load.",
                    Confidence = longEnough ? ConfidenceLevel.High : ConfidenceLevel.Medium,
                    Evidence = [evidence],
                },
                GameConditionKind.VideoMemoryNearlyFull => new GameFinding
                {
                    Kind = GameFindingKind.Anomaly,
                    Severity = DiagnosisSeverity.Warning,
                    Title = "Video memory was nearly full",
                    Description = $"Dedicated video memory stayed above {MetricFormatter.Percent(condition.Threshold)} of {MetricFormatter.Bytes(session.VideoMemoryTotalBytes)} for {total}{periods}.",
                    Qualifier = "Possible cause of stutters",
                    Recommendation = "Lowering texture quality or resolution reduces video memory use.",
                    Confidence = ConfidenceLevel.Medium,
                    Evidence = [evidence],
                },
                _ => new GameFinding
                {
                    Kind = GameFindingKind.Anomaly,
                    Severity = longEnough ? DiagnosisSeverity.Warning : DiagnosisSeverity.Info,
                    Title = "A disk was busy almost all the time",
                    Description = $"The busiest disk stayed above {MetricFormatter.Percent(condition.Threshold)} active time for {total}{periods}.",
                    Qualifier = "Possible cause of loading pauses",
                    Recommendation = "Installing the game on a faster drive (SSD) shortens loading; updates or scans running at the same time also keep disks busy.",
                    Confidence = ConfidenceLevel.Medium,
                    Evidence = [evidence],
                },
            };
        }
    }

    private static IEnumerable<GameFinding> Analysis(GameSession session)
    {
        var cpu = session.Cpu;
        var gpu = session.Gpu;
        if (gpu is { Samples: >= MinimumSamples } g && g.Average >= GpuBoundPercent)
        {
            yield return new GameFinding
            {
                Kind = GameFindingKind.Analysis,
                Severity = DiagnosisSeverity.Info,
                Title = "The graphics card was the most loaded component",
                Description = $"The GPU averaged {MetricFormatter.Percent(g.Average)}" + (cpu is { } c ? $" while the processor averaged {MetricFormatter.Percent(c.Average)}." : "."),
                Qualifier = "Likely limiting factor",
                Recommendation = "This is expected with a demanding game: the GPU sets the pace. Lower graphics settings or resolution for more smoothness.",
                Confidence = ConfidenceLevel.Medium,
                Evidence = [Stat("GPU usage (busiest adapter)", g, MetricSources.Gpu, session), .. CpuEvidence(session)],
            };
        }
        else if (cpu is { Samples: >= MinimumSamples } c && c.Average >= CpuHeavyPercent)
        {
            var gpuLow = gpu is { } low && low.Average < 70;
            yield return new GameFinding
            {
                Kind = GameFindingKind.Analysis,
                Severity = DiagnosisSeverity.Info,
                Title = "The processor was heavily used",
                Description = gpu is { } gg
                    ? $"The processor averaged {MetricFormatter.Percent(c.Average)} while the GPU averaged {MetricFormatter.Percent(gg.Average)}."
                    : $"The processor averaged {MetricFormatter.Percent(c.Average)}. GPU usage was not available, so PulseDesk cannot tell which component set the pace.",
                Qualifier = gpuLow ? "Likely limiting factor" : "Potential contributor",
                Recommendation = "Close background applications and lower CPU-heavy settings (view distance, crowds, physics).",
                Confidence = gpuLow ? ConfidenceLevel.Medium : ConfidenceLevel.Low,
                Evidence = [.. CpuEvidence(session), .. gpu is { } ge ? new[] { Stat("GPU usage (busiest adapter)", ge, MetricSources.Gpu, session) } : []],
            };
        }

        foreach (var app in session.BackgroundApps.Where(a => a.CpuAverage >= BackgroundAppPercent).Take(2))
        {
            yield return new GameFinding
            {
                Kind = GameFindingKind.Analysis,
                Severity = app.CpuAverage >= BackgroundAppPercent * 3 ? DiagnosisSeverity.Warning : DiagnosisSeverity.Info,
                Title = $"{app.Name} was busy during the game",
                Description = $"{app.Name} used {MetricFormatter.Percent(app.CpuAverage, 1)} of total CPU on average during the session (peak {MetricFormatter.Percent(app.CpuMaximum, 1)}).",
                Qualifier = "Potential contributor",
                Recommendation = $"If you don't need {app.Name} while playing, close it before starting the game.",
                Confidence = ConfidenceLevel.Medium,
                Evidence =
                [
                    new AnalysisEvidence($"{app.Name} CPU", $"Average {MetricFormatter.Percent(app.CpuAverage, 1)}, peak {MetricFormatter.Percent(app.CpuMaximum, 1)}, up to {MetricFormatter.Bytes(app.MemoryMaximumBytes)} of memory")
                    {
                        Reference = "Average over every process sample of the session (0 while it was not running)",
                        From = session.Start,
                        To = session.End,
                        SampleCount = session.ProcessSamples,
                        Source = MetricSources.Processes,
                    },
                ],
            };
        }

        if (session.DataGaps > 0)
        {
            yield return new GameFinding
            {
                Kind = GameFindingKind.Analysis,
                Severity = DiagnosisSeverity.Normal,
                Title = "Measurements were interrupted",
                Description = $"{MetricFormatter.Plural(session.DataGaps, "interruption")} ({MetricFormatter.DurationCompact(TimeSpan.FromSeconds(session.GapSeconds))} not measured): the PC was asleep or monitoring was paused. Values cover the measured time only.",
                Confidence = ConfidenceLevel.High,
                Evidence = [Coverage(session)],
            };
        }
    }

    private static (IReadOnlyList<GameComparisonItem> Items, string Note, IReadOnlyList<GameFinding> Findings) Compare(GameSession session, IReadOnlyList<GameSession> previous)
    {
        if (previous.Count == 0)
        {
            return ([], $"First recorded session of {session.Name}: nothing to compare with yet.", []);
        }

        var note = previous.Count == 1
            ? $"Compared with your previous session of {session.Name} ({previous[0].Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)})."
            : $"Compared with the average of your {previous.Count.ToString(CultureInfo.CurrentCulture)} previous sessions of {session.Name} (since {previous[^1].Start.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)}).";
        var reference = previous.Count == 1 ? "your previous session" : $"your {previous.Count.ToString(CultureInfo.CurrentCulture)} previous sessions";

        var items = new List<GameComparisonItem>();
        var findings = new List<GameFinding>();

        void ComparePercent(string metric, Func<GameSession, MetricStat?> value)
        {
            if (value(session) is not { } current || Average(previous, value) is not { } before)
            {
                return;
            }

            var difference = current.Average - before;
            var notable = Math.Abs(difference) >= NotablePoints;
            items.Add(new GameComparisonItem(metric, MetricFormatter.Percent(current.Average), MetricFormatter.Percent(before), Points(difference), notable));
            if (notable && difference > 0)
            {
                findings.Add(Difference($"{metric} was higher than usual for this game", $"{metric} averaged {MetricFormatter.Percent(current.Average)}, {Points(difference)} compared with {reference} ({MetricFormatter.Percent(before)}).", metric, current, before, v => MetricFormatter.Percent(v)));
            }
        }

        ComparePercent("CPU (whole PC)", s => s.Cpu);
        ComparePercent("GPU (busiest adapter)", s => s.Gpu);
        ComparePercent("Memory (whole PC)", s => s.Memory);
        ComparePercent("CPU used by the game", s => s.GameCpu);

        if (session.GameMemoryBytes is { } memory && Average(previous, s => s.GameMemoryBytes) is { } beforeMemory)
        {
            var difference = memory.Average - beforeMemory;
            var notable = Math.Abs(difference) >= NotableMemoryBytes && beforeMemory > 0 && Math.Abs(difference) / beforeMemory >= NotableMemoryRatio;
            items.Add(new GameComparisonItem("Memory used by the game", MetricFormatter.Bytes(memory.Average), MetricFormatter.Bytes(beforeMemory), SignedBytes(difference), notable));
            if (notable && difference > 0)
            {
                findings.Add(Difference("The game used more memory than usual", $"The game used {MetricFormatter.Bytes(memory.Average)} on average, {SignedBytes(difference)} compared with {reference} ({MetricFormatter.Bytes(beforeMemory)}).", "Memory used by the game", memory, beforeMemory, v => MetricFormatter.Bytes(v)));
            }
        }

        var averageDuration = TimeSpan.FromSeconds(previous.Average(s => s.Duration.TotalSeconds));
        items.Add(new GameComparisonItem("Duration", MetricFormatter.DurationPrecise(session.Duration), MetricFormatter.DurationPrecise(averageDuration), SignedDuration(session.Duration - averageDuration), false));

        foreach (var condition in session.Conditions)
        {
            var before = previous.Count(s => s.Conditions.Any(c => c.Kind == condition.Kind));
            if (before == 0)
            {
                findings.Add(new GameFinding
                {
                    Kind = GameFindingKind.Comparison,
                    Severity = DiagnosisSeverity.Info,
                    Title = $"{ConditionMetric(condition.Kind)}: new in this session",
                    Description = $"This limit was not reached in {reference} of {session.Name}.",
                    Qualifier = "Observed change, cause unknown",
                    Confidence = previous.Count >= 3 ? ConfidenceLevel.Medium : ConfidenceLevel.Low,
                });
            }
        }

        return (items, note, findings);

        GameFinding Difference(string title, string description, string metric, MetricStat current, double before, Func<double, string> format) => new()
        {
            Kind = GameFindingKind.Comparison,
            Severity = DiagnosisSeverity.Info,
            Title = title,
            Description = description,
            Qualifier = "Observed change, cause unknown",
            Recommendation = "A game update, different settings or a different part of the game can explain it; if it keeps happening, compare with Changes.",
            Confidence = previous.Count >= 3 ? ConfidenceLevel.Medium : ConfidenceLevel.Low,
            Evidence =
            [
                new AnalysisEvidence(metric, $"This session: {format(current.Average)} (peak {format(current.Maximum)})")
                {
                    Reference = $"Previous sessions: {format(before)} on average",
                    SampleCount = current.Samples,
                    From = session.Start,
                    To = session.End,
                },
            ],
        };
    }

    /// <summary>Average of the previous sessions' averages, weighted by measured time.</summary>
    private static double? Average(IReadOnlyList<GameSession> sessions, Func<GameSession, MetricStat?> value)
    {
        double sum = 0, weight = 0;
        foreach (var session in sessions)
        {
            if (value(session) is { } stat)
            {
                var w = Math.Max(session.MonitoredSeconds, 1);
                sum += stat.Average * w;
                weight += w;
            }
        }

        return weight > 0 ? sum / weight : null;
    }

    private static IReadOnlyList<string> Missing(GameSession session)
    {
        var missing = new List<string> { FpsNotAvailable };
        if (session.Gpu is null)
        {
            missing.Add("GPU usage: Not available (no graphics adapter reported it, or GPU monitoring is turned off in Settings).");
        }
        else if (session.GameGpu is null)
        {
            missing.Add("GPU usage of the game: Not available (Windows did not report GPU usage per process).");
        }

        if (session.VideoMemoryBytes is null)
        {
            missing.Add("Video memory: Not available (the graphics driver did not report it).");
        }

        missing.Add("Temperatures: Not available (Windows has no documented way to read them without a kernel driver).");
        missing.Add("Network usage of the game: Not available (per-application network usage requires administrator-level event tracing); network values are for the whole PC.");
        return missing;
    }

    private static string CoverageText(GameSession session)
    {
        var text = $"Measured from {Time(session.Start)} to {Time(session.End)} ({MetricFormatter.DurationPrecise(TimeSpan.FromSeconds(session.MonitoredSeconds))} of measurements).";
        if (session.GameStartedAt is { } started)
        {
            text += $" The game was already running since {Time(started)} when PulseDesk started: the time before is not covered.";
        }

        if (session.DataGaps > 0)
        {
            text += $" {MetricFormatter.Plural(session.DataGaps, "interruption")} ({MetricFormatter.DurationCompact(TimeSpan.FromSeconds(session.GapSeconds))}).";
        }

        text += session.EndReason switch
        {
            GameSessionEnd.PulseDeskClosed => " PulseDesk was closed before the game: the end of the session is not covered.",
            GameSessionEnd.TrackingStopped => " Game sessions were turned off before the game closed.",
            _ => string.Empty,
        };
        return text;
    }

    private static AnalysisEvidence Coverage(GameSession session) =>
        new("Measured time", MetricFormatter.DurationPrecise(TimeSpan.FromSeconds(session.MonitoredSeconds)))
        {
            Reference = session.DataGaps > 0 ? $"{MetricFormatter.Plural(session.DataGaps, "interruption")}, {MetricFormatter.DurationCompact(TimeSpan.FromSeconds(session.GapSeconds))} not measured" : "No interruption",
            From = session.Start,
            To = session.End,
        };

    private static IEnumerable<AnalysisEvidence> CpuEvidence(GameSession session)
    {
        if (session.Cpu is { } cpu)
        {
            yield return Stat("CPU usage (whole PC)", cpu, CpuSource, session);
        }

        if (session.GameCpu is { } game)
        {
            yield return Stat("CPU used by the game", game, MetricSources.Processes, session);
        }
    }

    private static AnalysisEvidence Stat(string metric, MetricStat stat, string source, GameSession session) =>
        new(metric, $"Average {MetricFormatter.Percent(stat.Average)}, peak {MetricFormatter.Percent(stat.Maximum)}")
        {
            From = session.Start,
            To = session.End,
            SampleCount = stat.Samples,
            Source = source,
        };

    private static GameAppUsage? TopBackgroundApp(GameSession session) =>
        session.BackgroundApps.FirstOrDefault(a => a.CpuAverage >= 1);

    private static GameAppUsage? BiggestBackgroundMemory(GameSession session) =>
        session.BackgroundApps.Where(a => a.MemoryMaximumBytes >= 1UL << 30).MaxBy(a => a.MemoryMaximumBytes);

    private static string ConditionMetric(GameConditionKind kind) => kind switch
    {
        GameConditionKind.CpuSaturated => "CPU usage",
        GameConditionKind.MemoryNearlyFull => "Memory usage",
        GameConditionKind.VideoMemoryNearlyFull => "Video memory",
        _ => "Disk active time",
    };

    private static string ConditionSource(GameConditionKind kind) => kind switch
    {
        GameConditionKind.CpuSaturated => CpuSource,
        GameConditionKind.MemoryNearlyFull => MetricSources.Memory,
        GameConditionKind.VideoMemoryNearlyFull => "Windows performance counter \\GPU Adapter Memory(*)\\Dedicated Usage",
        _ => MetricSources.Disk,
    };

    private static TimeSpan ConditionMinimum(GameConditionKind kind) =>
        kind == GameConditionKind.DiskSaturated ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(60);

    private static GameRecapMetric Percent(string name, MetricStat? stat, string? note = null) =>
        stat is { } s
            ? new GameRecapMetric(name, MetricFormatter.Percent(s.Average), MetricFormatter.Percent(s.Maximum), note)
            : new GameRecapMetric(name, NotAvailable, NotAvailable, note);

    private static GameRecapMetric Bytes(string name, MetricStat? stat, string? note = null) =>
        stat is { } s
            ? new GameRecapMetric(name, MetricFormatter.Bytes(s.Average), MetricFormatter.Bytes(s.Maximum), note)
            : new GameRecapMetric(name, NotAvailable, NotAvailable, note);

    private static GameRecapMetric Rate(string name, MetricStat? stat) =>
        stat is { } s
            ? new GameRecapMetric(name, MetricFormatter.BitsPerSecond(s.Average), MetricFormatter.BitsPerSecond(s.Maximum))
            : new GameRecapMetric(name, NotAvailable, NotAvailable);

    private static string? Join(string? first, string second) => first is null ? second : $"{first} · {second}";

    private static string Points(double difference) =>
        string.Create(CultureInfo.CurrentCulture, $"{(difference >= 0 ? "+" : "−")}{Math.Abs(difference):0} points");

    private static string SignedBytes(double difference) =>
        $"{(difference >= 0 ? "+" : "−")}{MetricFormatter.Bytes(Math.Abs(difference))}";

    private static string SignedDuration(TimeSpan difference) =>
        $"{(difference >= TimeSpan.Zero ? "+" : "−")}{MetricFormatter.DurationCompact(difference.Duration())}";

    private static string Time(DateTimeOffset time) => time.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    private static string Lower(string text) => text.Length > 0 ? char.ToLowerInvariant(text[0]) + text[1..] : text;
}
