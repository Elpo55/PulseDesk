using Sysora.Core.Models;
using Sysora.Core.Monitoring;

namespace Sysora.Tests.Core;

public sealed class SchedulingTests
{
    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Fact]
    public void Schedule_NewMetricsAreDueImmediately()
    {
        var schedule = new MetricSchedule();
        schedule.Configure(MetricKind.Cpu, Ms(1000), TimeSpan.Zero);
        schedule.Configure(MetricKind.Storage, Ms(15000), TimeSpan.Zero);

        Assert.Equal(MetricKind.Cpu | MetricKind.Storage, schedule.TakeDue(TimeSpan.Zero));
        Assert.Equal(MetricKind.None, schedule.TakeDue(Ms(10)));
    }

    [Fact]
    public void Schedule_EachMetricFollowsItsOwnInterval()
    {
        var schedule = new MetricSchedule();
        schedule.Configure(MetricKind.Cpu, Ms(1000), TimeSpan.Zero);
        schedule.Configure(MetricKind.Processes, Ms(2000), TimeSpan.Zero);
        schedule.TakeDue(TimeSpan.Zero);

        Assert.Equal(Ms(1000), schedule.TimeUntilNextDue(TimeSpan.Zero));
        Assert.Equal(MetricKind.Cpu, schedule.TakeDue(Ms(1000)));
        Assert.Equal(MetricKind.Cpu | MetricKind.Processes, schedule.TakeDue(Ms(2000)));
    }

    [Fact]
    public void Schedule_CoalescesMetricsDueAlmostTogether()
    {
        var schedule = new MetricSchedule();
        schedule.Configure(MetricKind.Cpu, Ms(1000), TimeSpan.Zero);
        schedule.Configure(MetricKind.Memory, Ms(1010), TimeSpan.Zero);
        schedule.TakeDue(TimeSpan.Zero);

        Assert.Equal(MetricKind.Cpu | MetricKind.Memory, schedule.TakeDue(Ms(1000)));
    }

    [Fact]
    public void Schedule_AfterFallingBehind_RestartsFromNowWithoutBurst()
    {
        var schedule = new MetricSchedule();
        schedule.Configure(MetricKind.Cpu, Ms(1000), TimeSpan.Zero);
        schedule.TakeDue(TimeSpan.Zero);

        // The machine was suspended for a minute: collect once, not sixty times.
        Assert.Equal(MetricKind.Cpu, schedule.TakeDue(TimeSpan.FromMinutes(1)));
        Assert.Equal(MetricKind.None, schedule.TakeDue(TimeSpan.FromMinutes(1) + Ms(100)));
        Assert.Equal(Ms(1000), schedule.TimeUntilNextDue(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Schedule_DisabledMetricsAreNeverDue()
    {
        var schedule = new MetricSchedule();
        schedule.Configure(MetricKind.Gpu, Ms(1000), TimeSpan.Zero);
        schedule.Configure(MetricKind.Gpu, null, TimeSpan.Zero);

        Assert.Equal(MetricKind.None, schedule.Enabled);
        Assert.Null(schedule.TimeUntilNextDue(TimeSpan.Zero));
        schedule.MarkDue(MetricKind.Gpu, TimeSpan.Zero);
        Assert.Equal(MetricKind.None, schedule.TakeDue(TimeSpan.Zero));
    }

    [Fact]
    public void Schedule_MarkDue_ForcesImmediateCollection()
    {
        var schedule = new MetricSchedule();
        schedule.Configure(MetricKind.Storage, TimeSpan.FromSeconds(30), TimeSpan.Zero);
        schedule.TakeDue(TimeSpan.Zero);

        schedule.MarkDue(MetricKind.Storage, Ms(500));

        Assert.Equal(MetricKind.Storage, schedule.TakeDue(Ms(500)));
    }

    [Fact]
    public void Schedule_ShorterInterval_TakesEffectWithoutWaitingForOldDeadline()
    {
        var schedule = new MetricSchedule();
        schedule.Configure(MetricKind.Storage, TimeSpan.FromSeconds(60), TimeSpan.Zero);
        schedule.TakeDue(TimeSpan.Zero);

        schedule.Configure(MetricKind.Storage, TimeSpan.FromSeconds(10), Ms(1000));

        Assert.Equal(TimeSpan.FromSeconds(9), schedule.TimeUntilNextDue(Ms(1000)));
    }

    [Fact]
    public void Governor_FirstCallOnlyEstablishesBaseline()
    {
        var cpu = TimeSpan.Zero;
        var governor = new SelfUsageGovernor(() => cpu, processorCount: 4);

        Assert.False(governor.Update(TimeSpan.Zero, budgetPercent: 2));
        Assert.Null(governor.LastCpuPercent);
    }

    [Fact]
    public void Governor_OverBudget_StretchesIntervals_UnderBudget_Relaxes()
    {
        var cpu = TimeSpan.Zero;
        var governor = new SelfUsageGovernor(() => cpu, processorCount: 4);
        governor.Update(TimeSpan.Zero, 2);

        // 2 s of CPU over 10 s on 4 processors = 5% > 2% budget.
        cpu += TimeSpan.FromSeconds(2);
        Assert.True(governor.Update(TimeSpan.FromSeconds(10), 2));
        Assert.Equal(5, governor.LastCpuPercent!.Value, precision: 6);
        Assert.Equal(1.5, governor.Factor, precision: 6);

        // Still over budget: grows again, but never beyond the maximum.
        for (var i = 2; i < 10; i++)
        {
            cpu += TimeSpan.FromSeconds(2);
            governor.Update(TimeSpan.FromSeconds(10 * i), 2);
        }

        Assert.Equal(SelfUsageGovernor.MaxFactor, governor.Factor);

        // Comfortably under budget (0.25%): relaxes step by step back to 1.
        for (var i = 10; i < 20; i++)
        {
            cpu += TimeSpan.FromMilliseconds(100);
            governor.Update(TimeSpan.FromSeconds(10 * i), 2);
        }

        Assert.Equal(1.0, governor.Factor);
    }

    [Fact]
    public void Governor_IgnoresUpdatesWithinMeasurementPeriod()
    {
        var cpu = TimeSpan.Zero;
        var governor = new SelfUsageGovernor(() => cpu, processorCount: 1);
        governor.Update(TimeSpan.Zero, 2);
        cpu += TimeSpan.FromSeconds(5);

        Assert.False(governor.Update(TimeSpan.FromSeconds(5), 2));
        Assert.Equal(0, governor.MeasurementCount);
    }

    [Fact]
    public void Governor_ZeroBudget_DisablesThrottling()
    {
        var cpu = TimeSpan.Zero;
        var governor = new SelfUsageGovernor(() => cpu, processorCount: 1);
        governor.Update(TimeSpan.Zero, 0);
        cpu += TimeSpan.FromSeconds(9);

        governor.Update(TimeSpan.FromSeconds(10), 0);

        Assert.Equal(1.0, governor.Factor);
        Assert.Equal(90, governor.LastCpuPercent!.Value, precision: 6);
    }
}
