using PulseDesk.Core.Changes;

namespace PulseDesk.Core.Interfaces;

/// <summary>
/// Reads the software and hardware inventory of the PC (installed applications, startup programs, devices).
/// Read-only: nothing is ever modified. Collection can take a second or two; callers run it in the background.
/// </summary>
public interface ISystemInventoryProvider
{
    Task<SystemInventory> CollectAsync(CancellationToken cancellationToken);
}

/// <summary>Detects what changed on the PC by comparing snapshots of its state over time.</summary>
public interface IChangeDetectionService
{
    /// <summary>Changes recorded since <paramref name="since"/>, most recent first.</summary>
    Task<IReadOnlyList<DetectedChange>> GetTimelineAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Compares the current state with a reference snapshot.</summary>
    Task<ChangeComparison> CompareAsync(BaselineReference reference, CancellationToken cancellationToken);

    /// <summary>Snapshots available, oldest first.</summary>
    Task<IReadOnlyList<SystemBaseline>> GetSnapshotsAsync(CancellationToken cancellationToken);
}
