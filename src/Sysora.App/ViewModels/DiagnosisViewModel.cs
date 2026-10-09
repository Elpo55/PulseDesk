using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sysora.App.Services;
using Sysora.Core.Analysis;
using Sysora.Core.Diagnosis;
using Sysora.Core.Formatting;
using Sysora.Core.Models;
using Sysora.Core.Reports;
using Sysora.Localization;

namespace Sysora.App.ViewModels;

/// <summary>
/// Diagnosis: "why is my PC slow?". Shows the overall state, the problems detected, potential issues, what is
/// normal, recommendations, and what could not be analyzed, each with its evidence.
/// </summary>
public sealed partial class DiagnosisViewModel : PageViewModel
{
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(10);

    private readonly DiagnosisService _diagnosis;
    private readonly UsageComparisonService _comparison;
    private readonly WhyNowService _whyNow;
    private readonly RecurringProblemService _recurring;
    private readonly NavigationRequests _requests;
    private readonly ReportExportService _export;
    private readonly InsightNavigator _navigator;
    private readonly ILogger<DiagnosisViewModel> _logger;
    private readonly Dictionary<string, DiagnosisItemViewModel> _items = new(StringComparer.Ordinal);
    private DiagnosisReport _report = DiagnosisReport.Empty;
    private UsageComparison? _usage;
    private WhyNowExplanation? _explanation;
    private CancellationTokenSource? _whyNowLoad;
    private long _lastRun;

    public DiagnosisViewModel(
        UiMetricsHub hub,
        DiagnosisService diagnosis,
        UsageComparisonService comparison,
        WhyNowService whyNow,
        RecurringProblemService recurring,
        NavigationRequests requests,
        ReportExportService export,
        InsightNavigator navigator,
        ILogger<DiagnosisViewModel> logger)
        : base(hub)
    {
        _diagnosis = diagnosis;
        _comparison = comparison;
        _whyNow = whyNow;
        _recurring = recurring;
        _requests = requests;
        _export = export;
        ComparisonSummary = UsageComparison.Empty.Summary;
        _navigator = navigator;
        _logger = logger;
        WhyNowHeadline = WhyNowSummary = WhyNowStarted = WhyNowChange = WhyNowDuration = WhyNowContributor = WhyNowSimilar = WhyNowUsual = RecurringText = string.Empty;
        _requests.Requested += (_, kind) =>
        {
            if (kind == NavigationRequestKind.WhyNow && IsActive && _requests.TakeWhyNow() is { } metric)
            {
                StartWhyNow(metric);
            }
        };
        Headline = DiagnosisReport.Empty.Headline;
        Summary = DiagnosisReport.Empty.Summary;
        AnalyzedText = BaselineText = string.Empty;
        StateGlyph = HealthGlyphs.Unknown;
        StateBrushKey = "StatusUnknownBrush";
    }

    public ObservableCollection<DiagnosisItemViewModel> Problems { get; } = [];

    public ObservableCollection<DiagnosisItemViewModel> Potential { get; } = [];

    public ObservableCollection<DiagnosisItemViewModel> NormalItems { get; } = [];

    public ObservableCollection<string> Recommendations { get; } = [];

    public ObservableCollection<string> NotAnalyzed { get; } = [];

    /// <summary>Current activity compared with the last hour, today, yesterday, 7 and 30 days, and the usual level at this hour.</summary>
    public ObservableCollection<ComparisonRowViewModel> ComparisonRows { get; } = [];

    [ObservableProperty]
    public partial string ComparisonSummary { get; set; }

    [ObservableProperty]
    public partial bool HasComparisonRows { get; set; }

