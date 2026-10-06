using Sysora.Core.Models;
using Sysora.Core.Monitoring;
using Sysora.Core.Settings;

namespace Sysora.Tests.Core;

public sealed class HealthEvaluatorTests
{
    private const ulong Gb = 1024UL * 1024 * 1024;
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly AlertSettings Alerts = new()
    {
        CpuWarningPercent = 80,
        CpuCriticalPercent = 95,
        CpuSustainSeconds = 30,
        MemoryWarningPercent = 85,
        MemoryCriticalPercent = 95,
        MemorySustainSeconds = 60,
        DiskWarningPercent = 85,
        DiskCriticalPercent = 95,
        ProcessMemoryWarningPercent = 30,
        ProcessCpuWarningPercent = 50,
    };

    private static SystemSnapshot Snapshot(int second, double cpu = 10, double memoryPercent = 40) => new()
    {
        Timestamp = T0.AddSeconds(second),
        Cpu = new CpuMetrics(cpu, null, null, 8),
        Memory = MemoryMetrics.FromTotalAndAvailable(32 * Gb, (ulong)(32 * Gb * (1 - (memoryPercent / 100)))),
    };

    private static HealthIndicator Indicator(HealthReport report, HealthCategory category) =>
        Assert.Single(report.Indicators, i => i.Category == category);

    [Fact]
    public void NormalSystem_ReportsNormal()
    {
        var evaluator = new HealthEvaluator(Alerts);

        var report = evaluator.Evaluate(Snapshot(0), MetricKind.All);

        Assert.Equal(HealthStatus.Normal, report.Overall);
        Assert.Equal("CPU normal", Indicator(report, HealthCategory.Cpu).Summary);
        Assert.Equal("Memory normal", Indicator(report, HealthCategory.Memory).Summary);
    }

    [Fact]
    public void CpuSpike_DoesNotRaiseWarning()
    {
        var evaluator = new HealthEvaluator(Alerts);
        HealthReport report = HealthReport.Empty;
        for (var s = 0; s < 10; s++)
        {
            report = evaluator.Evaluate(Snapshot(s, cpu: 99), MetricKind.Cpu);
        }

        Assert.Equal(HealthStatus.Normal, Indicator(report, HealthCategory.Cpu).Status);
    }

    [Fact]
    public void SustainedHighCpu_RaisesWarningThenCritical()
    {
        var evaluator = new HealthEvaluator(Alerts);
        HealthReport report = HealthReport.Empty;
        for (var s = 0; s <= 30; s++)
        {
            report = evaluator.Evaluate(Snapshot(s, cpu: 85), MetricKind.Cpu);
        }

        Assert.Equal(HealthStatus.Warning, Indicator(report, HealthCategory.Cpu).Status);

        for (var s = 31; s <= 61; s++)
        {
            report = evaluator.Evaluate(Snapshot(s, cpu: 98), MetricKind.Cpu);
        }

        var cpu = Indicator(report, HealthCategory.Cpu);
        Assert.Equal(HealthStatus.Critical, cpu.Status);
        Assert.Equal("High CPU usage", cpu.Summary);
        Assert.Equal("Above 95% for more than 30 s", cpu.Detail);
    }

    [Fact]
    public void StaleCpuValue_IsNotRefedWhenOtherMetricsUpdate()
    {
        var evaluator = new HealthEvaluator(Alerts);
        evaluator.Evaluate(Snapshot(0, cpu: 99), MetricKind.Cpu);

        // Only memory updates afterwards: the single high CPU sample must not be stretched over time.
        HealthReport report = HealthReport.Empty;
        for (var s = 1; s <= 60; s++)
        {
            report = evaluator.Evaluate(Snapshot(s, cpu: 99), MetricKind.Memory);
        }

        Assert.Equal(HealthStatus.Normal, Indicator(report, HealthCategory.Cpu).Status);
    }

