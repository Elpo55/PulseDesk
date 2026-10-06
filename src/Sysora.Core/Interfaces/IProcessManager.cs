using Sysora.Core.Models;
using Sysora.Core.Results;

namespace Sysora.Core.Interfaces;

/// <summary>
/// Reads process details and ends processes on explicit user request.
/// </summary>
public interface IProcessManager
{
    /// <summary>Reads details that may require additional access rights. Never throws for access errors.</summary>
    Task<ProcessDetails> GetDetailsAsync(ProcessIdentity process, CancellationToken cancellationToken);

    /// <summary>
    /// Ends the process. Fails without side effects when the process ID was reused by another process,
    /// when the process is critical to Windows, or when access is denied.
    /// </summary>
    Task<OperationResult> TerminateAsync(ProcessIdentity process, CancellationToken cancellationToken);
}
