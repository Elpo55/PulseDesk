using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Sysora.Core.Alerts;
using Sysora.Core.Analysis;
using Sysora.Core.History;
using Sysora.Core.Models;
using Sysora.Core.Settings;
using Sysora.Infrastructure.Storage;

namespace Sysora.Tests.Alerts;

public sealed class AlertEngineTests
{
    private static readonly DateTimeOffset T0 = TestData.Start;
    private static readonly SmartAlertSettings Defaults = new();

    [Fact]
    public void TemporarySpike_RaisesNoAlert()
    {
        // 90%+ CPU for 30 seconds, then back to normal.
        var engine = new AlertEngine();
        var raised = Run(engine, TestData.Series(T0, 600, i => i is >= 200 and < 230 ? 97 : 20));

        Assert.Empty(raised);
        Assert.Empty(engine.Alerts);
    }

    [Fact]
    public void ProlongedProblem_RaisesOneAlert_UpdatedWhileItLasts()
    {
        var engine = new AlertEngine();
        var raised = Run(engine, TestData.Series(T0, 600, i => i < 120 ? 20 : 93));

        var alert = Assert.Single(engine.Alerts);
        Assert.Single(raised);
        Assert.Equal("cpu.sustained", alert.RuleId);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal(AlertStatus.New, alert.Status);
        Assert.Equal("Sustained high CPU usage", alert.Title);
        // Raised once five minutes were reached (first evaluation at or after 120 + 300 s), then updated.
        Assert.Equal(T0.AddSeconds(420), alert.RaisedAt);
        Assert.True(alert.Duration >= TimeSpan.FromMinutes(7.9));
        Assert.Contains("Usual level not known yet", alert.Context, StringComparison.Ordinal);
    }

    [Fact]
    public void UnusualForThisPc_IsMoreImportant()
    {
        var engine = new AlertEngine();
        Run(engine, TestData.Series(T0, 600, i => i < 120 ? 20 : 93), Baseline(cpuMedian: 30, cpuP95: 45));

        var alert = Assert.Single(engine.Alerts);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal("CPU usage unusually high", alert.Title);
        Assert.Contains("Usual level 20–40%", alert.Context, StringComparison.Ordinal);
    }

    [Fact]
    public void HighButUsualForThisPc_IsOnlyInformation()
    {
        var engine = new AlertEngine();
        Run(engine, TestData.Series(T0, 600, _ => 93), Baseline(cpuMedian: 90, cpuP95: 97));

        Assert.Equal(AlertSeverity.Info, Assert.Single(engine.Alerts).Severity);
    }

