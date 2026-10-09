using System.Globalization;
using Sysora.Core.Analysis;
using Sysora.Core.Diagnosis;
using Sysora.Core.Formatting;
using Sysora.Localization;

namespace Sysora.Core.Gaming;

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

    /// <summary>What Sysora could not measure, and why.</summary>
    public required IReadOnlyList<string> NotAvailable { get; init; }
}

/// <summary>Builds the recap of a gaming session from its measurements and the previous sessions (pure, deterministic).</summary>
public static class GameRecapBuilder
{
    public static string NotAvailable => Strings.Common_NotAvailable;

    /// <summary>Why Sysora shows no frame rate.</summary>
    public static string FpsNotAvailable => Strings.Game_FpsNotAvailable;

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

    private static string CpuSource => MetricSources.Cpu;

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
                Title = Strings.Game_NoLimit_Title,
                Description = Strings.Game_NoLimit_Description,
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
                0 => Strings.Game_Headline_None,
                _ => Text.Plural(problems, Strings.Game_Headline_Points_One, Strings.Game_Headline_Points_Other),
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
        var parts = new List<string> { Text.Format(Strings.Game_Summary_DurationOf, MetricFormatter.DurationPrecise(session.Duration), session.Name) };
        var averages = new List<string>();
        if (session.Cpu is { } cpu)
        {
            averages.Add(Text.Format(Strings.Game_Summary_Cpu, MetricFormatter.Percent(cpu.Average)));
        }

        if (session.Gpu is { } gpu)
        {
            averages.Add($"GPU {MetricFormatter.Percent(gpu.Average)}");
        }

        if (session.Memory is { } memory)
        {
            averages.Add(Text.Format(Strings.Game_Summary_Memory, MetricFormatter.Percent(memory.Average)));
        }

        if (averages.Count > 0)
        {
            parts.Add(Text.Format(Strings.Game_Summary_OnAverage, string.Join(Strings.List_Separator, averages)));
        }

        if (findings.FirstOrDefault(f => f.Severity >= DiagnosisSeverity.Info) is { } first)
        {
            parts.Add(first.Qualifier is { } qualifier ? Text.Format(Strings.Game_Summary_Qualified, qualifier, Lower(first.Title)) : $"{first.Title}.");
        }
        else
        {
            parts.Add(Strings.Game_Summary_NoLimit);
        }

