using System.Globalization;
using Microsoft.Extensions.Logging;
using PulseDesk.Core.History;
using PulseDesk.Core.Interfaces;
using PulseDesk.Core.Models;
using PulseDesk.Core.Settings;

namespace PulseDesk.Core.Changes;

/// <summary>
/// Default <see cref="IChangeDetectionService"/>. Takes a snapshot of the PC shortly after start and every six
/// hours, records what changed since the previous snapshot, keeps the first snapshot of each day as a reference,
/// and compares the current state with references on demand (today, yesterday, 7 or 30 days ago).
/// </summary>
public sealed class ChangeDetectionService : IChangeDetectionService, IAsyncDisposable
{
    /// <summary>Interval between two snapshots.</summary>
    public static readonly TimeSpan CaptureInterval = TimeSpan.FromHours(6);

    /// <summary>On first use, applications installed within this many days (per Windows' records) are listed.</summary>
    public const int RecentInstallDays = 30;

    private static readonly TimeSpan FirstCaptureDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CurrentCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SnapshotHistory = TimeSpan.FromDays(40);

    private readonly ISystemInventoryProvider _inventory;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly IMetricsMonitor _monitor;
    private readonly IHistoryRepository _repository;
    private readonly SettingsService _settings;
    private readonly ILogger<ChangeDetectionService> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITimer? _timer;
    private SystemBaseline? _current;

    public ChangeDetectionService(
        ISystemInventoryProvider inventory,
        ISystemInfoProvider systemInfo,
        IMetricsMonitor monitor,
        IHistoryRepository repository,
        SettingsService settings,
        ILogger<ChangeDetectionService> logger,
        TimeProvider? timeProvider = null)
    {
        _inventory = inventory;
        _systemInfo = systemInfo;
        _monitor = monitor;
        _repository = repository;
        _settings = settings;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised on a background thread after new changes are recorded.</summary>
    public event EventHandler? Changed;

    /// <summary>Starts the periodic snapshots.</summary>
    public void Start() =>
        _timer ??= _time.CreateTimer(_ => _ = RecordAsync(CancellationToken.None), null, FirstCaptureDelay, CaptureInterval);

    /// <summary>
    /// Takes a snapshot, records the changes since the previous one and keeps the snapshot as today's reference
    /// if there is none yet. Returns the changes found.
    /// </summary>
    public async Task<IReadOnlyList<DetectedChange>> RecordAsync(CancellationToken cancellationToken)
    {
        try
        {
            var current = await CaptureCurrentAsync(force: true, cancellationToken).ConfigureAwait(false);
            if (!_settings.Current.History.RecordHistory)
            {
                return [];
            }

            var now = current.CapturedAt;
            var snapshots = await _repository.GetBaselinesAsync(now - SnapshotHistory, cancellationToken).ConfigureAwait(false);
            var changes = new List<DetectedChange>(snapshots.Count == 0
                ? BaselineComparer.RecentlyInstalled(current, RecentInstallDays)
                : BaselineComparer.Compare(snapshots[^1], current));

            var catalog = await _repository.GetKnownAppsAsync(cancellationToken).ConfigureAwait(false);
            var usage = await _repository.GetAppUsageAsync(now - FrequentAppDetector.Window, now, cancellationToken).ConfigureAwait(false);
            changes.AddRange(FrequentAppDetector.Detect(catalog, usage, now));

            await _repository.SaveChangesAsync(changes, cancellationToken).ConfigureAwait(false);
            await _repository.SaveBaselineAsync(current, cancellationToken).ConfigureAwait(false);
            if (changes.Count > 0)
            {
                _logger.LogInformation("{Count} change(s) detected on the system.", changes.Count);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return changes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "The system snapshot for change detection failed.");
            return [];
        }
    }

    public async Task<IReadOnlyList<DetectedChange>> GetTimelineAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        (await _repository.GetChangesAsync(since, cancellationToken).ConfigureAwait(false))
            .OrderByDescending(c => c.Before)
            .ThenByDescending(c => c.Importance)
            .ToArray();

    public Task<IReadOnlyList<SystemBaseline>> GetSnapshotsAsync(CancellationToken cancellationToken) =>
        _repository.GetBaselinesAsync(_time.GetUtcNow() - SnapshotHistory, cancellationToken);

    public async Task<ChangeComparison> CompareAsync(BaselineReference reference, CancellationToken cancellationToken)
    {
        var current = await CaptureCurrentAsync(force: false, cancellationToken).ConfigureAwait(false);
        var snapshots = await GetSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        var chosen = Pick(snapshots, reference, current.CapturedAt);
        if (chosen is null)
        {
            var oldest = snapshots.Count > 0
                ? $"The oldest snapshot is from {snapshots[0].CapturedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}."
                : "No snapshot has been recorded yet; the first one is taken a minute after PulseDesk starts.";
            return new ChangeComparison(reference, null, [], $"Not enough history for this comparison yet. {oldest}");
        }

        var changes = BaselineComparer.Compare(chosen, current)
            .OrderByDescending(c => c.Importance)
            .ThenBy(c => c.Type)
            .ToArray();
        var note = $"Current state compared with the snapshot of {chosen.CapturedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}.";
        if (!chosen.Inventory.AppsAvailable || !current.Inventory.AppsAvailable)
        {
            note += " Installed applications could not be compared (not readable in one of the snapshots).";
        }

        return new ChangeComparison(reference, chosen, changes, note);
    }

    /// <summary>Builds a snapshot of the current state (reused for a few minutes unless <paramref name="force"/>).</summary>
    public async Task<SystemBaseline> CaptureCurrentAsync(bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (!force && _current is { } cached && now - cached.CapturedAt < CurrentCacheDuration)
            {
                return cached;
            }

            var inventory = await _inventory.CollectAsync(cancellationToken).ConfigureAwait(false);
            var information = await _systemInfo.GetAsync(cancellationToken).ConfigureAwait(false);
            var volumes = (_monitor.Current.Storage ?? [])
                .Where(v => v.Kind == DriveKind.Fixed)
                .Select(v => new DeviceInfo("Volume", v.Letter, v.Label is { } label ? $"{v.Letter} ({label})" : v.Letter) { TotalBytes = v.TotalBytes, UsedBytes = v.UsedBytes });

            var baseline = new SystemBaseline
            {
                CapturedAt = now,
                OsName = information.OperatingSystem.ProductName,
                OsVersion = information.OperatingSystem.DisplayVersion,
                OsBuild = information.OperatingSystem.Build,
                Processor = information.Processor.Name,
                InstalledMemoryBytes = information.InstalledMemoryBytes,
                BiosVersion = information.Firmware.BiosVersion,
                Inventory = inventory with { Devices = [.. inventory.Devices, .. volumes] },
                Usage = await UsageAsync(now, cancellationToken).ConfigureAwait(false),
            };
            _current = baseline;
            return baseline;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
            _timer = null;
        }
    }

