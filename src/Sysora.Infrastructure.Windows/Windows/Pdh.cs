using System.Runtime.InteropServices;

namespace Sysora.Infrastructure.Windows;

/// <summary>
/// Performance Data Helper (pdh.dll) interop. PDH is the documented API behind Performance Monitor and
/// Task Manager's counters. Counters are added by their English path, so Sysora works on any
/// Windows display language.
/// </summary>
internal static unsafe partial class Pdh
{
    public const uint Success = 0;
    public const uint CStatusValidData = 0x00000000;
    public const uint CStatusNewData = 0x00000001;
    public const uint MoreData = 0x800007D2;
    public const uint NoData = 0x800007D5;

    public const uint FormatDouble = 0x00000200;
    public const uint FormatNoCap100 = 0x00008000;

    /// <summary>PDH_FMT_COUNTERVALUE (64-bit layout).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct FormattedValue
    {
        [FieldOffset(0)]
        public uint CStatus;

        [FieldOffset(8)]
        public double DoubleValue;
    }

    /// <summary>PDH_FMT_COUNTERVALUE_ITEM_W (64-bit layout).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct FormattedValueItem
    {
        [FieldOffset(0)]
        public char* Name;

        [FieldOffset(8)]
        public FormattedValue Value;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhAddEnglishCounter(nint query, string counterPath, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out FormattedValue value);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, void* itemBuffer);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCloseQuery(nint query);

    public static bool IsValid(uint cstatus) => cstatus is CStatusValidData or CStatusNewData;
}

/// <summary>Error reported by PDH, with its status code.</summary>
internal sealed class PdhException(string operation, uint status)
    : Exception($"{operation} failed with PDH status 0x{status:X8}.")
{
    public uint Status { get; } = status;
}
