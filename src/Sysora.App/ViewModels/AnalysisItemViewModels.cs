using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sysora.App.Services;
using Sysora.Core.Analysis;
using Sysora.Core.Changes;
using Sysora.Core.Diagnosis;
using Sysora.Core.Health;

namespace Sysora.App.ViewModels;

/// <summary>One statement of an analysis, with a badge saying whether it is observed, inferred or unknown.</summary>
public sealed record FindingItemViewModel(string Label, string Text, string BasisText, string BasisBrushKey)
{
    public static FindingItemViewModel From(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var brush = finding.Basis switch
        {
            FindingBasis.Observed => "StatusNormalBrush",
            FindingBasis.Inferred => "StatusWarningBrush",
            _ => "StatusUnknownBrush",
        };
        return new FindingItemViewModel(finding.Label, finding.Text, finding.BasisText, brush);
    }
}

/// <summary>Brush keys and glyphs for the PC Health score.</summary>
internal static class HealthDisplay
{
    public static (string Glyph, string BrushKey) For(PcHealthStatus status) => status switch
    {
        PcHealthStatus.Problem => (InsightDisplay.CriticalGlyph, "StatusCriticalBrush"),
        PcHealthStatus.Attention => (InsightDisplay.WarningGlyph, "StatusWarningBrush"),
        PcHealthStatus.Good => (InsightDisplay.NormalGlyph, "StatusNormalBrush"),
        _ => (InsightDisplay.InfoGlyph, "StatusUnknownBrush"),
    };

    public static string BrushKey(PcHealthGrade grade) => grade switch
    {
        PcHealthGrade.Good => "StatusNormalBrush",
        PcHealthGrade.Fair => "StatusInfoBrush",
        PcHealthGrade.NeedsAttention => "StatusWarningBrush",
        PcHealthGrade.Poor => "StatusCriticalBrush",
        _ => "StatusUnknownBrush",
    };

    public static (string Text, string BrushKey) Importance(ChangeImportance importance) => importance switch
    {
        ChangeImportance.High => ("High importance", "LevelHighBrush"),
        ChangeImportance.Medium => ("Medium", "LevelModerateBrush"),
        _ => ("Low", "LevelLowBrush"),
    };

    public static string Glyph(PcHealthArea area) => area switch
    {
        PcHealthArea.Cpu => "",
        PcHealthArea.Memory => "",
        PcHealthArea.Storage => "",
        PcHealthArea.DiskActivity => "",
        PcHealthArea.Gpu => "",
        PcHealthArea.Temperatures => "",
        PcHealthArea.Stability => "",
        PcHealthArea.RecentAnomalies => "",
        _ => "",
    };
}

/// <summary>One area of the PC Health score.</summary>
public sealed partial class HealthComponentItemViewModel : ObservableObject
{
    private readonly Action<HealthComponentItemViewModel> _open;

    public HealthComponentItemViewModel(Action<HealthComponentItemViewModel> open)
    {
        _open = open;
        Name = StatusText = Glyph = StatusGlyph = BrushKey = ImpactText = Summary = Explanation = ActionLabel = string.Empty;
    }

    public PcHealthArea Area { get; private set; }

    public DiagnosisAction Action { get; private set; }

    public ObservableCollection<EvidenceItemViewModel> Evidence { get; } = [];

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial string Glyph { get; set; }

    [ObservableProperty]
    public partial string StatusGlyph { get; set; }

    [ObservableProperty]
    public partial string BrushKey { get; set; }

    [ObservableProperty]
    public partial string ImpactText { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial string Explanation { get; set; }

    [ObservableProperty]
    public partial string ActionLabel { get; set; }

    [ObservableProperty]
    public partial bool HasAction { get; set; }

    [ObservableProperty]
    public partial bool HasEvidence { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [RelayCommand]
    private void Open() => _open(this);

    public void Set(PcHealthComponent component)
    {
        Area = component.Area;
        Action = component.Action;
        Name = component.Name;
        StatusText = component.StatusText;
        Glyph = HealthDisplay.Glyph(component.Area);
        (StatusGlyph, BrushKey) = HealthDisplay.For(component.Status);
        ImpactText = component.ImpactText;
        Summary = component.Summary;
        Explanation = component.Explanation;
        ActionLabel = InsightNavigator.Label(component.Action);
        HasAction = ActionLabel.Length > 0;
        var evidence = component.Evidence.Select(EvidenceItemViewModel.From).ToList();
        if (!Evidence.SequenceEqual(evidence))
        {
            Evidence.Clear();
            foreach (var item in evidence)
            {
                Evidence.Add(item);
            }
        }

        HasEvidence = Evidence.Count > 0;
    }
}

/// <summary>One recurring problem.</summary>
public sealed partial class RecurringItemViewModel(RecurringProblem problem, Action<RecurringItemViewModel> open)
{
    public string Title { get; } = problem.Title;

    public string Description { get; } = problem.Description;

    public string ConfidenceText { get; } = InsightDisplay.Text(problem.Confidence);

    public string ConfidenceBrushKey { get; } = problem.Confidence == ConfidenceLevel.High ? "StatusWarningBrush" : "StatusInfoBrush";

    public string OccurrencesText { get; } = $"{problem.Occurrences}× on {problem.Days} days";

    public IReadOnlyList<FindingItemViewModel> Findings { get; } = problem.Findings.Select(FindingItemViewModel.From).ToArray();

    public DiagnosisAction Action { get; } = problem.Action;

    public string? AppKey { get; } = problem.AssociatedAppKey;

    public string ActionLabel { get; } = InsightNavigator.Label(problem.Action);

    public bool HasAction => ActionLabel.Length > 0;

    [RelayCommand]
    private void Open() => open(this);
}

/// <summary>One change since yesterday.</summary>
public sealed partial class SinceYesterdayItemViewModel
{
    private readonly Action<SinceYesterdayItemViewModel> _open;

    public SinceYesterdayItemViewModel(SinceYesterdayItem item, Action<SinceYesterdayItemViewModel> open)
    {
        _open = open;
        Area = item.Area;
        Title = item.Title;
        Values = (item.OldValue, item.NewValue) switch
        {
            ({ } old, { } current) => $"{old} → {current}",
            (null, { } current) => current,
            ({ } old, null) => $"Was: {old}",
            _ => string.Empty,
        };
        HasValues = Values.Length > 0;
        When = item.When;
        Explanation = item.Explanation;
        Origin = item.Origin;
        ConfidenceText = InsightDisplay.Text(item.Confidence);
        (ImportanceText, ImportanceBrushKey) = HealthDisplay.Importance(item.Importance);
        (Glyph, BrushKey) = item.IsSignificant
            ? (InsightDisplay.CriticalGlyph, "StatusCriticalBrush")
            : (InsightDisplay.WarningGlyph, "StatusWarningBrush");
        Action = item.Action;
        AppKey = item.AppKey;
        ActionLabel = InsightNavigator.Label(item.Action);
    }

    public string Area { get; }

    public string Title { get; }

    public string Values { get; }

    public bool HasValues { get; }

    public string When { get; }

    public string Explanation { get; }

    public string Origin { get; }

    public string ConfidenceText { get; }

    public string ImportanceText { get; }

    public string ImportanceBrushKey { get; }

    public string Glyph { get; }

    public string BrushKey { get; }

    public DiagnosisAction Action { get; }

    public string? AppKey { get; }

    public string ActionLabel { get; }

    public bool HasAction => ActionLabel.Length > 0;

    [RelayCommand]
    private void Open() => _open(this);
}
