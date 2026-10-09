using System.Globalization;
using Sysora.Core.Formatting;
using Sysora.Core.Interfaces;
using Sysora.Core.Metrics;
using Sysora.Localization;

namespace Sysora.Core.Analysis;

/// <summary>
/// Default <see cref="IAppImpactAnalyzer"/>: deterministic, documented scoring (see <see cref="AppImpactScore.Formula"/>).
/// </summary>
/// <remarks>
/// The impact of an application on the PC is its resource use integrated over time: an application using
/// 8% of the CPU all day weighs more than one using 30% for two minutes. Each resource's load is therefore
/// "average usage while running × share of the period it was running", compared with a reference level.
/// </remarks>
public sealed class AppImpactAnalyzer : IAppImpactAnalyzer
{
    /// <summary>Average CPU load (percent of total capacity, over the period) considered very high.</summary>
    public const double CpuReferencePercent = 15;

    /// <summary>Average memory load (percent of physical memory, over the period) considered very high.</summary>
    public const double MemoryReferencePercent = 25;

    /// <summary>Average I/O load (bytes per second, over the period) considered very high.</summary>
    public const double IoReferenceBytesPerSecond = 5 * 1024 * 1024;

    private const double CpuWeight = 0.45;
    private const double MemoryWeight = 0.40;
    private const double IoWeight = 0.15;

    public AppImpactResult Analyze(AppUsageStatistics usage, AppImpactContext context)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(context);

