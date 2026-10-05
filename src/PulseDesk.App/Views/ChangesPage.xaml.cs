using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PulseDesk.App.ViewModels;

namespace PulseDesk.App.Views;

public sealed partial class ChangesPage : Page
{
    public ChangesPage()
    {
        ViewModel = App.Services.GetRequiredService<ChangesViewModel>();
        InitializeComponent();
    }

    public ChangesViewModel ViewModel { get; }

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
