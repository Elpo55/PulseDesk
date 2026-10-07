namespace Sysora.Core.History;

/// <summary>Kinds of events Sysora can actually observe. Nothing here is inferred from outside data.</summary>
public enum SystemEventKind
{
    /// <summary>The monitoring loop produced its first measurement.</summary>
    MonitoringStarted,

    /// <summary>The user paused monitoring.</summary>
    MonitoringPaused,

    /// <summary>The user resumed monitoring.</summary>
    MonitoringResumed,

    /// <summary>No measurement for a while: Sysora was paused or closed, or the PC was asleep.</summary>
    DataGap,

    /// <summary>An application started using a large share of the CPU.</summary>
    AppHighCpu,

    /// <summary>An application started using a large share of physical memory.</summary>
    AppHighMemory,

    /// <summary>An application that was among the top consumers exited.</summary>
    AppExited,

    /// <summary>Windows reported a different connectivity level.</summary>
    ConnectivityChanged,

    /// <summary>A volume appeared.</summary>
    VolumeAdded,

    /// <summary>A volume disappeared.</summary>
    VolumeRemoved,

    /// <summary>A metric could no longer be collected.</summary>
    MetricUnavailable,

    /// <summary>A metric can be collected again.</summary>
    MetricRestored,

    /// <summary>An alert was raised.</summary>
    AlertRaised,

    /// <summary>An alert condition ended.</summary>
    AlertResolved,

    /// <summary>A game was identified as running (start of a gaming session).</summary>
    GameStarted,

    /// <summary>A game was closed (end of a gaming session).</summary>
    GameEnded,

    /// <summary>Windows reported that the PC is going to sleep.</summary>
    SystemSuspending,

    /// <summary>Windows reported that the PC resumed from sleep.</summary>
    SystemResumed,

    /// <summary>An application started (and kept running for at least one more sample).</summary>
    AppStarted,

    /// <summary>A troubleshooting investigation started.</summary>
    InvestigationStarted,

    /// <summary>A troubleshooting investigation ended.</summary>
    InvestigationEnded,
}

/// <summary>
/// Something that happened at a given time, shown on the replay timeline and kept in the local history.
/// </summary>
/// <param name="Timestamp">When it happened (UTC), as observed by Sysora.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Title">Short description, e.g. "chrome.exe CPU rose to 62%".</param>
/// <param name="Detail">Optional details.</param>
public sealed record SystemEvent(DateTimeOffset Timestamp, SystemEventKind Kind, string Title, string? Detail = null)
{
    /// <summary>Key of the application concerned, for application events.</summary>
    public string? AppKey { get; init; }
}
