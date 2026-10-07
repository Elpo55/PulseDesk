using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Sysora.Core.Analysis;
using Sysora.Core.History;
using Sysora.Core.Interfaces;
using Sysora.Core.Metrics;
using Sysora.Core.Models;
using Sysora.Core.Monitoring;
using Sysora.Core.Troubleshooting;
using Sysora.Infrastructure.Storage;

namespace Sysora.Tests.Troubleshooting;

public sealed class TroubleshootingTests
{
    private static readonly DateTimeOffset T0 = TestData.Start;

    [Fact]
    public void QuietInvestigation_SaysNothingAbnormalWasMeasured_AndWhatIsUnknown()
    {
        var recorder = new TroubleshootingRecorder(T0);
        foreach (var snapshot in TestData.Series(T0, 300, _ => 12, apps: _ => [TestData.App("editor.exe", 2, 300)]))
        {
            recorder.Add(snapshot);
        }

        var report = recorder.Build(Guid.NewGuid(), T0.AddMinutes(5), TimeSpan.FromMinutes(5), TroubleshootingEndReason.Completed, null, null);

        Assert.Equal("Investigation complete: nothing abnormal measured", report.Headline);
        Assert.Empty(report.Anomalies);
        Assert.Empty(report.Likely);
        Assert.Equal(300, report.SampleCount);
        Assert.Contains(report.Unknowns, u => u.Label == "Temperatures" && u.Basis == FindingBasis.Unknown);
        Assert.Contains(report.Unknowns, u => u.Label == "Network per application");
        Assert.Contains(report.Unknowns, u => u.Label == "GPU usage");
        Assert.StartsWith("Nothing abnormal was measured.", report.Recommendations[0], StringComparison.Ordinal);
        Assert.Equal("CPU usage", report.Metrics[0].Name);
        Assert.Equal(12, report.Metrics[0].AverageValue, 6);
    }

