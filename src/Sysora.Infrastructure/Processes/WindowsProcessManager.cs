using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Sysora.Core.Interfaces;
using Sysora.Core.Models;
using Sysora.Core.Results;
using Sysora.Infrastructure.Windows;

namespace Sysora.Infrastructure.Processes;

/// <summary>
/// Reads process details and ends processes. Every operation first checks that the PID still belongs
/// to the same process (same creation time), so a reused PID can never be targeted by mistake.
/// </summary>
public sealed class WindowsProcessManager(ILogger<WindowsProcessManager> logger) : IProcessManager
{
    public Task<ProcessDetails> GetDetailsAsync(ProcessIdentity process, CancellationToken cancellationToken) =>
        Task.Run(() => ReadDetails(process), cancellationToken);

    public Task<OperationResult> TerminateAsync(ProcessIdentity process, CancellationToken cancellationToken) =>
        Task.Run(() => Terminate(process), cancellationToken);

    private static ProcessDetails ReadDetails(ProcessIdentity process)
    {
        var details = new ProcessDetails(process);
        using var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, (uint)process.ProcessId);
        if (handle.IsInvalid)
        {
            return details with { ExecutablePathError = DescribeOpenError(Marshal.GetLastPInvokeError()) };
        }

        if (!IsSameProcess(handle, process))
        {
            return details with { ExecutablePathError = "The process has exited." };
        }

        details = details with { IsCritical = NativeMethods.IsProcessCritical(handle, out var critical) ? critical : null };

        var path = QueryImagePath(handle, out var error);
        if (path is null)
        {
            return details with { ExecutablePathError = error };
        }

        details = details with { ExecutablePath = path };
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            details = details with
            {
                Description = NullIfEmpty(version.FileDescription),
                Company = NullIfEmpty(version.CompanyName),
                FileVersion = NullIfEmpty(version.FileVersion),
            };
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Version information is optional.
        }

        return details;
    }

    private OperationResult Terminate(ProcessIdentity process)
    {
        if (process.ProcessId is 0 or 4)
        {
            return OperationResult.Failure(OperationError.Protected, "This is a core Windows process and cannot be ended.");
        }

        if (process.ProcessId == Environment.ProcessId)
        {
            return OperationResult.Failure(OperationError.Protected, "Use Exit to close Sysora.");
        }

        using var handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessTerminate | NativeMethods.ProcessQueryLimitedInformation, false, (uint)process.ProcessId);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == NativeMethods.ErrorAccessDenied
                ? OperationResult.Failure(OperationError.AccessDenied, "Access denied. Ending this process requires administrator rights.")
                : OperationResult.Failure(OperationError.NotFound, "The process has already exited.");
        }

        if (!IsSameProcess(handle, process))
        {
            return OperationResult.Failure(OperationError.NotFound, "The process has already exited.");
        }

        // Ending a critical process makes Windows stop with a blue screen: never do it.
        if (NativeMethods.IsProcessCritical(handle, out var critical) && critical)
        {
            return OperationResult.Failure(OperationError.Protected, "This is a critical system process. Ending it would stop Windows.");
        }

        if (!NativeMethods.TerminateProcess(handle, 1))
        {
            var error = Marshal.GetLastPInvokeError();
            logger.LogWarning("Ending process {ProcessId} failed with error {Error}.", process.ProcessId, error);
            return error == NativeMethods.ErrorAccessDenied
                ? OperationResult.Failure(OperationError.AccessDenied, "Access denied. Ending this process requires administrator rights.")
                : OperationResult.Failure(OperationError.Failed, $"Windows could not end the process (error {error}).");
        }

        logger.LogInformation("Process {ProcessId} was ended at the user's request.", process.ProcessId);
        return OperationResult.Success;
    }

    private static bool IsSameProcess(SafeProcessHandle handle, ProcessIdentity process) =>
        process.CreationTimeTicks == 0
        || (NativeMethods.GetProcessTimes(handle, out var creationTime, out _, out _, out _) && creationTime == process.CreationTimeTicks);

    private static unsafe string? QueryImagePath(SafeProcessHandle handle, out string? error)
    {
        const int capacity = 32_768;
        var buffer = new char[capacity];
        fixed (char* pointer = buffer)
        {
            var size = (uint)capacity;
            if (NativeMethods.QueryFullProcessImageName(handle, 0, pointer, ref size))
            {
                error = null;
                return new string(pointer, 0, (int)size);
            }
        }

        error = DescribeOpenError(Marshal.GetLastPInvokeError());
        return null;
    }

    private static string DescribeOpenError(int error) => error switch
    {
        NativeMethods.ErrorAccessDenied => "Access denied. Viewing this information requires administrator rights.",
        NativeMethods.ErrorInvalidParameter => "The process has exited.",
        _ => $"Not available (Windows error {error}).",
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
