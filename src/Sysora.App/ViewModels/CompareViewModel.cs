using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sysora.App.Services;
using Sysora.Core.Analysis;
using Sysora.Core.Changes;
using Sysora.Core.Formatting;
using Sysora.Core.Gaming;
using Sysora.Core.Models;
using Sysora.Core.Reports;

namespace Sysora.App.ViewModels;

/// <summary>A comparison offered in the list.</summary>
/// <param name="Preset">Comparison.</param>
/// <param name="Label">Display text.</param>
public sealed record ComparePresetOption(ComparisonPreset Preset, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A game session offered for the game comparisons.</summary>
public sealed record GameSessionOption(Guid Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Before / after: compares the state of the PC between two periods, from real measurements only.</summary>
public sealed partial class CompareViewModel : PageViewModel
{
    private readonly StateComparisonService _comparisons;
    private readonly GameSessionService _games;
    private readonly NavigationRequests _requests;
    private readonly ReportExportService _export;
    private readonly ILogger<CompareViewModel> _logger;
    private StateComparisonResult? _result;
    private CancellationTokenSource? _load;
    private bool _applyingRequest;

    public CompareViewModel(UiMetricsHub hub, StateComparisonService comparisons, GameSessionService games, NavigationRequests requests, ReportExportService export, ILogger<CompareViewModel> logger)
        : base(hub)
    {
        _comparisons = comparisons;
        _games = games;
        _requests = requests;
        _export = export;
        _logger = logger;
        Summary = BeforeLabel = AfterLabel = string.Empty;
        var now = DateTimeOffset.Now;
        FirstDate = now.Date;
        FirstTime = now.AddHours(-2).TimeOfDay;
        SecondDate = now.Date;
        SecondTime = now.TimeOfDay;
        Preset = Presets[0];
        _requests.Requested += (_, kind) =>
        {
            if (kind == NavigationRequestKind.Compare && IsActive)
            {
                TakeRequest();
            }
        };
    }

    public IReadOnlyList<ComparePresetOption> Presets { get; } =
    [
        new(ComparisonPreset.NowVsHourAgo, "Now vs 1 hour ago"),
        new(ComparisonPreset.NowVsYesterday, "Now vs yesterday at this time"),
        new(ComparisonPreset.TodayVsYesterday, "Today vs yesterday"),
        new(ComparisonPreset.GameBeforeVsDuring, "Game session: before vs during"),
        new(ComparisonPreset.GameBeforeVsAfter, "Game session: before vs after"),
        new(ComparisonPreset.GameVsPreviousGame, "Game session vs the previous one"),
        new(ComparisonPreset.AroundTime, "Before vs after a moment"),
        new(ComparisonPreset.TwoMoments, "Two moments"),
    ];

    public ObservableCollection<GameSessionOption> Sessions { get; } = [];

    public ObservableCollection<CompareRowViewModel> Rows { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsSession), nameof(NeedsFirstMoment), nameof(NeedsSecondMoment))]
    public partial ComparePresetOption Preset { get; set; }

    public bool NeedsSession => Preset.Preset is ComparisonPreset.GameBeforeVsDuring or ComparisonPreset.GameBeforeVsAfter or ComparisonPreset.GameVsPreviousGame;

    public bool NeedsFirstMoment => Preset.Preset is ComparisonPreset.AroundTime or ComparisonPreset.TwoMoments;

    public bool NeedsSecondMoment => Preset.Preset == ComparisonPreset.TwoMoments;

    [ObservableProperty]
    public partial GameSessionOption? Session { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? FirstDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan FirstTime { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? SecondDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan SecondTime { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial string BeforeLabel { get; set; }

    [ObservableProperty]
    public partial string AfterLabel { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool HasResult { get; set; }

    partial void OnPresetChanged(ComparePresetOption value)
    {
        if (!_applyingRequest && IsActive && !NeedsFirstMoment)
        {
            _ = CompareAsync();
        }
    }

    partial void OnSessionChanged(GameSessionOption? value)
    {
        if (!_applyingRequest && IsActive && NeedsSession)
        {
            _ = CompareAsync();
        }
    }

    [RelayCommand]
    private Task Compare() => CompareAsync();

    [RelayCommand]
    private Task Export()
    {
        if (_result is not { } result)
        {
            return Task.CompletedTask;
        }

        return _export.ExportAsync(system => ReportBuilder.Comparison(result, DateTimeOffset.Now, system));
    }

    protected override async void OnActivated()
    {
        await LoadSessionsAsync();
        if (!TakeRequest() && _result is null)
        {
            await CompareAsync();
        }
    }

    protected override void Update(SystemSnapshot snapshot, MetricKind updated)
    {
        // A comparison is a snapshot in time: it is recomputed on demand only.
    }

    /// <summary>Applies a comparison asked by another page (timeline entry, game recap, replay).</summary>
    private bool TakeRequest()
    {
        if (_requests.TakeCompare() is not { } request)
        {
            return false;
        }

        _applyingRequest = true;
        try
        {
            Preset = Presets.First(p => p.Preset == request.Preset);
            if (request.Time is { } time)
            {
                var local = time.ToLocalTime();
                FirstDate = local.Date;
                FirstTime = local.TimeOfDay;
            }

            if (request.SecondTime is { } second)
            {
                var local = second.ToLocalTime();
                SecondDate = local.Date;
                SecondTime = local.TimeOfDay;
            }

            if (request.SessionId is { } id)
            {
                Session = Sessions.FirstOrDefault(s => s.Id == id) ?? Session;
            }
        }
        finally
        {
            _applyingRequest = false;
        }

        _ = CompareAsync(request);
        return true;
    }

    private async Task LoadSessionsAsync()
    {
        try
        {
            var sessions = await _games.GetSessionsAsync(DateTimeOffset.UtcNow - GameSessionService.ListedHistory, CancellationToken.None);
            var options = sessions
                .OrderByDescending(s => s.Start)
                .Select(s => new GameSessionOption(s.Id, $"{s.Name} · {s.Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} ({MetricFormatter.DurationCompact(s.Duration)})"))
                .ToList();
            var selected = Session?.Id;
            _applyingRequest = true;
            try
            {
                Sessions.Clear();
                foreach (var option in options)
                {
                    Sessions.Add(option);
                }

                Session = Sessions.FirstOrDefault(s => s.Id == selected) ?? Sessions.FirstOrDefault();
            }
            finally
            {
                _applyingRequest = false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Game sessions could not be loaded for the comparison.");
        }
    }

    private async Task CompareAsync(ComparisonRequest? request = null)
    {
        request ??= new ComparisonRequest(Preset.Preset)
        {
            Time = Combine(FirstDate, FirstTime),
            SecondTime = Combine(SecondDate, SecondTime),
            SessionId = Session?.Id,
        };

        _load?.Cancel();
        _load?.Dispose();
        var load = _load = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            var result = await _comparisons.CompareAsync(request, load.Token);
            if (!load.IsCancellationRequested)
            {
                Apply(result);
            }
        }
        catch (OperationCanceledException)
        {
            // Replaced by another comparison.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "The comparison failed.");
            Summary = "The comparison could not be made. See the log for details.";
        }
        finally
        {
            if (_load == load)
            {
                IsLoading = false;
            }
        }
    }

    private void Apply(StateComparisonResult result)
    {
        _result = result;
        Summary = result.Summary;
        BeforeLabel = result.Before.Label;
        AfterLabel = result.After.Label;
        Rows.Clear();
        foreach (var row in result.Rows)
        {
            Rows.Add(new CompareRowViewModel(row));
        }

        Notes.Clear();
        foreach (var note in result.Notes)
        {
            Notes.Add(note);
        }

        HasResult = Rows.Count > 0;
    }

    private static DateTimeOffset? Combine(DateTimeOffset? date, TimeSpan time) =>
        date is { } day ? new DateTimeOffset(day.Date + time, TimeZoneInfo.Local.GetUtcOffset(day.Date + time)) : null;
}

/// <summary>One line of a comparison.</summary>
public sealed class CompareRowViewModel(StateDifference row)
{
    public string Name { get; } = row.Name;

    public string Before { get; } = row.BeforeText;

    public string After { get; } = row.AfterText;

    public string Change { get; } = row.RelativeText is { } relative ? $"{row.ChangeText} ({relative})" : row.ChangeText;

    public string Explanation { get; } = row.Explanation;

    public string DirectionGlyph { get; } = row.Direction switch
    {
        ChangeDirection.Up => "",
        ChangeDirection.Down => "",
        ChangeDirection.Same => "",
        _ => string.Empty,
    };

    public string ImportanceText { get; } = row.IsComparable ? HealthDisplay.Importance(row.Importance).Text : "Not compared";

    public string ImportanceBrushKey { get; } = row.IsComparable ? HealthDisplay.Importance(row.Importance).BrushKey : "StatusUnknownBrush";
}
