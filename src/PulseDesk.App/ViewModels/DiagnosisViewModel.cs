using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PulseDesk.App.Services;
using PulseDesk.Core.Analysis;
using PulseDesk.Core.Diagnosis;
using PulseDesk.Core.Formatting;
using PulseDesk.Core.Models;

namespace PulseDesk.App.ViewModels;

/// <summary>
/// Diagnosis: "why is my PC slow?". Shows the overall state, the problems detected, potential issues, what is
/// normal, recommendations, and what could not be analyzed, each with its evidence.
/// </summary>
public sealed partial class DiagnosisViewModel : PageViewModel
{
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(10);

    private readonly DiagnosisService _diagnosis;
    private readonly UsageComparisonService _comparison;
    private readonly InsightNavigator _navigator;
    private readonly ILogger<DiagnosisViewModel> _logger;
    private readonly Dictionary<string, DiagnosisItemViewModel> _items = new(StringComparer.Ordinal);
    private long _lastRun;

    public DiagnosisViewModel(UiMetricsHub hub, DiagnosisService diagnosis, UsageComparisonService comparison, InsightNavigator navigator, ILogger<DiagnosisViewModel> logger)
        : base(hub)
    {
        _diagnosis = diagnosis;
        _comparison = comparison;
        ComparisonSummary = UsageComparison.Empty.Summary;
        _navigator = navigator;
        _logger = logger;
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


    protected override void OnActivated() => _ = RunAsync();

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
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Diagnosis failed.");
            Summary = "The diagnosis could not be completed. See the log for details.";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void Apply(DiagnosisReport report)
    {
        Headline = report.Headline;
        Summary = report.Summary;
        (StateGlyph, StateBrushKey) = HealthGlyphs.For(report.State);
        AnalyzedText = report.To is { } to && report.From is { } from
            ? $"Analyzed: last {MetricFormatter.DurationPrecise(to - from)} · {MetricFormatter.Plural(report.SampleCount, "measurement")} · updated {InsightDisplay.Time(report.Timestamp)}"
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
                item = new DiagnosisItemViewModel(i => _navigator.Open(i.Action, i.AppKey));
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

    public DiagnosisItemViewModel(Action<DiagnosisItemViewModel> open)
    {
        _open = open;
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

    [RelayCommand]
    private void Open() => _open(this);

    public void Set(DiagnosisResult result)
    {
        Title = result.Title;
        Description = result.Description;
        (Glyph, BrushKey) = HealthGlyphs.For(result.Severity);
        Observed = result.Duration is { } duration && duration > TimeSpan.Zero
            ? $"{result.Metric}: {result.ObservedValue} for {MetricFormatter.DurationPrecise(duration)}"
            : $"{result.Metric}: {result.ObservedValue}";
        Reference = result.ReferenceValue is { } reference ? $"Reference: {reference}" : string.Empty;
        Explanation = result.Explanation;
        Recommendation = result.Recommendation ?? string.Empty;
        HasRecommendation = result.Recommendation is not null;
        ConfidenceText = InsightDisplay.Text(result.Confidence);
        TimeText = $"Observed {InsightDisplay.Time(result.Timestamp)}";
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
        PcHealthState.Problem => "Problem detected",
        PcHealthState.Attention => "Attention",
        PcHealthState.Healthy => "Healthy",
        _ => "Checking…",
    };

    public static (string Glyph, string BrushKey) For(ConfidenceLevel confidence) => confidence switch
    {
        ConfidenceLevel.High => (InsightDisplay.NormalGlyph, "StatusNormalBrush"),
        ConfidenceLevel.Medium => (InsightDisplay.InfoGlyph, "StatusInfoBrush"),
        _ => (InsightDisplay.InfoGlyph, "StatusUnknownBrush"),
    };
}
