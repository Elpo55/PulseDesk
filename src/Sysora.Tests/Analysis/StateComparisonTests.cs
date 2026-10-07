using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Sysora.Core.Analysis;
using Sysora.Core.Changes;
using Sysora.Core.History;
using Sysora.Infrastructure.Storage;

namespace Sysora.Tests.Analysis;

public sealed class StateComparisonTests
{
    private const double GB = 1024.0 * 1024 * 1024;
    private static readonly DateTimeOffset T0 = TestData.Start;

    [Fact]
    public void Differences_AreShownAsValueChangeAndRelativeChange()
    {
        var before = State("1 hour ago", (StateMetric.Cpu, 7), (StateMetric.MemoryUsed, 5.2 * GB), (StateMetric.MemoryPercent, 33), (StateMetric.SystemDriveFree, 180 * GB));
        var after = State("Now", (StateMetric.Cpu, 11), (StateMetric.MemoryUsed, 7.3 * GB), (StateMetric.MemoryPercent, 46), (StateMetric.SystemDriveFree, 162 * GB));

        var result = StateComparer.Compare(before, after);

        var memory = Row(result, StateMetric.MemoryUsed);
        Assert.Equal("5.2 GB", memory.BeforeText);
        Assert.Equal("7.3 GB", memory.AfterText);
        Assert.Equal("+2.1 GB", memory.ChangeText);
        Assert.Equal("+40%", memory.RelativeText);
        Assert.Equal(ChangeImportance.Medium, memory.Importance);
        var cpu = Row(result, StateMetric.Cpu);
        Assert.Equal("+4 percentage points", cpu.ChangeText);
        Assert.Null(cpu.RelativeText);
        Assert.Equal(ChangeImportance.Low, cpu.Importance);
        var free = Row(result, StateMetric.SystemDriveFree);
        Assert.Equal("−18 GB", free.ChangeText);
        Assert.Equal(ChangeDirection.Down, free.Direction);
        Assert.Equal(ChangeImportance.Medium, free.Importance);
        Assert.Equal("2 notable differences: Memory in use +2.1 GB, Free space (Windows volume) −18 GB.", result.Summary);
    }

    [Fact]
    public void MetricMeasuredOnOneSideOnly_IsNotCompared()
    {
        var result = StateComparer.Compare(State("Before", (StateMetric.Cpu, 10), (StateMetric.Gpu, 30)), State("After", (StateMetric.Cpu, 10)));

        var gpu = Row(result, StateMetric.Gpu);
        Assert.False(gpu.IsComparable);
        Assert.Equal("Not available", gpu.AfterText);
        Assert.Equal("—", gpu.ChangeText);
        Assert.Contains(result.Notes, n => n == "Not compared (measured in only one period): GPU usage.");
        Assert.Equal(ChangeDirection.Same, Row(result, StateMetric.Cpu).Direction);
        Assert.Equal("No notable difference between the two periods.", result.Summary);
    }

    [Fact]
    public void EmptyPeriod_SaysThereIsNotEnoughData()
    {
        var result = StateComparer.Compare(State("Yesterday"), State("Today", (StateMetric.Cpu, 10)));

        Assert.Equal("Not enough data to compare: no measurement for \"Yesterday\".", result.Summary);
    }

    [Fact]
    public void Summarize_AveragesOnlyMeasuredValues()
    {
        var points = TestData.Series(T0, 120, i => i < 60 ? 10 : 30, disk: _ => null);

        var state = StateComparer.Summarize("Now", T0, T0.AddMinutes(2), points, "test");

        Assert.Equal(20, state.Get(StateMetric.Cpu)!.Value, 6);
        Assert.Null(state.Get(StateMetric.Disk));
        Assert.Null(state.Get(StateMetric.Gpu));
        Assert.Equal(120, state.SampleCount);
    }

    [Fact]
    public void GameSession_UsesTheAveragesItRecorded()
    {
        var session = Gaming.GameRecapBuilderTests.Session(start: T0);

        var state = StateComparer.FromSession(session);

        Assert.Equal("During Space Game", state.Label);
        Assert.Equal(session.Cpu!.Value.Average, state.Get(StateMetric.Cpu));
    }

    [Fact]
    public async Task NowVsAnHourAgo_ReadsTheStoredMinutesAndTheMemoryHistory()
    {
        var time = new FakeTimeProvider(T0.AddHours(2));
        await using var repository = HistoryRepository.InMemory(NullLogger<HistoryRepository>.Instance);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var hourAgo = T0.AddHours(1);
        await repository.AppendAsync(new HistoryBatch(
            [.. Enumerable.Range(-12, 13).Select(i => new SystemUsageAggregate
            {
                Start = hourAgo.AddMinutes(i),
                Resolution = HistoryResolution.Minute,
                SampleCount = 60,
                Cpu = new AggregateValue(12, 20, 60),
                SystemDriveFree = new AggregateValue(180 * GB, 180 * GB, 60),
            })], []), TestContext.Current.CancellationToken);
        var history = new PerformanceHistory(TimeSpan.FromMinutes(15), time);
        foreach (var point in TestData.Series(T0.AddHours(2).AddMinutes(-12), 720, _ => 40))
        {
            history.Record(TestData.System(point.Timestamp, 40) with { Storage = [TestData.Volume("C:", 500, 162, system: true)] }, Sysora.Core.Models.MetricKind.Cpu | Sysora.Core.Models.MetricKind.Memory | Sysora.Core.Models.MetricKind.Storage);
        }

        var service = new StateComparisonService(history, repository, NullSettingsStore.Create(time: time), time);
        var result = await service.CompareAsync(new ComparisonRequest(ComparisonPreset.NowVsHourAgo), TestContext.Current.CancellationToken);

        Assert.Equal("Per-minute averages of the local history", result.Before.Source);
        Assert.Equal("Per-second measurements kept in memory", result.After.Source);
        Assert.Equal("+28 percentage points", Row(result, StateMetric.Cpu).ChangeText);
        Assert.Equal("−18 GB", Row(result, StateMetric.SystemDriveFree).ChangeText);
    }

    private static StateDifference Row(StateComparisonResult result, StateMetric metric) => result.Rows.Single(r => r.Metric == metric);

    private static PeriodState State(string label, params (StateMetric Metric, double Value)[] values) =>
        new(label, T0, T0.AddMinutes(10), values.Length == 0 ? 0 : 600, "test", values.ToDictionary(v => v.Metric, v => v.Value));
}
