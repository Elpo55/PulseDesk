using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Sysora.App.ViewModels;

namespace Sysora.App.Views;

public sealed partial class AppImpactPage : Page
{
    public AppImpactPage()
    {
        ViewModel = App.Services.GetRequiredService<AppImpactViewModel>();
        InitializeComponent();
    }

    public AppImpactViewModel ViewModel { get; }

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
