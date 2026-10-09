using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Sysora.App.Services;
using Sysora.Core.Changes;
using Sysora.Core.Formatting;
using Sysora.Core.Models;
using Sysora.Localization;

namespace Sysora.App.ViewModels;

/// <summary>Changes: "what changed on my PC recently?" as a timeline, or compared with a reference snapshot.</summary>
public sealed partial class ChangesViewModel : PageViewModel
{
    private static readonly TimeSpan TimelineLength = TimeSpan.FromDays(30);

    private readonly ChangeDetectionService _changes;
    private readonly SinceYesterdayService _sinceYesterday;
    private readonly ReportExportService _export;
    private readonly InsightNavigator _navigator;
    private readonly ILogger<ChangesViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _load;

    public ChangesViewModel(UiMetricsHub hub, ChangeDetectionService changes, SinceYesterdayService sinceYesterday, ReportExportService export, InsightNavigator navigator, DispatcherQueue dispatcher, ILogger<ChangesViewModel> logger)
        : base(hub)
    {
        _changes = changes;
        _sinceYesterday = sinceYesterday;
        _export = export;
        SinceHeadline = SinceYesterdaySummary.Loading.Headline;
        SinceNote = SinceUnchanged = SinceNotCompared = string.Empty;
        _navigator = navigator;
        _dispatcher = dispatcher;
        _logger = logger;
        Note = SnapshotText = string.Empty;
    }

    public IReadOnlyList<string> Views { get; } =
    [
        UiStrings.Changes_View_Timeline,
        UiStrings.Changes_View_Morning,
        UiStrings.Changes_View_Yesterday,
        UiStrings.Changes_View_7Days,
        UiStrings.Changes_View_30Days,
    ];

    public ObservableCollection<ChangeGroupViewModel> Groups { get; } = [];

    /// <summary>"What changed since yesterday?" in a few lines.</summary>
    public ObservableCollection<SinceYesterdayItemViewModel> SinceItems { get; } = [];

    [ObservableProperty]
    public partial string SinceHeadline { get; set; }

    [ObservableProperty]
    public partial string SinceNote { get; set; }

    [ObservableProperty]
    public partial string SinceUnchanged { get; set; }

    [ObservableProperty]
    public partial string SinceNotCompared { get; set; }

    [ObservableProperty]
    public partial int SignificantCount { get; set; }

    [ObservableProperty]
    public partial int MinorCount { get; set; }

    [ObservableProperty]
    public partial bool HasSignificant { get; set; }

    [ObservableProperty]
    public partial bool HasMinor { get; set; }

    [ObservableProperty]
    public partial bool IsMostlyUnchanged { get; set; }

    [ObservableProperty]
    public partial bool HasSinceNotCompared { get; set; }

    [ObservableProperty]
    public partial int ViewIndex { get; set; }

    [ObservableProperty]
    public partial string Note { get; set; }