    [Fact]
    public void ActivityAboveTheBaseline_ForMinutes_IsDetectedAsUnusual()
    {
        // Memory usually 40–50%, now 75% for 10 minutes (below the absolute 90% threshold).
        var engine = new AlertEngine();
        Run(engine, TestData.Series(T0, 700, _ => 20, i => i < 60 ? 45 : 75), Baseline(cpuMedian: 25, cpuP95: 40));

        var alert = Assert.Single(engine.Alerts);
        Assert.Equal("unusual.memory", alert.Key);
        Assert.Equal("Memory usage unusually high", alert.Title);
        Assert.StartsWith("Memory usage has remained above your recent baseline for", alert.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void ConditionEnding_ResolvesAfterADelay_AndComingBackReopensTheSameAlert()
    {
        var engine = new AlertEngine();
        // High for 6 min, a 30 s dip (the ongoing alert survives it), high again, normal 3 min (resolved),
        // then high for 6 min within the cooldown: the same alert reopens.
        var series = TestData.Series(T0, 1500, i => i switch
        {
            < 360 => 95,
            < 390 => 20,
            < 600 => 95,
            < 780 => 20,
            _ => 95,
        });
        var raised = Run(engine, series);

        var alert = Assert.Single(engine.Alerts);
        Assert.Equal(2, raised.Count); // first raise and one reopening, never a second alert
        Assert.Equal(2, alert.Occurrences);
        Assert.Equal(AlertStatus.New, alert.Status);
        Assert.Null(alert.ResolvedAt);
    }

    [Fact]
    public void ConditionGone_IsResolved()
    {
        var engine = new AlertEngine();
        Run(engine, TestData.Series(T0, 800, i => i < 400 ? 95 : 10));

        var alert = Assert.Single(engine.Alerts);
        Assert.Equal(AlertStatus.Resolved, alert.Status);
        Assert.Equal(T0.AddSeconds(400), alert.ResolvedAt);
        Assert.Equal(T0, alert.Since);
    }

    [Fact]
    public void HourlyLimit_SuppressesExtraAlerts()
    {
        var engine = new AlertEngine();
        var apps = Enumerable.Range(0, 8).Select(i => TestData.App($"app{i}.exe", 30, 100)).ToArray();
        var settings = Defaults with { MaxNewAlertsPerHour = 3, CpuPercent = 99 };
        Run(engine, TestData.Series(T0, 400, _ => 99, apps: _ => [.. apps]), settings: settings);

        Assert.Equal(3, engine.Alerts.Count);
        Assert.True(engine.SuppressedCount > 0);
    }

    [Fact]
    public void SteadyMemoryRise_IsReported()
    {
        var engine = new AlertEngine();
        Run(engine, TestData.Series(T0, 1300, _ => 20, i => 40 + (i / 60.0)));

        var alert = Assert.Single(engine.Alerts);
        Assert.Equal("memory.growth", alert.Key);
        Assert.Equal(AlertSeverity.Info, alert.Severity);
    }

    [Fact]
    public void MarkSeen_ChangesStatus_AndEscalationMakesItNewAgain()
    {
        var engine = new AlertEngine();
        var series = TestData.Series(T0, 700, _ => 20, i => i < 500 ? 91 : 99);
        Run(engine, series.Take(400).ToList());
        var alert = Assert.Single(engine.Alerts);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);

        Assert.Equal(AlertStatus.Seen, engine.MarkSeen(alert.Id)!.Status);
        Assert.Null(engine.MarkSeen(alert.Id));

        // Memory staying above 95% for the last three minutes escalates the same alert to critical.
        Run(engine, series, start: 400);
        var escalated = Assert.Single(engine.Alerts);
        Assert.Equal(alert.Id, escalated.Id);
        Assert.Equal(AlertSeverity.Critical, escalated.Severity);
        Assert.Equal(AlertStatus.New, escalated.Status);
        Assert.Equal(T0, escalated.Since);
    }

    [Fact]
    public void Disabled_RaisesNothing_AndResolvesActiveAlerts()
    {
        var engine = new AlertEngine();
        var series = TestData.Series(T0, 400, _ => 95);
        Run(engine, series);
        Assert.True(Assert.Single(engine.Alerts).IsActive);

        var evaluation = engine.Evaluate(Context(series, series[^1].Timestamp, settings: Defaults with { Enabled = false }));

        Assert.Single(evaluation.Resolved);
        Assert.False(engine.Alerts[0].IsActive);
    }

    [Fact]
    public async Task Service_PersistsAlerts_AndPreviousSessionAlertsAreLoadedAsResolved()
    {
        var time = new FakeTimeProvider(T0);
        var repository = HistoryRepository.InMemory(NullLogger<HistoryRepository>.Instance);
        await repository.InitializeAsync(TestContext.Current.CancellationToken);
        var history = new PerformanceHistory(TimeSpan.FromMinutes(30), time);
        var settings = NullSettingsStore.Create(time: time);
        var monitor = new StubMonitor();
        var baseline = new BaselineService(repository, NullLogger<BaselineService>.Instance, time);

        // Recorded before the service starts, so only the explicit evaluation below runs (deterministic).
        for (var i = 0; i < 400; i++)
        {
            history.Record(TestData.System(T0.AddSeconds(i), cpu: 96), MetricKind.Cpu);
        }

        var service = new AlertService(new AlertEngine(), history, monitor, baseline, settings, repository, NullLogger<AlertService>.Instance, time);
        await service.StartAsync(TestContext.Current.CancellationToken);
        time.SetUtcNow(T0.AddSeconds(399));
        Assert.Single(service.EvaluateNow().Raised);
        await service.StopAsync();

        var next = new AlertService(new AlertEngine(), history, monitor, baseline, settings, repository, NullLogger<AlertService>.Instance, time);
        await next.StartAsync(TestContext.Current.CancellationToken);
        var loaded = Assert.Single(next.Alerts);
        Assert.Equal(AlertStatus.Resolved, loaded.Status);
        Assert.Equal(loaded.UpdatedAt, loaded.ResolvedAt);
        Assert.Contains(history.GetEvents(DateTimeOffset.MinValue, DateTimeOffset.MaxValue), e => e.Kind == SystemEventKind.AlertRaised);
        await repository.DisposeAsync();
    }

    /// <summary>Evaluates the engine every 5 seconds over the series, as the service does. Returns raised alerts.</summary>
    private static List<Alert> Run(AlertEngine engine, IReadOnlyList<MetricSnapshot> series, UsageBaseline? baseline = null, SmartAlertSettings? settings = null, int start = 0)
    {
        var raised = new List<Alert>();
        for (var i = start; i < series.Count; i += 5)
        {
            var now = series[i].Timestamp;
            var recent = series.Where(s => s.Timestamp <= now && s.Timestamp > now - TimeSpan.FromMinutes(30)).ToList();
            raised.AddRange(engine.Evaluate(Context(recent, now, baseline, settings)).Raised);
        }

        return raised;
    }

    [Fact]
    public void LowDiskSpaceStillPresentAfterRestart_ContinuesTheSameAlert()
    {
        var first = new AlertEngine();
        var raised = Assert.Single(first.Evaluate(DiskContext(T0, freeGb: 20)).Raised);
        var stored = first.Alerts.ToArray();

        // Sysora restarts the next day: the disk is still nearly full.
        var next = new AlertEngine();
        next.Load(stored);
        var evaluation = next.Evaluate(DiskContext(T0.AddDays(1), freeGb: 19));

        Assert.Empty(evaluation.Raised);
        var alert = Assert.Single(next.Alerts);
        Assert.Equal(raised.Id, alert.Id);
        Assert.True(alert.IsActive);
    }

    [Fact]
    public void LowDiskSpaceGoneAfterRestart_IsResolvedAtItsLastObservation()
    {
        var first = new AlertEngine();
        first.Evaluate(DiskContext(T0, freeGb: 20));
        var next = new AlertEngine();
        next.Load(first.Alerts);

        var evaluation = next.Evaluate(DiskContext(T0.AddDays(1), freeGb: 400));

        var resolved = Assert.Single(evaluation.Resolved);
        Assert.Equal(T0, resolved.ResolvedAt);
    }

    [Fact]
    public void ActivityAlertOfAPreviousSession_IsResolvedWhenLoaded()
    {
        var engine = new AlertEngine();
        var series = TestData.Series(T0, 400, _ => 96);
        Run(engine, series);
        var active = Assert.Single(engine.Alerts);
        Assert.True(active.IsActive);

        var next = new AlertEngine();
        next.Load(engine.Alerts);

        var loaded = Assert.Single(next.Alerts);
        Assert.Equal(AlertStatus.Resolved, loaded.Status);
        Assert.Equal(active.UpdatedAt, loaded.ResolvedAt);
    }

    /// <summary>A context whose system drive (500 GB) has the given free space.</summary>
    private static AlertContext DiskContext(DateTimeOffset now, double freeGb)
    {
        var drive = StorageMetrics.FromTotalAndFree(@"C:\", 500UL << 30, (ulong)(freeGb * (1UL << 30))) with { IsSystemDrive = true };
        var recent = TestData.Series(now.AddMinutes(-1), 60, _ => 10);
        return new AlertContext(now, TestData.System(now, 10) with { Storage = [drive] }, recent, UsageBaseline.Empty, Defaults);
    }

    private static AlertContext Context(IReadOnlyList<MetricSnapshot> recent, DateTimeOffset now, UsageBaseline? baseline = null, SmartAlertSettings? settings = null) =>
        new(now, TestData.System(now, recent[^1].CpuPercent ?? 0), recent, baseline ?? UsageBaseline.Empty, settings ?? Defaults);

    private static UsageBaseline Baseline(double cpuMedian, double cpuP95) =>
        new(BaselineStatus.Ready, 3000, T0.AddDays(-7), T0, new Dictionary<HistoryMetric, MetricBaseline>
        {
            [HistoryMetric.Cpu] = new(HistoryMetric.Cpu, cpuMedian, cpuMedian - 10, cpuMedian + 10, cpuP95, 3000),
            [HistoryMetric.Memory] = new(HistoryMetric.Memory, 45, 40, 50, 55, 3000),
            [HistoryMetric.Disk] = new(HistoryMetric.Disk, 5, 2, 8, 20, 3000),
        });

    private sealed class StubMonitor : Sysora.Core.Interfaces.IMetricsMonitor
    {
        public event EventHandler<SystemMetricsUpdatedEventArgs>? MetricsUpdated { add { } remove { } }

        public event EventHandler? StateChanged { add { } remove { } }

        public SystemSnapshot Current => TestData.System(T0, 96);

        public Sysora.Core.Metrics.MetricHistory History { get; } = new(10);

        public bool IsRunning => true;

        public bool IsPaused => false;

        public Sysora.Core.Monitoring.SelfUsage SelfUsage => new(null, 0, 1);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void SetActivity(Sysora.Core.Interfaces.MonitoringActivity activity)
        {
        }

        public void RequestRefresh(MetricKind kinds)
        {
        }
    }
}
