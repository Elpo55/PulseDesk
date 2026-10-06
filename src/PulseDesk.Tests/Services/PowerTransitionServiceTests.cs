using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PulseDesk.Core.Analysis;
using PulseDesk.Core.History;
using PulseDesk.Core.Interfaces;
using PulseDesk.Core.Metrics;
using PulseDesk.Core.Models;
using PulseDesk.Core.Monitoring;
using PulseDesk.Infrastructure.Storage;

namespace PulseDesk.Tests.Services;

public sealed class PowerTransitionServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = TestData.Start;
    private readonly FakeTimeProvider _time = new(T0);
    private readonly HistoryRepository _repository = HistoryRepository.InMemory(NullLogger<HistoryRepository>.Instance);
    private readonly PerformanceHistory _history = new(TimeSpan.FromMinutes(15));
    private readonly FakePowerEvents _power = new();
    private readonly RefreshCountingMonitor _monitor = new();
    private HistoryRecorder _recorder = null!;
    private PowerTransitionService _service = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _recorder = new HistoryRecorder(_history, _repository, NullSettingsStore.Create(), NullLogger<HistoryRecorder>.Instance, _time);
        await _recorder.StartAsync(Token);
        _service = new PowerTransitionService(_power, _monitor, _history, _recorder, new ProcessHistory(), NullLogger<PowerTransitionService>.Instance, _time);
        _service.Start();
    }

    public async ValueTask DisposeAsync()
    {
        _service.Dispose();
        await _recorder.StopAsync();
        await _repository.DisposeAsync();
    }

    [Fact]
    public async Task Suspending_WritesTheMinuteInProgressBeforeSleep()
    {
        for (var i = 0; i < 30; i++)
        {
            _history.Record(TestData.System(T0.AddSeconds(i), cpu: 20), MetricKind.Cpu);
        }

        _time.SetUtcNow(T0.AddSeconds(30));
        _power.Suspend();

        // The half minute in progress is already on disk: nothing is lost if the PC never resumes.
        var minute = Assert.Single(await _repository.GetSystemUsageAsync(T0, T0.AddHours(1), HistoryResolution.Minute, Token));
        Assert.Equal(30, minute.SampleCount);
        var events = await _repository.GetEventsAsync(T0, T0.AddHours(1), 10, Token);
        Assert.Contains(events, e => e.Kind == SystemEventKind.SystemSuspending);
    }

    [Fact]
    public void Resuming_RecordsHowLongThePcSleptAndRefreshesEverything()
    {
        _power.Suspend();
        _time.Advance(TimeSpan.FromMinutes(42));
        _power.Resume();

        var resumed = Assert.Single(_history.GetEvents(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), e => e.Kind == SystemEventKind.SystemResumed);
        Assert.Equal("PC resumed from sleep (asleep 42m)", resumed.Title);
        Assert.Equal(MetricKind.All, _monitor.Refreshed);
    }

    [Fact]
    public void AfterDispose_NotificationsAreIgnored()
    {
        _service.Dispose();

        _power.Resume();

        Assert.Empty(_history.GetEvents(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        Assert.Equal(MetricKind.None, _monitor.Refreshed);
    }

    private sealed class FakePowerEvents : ISystemPowerEvents
    {
        public event EventHandler? Suspending;

        public event EventHandler? Resumed;

        public void Suspend() => Suspending?.Invoke(this, EventArgs.Empty);

        public void Resume() => Resumed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class RefreshCountingMonitor : IMetricsMonitor
    {
        public event EventHandler<SystemMetricsUpdatedEventArgs>? MetricsUpdated { add { } remove { } }

        public event EventHandler? StateChanged { add { } remove { } }

        public MetricKind Refreshed { get; private set; }

        public SystemSnapshot Current => SystemSnapshot.Empty;

        public MetricHistory History { get; } = new(10);

        public bool IsRunning => true;

        public bool IsPaused => false;

        public SelfUsage SelfUsage => new(null, 0, 1);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void SetActivity(MonitoringActivity activity)
        {
        }

        public void RequestRefresh(MetricKind kinds) => Refreshed |= kinds;
    }
}
