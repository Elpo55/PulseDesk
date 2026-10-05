using PulseDesk.Core.History;
using PulseDesk.Core.Models;

namespace PulseDesk.Tests.History;

public sealed class PerformanceHistoryTests
{
    private static readonly DateTimeOffset T0 = TestData.Start;

    [Fact]
    public void Record_AddsOneSnapshotPerCpuSample()
    {
        var history = new PerformanceHistory(TimeSpan.FromMinutes(15));
        MetricSnapshot? raised = null;
        history.SnapshotRecorded += (_, s) => raised = s;

        history.Record(TestData.System(T0, cpu: 37, memoryPercent: 52), MetricKind.Cpu | MetricKind.Memory);

        var latest = Assert.IsType<MetricSnapshot>(history.Latest);
        Assert.Same(latest, raised);
        Assert.Equal(37, latest.CpuPercent);
        Assert.Equal(52, latest.MemoryPercent!.Value, 1);
        Assert.Equal(5, latest.DiskActivePercent);
        Assert.Equal("C:", latest.DiskActiveDrive);
    }

    [Fact]
    public void Record_WithoutCpuUpdate_DoesNotAddSnapshot()
    {
        var history = new PerformanceHistory(TimeSpan.FromMinutes(15));

        history.Record(TestData.System(T0, cpu: 37), MetricKind.Memory | MetricKind.Storage);

        Assert.Null(history.Latest);
    }

    [Fact]
    public void Record_UnavailableMetric_StaysNullInsteadOfZero()
    {
        var history = new PerformanceHistory(TimeSpan.FromMinutes(15));
        var snapshot = TestData.System(T0, cpu: 10, disk: null) with { Gpus = null, Network = null };

        history.Record(snapshot, MetricKind.Cpu);

        Assert.Null(history.Latest!.DiskActivePercent);
        Assert.Null(history.Latest.GpuPercent);
        Assert.Null(history.Latest.NetworkReceiveBitsPerSecond);
    }

    [Fact]
    public void GetSnapshots_ReturnsRequestedPeriodOnly()
    {
        var history = new PerformanceHistory(TimeSpan.FromMinutes(15));
        for (var i = 0; i < 60; i++)
        {
            history.Record(TestData.System(T0.AddSeconds(i), cpu: i), MetricKind.Cpu);
        }

        var period = history.GetSnapshots(T0.AddSeconds(10), T0.AddSeconds(19));

        Assert.Equal(10, period.Count);
        Assert.Equal(10, period[0].CpuPercent);
        Assert.Equal(19, period[^1].CpuPercent);
        Assert.Equal(11, history.GetRecent(TimeSpan.FromSeconds(10)).Count);
    }

