using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Sysora.App.ViewModels;

namespace Sysora.App.Views;

public sealed partial class PerformancePage : Page
{
    public PerformancePage()
    {
        ViewModel = App.Services.GetRequiredService<PerformanceViewModel>();
        InitializeComponent();
    }

    public PerformanceViewModel ViewModel { get; }

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

    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag && Enum.TryParse<PerformanceSection>(tag, out var section))
        {
            ViewModel.Section = section;
        }
    }
}
