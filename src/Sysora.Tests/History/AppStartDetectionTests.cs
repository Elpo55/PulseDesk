using Sysora.Core.Analysis;
using Sysora.Core.History;
using Sysora.Core.Models;

namespace Sysora.Tests.History;

public sealed class AppStartDetectionTests
{
    private static readonly DateTimeOffset T0 = TestData.Start;

    [Fact]
    public void ApplicationsRunningAtTheFirstSample_AreNotReportedAsStarted()
    {
        var detector = new SystemEventDetector();

        var events = Detect(detector, T0, App("chat.exe", started: T0.AddSeconds(-5)));

        Assert.DoesNotContain(events, e => e.Kind == SystemEventKind.AppStarted);
    }

    [Fact]
    public void NewApplication_IsReportedOnceConfirmed_AtTheTimeItWasFirstSeen()
    {
        Requires.WindowsPaths();
        var detector = new SystemEventDetector();
        Detect(detector, T0, App("explorer2.exe", started: T0.AddHours(-1)));

        var first = Detect(detector, T0.AddSeconds(2), App("explorer2.exe", started: T0.AddHours(-1)), App("chat.exe", started: T0.AddSeconds(1)));
        var second = Detect(detector, T0.AddSeconds(4), App("explorer2.exe", started: T0.AddHours(-1)), App("chat.exe", started: T0.AddSeconds(1)));

        Assert.DoesNotContain(first, e => e.Kind == SystemEventKind.AppStarted);
        var started = Assert.Single(second, e => e.Kind == SystemEventKind.AppStarted);
        Assert.Equal("chat.exe started", started.Title);
        Assert.Equal(T0.AddSeconds(2), started.Timestamp);
        Assert.Equal(@"C:\Apps", started.Detail);
    }

    [Fact]
    public void ShortLivedProcesses_ServicesAndOldProcesses_AreNotLaunches()
    {
        var detector = new SystemEventDetector();
        Detect(detector, T0);

        Detect(detector, T0.AddSeconds(2),
            App("flash.exe", started: T0.AddSeconds(1)),
            App("service.exe", started: T0.AddSeconds(1), userSession: false),
            App("old.exe", started: T0.AddMinutes(-10)),
            App("conhost.exe", started: T0.AddSeconds(1)));
        var events = Detect(detector, T0.AddSeconds(4),
            App("service.exe", started: T0.AddSeconds(1), userSession: false),
            App("old.exe", started: T0.AddMinutes(-10)),
            App("conhost.exe", started: T0.AddSeconds(1)));

        Assert.DoesNotContain(events, e => e.Kind == SystemEventKind.AppStarted);
        Assert.DoesNotContain(events, e => e.Kind == SystemEventKind.AppExited);
    }

    [Fact]
    public void StartedApplication_IsReportedClosed_WithHowLongItRan()
    {
        var detector = new SystemEventDetector();
        Detect(detector, T0);
        Detect(detector, T0.AddSeconds(2), App("chat.exe", started: T0.AddSeconds(1)));
        Detect(detector, T0.AddSeconds(4), App("chat.exe", started: T0.AddSeconds(1)));

        var events = Detect(detector, T0.AddMinutes(12));

        var closed = Assert.Single(events, e => e.Kind == SystemEventKind.AppExited);
        Assert.Equal("chat.exe closed", closed.Title);
        Assert.Equal("Ran for 11m", closed.Detail);
    }

    [Fact]
    public void ApplicationRestartingOften_IsReportedOnlyOnceEvery30Minutes()
    {
        var detector = new SystemEventDetector();
        Detect(detector, T0);
        var starts = 0;
        for (var round = 0; round < 3; round++)
        {
            var at = T0.AddMinutes(round * 5);
            starts += Detect(detector, at.AddSeconds(2), App("updater.exe", started: at.AddSeconds(1))).Count(e => e.Kind == SystemEventKind.AppStarted);
            starts += Detect(detector, at.AddSeconds(4), App("updater.exe", started: at.AddSeconds(1))).Count(e => e.Kind == SystemEventKind.AppStarted);
            Detect(detector, at.AddSeconds(30));
        }

        Assert.Equal(1, starts);
    }

    private static IReadOnlyList<SystemEvent> Detect(SystemEventDetector detector, DateTimeOffset time, params AppGroup[] apps) =>
        detector.Detect(TestData.System(time, 10), MetricKind.Processes, apps);

    private static AppGroup App(string name, DateTimeOffset started, bool userSession = true) =>
        new(AppIdentity.Create(name, $@"C:\Apps\{name}"), 1, 0.5, 50UL << 20, 0)
        {
            StartedAt = started,
            InUserSession = userSession,
        };
}
