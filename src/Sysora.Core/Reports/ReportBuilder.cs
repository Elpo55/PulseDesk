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
using Sysora.Localization;

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
        Strings.Report_Limit_Temperatures,
        Strings.Diag_Missing_AppNetwork,
    ];

    public static ReportDocument PcHealth(PcHealthReport report, SelfImpactReport? self, DateTimeOffset now, ReportSystemInfo? system)
    {
        ArgumentNullException.ThrowIfNull(report);
        var areas = new ReportTable(Strings.Report_Areas, [Strings.Report_Col_Area, Strings.Report_Col_State, Strings.Report_Col_ImpactScore, Strings.Report_Col_Measured],
            report.Components.Select(c => (IReadOnlyList<string>)[c.Name, c.StatusText, c.ImpactText, c.Summary]).ToArray());
        var sections = new List<ReportSection>
        {
            new(Strings.Report_Score)
            {
                Facts =
                [
                    new ReportFact(Strings.Report_PcHealth, report.Score is { } score ? $"{score}/100 · {PcHealthReport.GradeText(report.Grade)}" : MetricFormatter.NotAvailable) { Value = report.Score, Unit = "/100" },
                    new ReportFact(Strings.Report_WhatLowers, report.Summary),
                    new ReportFact(Strings.Report_HowComputed, report.Method),
                ],
                Tables = [areas],
            },
        };
        foreach (var component in report.Components.Where(c => c.IsScored && c.Penalty > 0))
        {
            sections.Add(new ReportSection(Text.Format(Strings.Common_NameValue, component.Name, component.ImpactText))
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
            Title = Strings.Report_PcHealth,
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
        foreach (var group in new[] { (Strings.Report_Problems, report.Problems), (Strings.Report_PointsToWatch, report.Potential), (Strings.Report_CheckedNormal, report.Normal) })
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
                    new ReportTable(string.Empty, [Strings.Report_Col_Finding, Strings.Report_Col_Value, Strings.Report_Col_ComparedWith, Strings.Report_Col_Confidence, Strings.Report_Col_Explanation],
                        results.Select(r => (IReadOnlyList<string>)[r.Title, r.ObservedValue, r.ReferenceValue ?? "—", ConfidenceText.Label(r.Confidence), r.Explanation]).ToArray()),
                ],
                Items = results.Where(r => r.Recommendation is not null).Select(r => Text.Format(Strings.Common_NameValue, r.Title, r.Recommendation)).ToArray(),
            });
        }

        if (usage is { Metrics.Count: > 0 })
        {
            var periods = usage.Metrics[0].Periods.Select(p => UsageComparer.Label(p.Period)).ToArray();
            sections.Add(new ReportSection(Strings.Report_ComparedUsual)
            {
                Text = usage.Summary,
                Tables =
                [
                    new ReportTable(string.Empty, [Strings.Report_Col_Metric, Strings.State_Label_Now, .. periods],
                        usage.Metrics.Select(m => (IReadOnlyList<string>)[m.Name, MetricFormatter.Percent(m.Current), .. m.Periods.Select(p => p.Average is { } a ? MetricFormatter.Percent(a) : "—")]).ToArray()),
                ],
            });
        }

        return new ReportDocument
        {
            Kind = ReportKind.Diagnosis,
            Title = Strings.Report_Diagnosis,
            GeneratedAt = now,
            System = system,
            From = report.From,
            To = report.To,
            Summary = $"{report.Headline}. {report.Summary}",
            Sections = sections,
            MissingData = report.NotAnalyzed,
            Notes = [report.BaselineDescription, Text.Plural(report.SampleCount, Strings.Report_Analyzed_One, Strings.Report_Analyzed_Other)],
            Data = ReportWriter.ToElement(report, ReportJsonContext.Default.DiagnosisReport),
        };
    }

    public static ReportDocument WhyNow(WhyNowExplanation explanation, DateTimeOffset now, ReportSystemInfo? system)
    {
        ArgumentNullException.ThrowIfNull(explanation);
        var facts = new List<ReportFact>();
        if (explanation.Started is { } started)
        {
            facts.Add(new ReportFact(Strings.WhyNow_Label_Started, Local(started)));
        }

        if (explanation.BeforeText is { } before)
        {
            facts.Add(new ReportFact(Strings.State_Label_Before, before) { Value = explanation.Before });
        }

        if (explanation.NowText is { } current)
        {
            facts.Add(new ReportFact(Strings.State_Label_Now, current) { Value = explanation.Now });
        }

        if (explanation.Duration is { } duration)
        {
            facts.Add(new ReportFact(Strings.WhyNow_Label_Duration, MetricFormatter.DurationPrecise(duration)) { Value = duration.TotalSeconds, Unit = "s" });
        }

        facts.Add(new ReportFact(Strings.WhyNow_Label_Contributor, explanation.ContributorText));
        facts.Add(new ReportFact(Strings.WhyNow_Label_Similar, explanation.SimilarText));
        return new ReportDocument
        {
            Kind = ReportKind.WhyNow,
            Title = Text.Format(Strings.Report_WhyNowTitle, explanation.MetricName),
            GeneratedAt = now,
            System = system,
            From = explanation.From,
            To = explanation.Time,
            Summary = $"{explanation.Headline}. {explanation.Summary}",
            Sections =
            [
                new ReportSection(Strings.Report_WhatHappened) { Facts = facts, Items = explanation.Simultaneous },
                new ReportSection(Strings.Report_Statements) { Findings = explanation.Findings },
                new ReportSection(Strings.Report_EventsSameTime) { Text = Strings.Report_CoincidenceNotProof, Items = explanation.AssociatedEvents.Select(e => $"{Local(e.Timestamp)} · {e.Title}").ToArray() },
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
            (HistoryMetric.Cpu, Strings.Diag_Metric_CpuUsage, v => MetricFormatter.Percent(v)),
            (HistoryMetric.Memory, Strings.State_Metric_MemoryUsed, v => MetricFormatter.Percent(v)),
            (HistoryMetric.Disk, Strings.Diag_Metric_DiskActive, v => MetricFormatter.Percent(v)),
            (HistoryMetric.Gpu, Strings.Diag_Metric_GpuUsage, v => MetricFormatter.Percent(v)),
            (HistoryMetric.NetworkReceive, Strings.State_Metric_Download, v => MetricFormatter.BitsPerSecond(v)),
            (HistoryMetric.NetworkSend, Strings.State_Metric_Upload, v => MetricFormatter.BitsPerSecond(v)),
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
                missing.Add(Text.Format(Strings.Report_NotMeasured, name));
            }
        }

        var export = new ReplayExport(data.From, data.To, data.IsDetailed, data.Source, data.Story,
            data.Points.Select(p => p with { TopApps = [] }).ToArray(), data.Events);
        return new ReportDocument
        {
            Kind = ReportKind.Replay,
            Title = Strings.Report_Replay,
            GeneratedAt = now,
            System = system,
            From = data.From,
            To = data.To,
            Summary = data.Story.Summary,
            Sections =
            [
                new ReportSection(Strings.Report_Measurements) { Tables = [new ReportTable(string.Empty, [Strings.Report_Col_Metric, Strings.Report_Col_Average, Strings.Report_Col_Peak, Strings.Report_Col_Lowest], rows)] },
                new ReportSection(Strings.Report_WhatHappened) { Items = data.Story.Moments.Select(m => $"{Local(m.Time)} · {m.Text}").ToArray() },
                new ReportSection(Strings.Report_Events) { Items = data.Events.Select(e => $"{Local(e.Timestamp)} · {e.Title}" + (e.Detail is { } d ? $" ({d})" : string.Empty)).ToArray() },
            ],
            MissingData = [.. missing, .. CommonLimits],
            Notes = [data.Source, Text.Plural(data.Points.Count, Strings.Count_Measurement_One, Strings.Count_Measurement_Other) + "."],
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
            Title = Text.Format(Strings.Report_GameTitle, session.Name),
            GeneratedAt = now,
            System = system,
            From = session.Start,
            To = session.End,
            Summary = $"{recap.Headline}. {recap.Summary}",
            Sections =
            [
                new ReportSection(Strings.Report_Session)
                {
                    Facts =
                    [
                        new ReportFact(Strings.Trouble_Game, session.Name) { Detail = session.ExecutablePath },
                        new ReportFact(Strings.WhyNow_Label_Duration, MetricFormatter.DurationPrecise(session.Duration)) { Value = session.Duration.TotalSeconds, Unit = "s" },
                        new ReportFact(Strings.Report_IdentifiedAsGame, session.DetectionEvidence),
                        new ReportFact(Strings.Report_Coverage, recap.Coverage),
                    ],
                },
                new ReportSection(Strings.Report_Measurements)
                {
                    Tables = [new ReportTable(string.Empty, [Strings.Report_Col_Metric, Strings.Report_Col_Average, Strings.Report_Col_Maximum, Strings.Report_Col_Note], recap.Metrics.Select(m => (IReadOnlyList<string>)[m.Name, m.Average, m.Maximum, m.Note ?? string.Empty]).ToArray())],
                },
                new ReportSection(Strings.Report_Findings)
                {
                    Tables =
                    [
                        new ReportTable(string.Empty, [Strings.Report_Col_Finding, Strings.Report_Col_HowToRead, Strings.Report_Col_Confidence, Strings.Report_Col_Description],
                            recap.Findings.Select(f => (IReadOnlyList<string>)[f.Title, f.Qualifier ?? GameFindingKindText.Label(f.Kind), ConfidenceText.Label(f.Confidence), f.Description]).ToArray()),
                    ],
                },
                new ReportSection(Strings.Report_ComparedPrevious)
                {
                    Text = recap.ComparisonNote,
                    Tables = [new ReportTable(string.Empty, [Strings.Report_Col_Metric, Strings.Report_Col_ThisSession, Strings.Report_Col_Previous, Strings.Report_Col_Difference], recap.Comparison.Select(c => (IReadOnlyList<string>)[c.Metric, c.ThisSession, c.Previous, c.Difference]).ToArray())],
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
            $"{a.Score.Value} · {ImpactLevelText.Label(a.Score.Level)}",
            MetricFormatter.Percent(a.Usage.CpuAverage, 1),
            MetricFormatter.Bytes(a.Usage.MemoryAverageBytes),
            MetricFormatter.BytesPerSecond(a.Usage.IoAverageBytesPerSecond),
            MetricFormatter.DurationCompact(TimeSpan.FromSeconds(a.Usage.ActiveSeconds)),
        ]).ToArray();
        return new ReportDocument
        {
            Kind = ReportKind.AppImpact,
            Title = Strings.Report_AppImpact,
            GeneratedAt = now,
            System = system,
            From = report.From,
            To = report.To,
            Summary = report.Apps.Count == 0
                ? Strings.Report_Impact_None
                : Text.Format(Strings.Report_Impact_Highest, report.Apps[0].Usage.Identity.Name, ImpactLevelText.Label(report.Apps[0].Score.Level).ToLower(CultureInfo.CurrentCulture), report.Apps[0].Explanation),
            Sections =
            [
                new ReportSection(Strings.Report_Applications) { Tables = [new ReportTable(string.Empty, ["#", Strings.Report_Col_Application, Strings.Report_Col_Impact, Strings.Report_Col_CpuAvg, Strings.Report_Col_MemoryAvg, Strings.Report_Col_IoAvg, Strings.Report_Col_Running], rows)] },
                new ReportSection(Strings.Report_HowScoreComputed) { Text = AppImpactScore.Formula },
            ],
            MissingData = [.. CommonLimits, Strings.Report_Impact_IoLimit],
            Notes = report.Note is { } note
                ? [note, Text.Format(Strings.Report_Period, AppImpactPeriodText.Label(report.Period))]
                : [Text.Format(Strings.Report_Period, AppImpactPeriodText.Label(report.Period))],
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
            Title = Strings.Report_Alerts,
            GeneratedAt = now,
            System = system,
            From = ordered.Length > 0 ? ordered[^1].RaisedAt : null,
            To = now,
            Summary = ordered.Length == 0
                ? Strings.Report_Alerts_None
                : Text.Format(Strings.Report_Alerts_Summary, Text.Plural(ordered.Length, Strings.Count_Alert_One, Strings.Count_Alert_Other), active),
            Sections =
            [
                new ReportSection(Strings.Report_Alerts)
                {
                    Tables =
                    [
                        new ReportTable(string.Empty, [Strings.Report_Col_Raised, Strings.Report_Col_Alert, Strings.Report_Col_Severity, Strings.Report_Col_Status, Strings.Report_Col_Value, Strings.WhyNow_Label_Duration, Strings.Report_Col_Explanation],
                            ordered.Select(a => (IReadOnlyList<string>)[Local(a.RaisedAt), a.Title, AlertSeverityText.Label(a.Severity), AlertStatusText.Label(a.Status), a.Value, MetricFormatter.DurationCompact(a.Duration), a.Explanation]).ToArray()),
                    ],
                },
            ],
            Notes = [Strings.Report_Alerts_Note],
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
            sections.Add(new ReportSection(Strings.Report_SinceYesterday)
            {
                Text = $"{summary.Headline}. {summary.Note}",
                Tables =
                [
                    new ReportTable(string.Empty, [Strings.Report_Col_Area, Strings.Report_Col_Change, Strings.State_Label_Before, Strings.State_Label_After, Strings.Report_Col_Importance, Strings.Report_Col_Confidence, Strings.Report_Col_When],
                        summary.Items.Select(i => (IReadOnlyList<string>)[i.Area, i.Title, i.OldValue ?? "—", i.NewValue ?? "—", ChangeImportanceText.Label(i.Importance), ConfidenceText.Label(i.Confidence), i.When]).ToArray()),
                ],
                Items = summary.UnchangedAreas.Select(a => Text.Format(Strings.Report_Unchanged, a)).Concat(summary.NotCompared.Select(n => Text.Format(Strings.Report_NotCompared, n))).ToArray(),
            });
        }

        sections.Add(new ReportSection(Strings.Report_DetectedChanges)
        {
            Tables =
            [
                new ReportTable(string.Empty, [Strings.Report_Col_Change, Strings.State_Label_Before, Strings.State_Label_After, Strings.Report_Col_Importance, Strings.Report_Col_When, Strings.Report_Col_Origin],
                    ordered.Select(c => (IReadOnlyList<string>)[c.Title, c.OldValue ?? "—", c.NewValue ?? "—", ChangeImportanceText.Label(c.Importance), c.After is { } after ? $"{Local(after)} – {Local(c.Before)}" : Text.Format(Strings.Since_Before, Local(c.Before)), c.Origin]).ToArray()),
            ],
        });
        return new ReportDocument
        {
            Kind = ReportKind.Changes,
            Title = Strings.Report_Changes,
            GeneratedAt = now,
            System = system,
            From = ordered.Length > 0 ? ordered[^1].After ?? ordered[^1].Before : null,
            To = now,
            Summary = ordered.Length == 0
                ? Strings.Report_Changes_None
                : Text.Plural(ordered.Length, Strings.Report_Changes_One, Strings.Report_Changes_Other),
            Sections = sections,
            MissingData = [Strings.Report_Changes_Limit],
            Notes = [Strings.Report_Changes_Note],
            Data = ReportWriter.ToElement(ordered, ReportJsonContext.Default.DetectedChangeArray),
        };
    }

    public static ReportDocument Comparison(StateComparisonResult result, DateTimeOffset now, ReportSystemInfo? system)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ReportDocument
        {
            Kind = ReportKind.Comparison,
            Title = Text.Format(Strings.Report_ComparisonTitle, result.Before.Label, result.After.Label),
            GeneratedAt = now,
            System = system,
            From = result.Before.From,
            To = result.After.To,
            Summary = result.Summary,
            Sections =
            [
                new ReportSection(Strings.Report_BeforeAfter)
                {
                    Tables =
                    [
                        new ReportTable(string.Empty, [Strings.Report_Col_Metric, result.Before.Label, result.After.Label, Strings.Report_Col_Evolution, Strings.Report_Col_Relative, Strings.Report_Col_Importance],
                            result.Rows.Select(r => (IReadOnlyList<string>)[r.Name, r.BeforeText, r.AfterText, r.ChangeText, r.RelativeText ?? "—", r.IsComparable ? ChangeImportanceText.Label(r.Importance) : "—"]).ToArray()),
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
            Title = Strings.Report_Timeline,
            GeneratedAt = now,
            System = system,
            From = from,
            To = to,
            Summary = Text.Format(
                Strings.Report_Timeline_Summary,
                Text.Plural(ordered.Length, Strings.Count_Entry_One, Strings.Count_Entry_Other),
                Local(from),
                Local(to)),
            Sections =
            [
                new ReportSection(Strings.Report_Entries)
                {
                    Tables =
                    [
                        new ReportTable(string.Empty, [Strings.Report_Col_Time, Strings.Report_Col_Category, Strings.Report_Col_Entry, Strings.Report_Col_Detail, Strings.Report_Col_Source],
                            ordered.Select(i => (IReadOnlyList<string>)[(i.IsApproximate ? "≈ " : string.Empty) + Local(i.Time), TimelineCategoryText.Label(i.Category), i.Title, i.Detail ?? string.Empty, i.Source]).ToArray()),
                    ],
                },
            ],
            Notes = [Strings.Report_Timeline_Note],
            Data = ReportWriter.ToElement(ordered, ReportJsonContext.Default.TimelineItemArray),
        };
    }

    public static ReportDocument Troubleshooting(TroubleshootingReport report, DateTimeOffset now, ReportSystemInfo? system)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new ReportDocument
        {
            Kind = ReportKind.Troubleshooting,
            Title = Strings.Report_Troubleshooting,
            GeneratedAt = now,
            System = system,
            From = report.Start,
            To = report.End,
            Summary = $"{report.Headline}. {report.Summary}",
            Sections =
            [
                new ReportSection(Strings.Report_Measurements)
                {
                    Tables = [new ReportTable(string.Empty, [Strings.Report_Col_Metric, Strings.Report_Col_Average, Strings.Report_Col_Peak, Strings.Report_Measurements], report.Metrics.Select(m => (IReadOnlyList<string>)[m.Name, m.Average, m.Peak, m.Samples.ToString(CultureInfo.CurrentCulture)]).ToArray())],
                },
                new ReportSection(Strings.Report_Anomalies) { Findings = report.Anomalies, Text = report.Anomalies.Count == 0 ? Strings.Report_NoneMeasured : null },
                new ReportSection(Strings.Report_Correlations) { Findings = [.. report.Correlations, .. report.Likely] },
                new ReportSection(Strings.Report_AppsInvolved)
                {
                    Tables =
                    [
                        new ReportTable(string.Empty, [Strings.Report_Col_Application, Strings.Report_Col_CpuAvg, Strings.Report_Col_CpuPeak, Strings.Report_Col_MemoryPeak, Strings.Report_Col_IoAvg],
                            report.Applications.Select(a => (IReadOnlyList<string>)[a.Name, MetricFormatter.Percent(a.CpuAverage, 1), MetricFormatter.Percent(a.CpuPeak), MetricFormatter.Bytes(a.MemoryPeakBytes), MetricFormatter.BytesPerSecond(a.IoAverageBytesPerSecond)]).ToArray()),
                    ],
                },
                new ReportSection(Strings.Report_Events) { Items = report.Events.Select(e => $"{Local(e.Timestamp)} · {e.Title}").ToArray() },
                new ReportSection(Strings.Report_Unknown) { Findings = report.Unknowns },
                new ReportSection(Strings.Report_Recommendations) { Items = report.Recommendations },
            ],
            Notes =
            [
                Text.Format(Strings.Report_Trouble_Ended, TroubleshootingEndText.Label(report.EndReason), MetricFormatter.DurationCompact(report.Planned)),
                Strings.Report_Trouble_Collected,
            ],
            Data = ReportWriter.ToElement(report, ReportJsonContext.Default.TroubleshootingReport),
        };
    }

    public static ReportDocument LargeFiles(LargeFileScanResult result, DateTimeOffset now, ReportSystemInfo? system)
    {
        ArgumentNullException.ThrowIfNull(result);
        var missing = new List<string>();
        if (result.AccessDenied > 0)
        {
            missing.Add(Text.Format(Strings.Report_Large_Denied, Text.Plural(result.AccessDenied, Strings.LargeFiles_Denied_One, Strings.LargeFiles_Denied_Other), string.Join(Strings.List_Separator, result.AccessDeniedSamples.Take(5))));
        }

        if (result.Unavailable > 0)
        {
            missing.Add(Text.Plural(result.Unavailable, Strings.Report_Large_Unavailable_One, Strings.Report_Large_Unavailable_Other));
        }

        missing.AddRange(result.Exclusions.Select(e => Text.Format(Strings.Report_Large_NotScanned, e.Path, e.Reason)));
        return new ReportDocument
        {
            Kind = ReportKind.LargeFiles,
            Title = Strings.Report_LargeFiles,
            GeneratedAt = now,
            System = system,
            From = result.Started,
            To = result.Finished,
            Summary = result.Message,
            Sections =
            [
                new ReportSection(Strings.Report_LargestFiles)
                {
                    Text = Strings.Report_Large_ReadOnly,
                    Tables =
                    [
                        new ReportTable(string.Empty, [Strings.Report_Col_File, Strings.Report_Col_Size, Strings.Report_Col_Type, Strings.Report_Col_Modified, Strings.Report_Col_Folder, Strings.Report_Col_Note],
                            result.Files.Select(f => (IReadOnlyList<string>)[f.Name, MetricFormatter.Bytes(f.SizeBytes), LargeFileCategorizer.Name(f.Category), f.Modified is { } m ? Local(m) : "—", f.Directory, f.Note ?? string.Empty]).ToArray()),
                    ],
                },
                new ReportSection(Strings.Report_ByType) { Tables = [Groups(result.ByCategory)] },
                new ReportSection(Strings.Report_ByFolder) { Tables = [Groups(result.ByFolder)] },
            ],
            MissingData = missing,
            Notes =
            [
                Text.Format(Strings.Report_Large_Scanned, string.Join(Strings.List_Separator, result.Roots), MetricFormatter.Bytes(result.MinimumSizeBytes)),
                Text.Format(
                    Strings.Report_Large_Counts,
                    Text.Plural(result.FilesScanned, Strings.Count_File_One, Strings.Count_File_Other),
                    Text.Plural(result.DirectoriesScanned, Strings.Count_Folder_One, Strings.Count_Folder_Other),
                    Text.Plural(result.SkippedLinks, Strings.Count_Link_One, Strings.Count_Link_Other),
                    Text.Plural(result.CloudOnlyFiles, Strings.Count_CloudFile_One, Strings.Count_CloudFile_Other)),
            ],
            Data = ReportWriter.ToElement(result, ReportJsonContext.Default.LargeFileScanResult),
        };
    }

    private static ReportTable Groups(IReadOnlyList<LargeFileGroup> groups) =>
        new(string.Empty, [Strings.Report_Col_Group, Strings.Report_Col_Files, Strings.Report_Col_TotalSize], groups.Select(g => (IReadOnlyList<string>)[g.Name, g.Count.ToString(CultureInfo.CurrentCulture), MetricFormatter.Bytes(g.TotalBytes)]).ToArray());

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
