using Microsoft.Extensions.Logging;
using PulseDesk.Core.Analysis;
using PulseDesk.Core.Formatting;
using PulseDesk.Core.History;
using PulseDesk.Core.Interfaces;
using PulseDesk.Core.Models;

namespace PulseDesk.Core.Monitoring;

/// <summary>
/// Keeps the history reliable across sleep: before the PC sleeps, writes what is pending (the minute and the
/// application bucket in progress), so nothing is lost if it never resumes (battery empty); after it resumes, records
/// how long it slept and refreshes every metric at once instead of waiting for each interval.
/// </summary>
public sealed class PowerTransitionService(
    ISystemPowerEvents power,
    IMetricsMonitor monitor,
    IPerformanceHistory history,
    HistoryRecorder recorder,
    ProcessHistory processes,
    ILogger<PowerTransitionService> logger,
    TimeProvider? timeProvider = null) : IDisposable
{
    /// <summary>Longest wait for pending history writes before sleep (Windows allows about two seconds).</summary>
    public static readonly TimeSpan SuspendFlushTimeout = TimeSpan.FromSeconds(1.5);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _suspendedAtTicks;
    private bool _started;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        power.Suspending += OnSuspending;
        power.Resumed += OnResumed;
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        power.Suspending -= OnSuspending;
        power.Resumed -= OnResumed;
    }

    private void OnSuspending(object? sender, EventArgs e)
    {
        var now = _time.GetUtcNow();
        Interlocked.Exchange(ref _suspendedAtTicks, now.UtcTicks);
        logger.LogInformation("The PC is going to sleep.");
        try
        {
            history.AddEvent(new SystemEvent(now, SystemEventKind.SystemSuspending, "PC going to sleep"));
            if (processes.FlushBucket() is { } bucket)
            {
                recorder.Record(bucket);
            }

            // Bounded wait: the callback must return before Windows suspends.
            if (!recorder.FlushAsync().Wait(SuspendFlushTimeout))
            {
                logger.LogDebug("Pending history writes did not complete before sleep.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "The history could not be written before sleep.");
        }
    }

    private void OnResumed(object? sender, EventArgs e)
    {
        var now = _time.GetUtcNow();
        var suspendedAt = Interlocked.Exchange(ref _suspendedAtTicks, 0);
        var asleep = suspendedAt > 0 && now.UtcTicks > suspendedAt ? TimeSpan.FromTicks(now.UtcTicks - suspendedAt) : (TimeSpan?)null;
        logger.LogInformation("The PC resumed from sleep{Duration}.", asleep is { } d ? $" after {MetricFormatter.DurationCompact(d)}" : string.Empty);
        try
        {
            history.AddEvent(new SystemEvent(now, SystemEventKind.SystemResumed,
                asleep is { } duration ? $"PC resumed from sleep (asleep {MetricFormatter.DurationCompact(duration)})" : "PC resumed from sleep"));
            monitor.RequestRefresh(MetricKind.All);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Resuming after sleep failed.");
        }
    }
}
