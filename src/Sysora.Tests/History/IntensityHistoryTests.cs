using Sysora.Core.Analysis;
using Sysora.Core.History;
using Sysora.Core.Interfaces;
using Sysora.Core.Metrics;
using Sysora.Core.Models;
using Sysora.Core.Monitoring;
using Sysora.Core.Settings;

namespace Sysora.Tests.History;

public sealed class IntensityHistoryTests
{
    private static readonly DateTimeOffset T0 = TestData.Start;

    [Theory]
    [InlineData(MonitoringIntensity.Minimal, false, 3)]
    [InlineData(MonitoringIntensity.Balanced, false, 5)]
    [InlineData(MonitoringIntensity.Detailed, false, 8)]
    [InlineData(MonitoringIntensity.Minimal, true, 8)]
    public void ApplicationsKeptPerSample_FollowTheIntensity_AndAnInvestigation(MonitoringIntensity intensity, bool investigating, int perCriterion)
    {
        var monitor = new QuietMonitor { IsInvestigating = investigating };
        var settings = NullSettingsStore.Create(s => s with { Monitoring = s.Monitoring with { Intensity = intensity } });
        using var history = new PerformanceHistory(monitor, settings);

        // 20 applications that rank differently by CPU, memory and I/O: each criterion keeps its own top N.
        var processes = Enumerable.Range(0, 20)
            .Select(i => TestData.Process(100 + i, $"app{i}.exe", cpu: i, memoryMb: 1000 - (i * 10), path: $@"C:\Apps\app{i}.exe", io: i % 7))
            .ToArray();
        history.Record(TestData.System(T0, 30, processes: processes), MetricKind.Cpu | MetricKind.Processes);

        var kept = history.Latest!.TopApps;
        var expected = AppGrouper.SelectTop(new AppGrouper().Group(processes), perCriterion).Count;
        Assert.Equal(expected, kept.Count);
        Assert.InRange(kept.Count, perCriterion, perCriterion * 3);
    }

    [Fact]
    public void Grouping_KeepsTheOldestStartTime_AndWhetherTheApplicationRunsInAUserSession()
    {
        var processes = new[]
        {
            TestData.Process(1, "app.exe", 1, 10, path: @"C:\Apps\app.exe") with { StartTime = T0.AddMinutes(5), SessionId = 0 },
            TestData.Process(2, "app.exe", 1, 10, path: @"C:\Apps\app.exe", created: 2) with { StartTime = T0, SessionId = 1 },
            TestData.Process(3, "service.exe", 1, 10, path: @"C:\Apps\service.exe") with { SessionId = 0 },
        };

        var groups = new AppGrouper().Group(processes);

        var app = Assert.Single(groups, g => g.Identity.Name == "app.exe");
        Assert.Equal(T0, app.StartedAt);
        Assert.True(app.InUserSession);
        var service = Assert.Single(groups, g => g.Identity.Name == "service.exe");
        Assert.Null(service.StartedAt);
        Assert.False(service.InUserSession);
    }

    private sealed class QuietMonitor : IMetricsMonitor
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

        public bool IsInvestigating { get; init; }

        public MonitoringScheduleInfo ScheduleInfo => MonitoringScheduleInfo.Unknown;

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

        public void RequestRefresh(MetricKind kinds)
        {
        }

        public void SetInvestigationMode(bool enabled)
        {
        }
    }
}
