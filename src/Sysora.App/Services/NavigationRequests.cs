using Sysora.Core.Analysis;

namespace Sysora.App.Services;

/// <summary>What another page asked a page to show when it opens ("Replay at 19:42", "Why now? for memory"…).</summary>
public enum NavigationRequestKind
{
    ReplayPeriod,
    WhyNow,
    Compare,
    TroubleshootingReport,
}

/// <summary>
/// Hands a request from one page to another without the pages knowing each other (page view models depend on
/// <see cref="InsightNavigator"/>, so the navigator cannot depend on them). The target page takes the request when it
/// is shown, or at once when it is already visible.
/// </summary>
public sealed class NavigationRequests
{
    private readonly Lock _lock = new();
    private (DateTimeOffset From, DateTimeOffset To)? _replay;
    private WhyNowMetric? _whyNow;
    private ComparisonRequest? _compare;
    private Guid? _report;

    /// <summary>Raised on the UI thread when a request is made.</summary>
    public event EventHandler<NavigationRequestKind>? Requested;

    /// <summary>Show this period in Replay (the cursor goes to its middle).</summary>
    public void Replay(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            _replay = (from, to);
        }

        Requested?.Invoke(this, NavigationRequestKind.ReplayPeriod);
    }

    public void WhyNow(WhyNowMetric metric)
    {
        lock (_lock)
        {
            _whyNow = metric;
        }

        Requested?.Invoke(this, NavigationRequestKind.WhyNow);
    }

    public void Compare(ComparisonRequest request)
    {
        lock (_lock)
        {
            _compare = request;
        }

        Requested?.Invoke(this, NavigationRequestKind.Compare);
    }

    public void TroubleshootingReport(Guid id)
    {
        lock (_lock)
        {
            _report = id;
        }

        Requested?.Invoke(this, NavigationRequestKind.TroubleshootingReport);
    }

    public (DateTimeOffset From, DateTimeOffset To)? TakeReplay() => Take(ref _replay);

    public WhyNowMetric? TakeWhyNow() => Take(ref _whyNow);

    public ComparisonRequest? TakeCompare()
    {
        lock (_lock)
        {
            var request = _compare;
            _compare = null;
            return request;
        }
    }

    public Guid? TakeTroubleshootingReport() => Take(ref _report);

    private T? Take<T>(ref T? field)
        where T : struct
    {
        lock (_lock)
        {
            var value = field;
            field = null;
            return value;
        }
    }
}
