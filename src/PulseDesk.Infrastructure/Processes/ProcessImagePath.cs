using PulseDesk.Infrastructure.Windows;

namespace PulseDesk.Infrastructure.Processes;

/// <summary>
/// Reads the executable path of a process with the least access right Windows offers
/// (<c>PROCESS_QUERY_LIMITED_INFORMATION</c>). Called once per new process, never per sample.
/// </summary>
internal static class ProcessImagePath
{
    /// <summary>
    /// Returns the full path of the executable, or null when Windows denies access (protected and other users'
    /// processes) or when the process ID now belongs to a different process.
    /// </summary>
    /// <param name="processId">Process ID.</param>
    /// <param name="creationTime">Creation time from the snapshot, used to reject a reused process ID (0 = do not check).</param>
    public static unsafe string? TryGet(int processId, long creationTime)
    {
        if (processId is 0 or 4)
        {
            return null;
        }

        using var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle.IsInvalid)
        {
            return null;
        }

        if (creationTime != 0
            && (!NativeMethods.GetProcessTimes(handle, out var actual, out _, out _, out _) || actual != creationTime))
        {
            return null;
        }

        const int capacity = 1024;
        var buffer = stackalloc char[capacity];
        var size = (uint)capacity;
        if (NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size))
        {
            return new string(buffer, 0, (int)size);
        }

        // Paths longer than MAX_PATH-ish are rare; retry once with a large heap buffer.
        var large = new char[32_768];
        fixed (char* pointer = large)
        {
            size = (uint)large.Length;
            return NativeMethods.QueryFullProcessImageName(handle, 0, pointer, ref size) ? new string(pointer, 0, (int)size) : null;
        }
    }
}
