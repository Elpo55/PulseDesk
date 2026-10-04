using PulseDesk.Core.Interfaces;
using PulseDesk.Core.Models;

namespace PulseDesk.Core.Monitoring;

/// <summary>
/// Binds a provider to the part of <see cref="SystemSnapshot"/> it fills. Adding a metric to PulseDesk
/// means adding a provider, a snapshot property and one source in <see cref="MetricsMonitor"/>.
/// </summary>
internal abstract class MetricSource(MetricKind kind)
{
    private Task? _inFlight;

    public MetricKind Kind { get; } = kind;

    /// <summary>
    /// True while a previous collection has not completed (for example, after a timeout). The monitor then
    /// skips this source: providers are never called concurrently with themselves.
    /// </summary>
    public bool IsBusy => _inFlight is { IsCompleted: false };

    /// <summary>Collects and returns a function that applies the result to a snapshot.</summary>
    public Task<Func<SystemSnapshot, SystemSnapshot>> CollectAsync(CancellationToken cancellationToken)
    {
        var task = CollectCoreAsync(cancellationToken);
        _inFlight = task;
        return task;
    }

    /// <summary>Removes this source's data from a snapshot (used when collection fails).</summary>
    public abstract SystemSnapshot Clear(SystemSnapshot snapshot);

    protected abstract Task<Func<SystemSnapshot, SystemSnapshot>> CollectCoreAsync(CancellationToken cancellationToken);
}

internal sealed class MetricSource<T>(
    MetricKind kind,
    IMetricProvider<T> provider,
    Func<SystemSnapshot, T?, SystemSnapshot> apply) : MetricSource(kind)
{
    public override SystemSnapshot Clear(SystemSnapshot snapshot) => apply(snapshot, default);

    protected override async Task<Func<SystemSnapshot, SystemSnapshot>> CollectCoreAsync(CancellationToken cancellationToken)
    {
        var value = await provider.CollectAsync(cancellationToken).ConfigureAwait(false);
        return snapshot => apply(snapshot, value);
    }
}
