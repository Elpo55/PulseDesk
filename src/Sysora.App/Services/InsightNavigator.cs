using Sysora.App.ViewModels;
using Sysora.Core.Analysis;
using Sysora.Core.Diagnosis;

namespace Sysora.App.Services;

/// <summary>
/// Opens the page where the user can look further at a finding (diagnosis result, alert, insight, timeline entry),
/// selecting the application, the moment or the analysis concerned when there is one.
/// </summary>
public sealed class InsightNavigator(NavigationService navigation, AppImpactViewModel appImpact, NavigationRequests requests)
{
    /// <summary>Opens the page for <paramref name="action"/>.</summary>
    public void Open(DiagnosisAction action, string? appKey = null)
    {
        switch (action)
        {
            case DiagnosisAction.AppImpact:
                if (appKey is not null)
                {
                    appImpact.RequestSelection(appKey);
                }

                navigation.Navigate(AppPage.AppImpact);
                break;
            case DiagnosisAction.None:
                break;
            default:
                if (PageOf(action) is { } page)
                {
                    navigation.Navigate(page);
                }

                break;
        }
    }

    /// <summary>Opens Replay on the period around <paramref name="time"/> (15 minutes, or the minute history when older).</summary>
    public void OpenReplayAt(DateTimeOffset time) => OpenReplay(time - TimeSpan.FromMinutes(7), time + TimeSpan.FromMinutes(8));

    /// <summary>Opens Replay on a period.</summary>
    public void OpenReplay(DateTimeOffset from, DateTimeOffset to)
    {
        requests.Replay(from, to);
        navigation.Navigate(AppPage.Replay);
    }

    /// <summary>Opens "Why now?" for a metric on the Diagnosis page.</summary>
    public void OpenWhyNow(WhyNowMetric metric)
    {
        requests.WhyNow(metric);
        navigation.Navigate(AppPage.Diagnosis);
    }

    /// <summary>Opens a before/after comparison.</summary>
    public void OpenCompare(ComparisonRequest request)
    {
        requests.Compare(request);
        navigation.Navigate(AppPage.Compare);
    }

    /// <summary>Opens the report of an investigation.</summary>
    public void OpenInvestigation(Guid id)
    {
        requests.TroubleshootingReport(id);
        navigation.Navigate(AppPage.Troubleshooting);
    }

    /// <summary>Label of the button that opens <paramref name="action"/>, or empty when there is none.</summary>
    public static string Label(DiagnosisAction action) => action switch
    {
        DiagnosisAction.AppImpact => "Open App Impact",
        DiagnosisAction.Replay => "Open Replay",
        DiagnosisAction.Processes => "Open Processes",
        DiagnosisAction.Performance => "Open Performance",
        DiagnosisAction.Storage => "Open Storage",
        DiagnosisAction.Network => "Open Network",
        DiagnosisAction.Diagnosis => "Open Diagnosis",
        DiagnosisAction.Alerts => "Open Alerts",
        DiagnosisAction.Changes => "Open Changes",
        DiagnosisAction.Gaming => "Open Gaming",
        DiagnosisAction.PcHealth => "Open PC Health",
        DiagnosisAction.Timeline => "Open Timeline",
        DiagnosisAction.Compare => "Compare before / after",
        DiagnosisAction.Troubleshooting => "Open Troubleshooting",
        DiagnosisAction.LargeFiles => "Find large files",
        _ => string.Empty,
    };

    /// <summary>"Why now?" metric matching a diagnosis category, when there is one.</summary>
    public static WhyNowMetric? WhyNowFor(DiagnosisCategory category) => category switch
    {
        DiagnosisCategory.Cpu or DiagnosisCategory.Applications => WhyNowMetric.Cpu,
        DiagnosisCategory.Memory => WhyNowMetric.Memory,
        DiagnosisCategory.Disk => WhyNowMetric.Disk,
        DiagnosisCategory.Gpu => WhyNowMetric.Gpu,
        DiagnosisCategory.Network => WhyNowMetric.Network,
        _ => null,
    };

    private static AppPage? PageOf(DiagnosisAction action) => action switch
    {
        DiagnosisAction.Replay => AppPage.Replay,
        DiagnosisAction.Processes => AppPage.Processes,
        DiagnosisAction.Performance => AppPage.Performance,
        DiagnosisAction.Storage => AppPage.Storage,
        DiagnosisAction.Network => AppPage.Network,
        DiagnosisAction.Diagnosis => AppPage.Diagnosis,
        DiagnosisAction.Alerts => AppPage.Alerts,
        DiagnosisAction.Changes => AppPage.Changes,
        DiagnosisAction.Gaming => AppPage.Gaming,
        DiagnosisAction.PcHealth => AppPage.PcHealth,
        DiagnosisAction.Timeline => AppPage.Timeline,
        DiagnosisAction.Compare => AppPage.Compare,
        DiagnosisAction.Troubleshooting => AppPage.Troubleshooting,
        DiagnosisAction.LargeFiles => AppPage.LargeFiles,
        _ => null,
    };
}
