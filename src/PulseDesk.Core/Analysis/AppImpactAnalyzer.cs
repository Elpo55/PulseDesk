using System.Globalization;
using PulseDesk.Core.Formatting;
using PulseDesk.Core.Interfaces;
using PulseDesk.Core.Metrics;

namespace PulseDesk.Core.Analysis;

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
            Invariant($"{cpuLoad:0.#}% of total CPU capacity on average over the period ({usage.CpuAverage:0.#}% while running)"));

        var memoryShare = totalMemoryBytes > 0 ? usage.MemoryAverageBytes / totalMemoryBytes * 100 : 0;
        var memoryLoad = memoryShare * presence;
        var memoryNormalized = totalMemoryBytes > 0 ? Math.Min(memoryLoad / MemoryReferencePercent, 1) : 0;
        var memory = new ImpactComponent(
            "Memory",
            totalMemoryBytes > 0 ? MemoryLevel(memoryLoad) : ImpactLevel.Low,
            memoryLoad,
            memoryNormalized,
            totalMemoryBytes > 0
                ? Invariant($"{memoryLoad:0.#}% of physical memory on average over the period ({MetricFormatter.Bytes(usage.MemoryAverageBytes)} while running)")
                : "Physical memory size unknown: memory not scored");

        var ioLoad = usage.IoAverageBytesPerSecond * presence;
        var ioNormalized = Math.Min(ioLoad / IoReferenceBytesPerSecond, 1);
        var io = new ImpactComponent(
            "Disk I/O",
            IoLevel(ioLoad),
            ioLoad,
            ioNormalized,
            $"{MetricFormatter.BytesPerSecond(ioLoad)} on average over the period (files, devices and network combined)");

        var duration = new ImpactComponent(
            "Running time",
            presence >= 0.8 ? ImpactLevel.High : presence >= 0.3 ? ImpactLevel.Moderate : ImpactLevel.Low,
            presence,
            presence,
            Invariant($"Running {presence * 100:0}% of the monitored time"));

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
        var when = presence >= 0.8 ? "for most of the monitored time"
            : presence >= 0.3 ? "for a large part of the monitored time"
            : presence >= 0.05 ? "for part of the monitored time"
            : "briefly";
        var running = $"{MetricFormatter.DurationCompact(TimeSpan.FromSeconds(usage.ActiveSeconds))} running of {MetricFormatter.DurationCompact(TimeSpan.FromSeconds(usage.MonitoredSeconds))} measured";

        string sentence;
        if (drivers.Count == 0)
        {
            sentence = $"Low resource usage over the period ({running}).";
            if (usage.CpuMaximum >= 50)
            {
                sentence += Invariant($" Short CPU peaks up to {usage.CpuMaximum:0}% were observed.");
            }

            return sentence;
        }

        var parts = drivers.Select(c => $"{Amount(c.Level)} {Noun(c.Resource)}").ToList();
        var list = parts.Count switch
        {
            1 => parts[0],
            2 => $"{parts[0]} and {parts[1]}",
            _ => $"{parts[0]}, {parts[1]} and {parts[2]}",
        };

        return $"This application used {list} {when} ({running}).";
    }

    private static string Amount(ImpactLevel level) => level switch
    {
        ImpactLevel.VeryHigh => "a very large amount of",
        ImpactLevel.High => "a large amount of",
        _ => "a moderate amount of",
    };

    private static string Noun(string resource) => resource switch
    {
        "CPU" => "CPU",
        "Memory" => "memory",
        _ => "disk I/O",
    };

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
            new("Running time", $"{MetricFormatter.DurationCompact(running)} of {MetricFormatter.DurationCompact(measured)} measured")
            {
                From = context.From,
                To = context.To,
                SampleCount = usage.Samples,
                Source = "PulseDesk process samples (gaps such as sleep are excluded)",
            },
            new("CPU", Invariant($"Average {usage.CpuAverage:0.#}% of total CPU while running, peak {usage.CpuMaximum:0.#}%"))
            {
                Reference = Invariant($"Reference for \"very high\": {CpuReferencePercent:0}% of total CPU on average over the period"),
                Source = MetricSources.Processes,
            },
            new("Memory", $"Average {MetricFormatter.Bytes(usage.MemoryAverageBytes)} private working set while running, peak {MetricFormatter.Bytes(usage.MemoryMaximumBytes)}")
            {
                Reference = context.TotalMemoryBytes > 0
                    ? Invariant($"Reference for \"very high\": {MemoryReferencePercent:0}% of {MetricFormatter.Bytes(context.TotalMemoryBytes)} on average over the period")
                    : MetricFormatter.NotAvailable,
                Source = MetricSources.Processes,
            },
            new("Disk I/O", $"Average {MetricFormatter.BytesPerSecond(usage.IoAverageBytesPerSecond)} while running, peak {MetricFormatter.BytesPerSecond(usage.IoMaximumBytesPerSecond)}")
            {
                Reference = $"Reference for \"very high\": {MetricFormatter.BytesPerSecond(IoReferenceBytesPerSecond)} on average over the period",
                Source = MetricSources.ProcessIo,
            },
            new("Network", MetricFormatter.NotAvailable)
            {
                Reference = "Windows does not report network usage per application without event tracing; network I/O is included in Disk I/O.",
            },
            new("Launches", usage.Launches > 0
                ? MetricFormatter.Plural(usage.Launches, "process start") + " observed"
                : "No process start observed (already running, or not restarted)"),
            new("Identification", usage.Identity.IdentificationEvidence)
            {
                Source = usage.Identity.ExecutablePath ?? "Image name only",
            },
            new("Score", Invariant($"{score.Value}/100 ({score.Level})"))
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