    [ObservableProperty]
    public partial string SnapshotText { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    partial void OnViewIndexChanged(int value) => _ = LoadAsync();

    /// <summary>Takes a snapshot now, then reloads.</summary>
    [RelayCommand]
    private async Task CheckNowAsync()
    {
        IsLoading = true;
        await _changes.RecordAsync(CancellationToken.None);
        await LoadAsync();
        await LoadSinceYesterdayAsync(force: true);
    }

    [RelayCommand]
    private async Task Export()
    {
        var timeline = await _changes.GetTimelineAsync(DateTimeOffset.UtcNow - TimelineLength, CancellationToken.None);
        var since = _sinceYesterday.Latest;
        await _export.ExportAsync(system => Sysora.Core.Reports.ReportBuilder.Changes(timeline, since, DateTimeOffset.Now, system));
    }

    protected override void OnActivated()
    {
        _changes.Changed += OnSnapshotRecorded;
        _ = LoadAsync();
        _ = LoadSinceYesterdayAsync(force: false);
    }

    private async Task LoadSinceYesterdayAsync(bool force)
    {
        try
        {
            var summary = await _sinceYesterday.GetAsync(force, CancellationToken.None);
            SinceHeadline = summary.Headline;
            SinceNote = summary.Note;
            SignificantCount = summary.Significant;
            MinorCount = summary.Minor;
            HasSignificant = summary.Significant > 0;
            HasMinor = summary.Minor > 0;
            IsMostlyUnchanged = summary.HasReference && summary.MostlyUnchanged;
            SinceUnchanged = summary.UnchangedAreas.Count > 0 ? Text.Format(Strings.Report_Unchanged, string.Join(Strings.List_Separator, summary.UnchangedAreas)) + "." : string.Empty;
            SinceNotCompared = string.Join(Environment.NewLine, summary.NotCompared.Select(n => Text.Format(Strings.Report_NotCompared, n)));
            HasSinceNotCompared = summary.NotCompared.Count > 0;
            SinceItems.Clear();
            foreach (var item in summary.Items)
            {
                SinceItems.Add(new SinceYesterdayItemViewModel(item, i => _navigator.Open(i.Action, i.AppKey)));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "The comparison with yesterday failed.");
            SinceHeadline = UiStrings.Changes_SinceFailed;
        }
    }

    protected override void OnDeactivated() => _changes.Changed -= OnSnapshotRecorded;

    /// <summary>A snapshot was just taken in the background (for example the first one after start): reload.</summary>
    private void OnSnapshotRecorded(object? sender, EventArgs e) => _dispatcher.TryEnqueue(() => _ = LoadAsync());

    protected override void Update(SystemSnapshot snapshot, MetricKind updated)
    {
        // Changes are computed from snapshots taken every few hours, not from live metrics.
    }

    private async Task LoadAsync()
    {
        if (!IsActive)
        {
            return;
        }

        _load?.Cancel();
        _load?.Dispose();
        var load = _load = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            var snapshots = await _changes.GetSnapshotsAsync(load.Token);
            SnapshotText = snapshots.Count == 0
                ? UiStrings.Changes_NoSnapshotYet
                : Text.Format(
                    UiStrings.Changes_Snapshots,
                    Text.Plural(snapshots.Count, UiStrings.Count_DailySnapshot_One, UiStrings.Count_DailySnapshot_Other),
                    snapshots[0].CapturedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture),
                    InsightDisplay.Time(snapshots[^1].CapturedAt));

            if (ViewIndex == 0)
            {
                var timeline = await _changes.GetTimelineAsync(DateTimeOffset.UtcNow - TimelineLength, load.Token);
                if (load.IsCancellationRequested)
                {
                    return;
                }

                Note = timeline.Count == 0
                    ? UiStrings.Changes_NoChangeYet
                    : UiStrings.Changes_DetectedNote;
                ShowTimeline(timeline);
            }
            else
            {
                var comparison = await _changes.CompareAsync((BaselineReference)(ViewIndex - 1), load.Token);
                if (load.IsCancellationRequested)
                {
                    return;
                }

                Note = comparison.ReferenceSnapshot is null || comparison.Changes.Count > 0
                    ? comparison.Note
                    : $"{comparison.Note} No change found.";
                ShowGroups([(comparison.ReferenceSnapshot is null ? string.Empty : UiStrings.Common_Changes, comparison.Changes)]);
            }
        }
        catch (OperationCanceledException)
        {
            // Another view was selected.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Changes could not be loaded.");
            Note = UiStrings.Changes_LoadFailed;
        }
        finally
        {
            if (_load == load)
            {
                IsLoading = false;
            }
        }
    }

    private void ShowTimeline(IReadOnlyList<DetectedChange> changes)
    {
        var today = DateTimeOffset.Now.Date;
        string GroupOf(DetectedChange change)
        {
            var day = ChangeItemViewModel.Day(change).ToLocalTime().Date;
            return day >= today ? Strings.Usual_Period_Today
                : day >= today.AddDays(-1) ? Strings.Usual_Period_Yesterday
                : day >= today.AddDays(-7) ? UiStrings.Changes_Group_ThisWeek
                : UiStrings.Changes_Group_Earlier;
        }

        var order = new[] { Strings.Usual_Period_Today, Strings.Usual_Period_Yesterday, UiStrings.Changes_Group_ThisWeek, UiStrings.Changes_Group_Earlier };
        ShowGroups(order.Select(name => (name, (IReadOnlyList<DetectedChange>)changes.Where(c => GroupOf(c) == name).ToList())).ToList());
    }

    private void ShowGroups(IReadOnlyList<(string Title, IReadOnlyList<DetectedChange> Changes)> groups)
    {
        Groups.Clear();
        foreach (var (title, changes) in groups.Where(g => g.Changes.Count > 0))
        {
            var group = new ChangeGroupViewModel(title);
            foreach (var change in changes)
            {
                group.Items.Add(new ChangeItemViewModel(change, item => _navigator.Open(item.Action, item.AppKey)));
            }

            Groups.Add(group);
        }

        IsEmpty = Groups.Count == 0;
    }
}

