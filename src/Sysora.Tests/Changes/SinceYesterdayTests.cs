using Sysora.Core.Alerts;
using Sysora.Core.Analysis;
using Sysora.Core.Changes;

namespace Sysora.Tests.Changes;

public sealed class SinceYesterdayTests
{
    // Tuesday 3 March 2026, 15:00 UTC.
    private static readonly DateTimeOffset Now = new(2026, 3, 3, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WithoutAYesterdaySnapshot_ItSaysSo()
    {
        var comparison = new ChangeComparison(BaselineReference.Yesterday, null, [], "Not enough history for this comparison yet.");

        var summary = SinceYesterdayBuilder.Build(comparison, null, [], Now, TimeZoneInfo.Utc);

        Assert.False(summary.HasReference);
        Assert.Equal("Not enough history yet", summary.Headline);
        Assert.Empty(summary.UnchangedAreas);
    }

    [Fact]
    public void NothingChanged_SaysSo_AndListsTheAreasCompared()
    {
        var summary = SinceYesterdayBuilder.Build(Comparison([]), null, [], Now, TimeZoneInfo.Utc);

        Assert.Equal("No change since yesterday", summary.Headline);
        Assert.True(summary.MostlyUnchanged);
        Assert.Contains("Applications", summary.UnchangedAreas);
        Assert.DoesNotContain("Usage", summary.UnchangedAreas);
        Assert.Contains(summary.NotCompared, n => n.StartsWith("Usage:", StringComparison.Ordinal));
        Assert.Contains(summary.NotCompared, n => n.StartsWith("Firmware (BIOS):", StringComparison.Ordinal));
    }

    [Fact]
    public void Changes_AreCountedBySignificance_WithTheirValuesAndDates()
    {
        var changes = new[]
        {
            Change(ChangeType.WindowsUpdated, ChangeImportance.High, "Windows updated", "26100.1", "26100.2"),
            Change(ChangeType.AppInstalled, ChangeImportance.Medium, "Tool installed", null, "1.0"),
            Change(ChangeType.StartupProgramAdded, ChangeImportance.Medium, "Updater added to startup", null, "C:\\updater.exe"),
            Change(ChangeType.AppUpdated, ChangeImportance.Low, "Browser updated", "120", "121"),
        };

        var summary = SinceYesterdayBuilder.Build(Comparison(changes), null, [], Now, TimeZoneInfo.Utc);

        Assert.Equal(1, summary.Significant);
        Assert.Equal(3, summary.Minor);
        Assert.Equal("1 significant change · 3 minor changes", summary.Headline);
        var first = summary.Items[0];
        Assert.Equal("Windows updated", first.Title);
        Assert.Equal("26100.1", first.OldValue);
        Assert.Equal("26100.2", first.NewValue);
        Assert.Equal(ConfidenceLevel.High, first.Confidence);
        Assert.StartsWith("Between ", first.When, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows version", summary.UnchangedAreas);
        Assert.Contains("Devices", summary.UnchangedAreas);
    }

    [Fact]
    public void TodaysActivity_ReplacesThe24HourAveragesOfTheSnapshots()
    {
        var changes = new[] { Change(ChangeType.CpuUsageChanged, ChangeImportance.Medium, "Average CPU usage rose", "8%", "19%") };
        var before = new PeriodState("Yesterday", Now.AddDays(-1).Date, Now.Date, 1440, "Per-minute averages of the local history", new Dictionary<StateMetric, double> { [StateMetric.Cpu] = 8 });
        var after = new PeriodState("Today", Now.Date, Now, 900, "Per-minute averages of the local history", new Dictionary<StateMetric, double> { [StateMetric.Cpu] = 31 });

        var summary = SinceYesterdayBuilder.Build(Comparison(changes), StateComparer.Compare(before, after), [], Now, TimeZoneInfo.Utc);

        var usage = Assert.Single(summary.Items);
        Assert.Equal("Usage", usage.Area);
        Assert.Equal("CPU usage +23 percentage points today", usage.Title);
        Assert.Equal("8% yesterday", usage.OldValue);
        Assert.Equal(ChangeImportance.High, usage.Importance);
        Assert.Equal(ConfidenceLevel.Medium, usage.Confidence);
    }

    [Fact]
    public void MoreAlertsToday_IsAChange()
    {
        var alerts = new[]
        {
            TestData.Alert("disk.busy", Now.AddHours(-2), AlertSeverity.Warning, TimeSpan.FromMinutes(3)),
            TestData.Alert("disk.busy", Now.AddHours(-4), AlertSeverity.Warning, TimeSpan.FromMinutes(3)),
            TestData.Alert("cpu.sustained", Now.AddHours(-1), AlertSeverity.Critical),
            TestData.Alert("cpu.sustained", Now.AddDays(-1), AlertSeverity.Warning, TimeSpan.FromMinutes(3)),
        };

        var summary = SinceYesterdayBuilder.Build(Comparison([]), null, alerts, Now, TimeZoneInfo.Utc);

        var item = Assert.Single(summary.Items);
        Assert.Equal("More alerts today (3)", item.Title);
        Assert.Equal("1 alert yesterday", item.OldValue);
        Assert.Equal(ChangeImportance.High, item.Importance);
    }

    private static ChangeComparison Comparison(IReadOnlyList<DetectedChange> changes)
    {
        var reference = new SystemBaseline
        {
            CapturedAt = Now.AddDays(-1).AddHours(-5),
            OsBuild = "26100.1",
            InstalledMemoryBytes = TestData.TotalMemory,
            Inventory = new SystemInventory
            {
                AppsAvailable = true,
                StartupAvailable = true,
                Devices = [new DeviceInfo("Volume", "C:", "C:") { TotalBytes = 500, UsedBytes = 200 }],
            },
        };
        return new ChangeComparison(BaselineReference.Yesterday, reference, changes, "Compared.");
    }

    private static DetectedChange Change(ChangeType type, ChangeImportance importance, string title, string? oldValue, string? newValue) => new()
    {
        Id = title,
        Type = type,
        DetectedAt = Now,
        After = Now.AddDays(-1),
        Before = Now,
        Subject = title,
        Title = title,
        OldValue = oldValue,
        NewValue = newValue,
        Importance = importance,
        Explanation = "test",
        Origin = BaselineComparer.UnknownOrigin,
    };
}
