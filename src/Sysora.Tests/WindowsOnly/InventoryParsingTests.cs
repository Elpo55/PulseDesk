using Sysora.Infrastructure.SystemInfo;

namespace Sysora.Tests.WindowsOnly;

/// <summary>The parsing helpers of the Windows inventory (installed applications, startup programs, network adapters).</summary>
public sealed class InventoryParsingTests
{
    [Theory]
    [InlineData("20260302", 2026, 3, 2)]
    [InlineData("2026032", 0, 0, 0)]
    [InlineData("notadate", 0, 0, 0)]
    [InlineData(null, 0, 0, 0)]
    public void InstallDate_IsParsedStrictly(string? value, int year, int month, int day)
    {
        var parsed = InventoryParsing.ParseInstallDate(value);
        Assert.Equal(year == 0 ? null : new DateOnly(year, month, day), parsed);
    }

    [Fact]
    public void StartupApprovedFlags_FollowWindowsConvention()
    {
        Assert.True(InventoryParsing.IsApproved(null));
        Assert.True(InventoryParsing.IsApproved([2, 0, 0, 0]));
        Assert.False(InventoryParsing.IsApproved([3, 0, 0, 0]));
        Assert.False(InventoryParsing.IsPhysicalAdapter("Hyper-V Virtual Ethernet Adapter"));
        Assert.False(InventoryParsing.IsPhysicalAdapter("Realtek PCIe GbE Family Controller-WFP Native MAC Layer LightWeight Filter-0000"));
        Assert.False(InventoryParsing.IsPhysicalAdapter("MediaTek Wi-Fi 6E MT7922 (RZ616) 160MHz Wireless LAN Card-QoS Packet Scheduler-0000"));
        Assert.False(InventoryParsing.IsPhysicalAdapter("Microsoft Kernel Debug Network Adapter"));
        Assert.True(InventoryParsing.IsPhysicalAdapter("Intel(R) Wi-Fi 6E AX211 160MHz"));
        Assert.True(InventoryParsing.IsPhysicalAdapter("Realtek PCIe GbE Family Controller"));
    }
}
