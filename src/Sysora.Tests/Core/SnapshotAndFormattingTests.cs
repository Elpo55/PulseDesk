using System.Globalization;
using Sysora.Core.Formatting;
using Sysora.Core.Models;
using Sysora.Core.Monitoring;
using Sysora.Core.Settings;

namespace Sysora.Tests.Core;

public sealed class SnapshotAndFormattingTests
{
    private const ulong Gb = 1024UL * 1024 * 1024;

    [Fact]
    public void PrimaryGpu_IsTheBusiestAdapter()
    {
        var snapshot = new SystemSnapshot
        {
            Gpus =
            [
                new GpuMetrics("dgpu", "Discrete") { UsagePercent = 0, DedicatedMemoryTotalBytes = 8 * Gb },
                new GpuMetrics("igpu", "Integrated") { UsagePercent = 42, DedicatedMemoryTotalBytes = 512UL << 20 },
            ],
        };

        Assert.Equal("igpu", snapshot.PrimaryGpu?.AdapterId);
    }

    [Fact]
    public void PrimaryGpu_WhenIdle_PrefersTheAdapterWithMoreMemory()
    {
        var snapshot = new SystemSnapshot
        {
            Gpus =
            [
                new GpuMetrics("igpu", "Integrated") { UsagePercent = 0.2, DedicatedMemoryTotalBytes = 512UL << 20 },
                new GpuMetrics("dgpu", "Discrete") { UsagePercent = 0, DedicatedMemoryTotalBytes = 8 * Gb },
            ],
        };

        Assert.Equal("dgpu", snapshot.PrimaryGpu?.AdapterId);
    }

    [Fact]
    public void PrimaryGpu_WithoutData_IsNull()
    {
        Assert.Null(SystemSnapshot.Empty.PrimaryGpu);
        Assert.Null(SystemSnapshot.Empty.SystemDrive);
    }

    [Fact]
    public void StorageThreshold_IsComparedAtDisplayPrecision()
    {
        // 84.9 % is displayed as "85%": with an 85 % threshold, it must be reported too.
        var evaluator = new HealthEvaluator(new AlertSettings { DiskWarningPercent = 85, DiskCriticalPercent = 95 });
        var snapshot = new SystemSnapshot
        {
            Storage = [StorageMetrics.FromTotalAndFree(@"C:\", 1000 * Gb, 151 * Gb) with { Kind = DriveKind.Fixed }],
        };

        var storage = Assert.Single(evaluator.Evaluate(snapshot, MetricKind.Storage).Indicators);

        Assert.Equal(HealthStatus.Warning, storage.Status);
        Assert.Equal("Storage C: 85%", storage.Summary);
    }

    [Fact]
    public void Network_Rates_IgnoreHostOnlyAdapters_WhenAGatewayExists()
    {
        var network = new NetworkMetrics(NetworkConnectivity.InternetAccess,
        [
            new NetworkInterfaceMetrics("eth", "Ethernet", "NIC", NetworkInterfaceKind.Ethernet) { HasGateway = true, ReceiveBitsPerSecond = 1000, SendBitsPerSecond = 10 },
            new NetworkInterfaceMetrics("vbox", "Ethernet 2", "Host-only", NetworkInterfaceKind.Ethernet) { ReceiveBitsPerSecond = 500, SendBitsPerSecond = 500 },
        ]);

        Assert.Equal(1000, network.ReceiveBitsPerSecond);
        Assert.Equal(10, network.SendBitsPerSecond);
        Assert.Equal("eth", network.PrimaryInterface?.Id);
    }

    [Fact]
    public void Network_Rates_UseAllInterfaces_WhenNoneHasAGateway()
    {
        var network = new NetworkMetrics(NetworkConnectivity.LocalAccess,
        [
            new NetworkInterfaceMetrics("a", "A", "A", NetworkInterfaceKind.Ethernet) { ReceiveBitsPerSecond = 100 },
            new NetworkInterfaceMetrics("b", "B", "B", NetworkInterfaceKind.WiFi) { ReceiveBitsPerSecond = 50 },
        ]);

        Assert.Equal(150, network.ReceiveBitsPerSecond);
    }

    [Theory]
    [InlineData(1, "1 process")]
    [InlineData(2, "2 processes")]
    [InlineData(0, "0 processes")]
    public void Plural_UsesIrregularPluralWhenGiven(int count, string expected)
    {
        Assert.Equal(expected, MetricFormatter.Plural(count, "process", "processes"));
    }

    [Fact]
    public void Plural_DefaultsToAddingS()
    {
        Assert.Equal("16 threads", MetricFormatter.Plural(16, "thread"));
        Assert.Equal("1 core", MetricFormatter.Plural(1, "core"));
    }

    [Fact]
    public void Bytes_NonNullableAndNullableOverloads_Agree()
    {
        ulong value = 3 * Gb;
        ulong? nullable = value;

        Assert.Equal(MetricFormatter.Bytes(value, CultureInfo.InvariantCulture), MetricFormatter.Bytes(nullable, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Memory_And_Storage_DeriveUsedFromAvailable_AndClampOverflow()
    {
        var memory = MemoryMetrics.FromTotalAndAvailable(16 * Gb, 20 * Gb);
        var drive = StorageMetrics.FromTotalAndFree(@"D:\", 100 * Gb, 25 * Gb);

        Assert.Equal(0UL, memory.UsedBytes);
        Assert.Equal(16 * Gb, memory.AvailableBytes);
        Assert.Equal(75, drive.UsedPercent, precision: 6);
        Assert.Equal("D:", drive.Letter);
    }
}
