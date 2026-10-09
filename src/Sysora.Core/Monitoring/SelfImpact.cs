using System.Globalization;
using Sysora.Core.Formatting;
using Sysora.Core.Models;
using Sysora.Localization;

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
    public static SelfImpactReport Unknown { get; } = new(SelfImpactLevel.Unknown, Strings.Self_Measuring, [], []);
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
            warnings.Add(Text.Format(Strings.Self_OverBudget, MetricFormatter.Percent(budgetPercent, 1), usage.ThrottleFactor));
        }

        var memoryLevel = usage.WorkingSetBytes >= HighWorkingSetBytes ? SelfImpactLevel.High : SelfImpactLevel.Low;
        if (memoryLevel == SelfImpactLevel.High)
        {
            warnings.Add(Text.Format(Strings.Self_HighMemory, MetricFormatter.Bytes(usage.WorkingSetBytes)));
        }

        if (usage.AllocatedBytesPerSecond >= HighAllocationBytesPerSecond)
        {
            warnings.Add(Text.Format(Strings.Self_HighAllocations, MetricFormatter.BytesPerSecond(usage.AllocatedBytesPerSecond)));
        }

        var writes = usage.WriteBytesPerSecond;
        var writeLevel = writes is null ? SelfImpactLevel.Unknown : writes < LowWriteBytesPerSecond ? SelfImpactLevel.Low : writes < 1024 * 1024 ? SelfImpactLevel.Moderate : SelfImpactLevel.High;
        var rounds = usage.CollectionRoundsPerMinute;
        var backgroundLevel = rounds is null ? SelfImpactLevel.Unknown : rounds <= 70 && cpuLevel == SelfImpactLevel.Low ? SelfImpactLevel.Low : rounds <= 150 && cpuLevel != SelfImpactLevel.High ? SelfImpactLevel.Moderate : SelfImpactLevel.High;

        var items = new List<SelfImpactItem>
        {
            new(Strings.Self_Cpu, MetricFormatter.Percent(cpu, 2), Text.Format(Strings.Self_Cpu_Note, SelfUsageGovernor.MeasurementPeriod.TotalSeconds)),
            new(Strings.Self_Memory, MetricFormatter.Bytes(usage.WorkingSetBytes), usage.ManagedHeapBytes > 0
                ? Text.Format(Strings.Self_Memory_NoteManaged, MetricFormatter.Bytes(usage.ManagedHeapBytes))
                : Strings.Self_Memory_Note),
            new(Strings.Self_Allocations, usage.AllocatedBytesPerSecond is { } allocated ? MetricFormatter.BytesPerSecond(allocated) : MetricFormatter.Pending, Text.Format(Strings.Self_Allocations_Note, usage.Gen0Collections, usage.Gen1Collections, usage.Gen2Collections)),
            new(Strings.Self_DiskWrites, writes is { } w ? $"{LevelText(writeLevel)} · {MetricFormatter.BytesPerSecond(w)}" : MetricFormatter.NotAvailable, Strings.Self_DiskWrites_Note),
            new(Strings.Self_Background, rounds is { } r ? Text.Format(Strings.Self_Background_Value, LevelText(backgroundLevel), Math.Round(r)) : MetricFormatter.Pending, IntervalsText(schedule)),
            new(Strings.Self_Processes, usage.ProcessesAnalyzed is { } processes ? processes.ToString("N0", CultureInfo.CurrentCulture) : MetricFormatter.Pending, schedule.Get(MetricKind.Processes) is { } every ? Text.Format(Strings.Self_Processes_Note, Interval(every)) : null),
        };
        if (usage.ThreadCount is { } threads)
        {
            items.Add(new SelfImpactItem(Strings.Self_Threads, threads.ToString(CultureInfo.CurrentCulture)));
        }

        var level = new[] { cpuLevel, memoryLevel, writeLevel, backgroundLevel }.Max();
        return new SelfImpactReport(level, Text.Format(Strings.Self_Headline, LevelText(level)), items, warnings);
    }

    /// <summary>"Low", "Moderate", "High".</summary>
    public static string LevelText(SelfImpactLevel level) => level switch
    {
        SelfImpactLevel.Low => Strings.Self_Level_Low,
        SelfImpactLevel.Moderate => Strings.Self_Level_Moderate,
        SelfImpactLevel.High => Strings.Self_Level_High,
        _ => Strings.Self_Level_Measuring,
    };

    /// <summary>"Intensity Balanced: CPU 1 s · memory 1 s · processes 2 s…".</summary>
    public static string IntervalsText(MonitoringScheduleInfo schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var parts = new (MetricKind Kind, string Name)[]
        {
            (MetricKind.Cpu, Strings.Self_Interval_Cpu), (MetricKind.Memory, Strings.Self_Interval_Memory), (MetricKind.Processes, Strings.Self_Interval_Processes), (MetricKind.Gpu, Strings.Self_Interval_Gpu),
            (MetricKind.DiskActivity, Strings.Self_Interval_Disks), (MetricKind.Network, Strings.Self_Interval_Network), (MetricKind.Storage, Strings.Self_Interval_Space),
        }
            .Where(p => schedule.Get(p.Kind) is not null)
            .Select(p => $"{p.Name} {Interval(schedule.Get(p.Kind)!.Value)}");
        var mode = schedule.Investigating
            ? Strings.Self_Investigating
            : Text.Format(Strings.Self_Intensity, MonitoringProfile.Name(schedule.Intensity));
        var background = schedule.Background ? " · " + Strings.Self_Hidden : string.Empty;
        return Text.Format(Strings.Common_NameValue, mode, string.Join(" · ", parts) + background);
    }

    private static string Interval(TimeSpan interval) =>
        interval < TimeSpan.FromSeconds(1)
            ? string.Create(CultureInfo.CurrentCulture, $"{interval.TotalMilliseconds:0} ms")
            : interval < TimeSpan.FromMinutes(1)
                ? string.Create(CultureInfo.CurrentCulture, $"{interval.TotalSeconds:0.#} s")
                : string.Create(CultureInfo.CurrentCulture, $"{interval.TotalMinutes:0.#} min");
}
