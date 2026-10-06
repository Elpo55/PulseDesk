using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Sysora.App.ViewModels;

namespace Sysora.App.Views;

public sealed partial class ProcessesPage : Page
{
    public ProcessesPage()
    {
        ViewModel = App.Services.GetRequiredService<ProcessesViewModel>();
        InitializeComponent();
    }

    public ProcessesViewModel ViewModel { get; }

    /// <summary>Maps the view model's status to an InfoBar severity (x:Bind function).</summary>
    public static InfoBarSeverity Severity(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

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
