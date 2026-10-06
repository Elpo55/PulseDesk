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

namespace Sysora.App.ViewModels;

/// <summary>Changes: "what changed on my PC recently?" as a timeline, or compared with a reference snapshot.</summary>
public sealed partial class ChangesViewModel : PageViewModel
{
    private static readonly TimeSpan TimelineLength = TimeSpan.FromDays(30);

    private readonly ChangeDetectionService _changes;
    private readonly InsightNavigator _navigator;
    private readonly ILogger<ChangesViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _load;

    public ChangesViewModel(UiMetricsHub hub, ChangeDetectionService changes, InsightNavigator navigator, DispatcherQueue dispatcher, ILogger<ChangesViewModel> logger)
        : base(hub)
    {
        _changes = changes;
        _navigator = navigator;
        _dispatcher = dispatcher;
        _logger = logger;
        Note = SnapshotText = string.Empty;
    }

    public IReadOnlyList<string> Views { get; } = ["Timeline (last 30 days)", "Since this morning", "Since yesterday", "Since 7 days ago", "Since 30 days ago"];

    public ObservableCollection<ChangeGroupViewModel> Groups { get; } = [];

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
    }

    protected override void OnActivated()
    {
        _changes.Changed += OnSnapshotRecorded;
        _ = LoadAsync();
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
                ? "No snapshot recorded yet. Sysora takes one a minute after it starts, then every six hours."
                : $"{MetricFormatter.Plural(snapshots.Count, "daily snapshot")} since {snapshots[0].CapturedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)} · last one {InsightDisplay.Time(snapshots[^1].CapturedAt)}";

            if (ViewIndex == 0)
            {
                var timeline = await _changes.GetTimelineAsync(DateTimeOffset.UtcNow - TimelineLength, load.Token);
                if (load.IsCancellationRequested)
                {
                    return;
                }

                Note = timeline.Count == 0
                    ? "No change recorded yet. Changes appear here as Sysora compares daily snapshots of the PC (applications, startup programs, Windows version, devices, disk space and average usage)."
                    : "Detected by comparing snapshots of the PC. Desktop applications installed or updated in the last 30 days are also listed from the dates recorded in Windows.";
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
                ShowGroups([(comparison.ReferenceSnapshot is null ? string.Empty : "Changes", comparison.Changes)]);
            }
        }
        catch (OperationCanceledException)
        {
            // Another view was selected.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Changes could not be loaded.");
            Note = "Changes could not be loaded. See the log for details.";
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
            return day >= today ? "Today"
                : day >= today.AddDays(-1) ? "Yesterday"
                : day >= today.AddDays(-7) ? "This week"
                : "Earlier";
        }

        var order = new[] { "Today", "Yesterday", "This week", "Earlier" };
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
            ChangeImportance.High => ("High importance", "LevelHighBrush"),
            ChangeImportance.Medium => ("Medium", "LevelModerateBrush"),
            _ => ("Low", "LevelLowBrush"),
        };
        WhenText = IsWholeDay(change)
            ? $"On {change.After!.Value.ToLocalTime().ToString("D", CultureInfo.CurrentCulture)}"
            : change.After is { } after && after.ToLocalTime().Date != change.Before.ToLocalTime().Date
            ? $"Between {InsightDisplay.Time(after)} and {InsightDisplay.Time(change.Before)}"
            : change.After is { } sameDay
                ? $"{change.Before.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)}, between {sameDay.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)} and {change.Before.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}"
                : $"Before {InsightDisplay.Time(change.Before)}";
        Values = (change.OldValue, change.NewValue) switch
        {
            ({ } old, { } current) => $"{old} → {current}",
            (null, { } current) => current,
            ({ } old, null) => $"Was: {old}",
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
