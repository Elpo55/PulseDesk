using System.Globalization;
using Sysora.Core.Formatting;
using Sysora.Core.Models;

namespace Sysora.Core.Monitoring;

/// <summary>How much Sysora itself weighs on the PC.</summary>
public enum SelfImpactLevel
{
    /// <summary>Not measured yet (the first measurement takes ten seconds).</summary>
    Unknown,
    Low,
    Moderate,
    High,
}

/// <summary>One line of Sysora's own impact.</summary>
/// <param name="Label">"CPU", "Memory", "Disk writes"…</param>
/// <param name="Value">Formatted value.</param>
/// <param name="Note">What it means, or how it is measured.</param>
public sealed record SelfImpactItem(string Label, string Value, string? Note = null);

/// <summary>Sysora's own impact, measured, with a plain-language level and warnings when it is not negligible.</summary>
public sealed record SelfImpactReport(SelfImpactLevel Level, string Headline, IReadOnlyList<SelfImpactItem> Items, IReadOnlyList<string> Warnings)
{
    public static SelfImpactReport Unknown { get; } = new(SelfImpactLevel.Unknown, "Sysora impact: measuring…", [], []);
}

/// <summary>
/// Turns Sysora's own measurements into a short report (pure). Thresholds are deliberately low: Sysora must show it
/// is not a significant source of load, and say so when it becomes one.
/// </summary>
public static class SelfImpactAssessor
{
    /// <summary>Below this share of total CPU, Sysora's CPU impact is low.</summary>
    public const double LowCpuPercent = 0.5;

    /// <summary>Above this working set, Sysora warns about its memory use.</summary>
    public const long HighWorkingSetBytes = 500L * 1024 * 1024;

    /// <summary>Above this .NET allocation rate, Sysora warns (garbage collections would start to cost).</summary>
    public const double HighAllocationBytesPerSecond = 20.0 * 1024 * 1024;

    /// <summary>Above this write rate, disk writes are not low.</summary>
    public const double LowWriteBytesPerSecond = 50.0 * 1024;

    public static SelfImpactReport Assess(SelfUsage usage, MonitoringScheduleInfo schedule, double budgetPercent)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(schedule);
        if (usage.CpuPercent is not { } cpu)
        {
            return SelfImpactReport.Unknown;
        }

        var warnings = new List<string>();
        var cpuLevel = cpu < LowCpuPercent ? SelfImpactLevel.Low : budgetPercent > 0 && cpu >= budgetPercent ? SelfImpactLevel.High : SelfImpactLevel.Moderate;
        if (usage.OverBudgetSustained)
        {
            warnings.Add(Text($"Sysora has used more than its CPU budget ({budgetPercent:0.#}% of total capacity) for a minute: it stretches its collection intervals ×{usage.ThrottleFactor:0.##} to stay light."));
        }

        var memoryLevel = usage.WorkingSetBytes >= HighWorkingSetBytes ? SelfImpactLevel.High : SelfImpactLevel.Low;
        if (memoryLevel == SelfImpactLevel.High)
        {
            warnings.Add($"Sysora uses {MetricFormatter.Bytes(usage.WorkingSetBytes)} of memory, more than expected. Restarting it frees the memory; please report it if it keeps growing.");
        }

        if (usage.AllocatedBytesPerSecond >= HighAllocationBytesPerSecond)
        {
            warnings.Add($"Sysora allocates {MetricFormatter.BytesPerSecond(usage.AllocatedBytesPerSecond)} of memory: more garbage collection work than usual.");
        }

        var writes = usage.WriteBytesPerSecond;
        var writeLevel = writes is null ? SelfImpactLevel.Unknown : writes < LowWriteBytesPerSecond ? SelfImpactLevel.Low : writes < 1024 * 1024 ? SelfImpactLevel.Moderate : SelfImpactLevel.High;
        var rounds = usage.CollectionRoundsPerMinute;
        var backgroundLevel = rounds is null ? SelfImpactLevel.Unknown : rounds <= 70 && cpuLevel == SelfImpactLevel.Low ? SelfImpactLevel.Low : rounds <= 150 && cpuLevel != SelfImpactLevel.High ? SelfImpactLevel.Moderate : SelfImpactLevel.High;

