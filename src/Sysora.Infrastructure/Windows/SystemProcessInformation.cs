using System.Runtime.InteropServices;

namespace Sysora.Infrastructure.Windows;

/// <summary>
/// Native SYSTEM_PROCESS_INFORMATION entry returned by
/// <c>NtQuerySystemInformation(SystemProcessInformation)</c> — the same data source Task Manager uses.
/// One call returns CPU times, memory and I/O counters for every process without opening any process
/// handle, so it works for protected processes too and costs a single system call.
/// </summary>
/// <remarks>Layout for 64-bit Windows (x64 and ARM64), 256 bytes; checked by a unit test.</remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct SystemProcessInformation
{
    public uint NextEntryOffset;
    public uint NumberOfThreads;
    public long WorkingSetPrivateSize;
    public uint HardFaultCount;
    public uint NumberOfThreadsHighWatermark;
    public ulong CycleTime;
    public long CreateTime;
    public long UserTime;
    public long KernelTime;
    public UnicodeString ImageName;
    public int BasePriority;
    public nint UniqueProcessId;
    public nint InheritedFromUniqueProcessId;
    public uint HandleCount;
    public uint SessionId;
    public nuint UniqueProcessKey;
    public nuint PeakVirtualSize;
    public nuint VirtualSize;
    public uint PageFaultCount;
    public nuint PeakWorkingSetSize;
    public nuint WorkingSetSize;
    public nuint QuotaPeakPagedPoolUsage;
    public nuint QuotaPagedPoolUsage;
    public nuint QuotaPeakNonPagedPoolUsage;
    public nuint QuotaNonPagedPoolUsage;
    public nuint PagefileUsage;
    public nuint PeakPagefileUsage;
    public nuint PrivatePageCount;
    public long ReadOperationCount;
    public long WriteOperationCount;
    public long OtherOperationCount;
    public long ReadTransferCount;
    public long WriteTransferCount;
    public long OtherTransferCount;
}

/// <summary>Native UNICODE_STRING (length in bytes, not null-terminated).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UnicodeString
{
    public ushort Length;
    public ushort MaximumLength;
    public nint Buffer;
}
