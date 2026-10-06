using System.Runtime.CompilerServices;
using Sysora.Infrastructure.Hardware;
using Sysora.Infrastructure.Performance;
using Sysora.Infrastructure.SystemInfo;
using Sysora.Infrastructure.Windows;

namespace Sysora.Tests.Services;

/// <summary>Pure parsing and normalization helpers of the Windows layer (no hardware involved).</summary>
public sealed class InfrastructureParsingTests
{
    [Fact]
    public void NativeLayouts_MatchWindowsDefinitions()
    {
        // SYSTEM_PROCESS_INFORMATION is 0x100 bytes on 64-bit Windows; PDH items are 16 and 24 bytes.
        Assert.Equal(256, Unsafe.SizeOf<SystemProcessInformation>());
        Assert.Equal(16, Unsafe.SizeOf<Pdh.FormattedValue>());
        Assert.Equal(24, Unsafe.SizeOf<Pdh.FormattedValueItem>());
    }

    [Fact]
    public void GpuEngineInstance_IsParsed()
    {
        Assert.True(GpuCounterInstance.TryParseEngine(
            "pid_1234_luid_0x00000000_0x0000C3B5_phys_0_eng_3_engtype_VideoDecode",
            out var adapter, out var engine, out var type));

        Assert.Equal("luid_0x00000000_0x0000C3B5", adapter.ToString());
        Assert.Equal("phys_0_eng_3", engine.ToString());
        Assert.Equal("VideoDecode", type.ToString());
    }

    [Fact]
    public void GpuEngineInstance_WithSpacesInEngineType_IsParsed()
    {
        Assert.True(GpuCounterInstance.TryParseEngine(
            "pid_8_luid_0x00000000_0x00013158_phys_0_eng_5_engtype_High Priority 3D",
            out _, out _, out var type));

        Assert.Equal("High Priority 3D", type.ToString());
    }

    [Theory]
    [InlineData("pid_1234_phys_0_eng_3_engtype_3D")]
    [InlineData("luid_0x00000000_0x0000C3B5_phys_0")]
    [InlineData("garbage")]
    public void GpuEngineInstance_RejectsMalformedNames(string instance)
    {
        Assert.False(GpuCounterInstance.TryParseEngine(instance, out _, out _, out _));
    }

    [Fact]
    public void GpuMemoryInstance_YieldsAdapterId()
    {
        Assert.True(GpuCounterInstance.TryGetAdapterId("luid_0x00000000_0x000164FE_phys_0", out var adapter));
        Assert.Equal("luid_0x00000000_0x000164FE", adapter.ToString());
    }

    [Fact]
    public void Luid_IsFormattedLikePerformanceCounters()
    {
        Assert.Equal("luid_0x00000000_0x000164FE", DxgiAdapterEnumerator.FormatLuid(0, 0x164FE));
        Assert.Equal("luid_0xFFFFFFFF_0x00000001", DxgiAdapterEnumerator.FormatLuid(-1, 1));
    }

    [Theory]
    [InlineData("0,5", true, 0, 5)]
    [InlineData("1,12", true, 1, 12)]
    [InlineData("_Total", false, 0, 0)]
    [InlineData("0,_Total", false, 0, 0)]
    public void ProcessorInstance_IsParsed(string name, bool expected, int group, int number)
    {
        Assert.Equal(expected, ProcessorInstanceName.TryParse(name, out var g, out var n));
        if (expected)
        {
            Assert.Equal(group, g);
            Assert.Equal(number, n);
        }
    }

    [Theory]
    [InlineData("Windows 10 Pro", 26100, "Windows 11 Pro")]
    [InlineData("Windows 10 Home", 22000, "Windows 11 Home")]
    [InlineData("Windows 10 Pro", 19045, "Windows 10 Pro")]
    [InlineData("Windows 11 Home", 26200, "Windows 11 Home")]
    [InlineData(null, 26100, "Windows")]
    public void ProductName_ReportsWindows11FromBuildNumber(string? registryName, int build, string expected)
    {
        Assert.Equal(expected, SystemInfoNormalizer.NormalizeProductName(registryName, build));
    }

    [Theory]
    [InlineData("To be filled by O.E.M.", null)]
    [InlineData("Default string", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    [InlineData("ASUSTeK COMPUTER INC. ", "ASUSTeK COMPUTER INC.")]
    public void FirmwarePlaceholders_AreTreatedAsUnavailable(string? raw, string? expected)
    {
        Assert.Equal(expected, SystemInfoNormalizer.CleanFirmwareValue(raw));
    }

    [Fact]
    public void ProcessorName_CollapsesSpaces()
    {
        Assert.Equal("Intel(R) Core(TM) i7-8700 CPU @ 3.20GHz", SystemInfoNormalizer.CleanProcessorName("Intel(R) Core(TM) i7-8700 CPU @ 3.20GHz    "));
        Assert.Equal("AMD Ryzen 9 7845HX", SystemInfoNormalizer.CleanProcessorName("AMD   Ryzen 9   7845HX"));
    }
}
