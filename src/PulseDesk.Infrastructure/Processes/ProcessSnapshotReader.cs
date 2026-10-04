using System.Runtime.InteropServices;
using PulseDesk.Infrastructure.Windows;

namespace PulseDesk.Infrastructure.Processes;

/// <summary>Raw counters of one process, as read from the kernel.</summary>
internal readonly record struct RawProcessEntry(
    int ProcessId,
    int ParentProcessId,
    long CreateTime,
    nint ImageNameBuffer,
    int ImageNameLength,
    long CpuTime,
    long IoReadBytes,
    long IoWriteBytes,
    ulong PrivateWorkingSet,
    ulong WorkingSet,
    ulong PrivateBytes,
    int ThreadCount,
    int HandleCount,
    int SessionId);

/// <summary>
/// Reads the process list with a single <c>NtQuerySystemInformation(SystemProcessInformation)</c> call into
/// a reusable native buffer (no per-sample garbage for the buffer itself).
/// </summary>
/// <remarks>
/// The returned <see cref="RawProcessEntry.ImageNameBuffer"/> pointers are only valid until the next
/// <see cref="Read"/>. Not thread-safe.
/// </remarks>
internal sealed unsafe class ProcessSnapshotReader : IDisposable
{
    private const uint InitialBufferSize = 1024 * 1024;
    private const uint MaxBufferSize = 64 * 1024 * 1024;

    private void* _buffer;
    private uint _bufferSize;

    public ProcessSnapshotReader()
    {
        _bufferSize = InitialBufferSize;
        _buffer = NativeMemory.Alloc(_bufferSize);
    }

    /// <summary>Takes a snapshot and fills <paramref name="entries"/> (cleared first).</summary>
    public void Read(List<RawProcessEntry> entries)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        entries.Clear();

        int status;
        while ((status = NativeMethods.NtQuerySystemInformation(
                   NativeMethods.SystemProcessInformationClass, _buffer, _bufferSize, out var required)) == NativeMethods.StatusInfoLengthMismatch)
        {
            // Processes may start between the size query and the read: add headroom.
            var newSize = Math.Max(required + (64 * 1024), _bufferSize * 2);
            if (newSize > MaxBufferSize)
            {
                throw new InvalidOperationException("The process list is unexpectedly large.");
            }

            NativeMemory.Free(_buffer);
            _buffer = NativeMemory.Alloc(newSize);
            _bufferSize = newSize;
        }

        if (status < 0)
        {
            throw new InvalidOperationException($"NtQuerySystemInformation failed with status 0x{status:X8}.");
        }

        var offset = 0u;
        while (true)
        {
            var info = (SystemProcessInformation*)((byte*)_buffer + offset);
            entries.Add(new RawProcessEntry(
                ProcessId: (int)info->UniqueProcessId,
                ParentProcessId: (int)info->InheritedFromUniqueProcessId,
                CreateTime: info->CreateTime,
                ImageNameBuffer: info->ImageName.Buffer,
                ImageNameLength: info->ImageName.Length / sizeof(char),
                CpuTime: info->UserTime + info->KernelTime,
                IoReadBytes: info->ReadTransferCount,
                IoWriteBytes: info->WriteTransferCount,
                PrivateWorkingSet: (ulong)Math.Max(info->WorkingSetPrivateSize, 0),
                WorkingSet: info->WorkingSetSize,
                PrivateBytes: info->PrivatePageCount,
                ThreadCount: (int)info->NumberOfThreads,
                HandleCount: (int)info->HandleCount,
                SessionId: (int)info->SessionId));

            if (info->NextEntryOffset == 0)
            {
                break;
            }

            offset += info->NextEntryOffset;
        }
    }

    /// <summary>Reads an image name from the current snapshot buffer.</summary>
    public static ReadOnlySpan<char> GetName(in RawProcessEntry entry) =>
        entry.ImageNameBuffer == 0 ? default : new ReadOnlySpan<char>((char*)entry.ImageNameBuffer, entry.ImageNameLength);

    public void Dispose()
    {
        NativeMemory.Free(_buffer);
        _buffer = null;
    }
}
