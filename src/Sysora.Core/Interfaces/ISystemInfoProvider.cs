using Sysora.Core.Models;

namespace Sysora.Core.Interfaces;

/// <summary>
/// Provides the mostly static description of the machine (OS, processor, GPUs, firmware).
/// Implementations cache the result: hardware information is read once, then on demand.
/// </summary>
public interface ISystemInfoProvider
{
    /// <summary>Returns the cached information, collecting it on first call.</summary>
    Task<SystemInformation> GetAsync(CancellationToken cancellationToken);

    /// <summary>Collects the information again (for example after a driver change).</summary>
    Task<SystemInformation> RefreshAsync(CancellationToken cancellationToken);
}
