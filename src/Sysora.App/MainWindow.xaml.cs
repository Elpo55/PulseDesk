using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sysora.App.Services;
using Sysora.App.ViewModels;

namespace Sysora.App;

/// <summary>
/// Main window: custom title bar and the navigation pane. Visibility, closing and theme are handled by
/// <see cref="ApplicationShell"/>; this class only wires navigation.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly NavigationService _navigation;
    private bool _syncingSelection;

    public MainWindow(ShellViewModel viewModel, NavigationService navigation, StartupOptions options)
    {
        ViewModel = viewModel;
        _navigation = navigation;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Sysora.ico"));
        AppWindow.Title = "Sysora";

        // NavigationView localizes its built-in Settings item; keep the UI language consistent.
        NavView.Loaded += (_, _) =>
        {
            if (NavView.SettingsItem is NavigationViewItem settingsItem)
            {
                settingsItem.Content = "Settings";
            }

            // The first navigation happens before the menu exists: reflect it now.
            if (_navigation.Current is { } current)
            {
                OnNavigated(this, current);
            }
        };

        _navigation.Attach(ContentFrame);
        _navigation.Navigated += OnNavigated;
        _navigation.Navigate(options.InitialPage);
    }

    public ShellViewModel ViewModel { get; }

    private void OnPaneToggleRequested(TitleBar sender, object args) => NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection)
        {
            return;
        }

        if (args.IsSettingsSelected)
        {
            _navigation.Navigate(AppPage.Settings);
        }
        else if (args.SelectedItem is NavigationViewItem { Tag: string tag } && Enum.TryParse<AppPage>(tag, out var page))
        {
            _navigation.Navigate(page);
        }
    }

    /// <summary>Keeps the menu selection in sync when navigation comes from elsewhere (tray menu, links).</summary>
    private void OnNavigated(object? sender, AppPage page)
    {
        _syncingSelection = true;
        try
        {
            NavView.SelectedItem = page == AppPage.Settings
                ? NavView.SettingsItem
                : NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == page.ToString());
        }
        finally
        {
            _syncingSelection = false;
        }
    }
}
