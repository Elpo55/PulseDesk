using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using PulseDesk.App.Services;
using PulseDesk.Core.Interfaces;

namespace PulseDesk.App.ViewModels;

/// <summary>State shown in the title bar: demo mode and paused monitoring.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private const string PauseGlyphValue = "";
    private const string ResumeGlyphValue = "";

    private readonly IMetricsMonitor _monitor;

    public ShellViewModel(IMetricsMonitor monitor, StartupOptions options, DispatcherQueue dispatcher)
    {
        _monitor = monitor;
        IsDemoMode = options.DemoMode;
        _monitor.StateChanged += (_, _) => dispatcher.TryEnqueue(() => IsPaused = _monitor.IsPaused);
    }

    /// <summary>True when PulseDesk shows simulated data (<c>--demo</c>).</summary>
    public bool IsDemoMode { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseGlyph), nameof(PauseLabel))]
    public partial bool IsPaused { get; set; }

    public string PauseGlyph => IsPaused ? ResumeGlyphValue : PauseGlyphValue;

    public string PauseLabel => IsPaused ? "Resume monitoring" : "Pause monitoring";

    [RelayCommand]
    private void TogglePause()
    {
        if (_monitor.IsPaused)
        {
            _monitor.Resume();
        }
        else
        {
            _monitor.Pause();
        }
    }
}
