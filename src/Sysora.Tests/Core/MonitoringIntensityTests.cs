using Sysora.Core.Models;
using Sysora.Core.Monitoring;
using Sysora.Core.Settings;

namespace Sysora.Tests.Core;

public sealed class MonitoringIntensityTests
{
    [Theory]
    [InlineData(MonitoringIntensity.Minimal, MetricKind.Cpu, 1000, 2000)]
    [InlineData(MonitoringIntensity.Minimal, MetricKind.Processes, 2000, 6000)]
    [InlineData(MonitoringIntensity.Balanced, MetricKind.Processes, 2000, 2000)]
    [InlineData(MonitoringIntensity.Detailed, MetricKind.Processes, 2000, 1000)]
    [InlineData(MonitoringIntensity.Detailed, MetricKind.Processes, 1000, 1000)]
    [InlineData(MonitoringIntensity.Detailed, MetricKind.Cpu, 500, 500)]
    [InlineData(MonitoringIntensity.Detailed, MetricKind.Storage, 15000, 7500)]
    [InlineData(MonitoringIntensity.Detailed, MetricKind.Storage, 5000, 5000)]
    public void Profiles_ScaleIntervals_WithinSafeLimits(MonitoringIntensity intensity, MetricKind kind, int configuredMs, int expectedMs)
    {
        var effective = MonitoringProfile.For(intensity).Apply(kind, TimeSpan.FromMilliseconds(configuredMs));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), effective);
    }

    [Fact]
    public void Profiles_DifferInWhatTheyKeepAndHowOftenTheyEvaluate()
    {
        Assert.True(MonitoringProfile.Minimal.TopAppsPerCriterion < MonitoringProfile.Balanced.TopAppsPerCriterion);
        Assert.True(MonitoringProfile.Balanced.TopAppsPerCriterion < MonitoringProfile.Detailed.TopAppsPerCriterion);
        Assert.True(MonitoringProfile.Minimal.AlertEvaluationInterval > MonitoringProfile.Balanced.AlertEvaluationInterval);
        Assert.True(MonitoringProfile.Minimal.InsightRefreshInterval > MonitoringProfile.Detailed.InsightRefreshInterval);
    }

    [Fact]
    public void UnknownIntensityInTheSettingsFile_FallsBackToBalanced()
    {
        var settings = AppSettings.Default with { Monitoring = new MonitoringSettings { Intensity = (MonitoringIntensity)42 } };

        Assert.Equal(MonitoringIntensity.Balanced, SettingsValidator.Normalize(settings).Monitoring.Intensity);
        Assert.Equal(MonitoringIntensity.Balanced, AppSettings.Default.Monitoring.Intensity);
    }

    [Fact]
    public void SelfImpact_IsUnknownBeforeTheFirstMeasurement()
    {
        var report = SelfImpactAssessor.Assess(new SelfUsage(null, 100_000_000, 1), MonitoringScheduleInfo.Unknown, 2);

        Assert.Equal(SelfImpactLevel.Unknown, report.Level);
    }

    [Fact]
    public void SelfImpact_IsLowForALightProcess()
    {
        var usage = new SelfUsage(0.04, 140L << 20, 1)
        {
            ManagedHeapBytes = 30L << 20,
            AllocatedBytesPerSecond = 200_000,
            CollectionRoundsPerMinute = 62,
            ProcessesAnalyzed = 320,
            WriteBytesPerSecond = 1500,
        };
        var schedule = new MonitoringScheduleInfo(MonitoringIntensity.Balanced, false, false, 1, new Dictionary<MetricKind, TimeSpan>
        {
            [MetricKind.Cpu] = TimeSpan.FromSeconds(1),
            [MetricKind.Processes] = TimeSpan.FromSeconds(2),
        });

        var report = SelfImpactAssessor.Assess(usage, schedule, 2);

        Assert.Equal(SelfImpactLevel.Low, report.Level);
        Assert.Equal("Sysora impact: Low", report.Headline);
        Assert.Empty(report.Warnings);
        Assert.Contains(report.Items, i => i.Label == "CPU" && i.Value == "0.04%");
        Assert.Contains(report.Items, i => i.Label == "Disk writes" && i.Value.StartsWith("Low", StringComparison.Ordinal));
        Assert.Contains(report.Items, i => i.Label == "Processes analyzed" && i.Value == "320" && i.Note == "Every 2 s, in one system call");
        Assert.Equal("Intensity Balanced: CPU 1 s · processes 2 s", SelfImpactAssessor.IntervalsText(schedule));
    }

    [Fact]
    public void SelfImpact_WarnsWhenSysoraStaysAboveItsBudget_OrUsesTooMuchMemory()
    {
        var usage = new SelfUsage(3.1, 600L << 20, 2.25) { OverBudgetSustained = true, CollectionRoundsPerMinute = 40 };

        var report = SelfImpactAssessor.Assess(usage, MonitoringScheduleInfo.Unknown, 2);

        Assert.Equal(SelfImpactLevel.High, report.Level);
        Assert.Equal(2, report.Warnings.Count);
        Assert.Contains("×2.25", report.Warnings[0], StringComparison.Ordinal);
    }
}
