using Sysora.Core.Metrics;
using Sysora.Core.Models;
using Sysora.Core.Monitoring;

namespace Sysora.Core.Interfaces;

/// <summary>
/// Orchestrates metric providers: schedules collection, keeps the latest snapshot and short-term history,
/// and notifies subscribers.
/// </summary>
public interface IMetricsMonitor
{
    /// <summary>
    /// Raised after each collection round, on a background thread. Handlers must return quickly and marshal
    /// to the UI thread themselves.
    /// </summary>
    event EventHandler<SystemMetricsUpdatedEventArgs> MetricsUpdated;

    /// <summary>Raised when the monitor is paused, resumed, started or stopped.</summary>
    event EventHandler? StateChanged;

    /// <summary>Latest snapshot (never null).</summary>
    SystemSnapshot Current { get; }

    /// <summary>Recent samples for charts and averages, held in fixed-size ring buffers.</summary>
    MetricHistory History { get; }

    /// <summary>True between <see cref="StartAsync"/> and <see cref="StopAsync"/>.</summary>
    bool IsRunning { get; }

    /// <summary>True when collection is paused by the user.</summary>
    bool IsPaused { get; }

    /// <summary>Sysora's own measured CPU and memory usage, and the current self-imposed slowdown.</summary>
    SelfUsage SelfUsage { get; }

    /// <summary>Starts the monitoring loop. Returns once the loop is running.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Stops the monitoring loop and waits for it to finish.</summary>
    Task StopAsync();

    /// <summary>Suspends collection until <see cref="Resume"/> is called.</summary>
    void Pause();

    /// <summary>Resumes collection after <see cref="Pause"/>.</summary>
    void Resume();

    /// <summary>
    /// Switches between foreground (window visible) and background (window hidden) collection rates.
    /// </summary>
    void SetActivity(MonitoringActivity activity);

    /// <summary>Collects the given metrics as soon as possible, outside of their normal schedule.</summary>
    void RequestRefresh(MetricKind kinds);

    /// <summary>True while a troubleshooting investigation asks for detailed collection.</summary>
    bool IsInvestigating { get; }

    /// <summary>
    /// Temporarily collects with the Detailed intensity, even while the window is hidden (troubleshooting). Turning it off
    /// returns to the user's intensity.
    /// </summary>
    void SetInvestigationMode(bool enabled);

    /// <summary>Intensity and effective interval of each metric family right now.</summary>
    MonitoringScheduleInfo ScheduleInfo { get; }
}

/// <summary>How actively the monitor collects metrics.</summary>
public enum MonitoringActivity
{
    /// <summary>The dashboard is visible: use the configured intervals.</summary>
    Foreground,

    /// <summary>The dashboard is hidden: slow down metrics that only matter on screen.</summary>
    Background,
}
