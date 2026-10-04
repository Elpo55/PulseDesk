using System.Runtime.InteropServices;
using PulseDesk.Infrastructure.Windows;

namespace PulseDesk.Infrastructure.Hardware;

/// <summary>Processor layout read with <c>GetLogicalProcessorInformationEx</c>.</summary>
internal sealed record ProcessorTopology(
    int LogicalProcessors,
    int? PhysicalCores,
    int? Sockets,
    ulong? L2CacheBytes,
    ulong? L3CacheBytes,
    double? BaseFrequencyGHz)
{
    // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship (4) + Size (4), then a union.
    // For RelationCache, CACHE_RELATIONSHIP starts with Level (1 byte) at 8 and CacheSize (4 bytes) at 12.
    private const int RelationshipOffset = 0;
    private const int SizeOffset = 4;
    private const int CacheLevelOffset = 8;
    private const int CacheSizeOffset = 12;

    public static unsafe ProcessorTopology Read()
    {
        var logical = (int)NativeMethods.GetActiveProcessorCount(NativeMethods.AllProcessorGroups);
        if (logical <= 0)
        {
            logical = Environment.ProcessorCount;
        }

        int? cores = null, sockets = null;
        ulong? l2 = null, l3 = null;

        uint length = 0;
        NativeMethods.GetLogicalProcessorInformationEx(NativeMethods.LogicalProcessorRelationship.All, null, ref length);
        if (length > 0 && Marshal.GetLastPInvokeError() == NativeMethods.ErrorInsufficientBuffer)
        {
            var buffer = new byte[length];
            fixed (byte* pointer = buffer)
            {
                if (NativeMethods.GetLogicalProcessorInformationEx(NativeMethods.LogicalProcessorRelationship.All, pointer, ref length))
                {
                    int coreCount = 0, socketCount = 0;
                    ulong l2Total = 0, l3Total = 0;
                    for (var offset = 0; offset < length;)
                    {
                        var relationship = (NativeMethods.LogicalProcessorRelationship)(*(int*)(pointer + offset + RelationshipOffset));
                        var size = *(uint*)(pointer + offset + SizeOffset);
                        switch (relationship)
                        {
                            case NativeMethods.LogicalProcessorRelationship.ProcessorCore:
                                coreCount++;
                                break;
                            case NativeMethods.LogicalProcessorRelationship.ProcessorPackage:
                                socketCount++;
                                break;
                            case NativeMethods.LogicalProcessorRelationship.Cache:
                                var level = pointer[offset + CacheLevelOffset];
                                var cacheSize = *(uint*)(pointer + offset + CacheSizeOffset);
                                if (level == 2)
                                {
                                    l2Total += cacheSize;
                                }
                                else if (level == 3)
                                {
                                    l3Total += cacheSize;
                                }

                                break;
                        }

                        if (size == 0)
                        {
                            break;
                        }

                        offset += (int)size;
                    }

                    cores = coreCount > 0 ? coreCount : null;
                    sockets = socketCount > 0 ? socketCount : null;
                    l2 = l2Total > 0 ? l2Total : null;
                    l3 = l3Total > 0 ? l3Total : null;
                }
            }
        }

        return new ProcessorTopology(logical, cores, sockets, l2, l3, ReadBaseFrequency(logical));
    }

    /// <summary>Highest rated frequency among logical processors (hybrid CPUs have several core types).</summary>
    private static unsafe double? ReadBaseFrequency(int logicalProcessors)
    {
        var count = Math.Clamp(logicalProcessors, 1, 1024);
        var buffer = new NativeMethods.ProcessorPowerInformation[count];
        fixed (NativeMethods.ProcessorPowerInformation* pointer = buffer)
        {
            var status = NativeMethods.CallNtPowerInformation(
                NativeMethods.PowerInformationLevelProcessorInformation,
                null,
                0,
                pointer,
                (uint)(count * sizeof(NativeMethods.ProcessorPowerInformation)));
            if (status != 0)
            {
                return null;
            }
        }

        var maxMhz = buffer.Max(p => p.MaxMhz);
        return maxMhz > 0 ? maxMhz / 1000.0 : null;
    }
}