    /// <summary>The reference snapshot for a comparison, or null when none is old enough.</summary>
    internal static SystemBaseline? Pick(IReadOnlyList<SystemBaseline> snapshots, BaselineReference reference, DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.ToLocalTime().Date, now.ToLocalTime().Offset);
        return reference switch
        {
            BaselineReference.Today => snapshots.FirstOrDefault(s => s.CapturedAt >= today),
            BaselineReference.Yesterday => snapshots.LastOrDefault(s => s.CapturedAt >= today.AddDays(-1) && s.CapturedAt < today)
                ?? snapshots.LastOrDefault(s => s.CapturedAt < today),
            BaselineReference.SevenDaysAgo => snapshots.LastOrDefault(s => s.CapturedAt <= today.AddDays(-6)),
            _ => snapshots.LastOrDefault(s => s.CapturedAt <= today.AddDays(-29)),
        };
    }

    /// <summary>Average usage over the 24 hours before <paramref name="now"/> from the minute history.</summary>
    private async Task<UsageSummary?> UsageAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var minutes = await _repository.GetSystemUsageAsync(now.AddDays(-1), now, HistoryResolution.Minute, cancellationToken).ConfigureAwait(false);
        if (minutes.Count == 0)
        {
            return null;
        }

        static double? Weighted(IEnumerable<AggregateValue?> values)
        {
            double sum = 0;
            long count = 0;
            foreach (var value in values)
            {
                if (value is { } v)
                {
                    sum += v.Average * v.Count;
                    count += v.Count;
                }
            }

            return count == 0 ? null : sum / count;
        }

        return new UsageSummary(minutes.Count)
        {
            CpuAverage = Weighted(minutes.Select(m => m.Cpu)),
            MemoryAverage = Weighted(minutes.Select(m => m.Memory)),
            DiskAverage = Weighted(minutes.Select(m => m.Disk)),
        };
    }
}