    [ObservableProperty]
    public partial string Headline { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial string StateGlyph { get; set; }

    [ObservableProperty]
    public partial string StateBrushKey { get; set; }

    [ObservableProperty]
    public partial string AnalyzedText { get; set; }

    [ObservableProperty]
    public partial string BaselineText { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool HasProblems { get; set; }

    [ObservableProperty]
    public partial bool HasPotential { get; set; }

    [ObservableProperty]
    public partial bool HasRecommendations { get; set; }

    [RelayCommand]
    private Task Run() => RunAsync();

    // ---- Why now? -------------------------------------------------------------------------------

    public IReadOnlyList<string> WhyNowMetrics { get; } =
    [
        UiStrings.Common_CPU,
        UiStrings.Common_Memory,
        UiStrings.Replay_Disk,
        UiStrings.Common_GPU,
        UiStrings.Common_Network,
    ];

    public ObservableCollection<FindingItemViewModel> WhyNowFindings { get; } = [];

    public ObservableCollection<string> WhyNowEvents { get; } = [];

    [ObservableProperty]
    public partial int WhyNowIndex { get; set; }

    [ObservableProperty]
    public partial bool IsWhyNowRunning { get; set; }

    [ObservableProperty]
    public partial bool HasWhyNow { get; set; }

    [ObservableProperty]
    public partial bool IsWhyNowSignificant { get; set; }

    [ObservableProperty]
    public partial string WhyNowHeadline { get; set; }

    [ObservableProperty]
    public partial string WhyNowSummary { get; set; }

    [ObservableProperty]
    public partial string WhyNowStarted { get; set; }

    [ObservableProperty]
    public partial string WhyNowChange { get; set; }

    [ObservableProperty]
    public partial string WhyNowDuration { get; set; }

    [ObservableProperty]
    public partial string WhyNowContributor { get; set; }

    [ObservableProperty]
    public partial string WhyNowSimilar { get; set; }

    [ObservableProperty]
    public partial string WhyNowUsual { get; set; }

    [ObservableProperty]
    public partial bool HasWhyNowEvents { get; set; }

    /// <summary>Recurring problems of the last days, in one line, linking to PC Health.</summary>
    [ObservableProperty]
    public partial string RecurringText { get; set; }

    [RelayCommand]
    private Task AnalyzeWhyNow() => WhyNowAsync((WhyNowMetric)Math.Clamp(WhyNowIndex, 0, 4));

    [RelayCommand]
    private Task Export()
    {
        var report = _report;
        var usage = _usage;
        return _export.ExportAsync(system => ReportBuilder.Diagnosis(report, usage, DateTimeOffset.Now, system));
    }

    [RelayCommand]
    private Task ExportWhyNow()
    {
        if (_explanation is not { } explanation)
        {
            return Task.CompletedTask;
        }

        return _export.ExportAsync(system => ReportBuilder.WhyNow(explanation, DateTimeOffset.Now, system));
    }

    [RelayCommand]
    private void OpenRecurring() => _navigator.Open(DiagnosisAction.PcHealth);

    [RelayCommand]
    private void OpenTimeline() => _navigator.Open(DiagnosisAction.Timeline);

    /// <summary>Selects a metric and explains it (from a finding's "Why now?" button or another page).</summary>
    public void StartWhyNow(WhyNowMetric metric)
    {
        WhyNowIndex = (int)metric;
        _ = WhyNowAsync(metric);
    }

    private async Task WhyNowAsync(WhyNowMetric metric)
    {
        _whyNowLoad?.Cancel();
        _whyNowLoad?.Dispose();
        var load = _whyNowLoad = new CancellationTokenSource();
        IsWhyNowRunning = true;
        try
        {
            var explanation = await _whyNow.ExplainAsync(metric, load.Token);
            if (!load.IsCancellationRequested)
            {
                ApplyWhyNow(explanation);
            }
        }
        catch (OperationCanceledException)
        {
            // Another metric was chosen.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Why now could not be analyzed.");
            WhyNowHeadline = UiStrings.Diagnosis_WhyNowFailed;
        }
        finally
        {
            if (_whyNowLoad == load)
            {
                IsWhyNowRunning = false;
            }
        }
    }

    private void ApplyWhyNow(WhyNowExplanation explanation)
    {
        _explanation = explanation;
        HasWhyNow = true;
        IsWhyNowSignificant = explanation.IsSignificant;
        WhyNowHeadline = explanation.Headline;
        WhyNowSummary = explanation.Summary;
        WhyNowStarted = explanation.Started is { } started
            ? started.ToLocalTime().ToString("T", System.Globalization.CultureInfo.CurrentCulture)
            : explanation.StartedBeforeData ? UiStrings.Diagnosis_BeforeData : "—";
        WhyNowChange = (explanation.BeforeText, explanation.NowText) switch
        {
            ({ } before, { } now) => $"{before} → {now}",
            (null, { } now) => now,
            _ => "—",
        };
        WhyNowDuration = explanation.Duration is { } duration
            ? explanation.StartedBeforeData ? Text.Format(Strings.WhyNow_AtLeast, MetricFormatter.DurationPrecise(duration)) : MetricFormatter.DurationPrecise(duration)
            : "—";
        WhyNowContributor = explanation.ContributorText;
        WhyNowSimilar = explanation.SimilarText;
        WhyNowUsual = explanation.UsualText ?? UiStrings.Diagnosis_UsualNotKnown;
        WhyNowFindings.Clear();
        foreach (var finding in explanation.Findings)
        {
            WhyNowFindings.Add(FindingItemViewModel.From(finding));
        }

        WhyNowEvents.Clear();
        foreach (var associated in explanation.AssociatedEvents)
        {
            WhyNowEvents.Add($"{associated.Timestamp.ToLocalTime().ToString("T", System.Globalization.CultureInfo.CurrentCulture)} · {associated.Title}");
        }

        foreach (var simultaneous in explanation.Simultaneous)
        {
            WhyNowEvents.Add(simultaneous);
        }

        HasWhyNowEvents = WhyNowEvents.Count > 0;
    }


    protected override void OnActivated()
    {
        _ = RunAsync();
        if (_requests.TakeWhyNow() is { } metric)
        {
            StartWhyNow(metric);
        }
    }

    protected override void Update(SystemSnapshot snapshot, MetricKind updated)
    {
        if ((updated & MetricKind.Cpu) != 0 && Environment.TickCount64 - _lastRun > AutoRefreshInterval.TotalMilliseconds && !IsRunning)
        {
            _ = RunAsync();
        }
    }

    private async Task RunAsync()
    {
        _lastRun = Environment.TickCount64;
        IsRunning = true;
        try
        {
            Apply(await _diagnosis.RunAsync(CancellationToken.None));
            ApplyComparison(await _comparison.CompareAsync(CancellationToken.None));
            var recurring = await _recurring.GetAsync(force: false, CancellationToken.None);
            RecurringText = recurring.Time == DateTimeOffset.MinValue ? string.Empty : recurring.Summary;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Diagnosis failed.");
            Summary = UiStrings.Diagnosis_Failed;
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void Apply(DiagnosisReport report)
    {
        _report = report;
        Headline = report.Headline;
        Summary = report.Summary;
        (StateGlyph, StateBrushKey) = HealthGlyphs.For(report.State);
        AnalyzedText = report.To is { } to && report.From is { } from
            ? Text.Format(UiStrings.Diagnosis_Analyzed, MetricFormatter.DurationPrecise(to - from), Text.Plural(report.SampleCount, Strings.Count_Measurement_One, Strings.Count_Measurement_Other), InsightDisplay.Time(report.Timestamp))
            : string.Empty;
        BaselineText = report.BaselineDescription;

        Sync(Problems, report.Problems);
        Sync(Potential, report.Potential);
        Sync(NormalItems, report.Normal);
        HasProblems = Problems.Count > 0;
        HasPotential = Potential.Count > 0;

        var recommendations = report.Results
            .Where(r => r.Severity > DiagnosisSeverity.Normal && r.Recommendation is not null)
            .Select(r => r.Recommendation!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Replace(Recommendations, recommendations);
        HasRecommendations = Recommendations.Count > 0;
        Replace(NotAnalyzed, report.NotAnalyzed);

        var current = report.Results.Select(Key).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _items.Keys.Where(k => !current.Contains(k)).ToArray())
        {
            _items.Remove(stale);
        }
    }

    /// <summary>
    /// Updates a list in place, reusing the item of each rule, so an expanded item stays expanded when the
    /// diagnosis refreshes.
    /// </summary>
    private void Sync(ObservableCollection<DiagnosisItemViewModel> target, IEnumerable<DiagnosisResult> results)
    {
        var desired = results.Select(r =>
        {
            var key = Key(r);
            if (!_items.TryGetValue(key, out var item))
            {
                item = new DiagnosisItemViewModel(i => _navigator.Open(i.Action, i.AppKey), i =>
                {
                    if (i.WhyNowMetric is { } metric)
                    {
                        StartWhyNow(metric);
                    }
                });
                _items[key] = item;
            }

            item.Set(r);
            return item;
        }).ToList();

        for (var i = 0; i < desired.Count; i++)
        {
            var current = target.IndexOf(desired[i]);
            if (current == i)
            {
                continue;
            }

            if (current > i)
            {
                target.Move(current, i);
            }
            else
            {
                target.Insert(i, desired[i]);
            }
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private void ApplyComparison(UsageComparison comparison)
    {
        _usage = comparison;
        ComparisonSummary = comparison.Summary;
        CollectionSync.Resize(ComparisonRows, comparison.Metrics.Count, _ => new ComparisonRowViewModel(), (row, i) => row.Set(comparison.Metrics[i]));
        HasComparisonRows = ComparisonRows.Count > 0;
    }

    private static string Key(DiagnosisResult result) => result.RuleId;

    private static void Replace(ObservableCollection<string> target, IReadOnlyList<string> values)
    {
        if (target.SequenceEqual(values))
        {
            return;
        }

        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }
}

/// <summary>One diagnosis result.</summary>
public sealed partial class DiagnosisItemViewModel : ObservableObject
{
    private readonly Action<DiagnosisItemViewModel> _open;
    private readonly Action<DiagnosisItemViewModel>? _whyNow;

    public DiagnosisItemViewModel(Action<DiagnosisItemViewModel> open, Action<DiagnosisItemViewModel>? whyNow = null)
    {
        _open = open;
        _whyNow = whyNow;
        Title = Description = Glyph = BrushKey = Observed = Reference = Explanation = Recommendation = ConfidenceText = TimeText = ActionLabel = string.Empty;
    }

    public ObservableCollection<EvidenceItemViewModel> Evidence { get; } = [];

    public DiagnosisAction Action { get; private set; }

    public string? AppKey { get; private set; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    public partial string Glyph { get; set; }

    [ObservableProperty]
    public partial string BrushKey { get; set; }

    [ObservableProperty]
    public partial string Observed { get; set; }

    [ObservableProperty]
    public partial string Reference { get; set; }

    [ObservableProperty]
    public partial string Explanation { get; set; }

    [ObservableProperty]
    public partial string Recommendation { get; set; }

    [ObservableProperty]
    public partial bool HasRecommendation { get; set; }

    [ObservableProperty]
    public partial string ConfidenceText { get; set; }

    [ObservableProperty]
    public partial string TimeText { get; set; }

    [ObservableProperty]
    public partial string ActionLabel { get; set; }

    [ObservableProperty]
    public partial bool HasAction { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Metric "Why now?" can explain for this finding, when there is one.</summary>
    public WhyNowMetric? WhyNowMetric { get; private set; }

    [ObservableProperty]
    public partial bool CanExplainWhyNow { get; set; }

    [RelayCommand]
    private void Open() => _open(this);

    [RelayCommand]
    private void WhyNow() => _whyNow?.Invoke(this);

    public void Set(DiagnosisResult result)
    {
        WhyNowMetric = result.Severity > DiagnosisSeverity.Normal ? InsightNavigator.WhyNowFor(result.Category) : null;
        CanExplainWhyNow = WhyNowMetric is not null && _whyNow is not null;
        Title = result.Title;
        Description = result.Description;
        (Glyph, BrushKey) = HealthGlyphs.For(result.Severity);
        Observed = result.Duration is { } duration && duration > TimeSpan.Zero
            ? Text.Format(UiStrings.Diagnosis_ObservedFor, result.Metric, result.ObservedValue, MetricFormatter.DurationPrecise(duration))
            : Text.Format(Strings.Common_NameValue, result.Metric, result.ObservedValue);
        Reference = result.ReferenceValue is { } reference ? Text.Format(UiStrings.Diagnosis_Reference, reference) : string.Empty;
        Explanation = result.Explanation;
        Recommendation = result.Recommendation ?? string.Empty;
        HasRecommendation = result.Recommendation is not null;
        ConfidenceText = InsightDisplay.Text(result.Confidence);
        TimeText = Text.Format(UiStrings.Diagnosis_ObservedAt, InsightDisplay.Time(result.Timestamp));
        Action = result.Action;
        AppKey = result.AppKey;
        ActionLabel = InsightNavigator.Label(result.Action);
        HasAction = ActionLabel.Length > 0;

        var evidence = result.Evidence.Select(EvidenceItemViewModel.From).ToList();
        if (!Evidence.SequenceEqual(evidence))
        {
            Evidence.Clear();
            foreach (var item in evidence)
            {
                Evidence.Add(item);
            }
        }
    }
}

/// <summary>Glyphs and brush keys for severities and overall states.</summary>
internal static class HealthGlyphs
{
    public const string Unknown = "";

    public static (string Glyph, string BrushKey) For(DiagnosisSeverity severity) => severity switch
    {
        DiagnosisSeverity.Critical => (InsightDisplay.CriticalGlyph, "StatusCriticalBrush"),
        DiagnosisSeverity.Warning => (InsightDisplay.WarningGlyph, "StatusWarningBrush"),
        DiagnosisSeverity.Info => (InsightDisplay.InfoGlyph, "StatusInfoBrush"),
        _ => (InsightDisplay.NormalGlyph, "StatusNormalBrush"),
    };

    public static (string Glyph, string BrushKey) For(PcHealthState state) => state switch
    {
        PcHealthState.Problem => (InsightDisplay.CriticalGlyph, "StatusCriticalBrush"),
        PcHealthState.Attention => (InsightDisplay.WarningGlyph, "StatusWarningBrush"),
        PcHealthState.Healthy => (InsightDisplay.NormalGlyph, "StatusNormalBrush"),
        _ => (Unknown, "StatusUnknownBrush"),
    };

    public static string Text(PcHealthState state) => state switch
    {
        PcHealthState.Problem => UiStrings.State_ProblemDetected,
        PcHealthState.Attention => Strings.Health_Status_Attention,
        PcHealthState.Healthy => UiStrings.State_Healthy,
        _ => UiStrings.Common_Checking,
    };

    public static (string Glyph, string BrushKey) For(ConfidenceLevel confidence) => confidence switch
    {
        ConfidenceLevel.High => (InsightDisplay.NormalGlyph, "StatusNormalBrush"),
        ConfidenceLevel.Medium => (InsightDisplay.InfoGlyph, "StatusInfoBrush"),
        _ => (InsightDisplay.InfoGlyph, "StatusUnknownBrush"),
    };
}