        var score = Score(usage, context.TotalMemoryBytes);
        return new AppImpactResult(
            usage,
            score,
            Explain(usage, score),
            Confidence(usage),
            Evidence(usage, context, score),
            Trend(usage));
    }

    public IReadOnlyList<AppImpactResult> Rank(IEnumerable<AppUsageStatistics> usage, AppImpactContext context)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return usage
            .Where(u => u.Samples > 0)
            .Select(u => Analyze(u, context))
            .OrderByDescending(r => r.Score.Value)
            .ThenByDescending(r => (r.Usage.CpuAverage * r.Usage.Presence) + (r.Usage.MemoryAverageBytes * r.Usage.Presence / (1024 * 1024 * 1024)))
            .ThenBy(r => r.Usage.Identity.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Computes the score and the level of each resource.</summary>
    public static AppImpactScore Score(AppUsageStatistics usage, ulong totalMemoryBytes)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var presence = usage.Presence;

        var cpuLoad = usage.CpuAverage * presence;
        var cpuNormalized = Math.Min(cpuLoad / CpuReferencePercent, 1);
        var cpu = new ImpactComponent(
            "CPU",
            CpuLevel(cpuLoad),
            cpuLoad,
            cpuNormalized,
            Text.Format(Strings.Impact_Cpu_Description, MetricFormatter.Percent(cpuLoad, 1), MetricFormatter.Percent(usage.CpuAverage, 1)));

        var memoryShare = totalMemoryBytes > 0 ? usage.MemoryAverageBytes / totalMemoryBytes * 100 : 0;
        var memoryLoad = memoryShare * presence;
        var memoryNormalized = totalMemoryBytes > 0 ? Math.Min(memoryLoad / MemoryReferencePercent, 1) : 0;
        var memory = new ImpactComponent(
            "Memory",
            totalMemoryBytes > 0 ? MemoryLevel(memoryLoad) : ImpactLevel.Low,
            memoryLoad,
            memoryNormalized,
            totalMemoryBytes > 0
                ? Text.Format(Strings.Impact_Memory_Description, MetricFormatter.Percent(memoryLoad, 1), MetricFormatter.Bytes(usage.MemoryAverageBytes))
                : Strings.Impact_Memory_Unknown);

        var ioLoad = usage.IoAverageBytesPerSecond * presence;
        var ioNormalized = Math.Min(ioLoad / IoReferenceBytesPerSecond, 1);
        var io = new ImpactComponent(
            "Disk I/O",
            IoLevel(ioLoad),
            ioLoad,
            ioNormalized,
            Text.Format(Strings.Impact_Io_Description, MetricFormatter.BytesPerSecond(ioLoad)));

        var duration = new ImpactComponent(
            "Running time",
            presence >= 0.8 ? ImpactLevel.High : presence >= 0.3 ? ImpactLevel.Moderate : ImpactLevel.Low,
            presence,
            presence,
            Text.Format(Strings.Impact_Running_Description, MetricFormatter.Percent(presence * 100)));

        var value = (int)Math.Round(100 * ((CpuWeight * cpuNormalized) + (MemoryWeight * memoryNormalized) + (IoWeight * ioNormalized)));
        return new AppImpactScore(value, LevelOf(value), [cpu, memory, io, duration]);
    }

    /// <summary>Level of a 0–100 score.</summary>
    public static ImpactLevel LevelOf(int score) => score switch
    {
        < 10 => ImpactLevel.Low,
        < 25 => ImpactLevel.Moderate,
        < 50 => ImpactLevel.High,
        _ => ImpactLevel.VeryHigh,
    };

    /// <summary>Compares the first and second halves of the period.</summary>
    public static UsageTrend Trend(AppUsageStatistics usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (usage.FirstHalf is not { } first || usage.SecondHalf is not { } second)
        {
            return UsageTrend.Unknown;
        }

        var (cpu, cpuChange) = Direction(first.CpuAverage, second.CpuAverage, minimumDelta: 1);
        var (memory, memoryChange) = Direction(first.MemoryAverageBytes, second.MemoryAverageBytes, minimumDelta: 50 * 1024 * 1024);
        return new UsageTrend(cpu, memory, cpuChange, memoryChange);
    }

    private static (TrendDirection Direction, double? Change) Direction(double before, double after, double minimumDelta)
    {
        var delta = after - before;
        double? change = before > 0 ? delta / before * 100 : null;
        if (Math.Abs(delta) < minimumDelta || (change is { } c && Math.Abs(c) < 15))
        {
            return (TrendDirection.Stable, change);
        }

        return (delta > 0 ? TrendDirection.Rising : TrendDirection.Falling, change);
    }

    private static string Explain(AppUsageStatistics usage, AppImpactScore score)
    {
        var drivers = score.Components
            .Take(3)
            .Where(c => c.Level >= ImpactLevel.Moderate)
            .OrderByDescending(c => c.Level)
            .ThenByDescending(c => c.Normalized)
            .ToList();

        var presence = usage.Presence;
        var when = presence >= 0.8 ? Strings.Impact_When_Most
            : presence >= 0.3 ? Strings.Impact_When_Large
            : presence >= 0.05 ? Strings.Impact_When_Part
            : Strings.Impact_When_Briefly;
        var running = Text.Format(Strings.Impact_RunningOfMeasured, MetricFormatter.DurationCompact(TimeSpan.FromSeconds(usage.ActiveSeconds)), MetricFormatter.DurationCompact(TimeSpan.FromSeconds(usage.MonitoredSeconds)));

        string sentence;
        if (drivers.Count == 0)
        {
            sentence = Text.Format(Strings.Impact_Explain_Low, running);
            if (usage.CpuMaximum >= 50)
            {
                sentence += " " + Text.Format(Strings.Impact_Explain_Peaks, Percent(usage.CpuMaximum));
            }

            return sentence;
        }

        var list = Text.List(drivers.Select(c => Text.Format(Amount(c.Level), Noun(c.Resource))));
        return Text.Format(Strings.Impact_Explain_Used, list, when, running);
    }

    private static string Amount(ImpactLevel level) => level switch
    {
        ImpactLevel.VeryHigh => Strings.Impact_Amount_VeryHigh,
        ImpactLevel.High => Strings.Impact_Amount_High,
        _ => Strings.Impact_Amount_Moderate,
    };

    private static string Noun(string resource) => resource switch
    {
        "CPU" => Strings.Impact_Noun_Cpu,
        "Memory" => Strings.Impact_Noun_Memory,
        _ => Strings.Impact_Noun_Io,
    };

    private static string Percent(double value) => MetricFormatter.Percent(value);

    private static ConfidenceLevel Confidence(AppUsageStatistics usage) => usage.MonitoredSeconds switch
    {
        < 600 => ConfidenceLevel.Low,
        < 3600 => ConfidenceLevel.Medium,
        _ => ConfidenceLevel.High,
    };

    private static IReadOnlyList<AnalysisEvidence> Evidence(AppUsageStatistics usage, AppImpactContext context, AppImpactScore score)
    {
        var running = TimeSpan.FromSeconds(usage.ActiveSeconds);
        var measured = TimeSpan.FromSeconds(usage.MonitoredSeconds);
        var evidence = new List<AnalysisEvidence>
        {
            new(Strings.Impact_Ev_RunningTime, Text.Format(Strings.Impact_Ev_RunningValue, MetricFormatter.DurationCompact(running), MetricFormatter.DurationCompact(measured)))
            {
                From = context.From,
                To = context.To,
                SampleCount = usage.Samples,
                Source = Strings.Impact_Ev_RunningSource,
            },
            new(Strings.Impact_Ev_Cpu, Text.Format(Strings.Impact_Ev_CpuValue, MetricFormatter.Percent(usage.CpuAverage, 1), MetricFormatter.Percent(usage.CpuMaximum, 1)))
            {
                Reference = Text.Format(Strings.Impact_Ev_CpuReference, Percent(CpuReferencePercent)),
                Source = MetricSources.Processes,
            },
            new(Strings.Impact_Ev_Memory, Text.Format(Strings.Impact_Ev_MemoryValue, MetricFormatter.Bytes(usage.MemoryAverageBytes), MetricFormatter.Bytes(usage.MemoryMaximumBytes)))
            {
                Reference = context.TotalMemoryBytes > 0
                    ? Text.Format(Strings.Impact_Ev_MemoryReference, Percent(MemoryReferencePercent), MetricFormatter.Bytes(context.TotalMemoryBytes))
                    : MetricFormatter.NotAvailable,
                Source = MetricSources.Processes,
            },
            new(Strings.Impact_Ev_Io, Text.Format(Strings.Impact_Ev_IoValue, MetricFormatter.BytesPerSecond(usage.IoAverageBytesPerSecond), MetricFormatter.BytesPerSecond(usage.IoMaximumBytesPerSecond)))
            {
                Reference = Text.Format(Strings.Impact_Ev_IoReference, MetricFormatter.BytesPerSecond(IoReferenceBytesPerSecond)),
                Source = MetricSources.ProcessIo,
            },
            new(Strings.Impact_Ev_Network, MetricFormatter.NotAvailable)
            {
                Reference = Strings.Impact_Ev_NetworkReference,
            },
            new(Strings.Impact_Ev_Launches, usage.Launches > 0
                ? Text.Plural(usage.Launches, Strings.Impact_Ev_Launches_One, Strings.Impact_Ev_Launches_Other)
                : Strings.Impact_Ev_NoLaunch),
            new(Strings.Impact_Ev_Identification, usage.Identity.IdentificationEvidence)
            {
                Source = usage.Identity.ExecutablePath ?? Strings.Impact_Ev_ImageNameOnly,
            },
            new(Strings.Impact_Ev_Score, Text.Format(Strings.Impact_Ev_ScoreValue, score.Value, ImpactLevelText.Label(score.Level).ToLowerInvariant()))
            {
                Reference = AppImpactScore.Formula,
            },
        };

        return evidence;
    }

    private static ImpactLevel CpuLevel(double load) => load switch
    {
        < 1 => ImpactLevel.Low,
        < 4 => ImpactLevel.Moderate,
        < 10 => ImpactLevel.High,
        _ => ImpactLevel.VeryHigh,
    };

    private static ImpactLevel MemoryLevel(double load) => load switch
    {
        < 3 => ImpactLevel.Low,
        < 8 => ImpactLevel.Moderate,
        < 15 => ImpactLevel.High,
        _ => ImpactLevel.VeryHigh,
    };

    private static ImpactLevel IoLevel(double bytesPerSecond) => bytesPerSecond switch
    {
        < 100 * 1024 => ImpactLevel.Low,
        < 1024 * 1024 => ImpactLevel.Moderate,
        < 5 * 1024 * 1024 => ImpactLevel.High,
        _ => ImpactLevel.VeryHigh,
    };

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
