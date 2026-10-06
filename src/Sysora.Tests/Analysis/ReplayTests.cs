using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Sysora.Core.Analysis;
using Sysora.Core.History;
using Sysora.Core.Models;
using Sysora.Infrastructure.Storage;

namespace Sysora.Tests.Analysis;

public sealed class ReplayTests
{
    private static readonly DateTimeOffset T0 = TestData.Start;

    [Fact]
    public void CpuRiseFollowedByMemoryRise_IsExplainedInOrder_WithTheApplication()
    {
        var busy = (IReadOnlyList<AppSample>)[TestData.App("render.exe", 62, 900)];
        var calm = (IReadOnlyList<AppSample>)[TestData.App("render.exe", 1, 900)];
        var snapshots = TestData.Series(
            T0,
            300,
            i => i < 120 ? 38 : 87,
            i => i < 180 ? 50 : 91,
            apps: i => i < 120 ? calm : busy);

        var story = ReplayNarrator.Narrate(snapshots, []);

        var cpu = Assert.Single(story.Moments, m => m.Metric == HistoryMetric.Cpu && m.Kind == ReplayMomentKind.Rise);
        Assert.StartsWith("CPU rose from", cpu.Text, StringComparison.Ordinal);
        Assert.EndsWith("(render.exe 62%)", cpu.Text, StringComparison.Ordinal);
        Assert.Equal("name:RENDER.EXE", cpu.AppKey);
        Assert.InRange(cpu.Time, T0.AddSeconds(120), T0.AddSeconds(135));
        var memory = Assert.Single(story.Moments, m => m.Metric == HistoryMetric.Memory && m.Kind == ReplayMomentKind.Rise);
        Assert.True(memory.Time > cpu.Time);

        Assert.StartsWith("A sharp rise in CPU usage (render.exe 62%) appeared at", story.Summary, StringComparison.Ordinal);
        Assert.Contains("followed by memory usage at", story.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void QuietPeriod_IsSummarizedWithAverages()
    {
        var story = ReplayNarrator.Narrate(TestData.Series(T0, 120, _ => 20, _ => 40), []);

        Assert.Empty(story.Moments);
        Assert.StartsWith("No sharp rise or saturation in this period: CPU averaged 20%", story.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryAndEvents_AreMoments()
    {
        var events = new[] { new SystemEvent(T0.AddSeconds(50), SystemEventKind.AppExited, "render.exe exited") };

        var story = ReplayNarrator.Narrate(TestData.Series(T0, 200, i => i < 60 ? 95 : 20), events);

        Assert.Contains(story.Moments, m => m.Kind == ReplayMomentKind.Recovery && m.Text.StartsWith("CPU back to", StringComparison.Ordinal));
        Assert.Contains(story.Moments, m => m.Kind == ReplayMomentKind.Event && m.Text == "render.exe exited");
        Assert.Equal(story.Moments.OrderBy(m => m.Time).Select(m => m.Time), story.Moments.Select(m => m.Time));
    }

    [Fact]
    public void EmptyPeriod_SaysSo()
    {
        Assert.Equal("No measurements for this period.", ReplayNarrator.Narrate([], []).Summary);
    }

    [Fact]
    public async Task Service_UsesDetailedDataForRecentPeriods_AndMinuteHistoryForLongerOnes()
    {
        var token = TestContext.Current.CancellationToken;
        await using var repository = HistoryRepository.InMemory(NullLogger<HistoryRepository>.Instance);
        await repository.InitializeAsync(token);
        var history = new PerformanceHistory(TimeSpan.FromMinutes(15));
        for (var i = 0; i < 600; i++)
        {
            history.Record(TestData.System(T0.AddSeconds(i), cpu: 30), MetricKind.Cpu);
        }

        var minutes = Enumerable.Range(0, 120).Select(i => new SystemUsageAggregate
        {
            Start = T0.AddMinutes(-120 + i),
            Resolution = HistoryResolution.Minute,
            SampleCount = 60,
            Cpu = new AggregateValue(25, 40, 60),
            Memory = new AggregateValue(50, 55, 60),
            MemoryTotalBytes = TestData.TotalMemory,
        }).ToList();
        await repository.AppendAsync(new HistoryBatch(minutes, []), token);

        var service = new ReplayService(history, repository);
        var recent = await service.LoadAsync(TimeSpan.FromMinutes(5), null, token);
        var longer = await service.LoadAsync(TimeSpan.FromHours(1), T0, token);

        Assert.True(recent.IsDetailed);
        Assert.Equal(301, recent.Points.Count);
        Assert.Equal(T0.AddSeconds(599), recent.To);
        Assert.False(longer.IsDetailed);
        Assert.Equal(60, longer.Points.Count);
        Assert.Equal(T0.AddMinutes(-60).AddSeconds(30), longer.Points[0].Timestamp);
        Assert.Equal(25, longer.Points[0].CpuPercent);
        Assert.Equal(TestData.TotalMemory / 2, longer.Points[0].MemoryUsedBytes);
        Assert.Empty(longer.Points[0].TopApps);
    }

    [Fact]
    public void MinuteBuckets_AreReplayedAtTheirMiddle_WithoutInventingValues()
    {
        var snapshot = ReplayService.ToSnapshot(new SystemUsageAggregate
        {
            Start = T0,
            Resolution = HistoryResolution.Minute,
            Cpu = new AggregateValue(12, 30, 60),
        });

        Assert.Equal(T0.AddSeconds(30), snapshot.Timestamp);
        Assert.Equal(12, snapshot.CpuPercent);
        Assert.Null(snapshot.MemoryPercent);
        Assert.Null(snapshot.GpuPercent);
        Assert.Null(snapshot.ProcessCount);
        _ = CultureInfo.CurrentCulture;
    }
}