    [Fact]
    public void HighCpuMoments_AreCorrelatedWithTheBusiestApplication()
    {
        var recorder = new TroubleshootingRecorder(T0);
        IReadOnlyList<AppSample> Apps(int i) => i < 120
            ? [TestData.App("editor.exe", 2, 300), TestData.App("encoder.exe", 3, 500)]
            : [TestData.App("editor.exe", 2, 300), TestData.App("encoder.exe", 78, 800)];
        foreach (var snapshot in TestData.Series(T0, 300, i => i < 120 ? 15 : 92, apps: Apps))
        {
            recorder.Add(snapshot);
        }

        recorder.Add(new SystemEvent(T0.AddMinutes(2), SystemEventKind.AlertRaised, "Sustained high CPU usage"));
        var report = recorder.Build(Guid.NewGuid(), T0.AddMinutes(5), TimeSpan.FromMinutes(5), TroubleshootingEndReason.StoppedByUser, null, null);

        Assert.Equal("Investigation complete: 2 anomalies found", report.Headline);
        Assert.Contains(report.Anomalies, a => a.Text.StartsWith("CPU at or above 80% in 180 of 300 measurements", StringComparison.Ordinal));
        Assert.Contains(report.Anomalies, a => a.Label == "Alert");
        var correlation = Assert.Single(report.Correlations, c => c.Label == "High CPU");
        Assert.Equal(FindingBasis.Inferred, correlation.Basis);
        Assert.Equal(ConfidenceLevel.High, correlation.Confidence);
        Assert.StartsWith("encoder.exe was the busiest application in 180 of the 180 high-CPU moments", correlation.Text, StringComparison.Ordinal);
        Assert.Single(report.Likely);
        Assert.StartsWith("If the slowdown matched the moments encoder.exe was busy", report.Recommendations[0], StringComparison.Ordinal);
        Assert.Equal("encoder.exe", report.Applications[0].Name);
        Assert.Contains("Evidence suggests", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryGrowth_IsAttributedToTheApplicationThatGrew()
    {
        var recorder = new TroubleshootingRecorder(T0);
        foreach (var snapshot in TestData.Series(T0, 600, _ => 10, i => 30 + (i * 0.02), apps: i => [TestData.App("leaky.exe", 1, 400 + (i * 3))]))
        {
            recorder.Add(snapshot);
        }

        var report = recorder.Build(Guid.NewGuid(), T0.AddMinutes(10), TimeSpan.FromMinutes(10), TroubleshootingEndReason.Completed, null, null);

        var growth = Assert.Single(report.Correlations, c => c.Label == "Memory growth");
        Assert.StartsWith("Memory in use grew 1.9 GB; leaky.exe grew 1.8 GB of it", growth.Text, StringComparison.Ordinal);
        Assert.Equal(ConfidenceLevel.High, growth.Confidence);
    }

    [Fact]
    public void LongInvestigation_KeepsABoundedTimelineAndApplicationList()
    {
        var recorder = new TroubleshootingRecorder(T0);
        for (var i = 0; i < 3600 * 2; i++)
        {
            recorder.Add(TestData.Metric(T0.AddSeconds(i), 20, apps: [TestData.App($"app{i % 400}.exe", 1, 10)]));
        }

        var report = recorder.Build(Guid.NewGuid(), T0.AddHours(2), TimeSpan.FromHours(1), TroubleshootingEndReason.Completed, null, null);

        Assert.InRange(report.Timeline.Count, 1, TroubleshootingRecorder.MaxPoints);
        Assert.Equal(T0, report.Timeline[0].Start);
        Assert.True(report.Applications.Count <= 15);
    }

    [Fact]
    public async Task Investigation_SwitchesToDetailedCollection_EndsByItself_AndKeepsItsReport()
    {
        var time = new FakeTimeProvider(T0);
        var monitor = new InvestigationMonitor();
        var history = new PerformanceHistory(TimeSpan.FromMinutes(30), time);
        await using var repository = HistoryRepository.InMemory(NullLogger<HistoryRepository>.Instance);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var service = new TroubleshootingService(monitor, history, null, repository, NullSettingsStore.Create(time: time), NullLogger<TroubleshootingService>.Instance, time);
        var completed = new TaskCompletionSource<TroubleshootingReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Completed += (_, report) => completed.TrySetResult(report);

        Assert.True(service.Start(TimeSpan.FromMinutes(2)));
        Assert.False(service.Start(TimeSpan.FromMinutes(2)));
        Assert.True(monitor.IsInvestigating);
        Assert.Equal(MetricKind.All, monitor.Refreshed);
        foreach (var point in TestData.Series(T0, 120, _ => 30))
        {
            time.SetUtcNow(point.Timestamp);
            history.Record(TestData.System(point.Timestamp, 30), MetricKind.Cpu | MetricKind.Memory);
        }

        Assert.Equal(120, service.Status.Samples);
        time.Advance(TimeSpan.FromMinutes(1));
        var report = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(monitor.IsInvestigating);
        Assert.False(service.Status.IsRunning);
        Assert.Equal(TroubleshootingEndReason.Completed, report.EndReason);
        Assert.Equal(120, report.SampleCount);
        var stored = Assert.Single(await repository.GetTroubleshootingReportsAsync(T0.AddDays(-1), TestContext.Current.CancellationToken));
        Assert.Equal(report.Id, stored.Id);
        Assert.Equal(report.Headline, stored.Headline);
        Assert.Equal(report.Unknowns.Count, stored.Unknowns.Count);
        var events = history.GetEvents(T0, T0.AddHours(1));
        Assert.Contains(events, e => e.Kind == SystemEventKind.InvestigationStarted);
        Assert.Contains(events, e => e.Kind == SystemEventKind.InvestigationEnded);
    }

    [Fact]
    public async Task ClosingSysora_EndsTheInvestigation_AndReturnsToNormalCollection()
    {
        var time = new FakeTimeProvider(T0);
        var monitor = new InvestigationMonitor();
        var history = new PerformanceHistory(TimeSpan.FromMinutes(30), time);
        await using var repository = HistoryRepository.InMemory(NullLogger<HistoryRepository>.Instance);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var service = new TroubleshootingService(monitor, history, null, repository, NullSettingsStore.Create(time: time), NullLogger<TroubleshootingService>.Instance, time);
        service.Start(TimeSpan.FromMinutes(10));

        await service.DisposeAsync();

        Assert.False(monitor.IsInvestigating);
        Assert.Equal(TroubleshootingEndReason.SysoraClosed, service.LastReport!.EndReason);
        Assert.Null(await service.StopAsync());
    }

    private sealed class InvestigationMonitor : IMetricsMonitor
    {
        public event EventHandler<SystemMetricsUpdatedEventArgs>? MetricsUpdated
        {
            add { }
            remove { }
        }

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public SystemSnapshot Current => SystemSnapshot.Empty;

        public MetricHistory History { get; } = new(10);

        public bool IsRunning => true;

        public bool IsPaused => false;

        public SelfUsage SelfUsage => new(null, 0, 1);

        public bool IsInvestigating { get; private set; }

        public MonitoringScheduleInfo ScheduleInfo => MonitoringScheduleInfo.Unknown;

        public MetricKind Refreshed { get; private set; }

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

        public void SetInvestigationMode(bool enabled) => IsInvestigating = enabled;
    }
}
