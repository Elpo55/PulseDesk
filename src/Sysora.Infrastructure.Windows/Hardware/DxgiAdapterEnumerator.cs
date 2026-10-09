using System.Globalization;
using System.Runtime.InteropServices;
using Sysora.Core.Models;

namespace Sysora.Infrastructure.Hardware;

/// <summary>
/// Lists graphics adapters through DXGI (<c>IDXGIFactory1::EnumAdapters1</c>), which reports each adapter's
/// name, memory sizes and LUID. The LUID links an adapter to its "GPU Engine" performance counters.
/// </summary>
internal static unsafe partial class DxgiAdapterEnumerator
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint DxgiAdapterFlagSoftware = 2;

    // COM vtable slots (IUnknown: 0-2, IDXGIObject: 3-6, IDXGIFactory: 7-11, IDXGIFactory1: 12-13;
    // IDXGIAdapter: 7-9, IDXGIAdapter1: 10).
    private const int ReleaseSlot = 2;
    private const int EnumAdapters1Slot = 12;
    private const int GetDesc1Slot = 10;

    private static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLowPart;
        public int AdapterLuidHighPart;
        public uint Flags;
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(Guid* riid, void** factory);

    /// <summary>Returns hardware adapters (software renderers excluded). Returns an empty list when DXGI is unavailable.</summary>
    public static IReadOnlyList<GpuAdapterInfo> Enumerate()
    {
        var iid = IidDxgiFactory1;
        void* factory;
        if (CreateDXGIFactory1(&iid, &factory) < 0 || factory is null)
        {
            return [];
        }

        var adapters = new List<GpuAdapterInfo>();
        try
        {
            var enumAdapters = (delegate* unmanaged<void*, uint, void**, int>)(*(void***)factory)[EnumAdapters1Slot];
            for (uint index = 0; index < 32; index++)
            {
                void* adapter;
                var hr = enumAdapters(factory, index, &adapter);
                if (hr == DxgiErrorNotFound || hr < 0)
                {
                    break;
                }

                try
                {
                    DxgiAdapterDesc1 desc;
                    var getDesc = (delegate* unmanaged<void*, DxgiAdapterDesc1*, int>)(*(void***)adapter)[GetDesc1Slot];
                    if (getDesc(adapter, &desc) >= 0 && (desc.Flags & DxgiAdapterFlagSoftware) == 0)
                    {
                        var name = new string(desc.Description).TrimEnd('\0').Trim();
                        adapters.Add(new GpuAdapterInfo(FormatLuid(desc.AdapterLuidHighPart, desc.AdapterLuidLowPart), name)
                        {
                            DedicatedMemoryBytes = desc.DedicatedVideoMemory,
                            SharedMemoryBytes = desc.SharedSystemMemory,
                        });
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }

        // The same physical adapter is never listed twice by DXGI, but be defensive.
        return adapters.DistinctBy(a => a.AdapterId).ToArray();
    }

    /// <summary>Formats a LUID the way GPU performance counter instances name it: "luid_0x00000000_0x0000C3B5".</summary>
    public static string FormatLuid(int highPart, uint lowPart) =>
        string.Create(CultureInfo.InvariantCulture, $"luid_0x{(uint)highPart:X8}_0x{lowPart:X8}");

    private static void Release(void* comObject)
    {
        if (comObject is not null)
        {
            var release = (delegate* unmanaged<void*, uint>)(*(void***)comObject)[ReleaseSlot];
            release(comObject);
        }
    }
}
