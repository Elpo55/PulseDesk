using PulseDesk.Core.Models;
using PulseDesk.Core.Monitoring;

namespace PulseDesk.Tests.Core;

public sealed class ProcessAggregationTests
{
    private static ProcessMetrics Process(int pid, string name, double cpu, ulong memory, double? ioRead = null) =>
        new(new ProcessIdentity(pid, 0), name)
        {
            CpuPercent = cpu,
            PrivateWorkingSetBytes = memory,
            IoReadBytesPerSecond = ioRead,
        };

    [Fact]
    public void GroupByName_SumsUsageAcrossInstances_IgnoringCase()
    {
        var groups = ProcessAggregation.GroupByName(
        [
            Process(1, "chrome.exe", 5, 100),
            Process(2, "Chrome.exe", 7, 200, ioRead: 1000),
            Process(3, "code.exe", 2, 50),
        ]);

        var chrome = Assert.Single(groups, g => g.Name.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, chrome.InstanceCount);
        Assert.Equal(12, chrome.CpuPercent);
        Assert.Equal(300UL, chrome.PrivateWorkingSetBytes);
        Assert.Equal(1000, chrome.IoBytesPerSecond);
    }

    [Fact]
    public void GroupByName_TreatsUnknownCpuAsZero()
    {
        var group = Assert.Single(ProcessAggregation.GroupByName(
        [
            new ProcessMetrics(new ProcessIdentity(1, 0), "new.exe") { CpuPercent = null },
        ]));

        Assert.Equal(0, group.CpuPercent);
    }

    [Fact]
    public void Top_RanksByRequestedResource()
    {
        var groups = ProcessAggregation.GroupByName(
        [
            Process(1, "a.exe", 1, 900),
            Process(2, "b.exe", 9, 100),
            Process(3, "c.exe", 5, 500, ioRead: 10),
        ]);

        Assert.Equal(["b.exe", "c.exe"], ProcessAggregation.TopByCpu(groups, 2).Select(g => g.Name));
        Assert.Equal(["a.exe", "c.exe"], ProcessAggregation.TopByMemory(groups, 2).Select(g => g.Name));
        Assert.Equal("c.exe", ProcessAggregation.TopByIo(groups, 1)[0].Name);
    }
}
