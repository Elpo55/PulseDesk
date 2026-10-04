using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using PulseDesk.App.Views;

namespace PulseDesk.App.Services;

/// <summary>Pages of the application.</summary>
public enum AppPage
{
    Dashboard,
    Performance,
    Processes,
    Storage,
    Network,
    System,
    History,
    Settings,
}

/// <summary>Navigates the main frame between pages.</summary>
public sealed class NavigationService
{
    private Frame? _frame;

    /// <summary>Raised after navigation, so the navigation menu can reflect the current page.</summary>
    public event EventHandler<AppPage>? Navigated;

    public AppPage? Current { get; private set; }

    public void Attach(Frame frame) => _frame = frame;

    public void Navigate(AppPage page)
    {
        if (_frame is null || Current == page)
        {
            return;
        }

        _frame.Navigate(GetPageType(page), null, new EntranceNavigationTransitionInfo());
        Current = page;
        Navigated?.Invoke(this, page);
    }

    private static Type GetPageType(AppPage page) => page switch
    {
        AppPage.Dashboard => typeof(DashboardPage),
        AppPage.Performance => typeof(PerformancePage),
        AppPage.Processes => typeof(ProcessesPage),
        AppPage.Storage => typeof(StoragePage),
        AppPage.Network => typeof(NetworkPage),
        AppPage.System => typeof(SystemPage),
        AppPage.History => typeof(HistoryPage),
        AppPage.Settings => typeof(SettingsPage),
        _ => throw new ArgumentOutOfRangeException(nameof(page)),
    };
}