/// <summary>Changes of one period of the timeline.</summary>
public sealed class ChangeGroupViewModel(string title)
{
    public string Title { get; } = title;

    public bool HasTitle => Title.Length > 0;

    public ObservableCollection<ChangeItemViewModel> Items { get; } = [];
}

/// <summary>One detected change.</summary>
public sealed partial class ChangeItemViewModel : ObservableObject
{
    private readonly Action<ChangeItemViewModel> _open;

    public ChangeItemViewModel(DetectedChange change, Action<ChangeItemViewModel> open)
    {
        _open = open;
        Title = change.Title;
        Glyph = GlyphOf(change.Type);
        (ImportanceText, ImportanceBrushKey) = change.Importance switch
        {
            ChangeImportance.High => (UiStrings.Importance_HighLong, "LevelHighBrush"),
            ChangeImportance.Medium => (Strings.Importance_Medium, "LevelModerateBrush"),
            _ => (Strings.Importance_Low, "LevelLowBrush"),
        };
        WhenText = IsWholeDay(change)
            ? Text.Format(UiStrings.Changes_On, change.After!.Value.ToLocalTime().ToString("D", CultureInfo.CurrentCulture))
            : change.After is { } after && after.ToLocalTime().Date != change.Before.ToLocalTime().Date
            ? Text.Format(UiStrings.Changes_Between, InsightDisplay.Time(after), InsightDisplay.Time(change.Before))
            : change.After is { } sameDay
                ? Text.Format(UiStrings.Changes_SameDayBetween, change.Before.ToLocalTime().ToString("d", CultureInfo.CurrentCulture), sameDay.ToLocalTime().ToString("t", CultureInfo.CurrentCulture), change.Before.ToLocalTime().ToString("t", CultureInfo.CurrentCulture))
                : Text.Format(UiStrings.Changes_BeforeTime, InsightDisplay.Time(change.Before));
        Values = (change.OldValue, change.NewValue) switch
        {
            ({ } old, { } current) => $"{old} → {current}",
            (null, { } current) => current,
            ({ } old, null) => Text.Format(Strings.Timeline_Was, old),
            _ => string.Empty,
        };
        HasValues = Values.Length > 0;
        Explanation = change.Explanation;
        Origin = change.Origin;
        Action = change.Action;
        AppKey = change.AppKey;
        ActionLabel = InsightNavigator.Label(change.Action);
        HasAction = ActionLabel.Length > 0;
        foreach (var evidence in change.Evidence)
        {
            Evidence.Add(EvidenceItemViewModel.From(evidence));
        }
    }

    public string Title { get; }

    public string Glyph { get; }

    public string ImportanceText { get; }

    public string ImportanceBrushKey { get; }

    public string WhenText { get; }

    public string Values { get; }

    public bool HasValues { get; }

    public string Explanation { get; }

    public string Origin { get; }

    public Core.Diagnosis.DiagnosisAction Action { get; }

    public string? AppKey { get; }

    public string ActionLabel { get; }

    public bool HasAction { get; }

    public ObservableCollection<EvidenceItemViewModel> Evidence { get; } = [];

    [RelayCommand]
    private void Open() => _open(this);

    /// <summary>The day a change belongs to: its day when it is known to the day, otherwise when it was last possible.</summary>
    public static DateTimeOffset Day(DetectedChange change) => IsWholeDay(change) ? change.After!.Value : change.Before;

    /// <summary>True when the change is dated to one calendar day (an installer's date), not to a period between snapshots.</summary>
    private static bool IsWholeDay(DetectedChange change) =>
        change.After is { } after
        && after.ToLocalTime().TimeOfDay == TimeSpan.Zero
        && change.Before - after == TimeSpan.FromDays(1);

    private static string GlyphOf(ChangeType type) => type switch
    {
        ChangeType.AppInstalled => "",
        ChangeType.AppRemoved => "",
        ChangeType.AppUpdated => "",
        ChangeType.NewFrequentApp => "",
        ChangeType.StartupProgramAdded or ChangeType.StartupProgramRemoved or ChangeType.StartupProgramEnabled or ChangeType.StartupProgramDisabled => "",
        ChangeType.WindowsUpdated => "",
        ChangeType.FirmwareUpdated => "",
        ChangeType.MemoryChanged => "",
        ChangeType.DeviceAdded or ChangeType.DeviceRemoved => "",
        ChangeType.DiskSpaceChanged => "",
        _ => "",
    };
}
