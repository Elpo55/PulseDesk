using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sysora.Infrastructure.Windows;

/// <summary>
/// P/Invoke declarations for the documented Win32 APIs Sysora uses. Every call is read-only except
/// <see cref="TerminateProcess"/>, which is only reached after explicit user confirmation.
/// </summary>
internal static unsafe partial class NativeMethods
{
    public const int ErrorAccessDenied = 5;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorInsufficientBuffer = 122;

    public const uint ProcessTerminate = 0x0001;
    public const uint ProcessQueryLimitedInformation = 0x1000;

    public const ushort AllProcessorGroups = 0xFFFF;

    // ---- Memory ---------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPerformanceInfo(out PerformanceInformation information, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    // ---- Processors -----------------------------------------------------------------------------

    public enum LogicalProcessorRelationship
    {
        ProcessorCore = 0,
        NumaNode = 1,
        Cache = 2,
        ProcessorPackage = 3,
        Group = 4,
        All = 0xFFFF,
    }

    [LibraryImport("kernel32.dll")]
    public static partial uint GetActiveProcessorCount(ushort groupNumber);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLogicalProcessorInformationEx(LogicalProcessorRelationship relationship, byte* buffer, ref uint returnedLength);

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessorPowerInformation
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    public const int PowerInformationLevelProcessorInformation = 11;

    [LibraryImport("powrprof.dll")]
    public static partial int CallNtPowerInformation(int informationLevel, void* inputBuffer, uint inputBufferLength, void* outputBuffer, uint outputBufferLength);

    // ---- Processes ------------------------------------------------------------------------------

    public const int SystemProcessInformationClass = 5;
    public const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int systemInformationClass, void* systemInformation, uint systemInformationLength, out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* exeName, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(SafeProcessHandle process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