        var items = new List<SelfImpactItem>
        {
            new("CPU", MetricFormatter.Percent(cpu, 2), Text($"Of total capacity over the last {SelfUsageGovernor.MeasurementPeriod.TotalSeconds:0} seconds")),
            new("Memory", MetricFormatter.Bytes(usage.WorkingSetBytes), usage.ManagedHeapBytes > 0 ? $"Working set, of which {MetricFormatter.Bytes(usage.ManagedHeapBytes)} of .NET objects" : "Working set"),
            new("Allocations", usage.AllocatedBytesPerSecond is { } allocated ? MetricFormatter.BytesPerSecond(allocated) : MetricFormatter.Pending, Text($"Garbage collections in the last period: {usage.Gen0Collections} / {usage.Gen1Collections} / {usage.Gen2Collections} (generation 0 / 1 / 2)")),
            new("Disk writes", writes is { } w ? $"{LevelText(writeLevel)} · {MetricFormatter.BytesPerSecond(w)}" : MetricFormatter.NotAvailable, "Sysora's own writes (history database, settings, logs), from its entry in the process list"),
            new("Background activity", rounds is { } r ? $"{LevelText(backgroundLevel)} · {Math.Round(r):0} collection rounds per minute" : MetricFormatter.Pending, IntervalsText(schedule)),
            new("Processes analyzed", usage.ProcessesAnalyzed is { } processes ? processes.ToString("N0", CultureInfo.CurrentCulture) : MetricFormatter.Pending, schedule.Get(MetricKind.Processes) is { } every ? $"Every {Interval(every)}, in one system call" : null),
        };
        if (usage.ThreadCount is { } threads)
        {
            items.Add(new SelfImpactItem("Threads", threads.ToString(CultureInfo.CurrentCulture)));
        }

        var level = new[] { cpuLevel, memoryLevel, writeLevel, backgroundLevel }.Max();
        return new SelfImpactReport(level, $"Sysora impact: {LevelText(level)}", items, warnings);
    }

    /// <summary>"Low", "Moderate", "High".</summary>
    public static string LevelText(SelfImpactLevel level) => level switch
    {
        SelfImpactLevel.Low => "Low",
        SelfImpactLevel.Moderate => "Moderate",
        SelfImpactLevel.High => "High",
        _ => "Measuring…",
    };

    /// <summary>"Intensity Balanced: CPU 1 s · memory 1 s · processes 2 s…".</summary>
    public static string IntervalsText(MonitoringScheduleInfo schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var parts = new (MetricKind Kind, string Name)[]
        {
            (MetricKind.Cpu, "CPU"), (MetricKind.Memory, "memory"), (MetricKind.Processes, "processes"), (MetricKind.Gpu, "GPU"),
            (MetricKind.DiskActivity, "disks"), (MetricKind.Network, "network"), (MetricKind.Storage, "free space"),
        }
            .Where(p => schedule.Get(p.Kind) is not null)
            .Select(p => $"{p.Name} {Interval(schedule.Get(p.Kind)!.Value)}");
        var mode = schedule.Investigating ? "Investigation in progress (detailed)" : $"Intensity {schedule.Intensity}";
        var background = schedule.Background ? " · window hidden: on-screen metrics slowed down" : string.Empty;
        return $"{mode}: {string.Join(" · ", parts)}{background}";
    }

    private static string Interval(TimeSpan interval) =>
        interval < TimeSpan.FromSeconds(1)
            ? Text($"{interval.TotalMilliseconds:0} ms")
            : interval < TimeSpan.FromMinutes(1) ? Text($"{interval.TotalSeconds:0.#} s") : Text($"{interval.TotalMinutes:0.#} min");

    private static string Text(FormattableString text) => text.ToString(CultureInfo.CurrentCulture);
}