    [Fact]
    public void RingBuffer_KeepsOnlyTheConfiguredDuration()
    {
        var history = new PerformanceHistory(TimeSpan.FromMinutes(1));
        Assert.Equal(120, history.Capacity);

        for (var i = 0; i < 500; i++)
        {
            history.Record(TestData.System(T0.AddMilliseconds(500 * i), cpu: i % 100), MetricKind.Cpu);
        }

        var all = history.GetSnapshots(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.Equal(120, all.Count);
        Assert.Equal(T0.AddMilliseconds(500 * 380), all[0].Timestamp);
        Assert.Equal(T0.AddMilliseconds(500 * 499), all[^1].Timestamp);
    }

    [Fact]
    public void Resize_KeepsMostRecentSnapshots()
    {
        var history = new PerformanceHistory(TimeSpan.FromMinutes(5));
        for (var i = 0; i < 300; i++)
        {
            history.Record(TestData.System(T0.AddSeconds(i), cpu: 1), MetricKind.Cpu);
        }

        history.Resize(TimeSpan.FromMinutes(1));

        var all = history.GetSnapshots(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.Equal(120, all.Count);
        Assert.Equal(T0.AddSeconds(299), all[^1].Timestamp);
    }

    [Fact]
    public void GetNearest_ReturnsClosestSnapshot()
    {
        var history = new PerformanceHistory(TimeSpan.FromMinutes(15));
        history.Record(TestData.System(T0, cpu: 1), MetricKind.Cpu);
        history.Record(TestData.System(T0.AddSeconds(10), cpu: 2), MetricKind.Cpu);

        Assert.Equal(1, history.GetNearest(T0.AddSeconds(4))!.CpuPercent);
        Assert.Equal(2, history.GetNearest(T0.AddSeconds(6))!.CpuPercent);
        Assert.Equal(2, history.GetNearest(T0.AddHours(1))!.CpuPercent);
    }

    [Fact]
    public void OutOfOrderSnapshot_IsIgnored()
    {
        var history = new PerformanceHistory(TimeSpan.FromMinutes(15));
        history.Record(TestData.System(T0.AddSeconds(5), cpu: 1), MetricKind.Cpu);
        history.Record(TestData.System(T0, cpu: 2), MetricKind.Cpu);

        Assert.Single(history.GetSnapshots(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
    }

    [Fact]
    public void LongInterval_IsRecordedAsDataGap()
    {
        var history = new PerformanceHistory(TimeSpan.FromMinutes(15));
        history.Record(TestData.System(T0, cpu: 1), MetricKind.Cpu);
        history.Record(TestData.System(T0.AddMinutes(10), cpu: 1), MetricKind.Cpu);

        var events = history.GetEvents(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.Equal(SystemEventKind.MonitoringStarted, events[0].Kind);
        var gap = Assert.Single(events, e => e.Kind == SystemEventKind.DataGap);
        Assert.Equal(T0, gap.Timestamp);
        Assert.Contains("10m", gap.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void TopApps_AreRefreshedOnlyWhenProcessesAreSampled()
    {
        var history = new PerformanceHistory(TimeSpan.FromMinutes(15));
        var processes = new[]
        {
            TestData.Process(1, "a.exe", 30, 100),
            TestData.Process(2, "b.exe", 1, 900),
            TestData.Process(3, "b.exe", 1, 100),
        };

        history.Record(TestData.System(T0, cpu: 40, processes: processes), MetricKind.Cpu | MetricKind.Processes);
        history.Record(TestData.System(T0.AddSeconds(1), cpu: 40), MetricKind.Cpu);

        var first = history.GetNearest(T0)!;
        var second = history.GetNearest(T0.AddSeconds(1))!;
        Assert.Same(first.TopApps, second.TopApps);
        var b = Assert.Single(first.TopApps, a => a.Name == "b.exe");
        Assert.Equal(2, b.InstanceCount);
        Assert.Equal(1000UL * 1024 * 1024, b.MemoryBytes);
    }

    [Fact]
    public void BusyApplication_ProducesOneEvent_AndItsExitAnother()
    {
        var history = new PerformanceHistory(TimeSpan.FromMinutes(15));
        for (var i = 0; i < 10; i++)
        {
            var processes = new[] { TestData.Process(1, "render.exe", 60, 500), TestData.Process(2, "idle.exe", 0, 10) };
            history.Record(TestData.System(T0.AddSeconds(i), cpu: 70, processes: processes), MetricKind.Cpu | MetricKind.Processes);
        }

        history.Record(TestData.System(T0.AddSeconds(10), cpu: 5, processes: [TestData.Process(2, "idle.exe", 0, 10)]), MetricKind.Cpu | MetricKind.Processes);

        var events = history.GetEvents(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        var busy = Assert.Single(events, e => e.Kind == SystemEventKind.AppHighCpu);
        Assert.Equal("render.exe CPU rose to 60%", busy.Title);
        var exit = Assert.Single(events, e => e.Kind == SystemEventKind.AppExited);
        Assert.Equal(T0.AddSeconds(10), exit.Timestamp);
        Assert.Equal("render.exe exited", exit.Title);
    }

    [Fact]
    public void SameNameDifferentPaths_AreDifferentApplications()
    {
        var history = new PerformanceHistory(TimeSpan.FromMinutes(15));
        var processes = new[]
        {
            TestData.Process(1, "Update.exe", 2, 50, path: @"C:\Apps\Chat\Update.exe"),
            TestData.Process(2, "Update.exe", 1, 40, path: @"C:\Apps\Music\Update.exe"),
        };

        history.Record(TestData.System(T0, cpu: 10, processes: processes), MetricKind.Cpu | MetricKind.Processes);

        Assert.Equal(2, history.Latest!.TopApps.Count(a => a.Name == "Update.exe"));
    }
}