    [Fact]
    public void FullDrive_IsReportedWithLetterAndPercent()
    {
        var evaluator = new HealthEvaluator(Alerts);
        var snapshot = Snapshot(0) with
        {
            Storage =
            [
                StorageMetrics.FromTotalAndFree(@"C:\", 1000 * Gb, 130 * Gb) with { Kind = DriveKind.Fixed },
                StorageMetrics.FromTotalAndFree(@"D:\", 1000 * Gb, 900 * Gb) with { Kind = DriveKind.Fixed },
                StorageMetrics.FromTotalAndFree(@"E:\", 64 * Gb, 1 * Gb) with { Kind = DriveKind.Removable },
            ],
        };

        var report = evaluator.Evaluate(snapshot, MetricKind.All);

        var storage = Indicator(report, HealthCategory.Storage);
        Assert.Equal(HealthStatus.Warning, storage.Status);
        Assert.Equal("Storage C: 87%", storage.Summary);
    }

    [Theory]
    [InlineData(NetworkConnectivity.InternetAccess, HealthStatus.Normal, "Network connected")]
    [InlineData(NetworkConnectivity.LocalAccess, HealthStatus.Warning, "Network available but Internet unavailable")]
    [InlineData(NetworkConnectivity.None, HealthStatus.Warning, "No network connection")]
    [InlineData(NetworkConnectivity.Unknown, HealthStatus.Unknown, "Network status unknown")]
    public void Network_ReflectsWindowsConnectivity(NetworkConnectivity connectivity, HealthStatus status, string summary)
    {
        var evaluator = new HealthEvaluator(Alerts);
        var snapshot = Snapshot(0) with { Network = new NetworkMetrics(connectivity, []) };

        var network = Indicator(evaluator.Evaluate(snapshot, MetricKind.All), HealthCategory.Network);

        Assert.Equal(status, network.Status);
        Assert.Equal(summary, network.Summary);
    }

    [Fact]
    public void UnavailableMetric_IsReportedAsUnknown_NotAsNormal()
    {
        var evaluator = new HealthEvaluator(Alerts);
        var snapshot = new SystemSnapshot { Timestamp = T0, Unavailable = MetricKind.Cpu };

        var cpu = Indicator(evaluator.Evaluate(snapshot, MetricKind.Cpu), HealthCategory.Cpu);

        Assert.Equal(HealthStatus.Unknown, cpu.Status);
    }

    [Fact]
    public void MissingMetric_IsOmitted()
    {
        var evaluator = new HealthEvaluator(Alerts);

        var report = evaluator.Evaluate(new SystemSnapshot { Timestamp = T0 }, MetricKind.None);

        Assert.Empty(report.Indicators);
        Assert.Equal(HealthStatus.Unknown, report.Overall);
    }

    [Fact]
    public void ApplicationUsingTooMuchMemory_IsReported()
    {
        var evaluator = new HealthEvaluator(Alerts);
        var snapshot = Snapshot(0) with
        {
            Processes = new ProcessSnapshot(
            [
                Process(1, "browser.exe", memoryGb: 6),
                Process(2, "browser.exe", memoryGb: 5),
                Process(3, "editor.exe", memoryGb: 1),
                Process(4, "Memory Compression", memoryGb: 15),
            ]),
        };

        var report = evaluator.Evaluate(snapshot, MetricKind.All);

        var processes = Indicator(report, HealthCategory.Processes);
        Assert.Equal(HealthStatus.Warning, processes.Status);
        Assert.Equal("browser.exe uses 34% of memory", processes.Summary);
    }

    [Fact]
    public void ApplicationWithSustainedHighCpu_IsReported()
    {
        var evaluator = new HealthEvaluator(Alerts);
        HealthReport report = HealthReport.Empty;
        for (var s = 0; s <= 30; s += 2)
        {
            var snapshot = Snapshot(s) with { Processes = new ProcessSnapshot([Process(1, "miner.exe", cpu: 70)]) };
            report = evaluator.Evaluate(snapshot, MetricKind.Processes);
        }

        var processes = Indicator(report, HealthCategory.Processes);
        Assert.Equal("miner.exe uses a lot of CPU", processes.Summary);
    }

    [Fact]
    public void QuietProcesses_ReportNoAnomaly()
    {
        var evaluator = new HealthEvaluator(Alerts);
        var snapshot = Snapshot(0) with { Processes = new ProcessSnapshot([Process(1, "editor.exe", memoryGb: 1, cpu: 3)]) };

        var processes = Indicator(evaluator.Evaluate(snapshot, MetricKind.All), HealthCategory.Processes);

        Assert.Equal("No abnormal process detected", processes.Summary);
    }

    [Fact]
    public void Configure_AppliesNewThresholds()
    {
        var evaluator = new HealthEvaluator(Alerts);
        evaluator.Configure(Alerts with { DiskWarningPercent = 10, DiskCriticalPercent = 20 });
        var snapshot = Snapshot(0) with
        {
            Storage = [StorageMetrics.FromTotalAndFree(@"C:\", 100 * Gb, 85 * Gb) with { Kind = DriveKind.Fixed }],
        };

        Assert.Equal(HealthStatus.Warning, Indicator(evaluator.Evaluate(snapshot, MetricKind.All), HealthCategory.Storage).Status);
    }

    private static ProcessMetrics Process(int pid, string name, double memoryGb = 0.1, double cpu = 0) =>
        new(new ProcessIdentity(pid, pid), name)
        {
            PrivateWorkingSetBytes = (ulong)(memoryGb * Gb),
            CpuPercent = cpu,
        };
}
