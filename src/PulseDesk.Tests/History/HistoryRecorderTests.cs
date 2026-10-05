using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PulseDesk.Core.History;
using PulseDesk.Core.Models;
using PulseDesk.Infrastructure.Storage;

namespace PulseDesk.Tests.History;

public sealed class HistoryRecorderTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = TestData.Start;
    private readonly FakeTimeProvider _time = new(T0);
    private readonly HistoryRepository _repository = HistoryRepository.InMemory(NullLogger<HistoryRepository>.Instance);
    private readonly PerformanceHistory _history = new(TimeSpan.FromMinutes(15));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _repository.DisposeAsync();

    [Fact]
    public async Task Snapshots_AreWrittenAsMinuteAggregates()
    {
        var recorder = new HistoryRecorder(_history, _repository, NullSettingsStore.Create(), NullLogger<HistoryRecorder>.Instance, _time);
        await recorder.StartAsync(Token);

        for (var i = 0; i < 90; i++)
        {
            _history.Record(TestData.System(T0.AddSeconds(i), cpu: i < 60 ? 10 : 30), MetricKind.Cpu | MetricKind.Memory);
        }

        await recorder.FlushAsync(Token);
        var minutes = await _repository.GetSystemUsageAsync(T0, T0.AddHours(1), HistoryResolution.Minute, Token);
        await recorder.StopAsync();

        Assert.Equal(2, minutes.Count);
        Assert.Equal(10, minutes[0].Cpu!.Value.Average, 6);
        Assert.Equal(30, minutes[1].Cpu!.Value.Average, 6);
        Assert.Equal(30, minutes[1].SampleCount);
        var events = await _repository.GetEventsAsync(T0, T0.AddHours(1), 10, Token);
        Assert.Equal(SystemEventKind.MonitoringStarted, Assert.Single(events).Kind);
    }

    [Fact]
    public async Task RecordingTurnedOff_WritesNothing()
    {
        var settings = NullSettingsStore.Create(s => s with { History = s.History with { RecordHistory = false } });
        var recorder = new HistoryRecorder(_history, _repository, settings, NullLogger<HistoryRecorder>.Instance, _time);
        await recorder.StartAsync(Token);

        for (var i = 0; i < 90; i++)
        {
            _history.Record(TestData.System(T0.AddSeconds(i), cpu: 10), MetricKind.Cpu);
        }

        await recorder.FlushAsync(Token);
        await recorder.StopAsync();

        Assert.Empty(await _repository.GetSystemUsageAsync(T0, T0.AddHours(1), HistoryResolution.Minute, Token));
        Assert.Empty(await _repository.GetEventsAsync(T0, T0.AddHours(1), 10, Token));
        Assert.NotNull(_history.Latest);
    }
}
