using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Sysora.App.ViewModels;
using Sysora.Localization;

namespace Sysora.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();

        // Implicit styles apply when the switches load, after this point; the texts come from Sysora's language
        // (WinUI would use the Windows display language).
        var toggles = new Style(typeof(ToggleSwitch));
        toggles.Setters.Add(new Setter(ToggleSwitch.OnContentProperty, UiStrings.Common_On));
        toggles.Setters.Add(new Setter(ToggleSwitch.OffContentProperty, UiStrings.Common_Off));
        toggles.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
        Resources[typeof(ToggleSwitch)] = toggles;
    }

    public SettingsViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Activate();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }
}
