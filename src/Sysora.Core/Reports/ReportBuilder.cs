using System.Globalization;
using System.Text.Json.Serialization;
using Sysora.Core.Alerts;
using Sysora.Core.Analysis;
using Sysora.Core.Changes;
using Sysora.Core.Diagnosis;
using Sysora.Core.Formatting;
using Sysora.Core.Gaming;
using Sysora.Core.Health;
using Sysora.Core.History;
using Sysora.Core.Monitoring;
using Sysora.Core.Storage;
using Sysora.Core.Timeline;
using Sysora.Core.Troubleshooting;

namespace Sysora.Core.Reports;

/// <summary>
/// Turns the results of Sysora's functions into reports (pure). Every value comes from the result itself: a report
/// never adds a measurement, and lists what was not available.
/// </summary>
public static class ReportBuilder
{
    /// <summary>Limits Sysora always states, whatever the report.</summary>
    public static IReadOnlyList<string> CommonLimits { get; } =
    [
        "Temperatures: not available (Windows has no documented way to read them without a kernel driver).",
        "Network usage per application: not available (requires administrator-level event tracing).",
    ];

    public static ReportDocument PcHealth(PcHealthReport report, SelfImpactReport? self, DateTimeOffset now, ReportSystemInfo? system)
    {
        ArgumentNullException.ThrowIfNull(report);
        var areas = new ReportTable("Areas", ["Area", "State", "Impact on the score", "Measured"],
            report.Components.Select(c => (IReadOnlyList<string>)[c.Name, c.StatusText, c.ImpactText, c.Summary]).ToArray());
        var sections = new List<ReportSection>
        {
            new("Score")
            {
                Facts =
                [
                    new ReportFact("PC Health", report.Score is { } score ? $"{score}/100 · {PcHealthReport.GradeText(report.Grade)}" : MetricFormatter.NotAvailable) { Value = report.Score, Unit = "/100" },
                    new ReportFact("What lowers it", report.Summary),
                    new ReportFact("How it is computed", report.Method),
                ],
                Tables = [areas],
            },
        };
        foreach (var component in report.Components.Where(c => c.IsScored && c.Penalty > 0))
        {
            sections.Add(new ReportSection($"{component.Name}: {component.ImpactText}")
            {
                Text = component.Explanation,
                Facts = component.Evidence.Select(Fact).ToArray(),
            });
        }

        if (self is { Level: not SelfImpactLevel.Unknown })
        {
            sections.Add(new ReportSection(self.Headline)
            {
                Facts = self.Items.Select(i => new ReportFact(i.Label, i.Value) { Detail = i.Note }).ToArray(),
                Items = self.Warnings,
            });
        }

        return new ReportDocument
        {
            Kind = ReportKind.PcHealth,
            Title = "PC Health",
            GeneratedAt = now,
            System = system,
            From = report.From,
            To = report.To,
            Summary = $"{report.Headline}. {report.Summary}",
            Sections = sections,
            MissingData = report.NotAvailable,
            Data = ReportWriter.ToElement(report, ReportJsonContext.Default.PcHealthReport),
        };
    }

    public static ReportDocument Diagnosis(DiagnosisReport report, UsageComparison? usage, DateTimeOffset now, ReportSystemInfo? system)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sections = new List<ReportSection>();
        foreach (var group in new[] { ("Problems", report.Problems), ("Points to watch", report.Potential), ("Checked and normal", report.Normal) })
        {
            var results = group.Item2.ToArray();
            if (results.Length == 0)
            {
                continue;
            }

            sections.Add(new ReportSection(group.Item1)
            {
                Tables =
                [
                    new ReportTable(string.Empty, ["Finding", "Value", "Compared with", "Confidence", "Explanation"],
                        results.Select(r => (IReadOnlyList<string>)[r.Title, r.ObservedValue, r.ReferenceValue ?? "—", r.Confidence.ToString(), r.Explanation]).ToArray()),
                ],
                Items = results.Where(r => r.Recommendation is not null).Select(r => $"{r.Title}: {r.Recommendation}").ToArray(),
            });
        }

        if (usage is { Metrics.Count: > 0 })
        {
            var periods = usage.Metrics[0].Periods.Select(p => p.Period.ToString()).ToArray();
            sections.Add(new ReportSection("Compared with your usual activity")
            {
                Text = usage.Summary,
                Tables =
                [
                    new ReportTable(string.Empty, ["Metric", "Now", .. periods],
                        usage.Metrics.Select(m => (IReadOnlyList<string>)[m.Name, MetricFormatter.Percent(m.Current), .. m.Periods.Select(p => p.Average is { } a ? MetricFormatter.Percent(a) : "—")]).ToArray()),
                ],
            });
        }