        return string.Join(" ", parts);
    }

    private static IReadOnlyList<GameRecapMetric> Metrics(GameSession session)
    {
        var memoryTotal = session.MemoryTotalBytes is { } total ? Text.Format(Strings.Game_OfTotal, MetricFormatter.Bytes(total)) : null;
        var videoTotal = session.VideoMemoryTotalBytes is { } video ? Text.Format(Strings.Game_OfTotal, MetricFormatter.Bytes(video)) : null;
        return
        [
            new("FPS", NotAvailable, NotAvailable, Strings.Game_Fps_Note),
            Percent(Strings.Game_Metric_CpuPc, session.Cpu),
            Percent(Strings.Game_Metric_CpuGame, session.GameCpu, Strings.Game_CpuGame_Note),
            Percent(Strings.Game_Metric_GpuBusiest, session.Gpu, session.GpuName),
            session.GameGpu is null
                ? new GameRecapMetric(Strings.Game_Metric_GpuGame, NotAvailable, NotAvailable, Strings.Game_GpuGame_NotReported)
                : Percent(Strings.Game_Metric_GpuGame, session.GameGpu, Strings.Game_GpuGame_Note),
            Bytes(Strings.Game_Metric_VideoMemory, session.VideoMemoryBytes, Join(videoTotal, Strings.Game_VideoMemory_Note)),
            Percent(Strings.Game_Metric_MemoryPc, session.Memory, memoryTotal),
            Bytes(Strings.Game_Metric_MemoryGame, session.GameMemoryBytes, Strings.Game_MemoryGame_Note),
            Percent(Strings.Game_Metric_Disk, session.Disk),
            Rate(Strings.Game_Metric_NetReceived, session.NetworkReceive),
            Rate(Strings.Game_Metric_NetSent, session.NetworkSend),
            Percent(Strings.Game_Metric_SelfCpu, session.SelfCpu, Strings.Game_SelfCpu_Note),
        ];
    }

    private static IEnumerable<GameFinding> Conditions(GameSession session)
    {
        foreach (var condition in session.Conditions)
        {
            var total = MetricFormatter.DurationPrecise(TimeSpan.FromSeconds(condition.TotalSeconds));
            var periods = condition.Periods > 1 ? Text.Format(Strings.Game_OverPeriods, condition.Periods) : string.Empty;
            var evidence = new AnalysisEvidence(ConditionMetric(condition.Kind), Text.Format(Strings.Game_Condition_Value, MetricFormatter.Percent(condition.Threshold), total, periods, MetricFormatter.Percent(condition.Peak)))
            {
                Reference = Text.Format(Strings.Game_Condition_Reference, MetricFormatter.DurationCompact(ConditionMinimum(condition.Kind))),
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
                    Title = Strings.Game_Memory_Title,
                    Description = Text.Format(Strings.Game_Memory_Description, MetricFormatter.Percent(condition.Threshold), total, periods),
                    Qualifier = Strings.Game_Qualifier_Stutters,
                    Recommendation = BiggestBackgroundMemory(session) is { } app
                        ? Text.Format(Strings.Game_Memory_RecommendationApp, app.Name, MetricFormatter.Bytes(app.MemoryMaximumBytes))
                        : Strings.Game_Memory_Recommendation,
                    Confidence = longEnough ? ConfidenceLevel.High : ConfidenceLevel.Medium,
                    Evidence = [evidence],
                },
                GameConditionKind.CpuSaturated => new GameFinding
                {
                    Kind = GameFindingKind.Anomaly,
                    Severity = DiagnosisSeverity.Warning,
                    Title = Strings.Game_Cpu_Title,
                    Description = Text.Format(Strings.Game_Cpu_Description, MetricFormatter.Percent(condition.Threshold), total, periods),
                    Qualifier = Strings.Game_Qualifier_Slowdowns,
                    Recommendation = TopBackgroundApp(session) is { } app
                        ? Text.Format(Strings.Game_Cpu_RecommendationApp, app.Name, MetricFormatter.Percent(app.CpuAverage, 1))
                        : Strings.Game_Cpu_Recommendation,
                    Confidence = longEnough ? ConfidenceLevel.High : ConfidenceLevel.Medium,
                    Evidence = [evidence],
                },
                GameConditionKind.VideoMemoryNearlyFull => new GameFinding
                {
                    Kind = GameFindingKind.Anomaly,
                    Severity = DiagnosisSeverity.Warning,
                    Title = Strings.Game_Video_Title,
                    Description = Text.Format(Strings.Game_Video_Description, MetricFormatter.Percent(condition.Threshold), MetricFormatter.Bytes(session.VideoMemoryTotalBytes), total, periods),
                    Qualifier = Strings.Game_Qualifier_Stutters,
                    Recommendation = Strings.Game_Video_Recommendation,
                    Confidence = ConfidenceLevel.Medium,
                    Evidence = [evidence],
                },
                _ => new GameFinding
                {
                    Kind = GameFindingKind.Anomaly,
                    Severity = longEnough ? DiagnosisSeverity.Warning : DiagnosisSeverity.Info,
                    Title = Strings.Game_Disk_Title,
                    Description = Text.Format(Strings.Game_Disk_Description, MetricFormatter.Percent(condition.Threshold), total, periods),
                    Qualifier = Strings.Game_Qualifier_Loading,
                    Recommendation = Strings.Game_Disk_Recommendation,
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
                Title = Strings.Game_GpuBound_Title,
                Description = cpu is { } c
                    ? Text.Format(Strings.Game_GpuBound_DescriptionCpu, MetricFormatter.Percent(g.Average), MetricFormatter.Percent(c.Average))
                    : Text.Format(Strings.Game_GpuBound_Description, MetricFormatter.Percent(g.Average)),
                Qualifier = Strings.Game_Qualifier_LikelyLimit,
                Recommendation = Strings.Game_GpuBound_Recommendation,
                Confidence = ConfidenceLevel.Medium,
                Evidence = [Stat(Strings.Game_Ev_GpuBusiest, g, MetricSources.Gpu, session), .. CpuEvidence(session)],
            };
        }
        else if (cpu is { Samples: >= MinimumSamples } c && c.Average >= CpuHeavyPercent)
        {
            var gpuLow = gpu is { } low && low.Average < 70;
            yield return new GameFinding
            {
                Kind = GameFindingKind.Analysis,
                Severity = DiagnosisSeverity.Info,
                Title = Strings.Game_CpuHeavy_Title,
                Description = gpu is { } gg
                    ? Text.Format(Strings.Game_CpuHeavy_DescriptionGpu, MetricFormatter.Percent(c.Average), MetricFormatter.Percent(gg.Average))
                    : Text.Format(Strings.Game_CpuHeavy_Description, MetricFormatter.Percent(c.Average)),
                Qualifier = gpuLow ? Strings.Game_Qualifier_LikelyLimit : Strings.Game_Qualifier_Potential,
                Recommendation = Strings.Game_CpuHeavy_Recommendation,
                Confidence = gpuLow ? ConfidenceLevel.Medium : ConfidenceLevel.Low,
                Evidence = [.. CpuEvidence(session), .. gpu is { } ge ? new[] { Stat(Strings.Game_Ev_GpuBusiest, ge, MetricSources.Gpu, session) } : []],
            };
        }

        foreach (var app in session.BackgroundApps.Where(a => a.CpuAverage >= BackgroundAppPercent).Take(2))
        {
            yield return new GameFinding
            {
                Kind = GameFindingKind.Analysis,
                Severity = app.CpuAverage >= BackgroundAppPercent * 3 ? DiagnosisSeverity.Warning : DiagnosisSeverity.Info,
                Title = Text.Format(Strings.Game_BusyApp_Title, app.Name),
                Description = Text.Format(Strings.Game_BusyApp_Description, app.Name, MetricFormatter.Percent(app.CpuAverage, 1), MetricFormatter.Percent(app.CpuMaximum, 1)),
                Qualifier = Strings.Game_Qualifier_Potential,
                Recommendation = Text.Format(Strings.Game_BusyApp_Recommendation, app.Name),
                Confidence = ConfidenceLevel.Medium,
                Evidence =
                [
                    new AnalysisEvidence(Diagnosis.Rules.CpuHungryAppRule.AppCpu(app.Name), Text.Format(Strings.Game_BusyApp_Value, MetricFormatter.Percent(app.CpuAverage, 1), MetricFormatter.Percent(app.CpuMaximum, 1), MetricFormatter.Bytes(app.MemoryMaximumBytes)))
                    {
                        Reference = Strings.Game_BusyApp_Reference,
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
                Title = Strings.Game_Gaps_Title,
                Description = Text.Format(Strings.Game_Gaps_Description, Interruptions(session.DataGaps), MetricFormatter.DurationCompact(TimeSpan.FromSeconds(session.GapSeconds))),
                Confidence = ConfidenceLevel.High,
                Evidence = [Coverage(session)],
            };
        }
    }

    private static (IReadOnlyList<GameComparisonItem> Items, string Note, IReadOnlyList<GameFinding> Findings) Compare(GameSession session, IReadOnlyList<GameSession> previous)
    {
        if (previous.Count == 0)
        {
            return ([], Text.Format(Strings.Game_Compare_First, session.Name), []);
        }

        var note = previous.Count == 1
            ? Text.Format(Strings.Game_Compare_Previous, session.Name, previous[0].Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
            : Text.Format(Strings.Game_Compare_Average, previous.Count, session.Name, previous[^1].Start.ToLocalTime().ToString("d", CultureInfo.CurrentCulture));
        var reference = previous.Count == 1
            ? Strings.Game_Ref_Previous
            : Text.Format(Strings.Game_Ref_PreviousN, previous.Count);

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
                findings.Add(Difference(
                    Text.Format(Strings.Game_Compare_HigherTitle, metric),
                    Text.Format(Strings.Game_Compare_HigherDescription, metric, MetricFormatter.Percent(current.Average), Points(difference), reference, MetricFormatter.Percent(before)),
                    metric,
                    current,
                    before,
                    v => MetricFormatter.Percent(v)));
            }
        }

        ComparePercent(Strings.Game_Metric_CpuPc, s => s.Cpu);
        ComparePercent(Strings.Game_Metric_GpuBusiest, s => s.Gpu);
        ComparePercent(Strings.Game_Metric_MemoryPc, s => s.Memory);
        ComparePercent(Strings.Game_Metric_CpuGame, s => s.GameCpu);

        if (session.GameMemoryBytes is { } memory && Average(previous, s => s.GameMemoryBytes) is { } beforeMemory)
        {
            var difference = memory.Average - beforeMemory;
            var notable = Math.Abs(difference) >= NotableMemoryBytes && beforeMemory > 0 && Math.Abs(difference) / beforeMemory >= NotableMemoryRatio;
            items.Add(new GameComparisonItem(Strings.Game_Metric_MemoryGame, MetricFormatter.Bytes(memory.Average), MetricFormatter.Bytes(beforeMemory), SignedBytes(difference), notable));
            if (notable && difference > 0)
            {
                findings.Add(Difference(
                    Strings.Game_Compare_MoreMemoryTitle,
                    Text.Format(Strings.Game_Compare_MoreMemoryDescription, MetricFormatter.Bytes(memory.Average), SignedBytes(difference), reference, MetricFormatter.Bytes(beforeMemory)),
                    Strings.Game_Metric_MemoryGame,
                    memory,
                    beforeMemory,
                    v => MetricFormatter.Bytes(v)));
            }
        }

        var averageDuration = TimeSpan.FromSeconds(previous.Average(s => s.Duration.TotalSeconds));
        items.Add(new GameComparisonItem(Strings.WhyNow_Label_Duration, MetricFormatter.DurationPrecise(session.Duration), MetricFormatter.DurationPrecise(averageDuration), SignedDuration(session.Duration - averageDuration), false));

        foreach (var condition in session.Conditions)
        {
            var before = previous.Count(s => s.Conditions.Any(c => c.Kind == condition.Kind));
            if (before == 0)
            {
                findings.Add(new GameFinding
                {
                    Kind = GameFindingKind.Comparison,
                    Severity = DiagnosisSeverity.Info,
                    Title = Text.Format(Strings.Game_Compare_NewLimitTitle, ConditionMetric(condition.Kind)),
                    Description = Text.Format(Strings.Game_Compare_NewLimitDescription, reference, session.Name),
                    Qualifier = Strings.Game_Qualifier_ObservedChange,
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
            Qualifier = Strings.Game_Qualifier_ObservedChange,
            Recommendation = Strings.Game_Compare_Recommendation,
            Confidence = previous.Count >= 3 ? ConfidenceLevel.Medium : ConfidenceLevel.Low,
            Evidence =
            [
                new AnalysisEvidence(metric, Text.Format(Strings.Game_Compare_ThisSession, format(current.Average), format(current.Maximum)))
                {
                    Reference = Text.Format(Strings.Game_Compare_PreviousSessions, format(before)),
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
            missing.Add(Strings.Game_Missing_Gpu);
        }
        else if (session.GameGpu is null)
        {
            missing.Add(Strings.Game_Missing_GpuGame);
        }

        if (session.VideoMemoryBytes is null)
        {
            missing.Add(Strings.Game_Missing_Video);
        }

        missing.Add(Strings.Game_Missing_Temperatures);
        missing.Add(Strings.Game_Missing_Network);
        return missing;
    }

    private static string CoverageText(GameSession session)
    {
        var text = Text.Format(Strings.Game_Coverage, Time(session.Start), Time(session.End), MetricFormatter.DurationPrecise(TimeSpan.FromSeconds(session.MonitoredSeconds)));
        if (session.GameStartedAt is { } started)
        {
            text += " " + Text.Format(Strings.Game_Coverage_AlreadyRunning, Time(started));
        }

        if (session.DataGaps > 0)
        {
            text += " " + Text.Format(Strings.Game_Coverage_Gaps, Interruptions(session.DataGaps), MetricFormatter.DurationCompact(TimeSpan.FromSeconds(session.GapSeconds)));
        }

        text += session.EndReason switch
        {
            GameSessionEnd.SysoraClosed => " " + Strings.Game_Coverage_SysoraClosed,
            GameSessionEnd.TrackingStopped => " " + Strings.Game_Coverage_TrackingStopped,
            _ => string.Empty,
        };
        return text;
    }

    private static AnalysisEvidence Coverage(GameSession session) =>
        new(Strings.Game_Ev_MeasuredTime, MetricFormatter.DurationPrecise(TimeSpan.FromSeconds(session.MonitoredSeconds)))
        {
            Reference = session.DataGaps > 0
                ? Text.Format(Strings.Game_Ev_Gaps, Interruptions(session.DataGaps), MetricFormatter.DurationCompact(TimeSpan.FromSeconds(session.GapSeconds)))
                : Strings.Game_Ev_NoGap,
            From = session.Start,
            To = session.End,
        };

    private static IEnumerable<AnalysisEvidence> CpuEvidence(GameSession session)
    {
        if (session.Cpu is { } cpu)
        {
            yield return Stat(Strings.Game_Ev_CpuPc, cpu, CpuSource, session);
        }

        if (session.GameCpu is { } game)
        {
            yield return Stat(Strings.Game_Metric_CpuGame, game, MetricSources.Processes, session);
        }
    }

    private static AnalysisEvidence Stat(string metric, MetricStat stat, string source, GameSession session) =>
        new(metric, Text.Format(Strings.Diag_AveragePeak, MetricFormatter.Percent(stat.Average), MetricFormatter.Percent(stat.Maximum)))
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
        GameConditionKind.CpuSaturated => Strings.Diag_Metric_CpuUsage,
        GameConditionKind.MemoryNearlyFull => Strings.Diag_Metric_MemoryUsage,
        GameConditionKind.VideoMemoryNearlyFull => Strings.Game_Condition_Video,
        _ => Strings.Diag_Metric_DiskActive,
    };

    private static string ConditionSource(GameConditionKind kind) => kind switch
    {
        GameConditionKind.CpuSaturated => CpuSource,
        GameConditionKind.MemoryNearlyFull => MetricSources.Memory,
        GameConditionKind.VideoMemoryNearlyFull => Strings.Source_VideoMemory,
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
        (difference >= 0 ? "+" : "−") + Text.Format(Strings.Game_Points, Math.Abs(difference));

    private static string SignedBytes(double difference) =>
        $"{(difference >= 0 ? "+" : "−")}{MetricFormatter.Bytes(Math.Abs(difference))}";

    private static string SignedDuration(TimeSpan difference) =>
        $"{(difference >= TimeSpan.Zero ? "+" : "−")}{MetricFormatter.DurationCompact(difference.Duration())}";

    private static string Time(DateTimeOffset time) => time.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    private static string Lower(string text) => text.Length > 0 ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    private static string Interruptions(int count) =>
        Text.Plural(count, Strings.Count_Interruption_One, Strings.Count_Interruption_Other);
}

/// <summary>Kinds of game findings in words.</summary>
public static class GameFindingKindText
{
    public static string Label(GameFindingKind kind) => kind switch
    {
        GameFindingKind.Anomaly => Strings.GameFinding_Anomaly,
        GameFindingKind.Analysis => Strings.GameFinding_Analysis,
        _ => Strings.GameFinding_Comparison,
    };
}
