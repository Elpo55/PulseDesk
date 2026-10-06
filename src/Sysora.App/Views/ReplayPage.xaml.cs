using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Sysora.App.ViewModels;

namespace Sysora.App.Views;

public sealed partial class ReplayPage : Page
{
    public ReplayPage()
    {
        ViewModel = App.Services.GetRequiredService<ReplayViewModel>();
        InitializeComponent();
    }

    public ReplayViewModel ViewModel { get; }

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

    /// <summary>A click or drag on any chart moves the replay cursor.</summary>
    private void OnTimeSelected(object? sender, DateTimeOffset time) => ViewModel.SetCursor(time);
}