        return new ReportDocument
        {
            Kind = ReportKind.Diagnosis,
            Title = "Diagnosis",
            GeneratedAt = now,
            System = system,
            From = report.From,
            To = report.To,
            Summary = $"{report.Headline}. {report.Summary}",
            Sections = sections,
            MissingData = report.NotAnalyzed,
            Notes = [report.BaselineDescription, $"{MetricFormatter.Plural(report.SampleCount, "measurement")} analyzed."],
            Data = ReportWriter.ToElement(report, ReportJsonContext.Default.DiagnosisReport),
        };
    }

    public static ReportDocument WhyNow(WhyNowExplanation explanation, DateTimeOffset now, ReportSystemInfo? system)
    {
        ArgumentNullException.ThrowIfNull(explanation);
        var facts = new List<ReportFact>();
        if (explanation.Started is { } started)
        {
            facts.Add(new ReportFact("Started", Local(started)));
        }

        if (explanation.BeforeText is { } before)
        {
            facts.Add(new ReportFact("Before", before) { Value = explanation.Before });
        }

        if (explanation.NowText is { } current)
        {
            facts.Add(new ReportFact("Now", current) { Value = explanation.Now });
        }

        if (explanation.Duration is { } duration)
        {
            facts.Add(new ReportFact("Duration", MetricFormatter.DurationPrecise(duration)) { Value = duration.TotalSeconds, Unit = "s" });
        }

        facts.Add(new ReportFact("Contributor", explanation.ContributorText));
        facts.Add(new ReportFact("Similar events", explanation.SimilarText));
        return new ReportDocument
        {
            Kind = ReportKind.WhyNow,
            Title = $"Why now? {explanation.MetricName}",
            GeneratedAt = now,
            System = system,
            From = explanation.From,
            To = explanation.Time,
            Summary = $"{explanation.Headline}. {explanation.Summary}",
            Sections =
            [
                new ReportSection("What happened") { Facts = facts, Items = explanation.Simultaneous },
                new ReportSection("Statements and what they rest on") { Findings = explanation.Findings },
                new ReportSection("Events at the same time") { Text = "A coincidence in time is not proof of cause.", Items = explanation.AssociatedEvents.Select(e => $"{Local(e.Timestamp)} · {e.Title}").ToArray() },
            ],
            MissingData = CommonLimits,
            Notes = [explanation.Source],
            Data = ReportWriter.ToElement(explanation, ReportJsonContext.Default.WhyNowExplanation),
        };
    }

    public static ReportDocument Replay(ReplayData data, DateTimeOffset now, ReportSystemInfo? system)
    {
        ArgumentNullException.ThrowIfNull(data);
        var metrics = new (HistoryMetric Metric, string Name, Func<double, string> Format)[]
        {
            (HistoryMetric.Cpu, "CPU usage", v => MetricFormatter.Percent(v)),
            (HistoryMetric.Memory, "Memory in use", v => MetricFormatter.Percent(v)),
            (HistoryMetric.Disk, "Disk active time", v => MetricFormatter.Percent(v)),
            (HistoryMetric.Gpu, "GPU usage", v => MetricFormatter.Percent(v)),
            (HistoryMetric.NetworkReceive, "Download", v => MetricFormatter.BitsPerSecond(v)),
            (HistoryMetric.NetworkSend, "Upload", v => MetricFormatter.BitsPerSecond(v)),
        };
        var rows = new List<IReadOnlyList<string>>();
        var missing = new List<string>();
        foreach (var (metric, name, format) in metrics)
        {
            if (SnapshotStatistics.Summarize(data.Points, s => s.Get(metric)) is { } summary)
            {
                rows.Add([name, format(summary.Average), format(summary.Peak), format(summary.Minimum)]);
            }
            else
            {
                missing.Add($"{name}: not measured in this period.");
            }
        }

        var export = new ReplayExport(data.From, data.To, data.IsDetailed, data.Source, data.Story,
            data.Points.Select(p => p with { TopApps = [] }).ToArray(), data.Events);
        return new ReportDocument
        {
            Kind = ReportKind.Replay,
            Title = "Performance replay",
            GeneratedAt = now,
            System = system,
            From = data.From,
            To = data.To,
            Summary = data.Story.Summary,
            Sections =
            [
                new ReportSection("Measurements") { Tables = [new ReportTable(string.Empty, ["Metric", "Average", "Peak", "Lowest"], rows)] },
                new ReportSection("What happened") { Items = data.Story.Moments.Select(m => $"{Local(m.Time)} · {m.Text}").ToArray() },
                new ReportSection("Events") { Items = data.Events.Select(e => $"{Local(e.Timestamp)} · {e.Title}" + (e.Detail is { } d ? $" ({d})" : string.Empty)).ToArray() },
            ],
            MissingData = [.. missing, .. CommonLimits],
            Notes = [data.Source, $"{MetricFormatter.Plural(data.Points.Count, "measurement")}."],
            Data = ReportWriter.ToElement(export, ReportJsonContext.Default.ReplayExport),
        };
    }

    public static ReportDocument GameSession(GameRecap recap, DateTimeOffset now, ReportSystemInfo? system)
    {
        ArgumentNullException.ThrowIfNull(recap);
        var session = recap.Session;
        return new ReportDocument
        {
            Kind = ReportKind.GameSession,
            Title = $"Game session: {session.Name}",
            GeneratedAt = now,
            System = system,
            From = session.Start,
            To = session.End,
            Summary = $"{recap.Headline}. {recap.Summary}",
            Sections =
            [
                new ReportSection("Session")
                {
                    Facts =
                    [
                        new ReportFact("Game", session.Name) { Detail = session.ExecutablePath },
                        new ReportFact("Duration", MetricFormatter.DurationPrecise(session.Duration)) { Value = session.Duration.TotalSeconds, Unit = "s" },
                        new ReportFact("Identified as a game", session.DetectionEvidence),
                        new ReportFact("Coverage", recap.Coverage),
                    ],
                },
                new ReportSection("Measurements")
                {
                    Tables = [new ReportTable(string.Empty, ["Metric", "Average", "Maximum", "Note"], recap.Metrics.Select(m => (IReadOnlyList<string>)[m.Name, m.Average, m.Maximum, m.Note ?? string.Empty]).ToArray())],
                },
                new ReportSection("Findings")
                {
                    Tables =
                    [
                        new ReportTable(string.Empty, ["Finding", "How to read it", "Confidence", "Description"],
                            recap.Findings.Select(f => (IReadOnlyList<string>)[f.Title, f.Qualifier ?? f.Kind.ToString(), f.Confidence.ToString(), f.Description]).ToArray()),
                    ],
                },
                new ReportSection("Compared with previous sessions")
                {
                    Text = recap.ComparisonNote,
                    Tables = [new ReportTable(string.Empty, ["Metric", "This session", "Previous", "Difference"], recap.Comparison.Select(c => (IReadOnlyList<string>)[c.Metric, c.ThisSession, c.Previous, c.Difference]).ToArray())],
                },
            ],
            MissingData = recap.NotAvailable,
            Data = ReportWriter.ToElement(recap, ReportJsonContext.Default.GameRecap),
        };
    }

    public static ReportDocument AppImpact(AppImpactReport report, DateTimeOffset now, ReportSystemInfo? system)
    {
        ArgumentNullException.ThrowIfNull(report);
        var rows = report.Apps.Take(50).Select((a, i) => (IReadOnlyList<string>)
        [
            (i + 1).ToString(CultureInfo.CurrentCulture),
            a.Usage.Identity.Name,
            $"{a.Score.Value} · {a.Score.Level}",
            MetricFormatter.Percent(a.Usage.CpuAverage, 1),
            MetricFormatter.Bytes(a.Usage.MemoryAverageBytes),
            MetricFormatter.BytesPerSecond(a.Usage.IoAverageBytesPerSecond),
            MetricFormatter.DurationCompact(TimeSpan.FromSeconds(a.Usage.ActiveSeconds)),
        ]).ToArray();
        return new ReportDocument
        {
            Kind = ReportKind.AppImpact,
            Title = "App Impact",
            GeneratedAt = now,
            System = system,
            From = report.From,
            To = report.To,
            Summary = report.Apps.Count == 0
                ? "No application usage recorded for this period."
                : $"Highest impact: {report.Apps[0].Usage.Identity.Name} ({report.Apps[0].Score.Level}). {report.Apps[0].Explanation}",
            Sections =
            [
                new ReportSection("Applications") { Tables = [new ReportTable(string.Empty, ["#", "Application", "Impact", "CPU (avg)", "Memory (avg)", "I/O (avg)", "Running"], rows)] },
                new ReportSection("How the score is computed") { Text = AppImpactScore.Formula },
            ],
            MissingData = [.. CommonLimits, "Disk I/O per application combines files, devices and network: Windows does not split it."],
            Notes = report.Note is { } note ? [note, $"Period: {report.Period}"] : [$"Period: {report.Period}"],
            Data = ReportWriter.ToElement(report, ReportJsonContext.Default.AppImpactReport),
        };
    }

    public static ReportDocument Alerts(IReadOnlyList<Alert> alerts, DateTimeOffset now, ReportSystemInfo? system)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        var ordered = alerts.OrderByDescending(a => a.RaisedAt).ToArray();
        var active = ordered.Count(a => a.IsActive);
        return new ReportDocument
        {
            Kind = ReportKind.Alerts,
            Title = "Alerts",
            GeneratedAt = now,
            System = system,
            From = ordered.Length > 0 ? ordered[^1].RaisedAt : null,
            To = now,
            Summary = ordered.Length == 0 ? "No alert recorded." : $"{MetricFormatter.Plural(ordered.Length, "alert")}, {active} still active.",
            Sections =
            [
                new ReportSection("Alerts")
                {
                    Tables =
                    [
                        new ReportTable(string.Empty, ["Raised", "Alert", "Severity", "Status", "Value", "Duration", "Explanation"],
                            ordered.Select(a => (IReadOnlyList<string>)[Local(a.RaisedAt), a.Title, a.Severity.ToString(), a.Status.ToString(), a.Value, MetricFormatter.DurationCompact(a.Duration), a.Explanation]).ToArray()),
                    ],
                },
            ],
            Notes = ["Alerts are raised only for problems that last or are unusual for this PC, at most a few per hour."],
            Data = ReportWriter.ToElement(ordered, ReportJsonContext.Default.AlertArray),
        };
    }

    public static ReportDocument Changes(IReadOnlyList<DetectedChange> changes, SinceYesterdaySummary? sinceYesterday, DateTimeOffset now, ReportSystemInfo? system)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var ordered = changes.OrderByDescending(c => c.Before).ToArray();
        var sections = new List<ReportSection>();
        if (sinceYesterday is { HasReference: true } summary)
        {
            sections.Add(new ReportSection("Since yesterday")
            {
                Text = $"{summary.Headline}. {summary.Note}",
                Tables =
                [
                    new ReportTable(string.Empty, ["Area", "Change", "Before", "After", "Importance", "Confidence", "When"],
                        summary.Items.Select(i => (IReadOnlyList<string>)[i.Area, i.Title, i.OldValue ?? "—", i.NewValue ?? "—", i.Importance.ToString(), i.Confidence.ToString(), i.When]).ToArray()),
                ],
                Items = summary.UnchangedAreas.Select(a => $"Unchanged: {a}").Concat(summary.NotCompared.Select(n => $"Not compared: {n}")).ToArray(),
            });
        }

        sections.Add(new ReportSection("Detected changes")
        {
            Tables =
            [
                new ReportTable(string.Empty, ["Change", "Before", "After", "Importance", "When", "Origin"],
                    ordered.Select(c => (IReadOnlyList<string>)[c.Title, c.OldValue ?? "—", c.NewValue ?? "—", c.Importance.ToString(), c.After is { } after ? $"{Local(after)} – {Local(c.Before)}" : $"Before {Local(c.Before)}", c.Origin]).ToArray()),
            ],
        });
        return new ReportDocument
        {
            Kind = ReportKind.Changes,
            Title = "Changes",
            GeneratedAt = now,
            System = system,
            From = ordered.Length > 0 ? ordered[^1].After ?? ordered[^1].Before : null,
            To = now,
            Summary = ordered.Length == 0 ? "No change recorded." : $"{MetricFormatter.Plural(ordered.Length, "change")} detected by comparing snapshots of the PC.",
            Sections = sections,
            MissingData = ["Drivers, services, scheduled tasks and individual files are not compared."],
            Notes = ["A change is dated between two snapshots unless its installer recorded a date. When its origin is not observable, it says so."],
            Data = ReportWriter.ToElement(ordered, ReportJsonContext.Default.DetectedChangeArray),
        };
    }

    public static ReportDocument Comparison(StateComparisonResult result, DateTimeOffset now, ReportSystemInfo? system)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ReportDocument
        {
            Kind = ReportKind.Comparison,
            Title = $"Comparison: {result.Before.Label} → {result.After.Label}",
            GeneratedAt = now,
            System = system,
            From = result.Before.From,
            To = result.After.To,
            Summary = result.Summary,
            Sections =
            [
                new ReportSection("Before / after")
                {
                    Tables =
                    [
                        new ReportTable(string.Empty, ["Metric", result.Before.Label, result.After.Label, "Change", "Relative", "Importance"],
                            result.Rows.Select(r => (IReadOnlyList<string>)[r.Name, r.BeforeText, r.AfterText, r.ChangeText, r.RelativeText ?? "—", r.IsComparable ? r.Importance.ToString() : "—"]).ToArray()),
                    ],
                },
            ],
            Notes = result.Notes,
            Data = ReportWriter.ToElement(result, ReportJsonContext.Default.StateComparisonResult),
        };
    }

    public static ReportDocument Timeline(IReadOnlyList<TimelineItem> items, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now, ReportSystemInfo? system)
    {
        ArgumentNullException.ThrowIfNull(items);
        var ordered = items.OrderBy(i => i.Time).ToArray();
        return new ReportDocument
        {
            Kind = ReportKind.Timeline,
            Title = "Timeline",
            GeneratedAt = now,
            System = system,
            From = from,
            To = to,
            Summary = $"{MetricFormatter.Plural(ordered.Length, "entry", "entries")} between {Local(from)} and {Local(to)}.",
            Sections =
            [
                new ReportSection("Entries")
                {
                    Tables =
                    [
                        new ReportTable(string.Empty, ["Time", "Category", "Entry", "Detail", "Source"],
                            ordered.Select(i => (IReadOnlyList<string>)[(i.IsApproximate ? "≈ " : string.Empty) + Local(i.Time), i.Category.ToString(), i.Title, i.Detail ?? string.Empty, i.Source]).ToArray()),
                    ],
                },
            ],
            Notes = ["Times are those observed by Sysora. A change found by comparing snapshots (≈) happened at the latest at the time shown."],
            Data = ReportWriter.ToElement(ordered, ReportJsonContext.Default.TimelineItemArray),
        };
    }

    public static ReportDocument Troubleshooting(TroubleshootingReport report, DateTimeOffset now, ReportSystemInfo? system)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new ReportDocument
        {
            Kind = ReportKind.Troubleshooting,
            Title = "Troubleshooting investigation",
            GeneratedAt = now,
            System = system,
            From = report.Start,
            To = report.End,
            Summary = $"{report.Headline}. {report.Summary}",
            Sections =
            [
                new ReportSection("Measurements")
                {
                    Tables = [new ReportTable(string.Empty, ["Metric", "Average", "Peak", "Measurements"], report.Metrics.Select(m => (IReadOnlyList<string>)[m.Name, m.Average, m.Peak, m.Samples.ToString(CultureInfo.CurrentCulture)]).ToArray())],
                },
                new ReportSection("Anomalies") { Findings = report.Anomalies, Text = report.Anomalies.Count == 0 ? "None measured." : null },
                new ReportSection("Correlations and likely explanations") { Findings = [.. report.Correlations, .. report.Likely] },
                new ReportSection("Applications involved")
                {
                    Tables =
                    [
                        new ReportTable(string.Empty, ["Application", "CPU (avg)", "CPU (peak)", "Memory (peak)", "I/O (avg)"],
                            report.Applications.Select(a => (IReadOnlyList<string>)[a.Name, MetricFormatter.Percent(a.CpuAverage, 1), MetricFormatter.Percent(a.CpuPeak), MetricFormatter.Bytes(a.MemoryPeakBytes), MetricFormatter.BytesPerSecond(a.IoAverageBytesPerSecond)]).ToArray()),
                    ],
                },
                new ReportSection("Events") { Items = report.Events.Select(e => $"{Local(e.Timestamp)} · {e.Title}").ToArray() },
                new ReportSection("Unknown") { Findings = report.Unknowns },
                new ReportSection("Recommendations") { Items = report.Recommendations },
            ],
            Notes = [$"Ended: {report.EndReason}. Planned duration {MetricFormatter.DurationCompact(report.Planned)}.", "Collected with the Detailed monitoring intensity."],
            Data = ReportWriter.ToElement(report, ReportJsonContext.Default.TroubleshootingReport),
        };
    }

    public static ReportDocument LargeFiles(LargeFileScanResult result, DateTimeOffset now, ReportSystemInfo? system)
    {
        ArgumentNullException.ThrowIfNull(result);
        var missing = new List<string>();
        if (result.AccessDenied > 0)
        {
            missing.Add($"{MetricFormatter.Plural(result.AccessDenied, "folder")} could not be read (access denied); their content is unknown. Examples: {string.Join(", ", result.AccessDeniedSamples.Take(5))}");
        }

        if (result.Unavailable > 0)
        {
            missing.Add($"{MetricFormatter.Plural(result.Unavailable, "folder")} unavailable during the scan.");
        }

        missing.AddRange(result.Exclusions.Select(e => $"Not scanned: {e.Path} ({e.Reason})"));
        return new ReportDocument
        {
            Kind = ReportKind.LargeFiles,
            Title = "Large files",
            GeneratedAt = now,
            System = system,
            From = result.Started,
            To = result.Finished,
            Summary = result.Message,
            Sections =
            [
                new ReportSection("Largest files")
                {
                    Text = "Sysora lists files; it never moves, modifies or deletes them.",
                    Tables =
                    [
                        new ReportTable(string.Empty, ["File", "Size", "Type", "Modified", "Folder", "Note"],
                            result.Files.Select(f => (IReadOnlyList<string>)[f.Name, MetricFormatter.Bytes(f.SizeBytes), LargeFileCategorizer.Name(f.Category), f.Modified is { } m ? Local(m) : "—", f.Directory, f.Note ?? string.Empty]).ToArray()),
                    ],
                },
                new ReportSection("By type") { Tables = [Groups(result.ByCategory)] },
                new ReportSection("By folder") { Tables = [Groups(result.ByFolder)] },
            ],
            MissingData = missing,
            Notes =
            [
                $"Scanned: {string.Join(", ", result.Roots)}; minimum size {MetricFormatter.Bytes(result.MinimumSizeBytes)}.",
                $"{result.FilesScanned.ToString("N0", CultureInfo.CurrentCulture)} files in {result.DirectoriesScanned.ToString("N0", CultureInfo.CurrentCulture)} folders; {MetricFormatter.Plural(result.SkippedLinks, "link")} not followed; {MetricFormatter.Plural(result.CloudOnlyFiles, "online-only file")} skipped.",
            ],
            Data = ReportWriter.ToElement(result, ReportJsonContext.Default.LargeFileScanResult),
        };
    }

    private static ReportTable Groups(IReadOnlyList<LargeFileGroup> groups) =>
        new(string.Empty, ["Group", "Files", "Total size"], groups.Select(g => (IReadOnlyList<string>)[g.Name, g.Count.ToString(CultureInfo.CurrentCulture), MetricFormatter.Bytes(g.TotalBytes)]).ToArray());

    private static ReportFact Fact(AnalysisEvidence evidence)
    {
        var details = new List<string>(3);
        if (evidence.Reference is { Length: > 0 } reference)
        {
            details.Add(reference);
        }

        if (evidence.From is { } from && evidence.To is { } to)
        {
            details.Add($"{Local(from)} – {Local(to)}");
        }

        if (evidence.Source is { Length: > 0 } source)
        {
            details.Add(source);
        }

        return new ReportFact(evidence.Metric, evidence.Observed) { Detail = string.Join(" · ", details) };
    }

    private static string Local(DateTimeOffset time) => time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}

/// <summary>Replay data as exported (applications per sample are left out to keep the file small).</summary>
public sealed record ReplayExport(DateTimeOffset From, DateTimeOffset To, bool IsDetailed, string Source, ReplayStory Story, IReadOnlyList<MetricSnapshot> Points, IReadOnlyList<SystemEvent> Events);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReportDocument))]
[JsonSerializable(typeof(PcHealthReport))]
[JsonSerializable(typeof(DiagnosisReport))]
[JsonSerializable(typeof(WhyNowExplanation))]
[JsonSerializable(typeof(ReplayExport))]
[JsonSerializable(typeof(GameRecap))]
[JsonSerializable(typeof(AppImpactReport))]
[JsonSerializable(typeof(Alert[]))]
[JsonSerializable(typeof(DetectedChange[]))]
[JsonSerializable(typeof(StateComparisonResult))]
[JsonSerializable(typeof(TimelineItem[]))]
[JsonSerializable(typeof(TroubleshootingReport))]
[JsonSerializable(typeof(LargeFileScanResult))]
internal sealed partial class ReportJsonContext : JsonSerializerContext;
