using PulseDesk.App.ViewModels;
using PulseDesk.Core.Diagnosis;

namespace PulseDesk.App.Services;

/// <summary>
/// Opens the page where the user can look further at a finding (diagnosis result, alert, insight), selecting
/// the application concerned when there is one.
/// </summary>
public sealed class InsightNavigator(NavigationService navigation, AppImpactViewModel appImpact)
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
            case DiagnosisAction.Replay:
                navigation.Navigate(AppPage.Replay);
                break;
            case DiagnosisAction.Processes:
                navigation.Navigate(AppPage.Processes);
                break;
            case DiagnosisAction.Performance:
                navigation.Navigate(AppPage.Performance);
                break;
            case DiagnosisAction.Storage:
                navigation.Navigate(AppPage.Storage);
                break;
            case DiagnosisAction.Network:
                navigation.Navigate(AppPage.Network);
                break;
            case DiagnosisAction.Diagnosis:
                navigation.Navigate(AppPage.Diagnosis);
                break;
            case DiagnosisAction.Alerts:
                navigation.Navigate(AppPage.Alerts);
                break;
            case DiagnosisAction.Changes:
                navigation.Navigate(AppPage.Changes);
                break;
        }
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
        _ => string.Empty,
    };
}
