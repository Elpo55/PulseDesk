using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Sysora.App.ViewModels;

namespace Sysora.App.Views;

public sealed partial class GamingPage : Page
{
    public GamingPage()
    {
        ViewModel = App.Services.GetRequiredService<GamingViewModel>();
        InitializeComponent();
    }

    public GamingViewModel ViewModel { get; }

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
