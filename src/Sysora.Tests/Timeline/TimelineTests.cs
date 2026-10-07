using Microsoft.Extensions.Logging.Abstractions;
using Sysora.Core.Changes;
using Sysora.Core.Diagnosis;
using Sysora.Core.History;
using Sysora.Core.Timeline;
using Sysora.Infrastructure.Storage;

namespace Sysora.Tests.Timeline;

public sealed class TimelineTests
{
    private static readonly DateTimeOffset T0 = TestData.Start;

    [Fact]
    public void Events_ChangesAndSpikes_AreMergedNewestFirst_WithTheirCategories()
    {
        var input = new TimelineInput
        {
            From = T0,
            To = T0.AddHours(1),
            Events =
            [
                new SystemEvent(T0.AddMinutes(2), SystemEventKind.GameStarted, "Space Game started"),
                new SystemEvent(T0.AddMinutes(18), SystemEventKind.AppStarted, "chat.exe started") { AppKey = "path:C:\\CHAT.EXE" },
                new SystemEvent(T0.AddMinutes(15), SystemEventKind.AlertRaised, "Memory nearly full"),
                new SystemEvent(T0.AddMinutes(31), SystemEventKind.GameEnded, "Space Game closed"),
                new SystemEvent(T0.AddHours(3), SystemEventKind.AppStarted, "outside.exe started"),
            ],
            Changes = [Change(T0.AddMinutes(40))],
        };

        var items = TimelineBuilder.Build(input);

        Assert.Equal(["Tool installed", "Space Game closed", "chat.exe started", "Memory nearly full", "Space Game started"], items.Select(i => i.Title));
        Assert.Equal(TimelineCategory.Game, items[1].Category);
        Assert.Equal(TimelineCategory.Application, items[2].Category);
        Assert.Equal("path:C:\\CHAT.EXE", items[2].AppKey);
        Assert.Equal(TimelineCategory.Alert, items[3].Category);
        Assert.Equal(DiagnosisSeverity.Warning, items[3].Severity);
        var change = items[0];
        Assert.True(change.IsApproximate);
        Assert.Equal(T0, change.EarliestTime);
        Assert.Equal(DiagnosisAction.Changes, change.Action);
        Assert.StartsWith("1.0 Happened between", change.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Spikes_MarkTheStartAndTheReturnToNormal()
    {
        var minutes = Enumerable.Range(0, 20)
            .Select(i => Minute(T0.AddMinutes(i), i is >= 5 and < 9 ? 85 : 15))
            .ToArray();

        var spikes = TimelineBuilder.Spikes(minutes).OrderBy(s => s.Time).ToArray();

        Assert.Equal(2, spikes.Length);
        Assert.Equal("CPU usage high", spikes[0].Title);
        Assert.Equal(T0.AddMinutes(5), spikes[0].Time);
        Assert.Equal("CPU usage back to normal", spikes[1].Title);
        Assert.Equal(T0.AddMinutes(9), spikes[1].Time);
        Assert.StartsWith("After 4m of high activity", spikes[1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void GapInTheData_DoesNotClaimAReturnToNormal()
    {
        var minutes = new[] { Minute(T0, 15), Minute(T0.AddMinutes(1), 90), Minute(T0.AddMinutes(30), 10) };

        var spikes = TimelineBuilder.Spikes(minutes);

        var only = Assert.Single(spikes);
        Assert.Equal("CPU usage high", only.Title);
    }

    [Fact]
    public async Task Service_MergesStoredAndInMemoryEvents_WithoutDuplicates()
    {
        await using var repository = HistoryRepository.InMemory(NullLogger<HistoryRepository>.Instance);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var shared = new SystemEvent(T0.AddMinutes(1), SystemEventKind.AppStarted, "chat.exe started");
        await repository.AppendAsync(new HistoryBatch([], [shared, new SystemEvent(T0.AddMinutes(2), SystemEventKind.GameStarted, "Space Game started")]), TestContext.Current.CancellationToken);
        var history = new PerformanceHistory(TimeSpan.FromMinutes(15));
        history.AddEvent(shared);
        history.AddEvent(new SystemEvent(T0.AddMinutes(3), SystemEventKind.AlertRaised, "CPU busy"));

        var items = await new TimelineService(history, repository).LoadAsync(T0, T0.AddHours(1), TestContext.Current.CancellationToken);

        Assert.Equal(["CPU busy", "Space Game started", "chat.exe started"], items.Select(i => i.Title));
    }

    private static SystemUsageAggregate Minute(DateTimeOffset start, double cpu) => new()
    {
        Start = start,
        Resolution = HistoryResolution.Minute,
        SampleCount = 60,
        Cpu = new AggregateValue(cpu, cpu + 5, 60),
    };

    private static DetectedChange Change(DateTimeOffset before) => new()
    {
        Id = "x",
        Type = ChangeType.AppInstalled,
        DetectedAt = before,
        After = T0,
        Before = before,
        Subject = "Tool",
        Title = "Tool installed",
        NewValue = "1.0",
        Importance = ChangeImportance.Medium,
        Explanation = "test",
        Origin = BaselineComparer.UnknownOrigin,
    };
}
