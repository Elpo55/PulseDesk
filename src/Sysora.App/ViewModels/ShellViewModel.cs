using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Sysora.App.Services;
using Sysora.Core.Alerts;
using Sysora.Core.Gaming;
using Sysora.Core.Interfaces;
using Sysora.Core.Troubleshooting;

namespace Sysora.App.ViewModels;

/// <summary>State shown in the title bar and navigation: demo mode, paused monitoring, new alerts, game running.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private const string PauseGlyphValue = "";
    private const string ResumeGlyphValue = "";

    private static readonly TimeSpan MessageDuration = TimeSpan.FromSeconds(10);

    private readonly IMetricsMonitor _monitor;
    private readonly ExternalLauncher _launcher;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _messageTimer;

    public ShellViewModel(IMetricsMonitor monitor, AlertService alerts, GameSessionService games, TroubleshootingService troubleshooting, ExternalLauncher launcher, StartupOptions options, DispatcherQueue dispatcher)
    {
        _monitor = monitor;
        _launcher = launcher;
        _dispatcher = dispatcher;
        MessageText = string.Empty;
        _messageTimer = dispatcher.CreateTimer();
        _messageTimer.Interval = MessageDuration;
        _messageTimer.IsRepeating = false;
        _messageTimer.Tick += (_, _) => IsMessageOpen = false;
        troubleshooting.StatusChanged += (_, status) => dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => IsInvestigating = status.IsRunning);
        IsDemoMode = options.DemoMode;
        _monitor.StateChanged += (_, _) => dispatcher.TryEnqueue(() => IsPaused = _monitor.IsPaused);
        alerts.Changed += (_, _) => dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => NewAlertCount = alerts.NewCount);
        games.SessionsChanged += (_, _) => dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => IsGameRunning = games.IsGameRunning);
    }

    /// <summary>True when Sysora shows simulated data (<c>--demo</c>).</summary>
    public bool IsDemoMode { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseGlyph), nameof(PauseLabel))]
    public partial bool IsPaused { get; set; }

    /// <summary>Alerts not looked at yet (badge on the Alerts menu item).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewAlerts))]
    public partial int NewAlertCount { get; set; }

    public bool HasNewAlerts => NewAlertCount > 0;

    /// <summary>A game session is being recorded (dot on the Gaming menu item).</summary>
    [ObservableProperty]
    public partial bool IsGameRunning { get; set; }

    /// <summary>A troubleshooting investigation is running (dot on the Troubleshooting menu item).</summary>
    [ObservableProperty]
    public partial bool IsInvestigating { get; set; }

    /// <summary>A short message shown above the pages (a report was saved…).</summary>
    [ObservableProperty]
    public partial string MessageText { get; set; }

    [ObservableProperty]
    public partial bool IsMessageOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageSeverity))]
    public partial bool IsMessageError { get; set; }

    public Microsoft.UI.Xaml.Controls.InfoBarSeverity MessageSeverity =>
        IsMessageError ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;

    [ObservableProperty]
    public partial bool HasMessageFolder { get; set; }

    private string? MessageFolder { get; set; }

    public string PauseGlyph => IsPaused ? ResumeGlyphValue : PauseGlyphValue;

    /// <summary>Shows a message above the pages for a few seconds, with an "Open folder" button when a folder is given.</summary>
    public void ShowMessage(string text, string? folder, bool isError = false) =>
        _dispatcher.TryEnqueue(() =>
        {
            MessageText = text;
            MessageFolder = folder;
            HasMessageFolder = folder is not null;
            IsMessageError = isError;
            IsMessageOpen = true;
            _messageTimer.Stop();
            _messageTimer.Start();
        });

    [RelayCommand]
    private void OpenMessageFolder()
    {
        if (MessageFolder is { } folder)
        {
            _launcher.OpenFolder(folder);
        }
    }

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
