using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Sysora.App.Services.Tray;
using Sysora.App.ViewModels;
using Sysora.Core.Alerts;
using Sysora.Core.Formatting;
using Sysora.Core.Gaming;
using Sysora.Core.Interfaces;
using Sysora.Core.Models;
using Sysora.Core.Settings;
using Sysora.Core.Troubleshooting;
using Sysora.Localization;
using Windows.Graphics;

namespace Sysora.App.Services;

/// <summary>
/// Owns the main window's life cycle: initial visibility, close behavior (quit or minimize to the
/// notification area), the tray icon, notifications, and the switch to background mode while the window is hidden
/// (or, during a game, while it is not the active window).
/// </summary>
public sealed partial class ApplicationShell : IDisposable
{
    private static readonly TimeSpan TooltipInterval = TimeSpan.FromSeconds(5);

    private readonly MainWindow _window;
    private readonly IMetricsMonitor _monitor;
    private readonly SettingsService _settings;
    private readonly UiMetricsHub _hub;
    private readonly ThemeService _theme;
    private readonly NavigationService _navigation;
    private readonly DialogService _dialogs;
    private readonly AlertService _alerts;
    private readonly GameSessionService _games;
    private readonly GamingViewModel _gaming;
    private readonly TroubleshootingService _troubleshooting;
    private readonly PickerService _pickers;
    private readonly InsightNavigator _navigator;
    private readonly StartupOptions _options;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<ApplicationShell> _logger;
    private TrayIcon? _tray;
    private AppPage? _balloonPage;
    private Guid? _balloonSession;
    private Guid? _balloonInvestigation;
    private bool _windowActive = true;
    private bool _gameRunning;
    private bool _behindGame;
    private long _lastTooltip;
    private bool _allowClose;
    private bool _placementApplied;

    public ApplicationShell(
        MainWindow window,
        IMetricsMonitor monitor,
        SettingsService settings,
        UiMetricsHub hub,
        ThemeService theme,
        NavigationService navigation,
        DialogService dialogs,
        AlertService alerts,
        GameSessionService games,
        GamingViewModel gaming,
        TroubleshootingService troubleshooting,
        PickerService pickers,
        InsightNavigator navigator,
        StartupOptions options,
        DispatcherQueue dispatcher,
        ILogger<ApplicationShell> logger)
    {
        _window = window;
        _monitor = monitor;
        _settings = settings;
        _hub = hub;
        _theme = theme;
        _navigation = navigation;
        _dialogs = dialogs;
        _alerts = alerts;
        _games = games;
        _gaming = gaming;
        _troubleshooting = troubleshooting;
        _pickers = pickers;
        _navigator = navigator;
        _options = options;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>Raised when the user asks to exit (tray menu, or closing with "Quit application").</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Prepares the window and tray, starts monitoring and shows the window unless told to start hidden.</summary>
    public void Start()
    {
        _theme.Attach(_window);
        _dialogs.Attach(_window);
        _pickers.Attach(_window);
        _theme.Apply(_settings.Current.General.Theme);
        RestoreSize();

        _window.AppWindow.Closing += OnClosing;
        _window.AppWindow.Changed += OnWindowChanged;
        _window.Activated += OnWindowActivated;
        _settings.Changed += OnSettingsChanged;
        _monitor.StateChanged += OnMonitorStateChanged;
        _monitor.MetricsUpdated += OnMetricsUpdated;
        _alerts.AlertRaised += OnAlertRaised;
        _games.SessionsChanged += OnGamesChanged;
        _games.RecapReady += OnRecapReady;
        _troubleshooting.Completed += OnInvestigationCompleted;
        CreateTray();

        _ = _monitor.StartAsync(CancellationToken.None);

        var general = _settings.Current.General;
        var startHidden = _tray is not null && (_options.StartInTray || (_options.LaunchedAtSignIn && general.StartInTray));
        if (startHidden)
        {
            _logger.LogInformation("Started in the notification area.");
            UpdateActivity();
            return;
        }

        ShowMainWindow();
        if (_options.LaunchedAtSignIn && general.StartMinimized && _window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Minimize();
        }
    }

    /// <summary>Shows, restores and focuses the main window.</summary>
    public void ShowMainWindow()
    {
        var appWindow = _window.AppWindow;
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            if (!_placementApplied && _settings.Current.Window.IsMaximized)
            {
                presenter.Maximize();
            }
            else if (presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }
        }

        _placementApplied = true;
        appWindow.Show();
        _window.Activate();
        UpdateActivity();
    }

    /// <summary>Opens the window on a given page.</summary>
    public void ShowPage(AppPage page)
    {
        ShowMainWindow();
        _navigation.Navigate(page);
    }

    /// <summary>Saves the window placement and removes the tray icon. Called once, just before exiting.</summary>
    public void PrepareForExit()
    {
        _allowClose = true;
        SavePlacement();
        _monitor.MetricsUpdated -= OnMetricsUpdated;
        _monitor.StateChanged -= OnMonitorStateChanged;
        _alerts.AlertRaised -= OnAlertRaised;
        _games.SessionsChanged -= OnGamesChanged;
        _games.RecapReady -= OnRecapReady;
        _troubleshooting.Completed -= OnInvestigationCompleted;
        _window.Activated -= OnWindowActivated;
        _settings.Changed -= OnSettingsChanged;
        _tray?.Dispose();
        _tray = null;
    }

    public void Dispose() => _tray?.Dispose();

    private void CreateTray()
    {
        try
        {
            _tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Sysora.ico"));
            _tray.OpenRequested += (_, _) => ShowMainWindow();
            _tray.SettingsRequested += (_, _) => ShowPage(AppPage.Settings);
            _tray.PauseResumeRequested += (_, _) => TogglePause();
            _tray.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
            _tray.BalloonClicked += (_, _) => OnBalloonClicked();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Without a tray icon Sysora still works; closing the window then quits.
            _logger.LogWarning(ex, "The notification area icon could not be created.");
            _tray = null;
        }
    }

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

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        if (_settings.Current.General.CloseBehavior == CloseBehavior.MinimizeToTray && _tray is not null)
        {
            HideToTray();
        }
        else
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void HideToTray()
    {
        SavePlacement();
        _window.AppWindow.Hide();
        UpdateActivity();

        if (!_settings.Current.General.TrayHintShown)
        {
            _tray?.ShowInfo(UiStrings.Tray_StillRunning, UiStrings.Tray_StillRunningText);
            _settings.Update(s => s with { General = s.General with { TrayHintShown = true } });
        }
    }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidVisibilityChange || args.DidPresenterChange || args.DidSizeChange || args.DidPositionChange)
        {
            UpdateActivity();
        }
    }

    /// <summary>
    /// Stops UI updates and slows on-screen-only metrics while the window is hidden or minimized, and while a game runs
    /// with another window (the game) in front: Sysora then takes as little as possible from the game.
    /// </summary>
    private void UpdateActivity()
    {
        var appWindow = _window.AppWindow;
        var visible = appWindow.IsVisible
            && appWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        var behindGame = _gameRunning && !_windowActive && _settings.Current.Gaming.ReduceMonitoringDuringGames;
        var active = visible && !behindGame;
        if (visible && behindGame != _behindGame)
        {
            _logger.LogDebug(behindGame ? "A game is in front: window updates paused, detailed metrics sampled less often." : "Window updates resumed.");
        }

        _behindGame = visible && behindGame;
        _hub.IsUiVisible = active;
        _monitor.SetActivity(active ? MonitoringActivity.Foreground : MonitoringActivity.Background);
    }

    private void OnWindowActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        _windowActive = args.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated;
        UpdateActivity();
    }

    private void OnGamesChanged(object? sender, EventArgs e) =>
        _dispatcher.TryEnqueue(() =>
        {
            var running = _games.IsGameRunning;
            if (running != _gameRunning)
            {
                _gameRunning = running;
                UpdateActivity();
            }
        });

    /// <summary>Windows notification when a game recap is ready; clicking it opens the recap.</summary>
    private void OnRecapReady(object? sender, GameRecap recap)
    {
        if (!_settings.Current.Gaming.NotifyRecap)
        {
            return;
        }

        var session = recap.Session;
        _dispatcher.TryEnqueue(() =>
        {
            _balloonPage = AppPage.Gaming;
            _balloonSession = session.Id;
            _tray?.ShowInfo(Text.Format(UiStrings.Tray_GameRecap, session.Name), Text.Format(UiStrings.Tray_GameRecapText, MetricFormatter.DurationPrecise(session.Duration), recap.Headline));
        });
    }

    /// <summary>Windows notification when an investigation ends while Sysora is not in front; clicking it opens the report.</summary>
    private void OnInvestigationCompleted(object? sender, TroubleshootingReport report)
    {
        if (report.EndReason == TroubleshootingEndReason.SysoraClosed)
        {
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            if (_window.AppWindow.IsVisible && _windowActive)
            {
                return;
            }

            _balloonPage = AppPage.Troubleshooting;
            _balloonSession = null;
            _balloonInvestigation = report.Id;
            var headline = report.Headline;
            var colon = headline.IndexOf(':', StringComparison.Ordinal);
            _tray?.ShowInfo(UiStrings.Tray_InvestigationComplete, Text.Format(UiStrings.Tray_InvestigationText, colon > 0 ? headline[(colon + 1)..].Trim() : headline));
        });
    }

    private void OnBalloonClicked()
    {
        if (_balloonPage is not { } page)
        {
            ShowMainWindow();
            return;
        }

        if (page == AppPage.Troubleshooting && _balloonInvestigation is { } investigation)
        {
            ShowMainWindow();
            _navigator.OpenInvestigation(investigation);
            return;
        }

        if (page == AppPage.Gaming && _balloonSession is { } id)
        {
            _gaming.RequestSelection(id);
        }

        ShowPage(page);
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.General.Theme != e.Current.General.Theme)
        {
            _dispatcher.TryEnqueue(() => _theme.Apply(e.Current.General.Theme));
        }

        if (e.Previous.Gaming.ReduceMonitoringDuringGames != e.Current.Gaming.ReduceMonitoringDuringGames)
        {
            _dispatcher.TryEnqueue(UpdateActivity);
        }
    }

    private void OnMonitorStateChanged(object? sender, EventArgs e) =>
        _dispatcher.TryEnqueue(() =>
        {
            _tray?.SetPaused(_monitor.IsPaused);
            UpdateTooltip(_monitor.Current);
        });

    private void OnMetricsUpdated(object? sender, SystemMetricsUpdatedEventArgs e)
    {
        // Runs on the monitoring thread: only hop to the UI thread every few seconds.
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastTooltip) < TooltipInterval.TotalMilliseconds)
        {
            return;
        }

        Interlocked.Exchange(ref _lastTooltip, now);
        var snapshot = e.Snapshot;
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => UpdateTooltip(snapshot));
    }

    /// <summary>Optional Windows notification for new warnings (off by default; alerts are already rate-limited).</summary>
    private void OnAlertRaised(object? sender, Alert alert)
    {
        if (!_settings.Current.SmartAlerts.ShowNotifications || alert.Severity < AlertSeverity.Warning)
        {
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            _balloonPage = AppPage.Alerts;
            _balloonSession = null;
            _tray?.ShowInfo(alert.Title, Text.Format(UiStrings.Tray_AlertText, alert.Value));
        });
    }

    private void UpdateTooltip(SystemSnapshot snapshot)
    {
        if (_tray is null)
        {
            return;
        }

        var cpu = snapshot.Cpu is { } c ? MetricFormatter.Percent(c.UsagePercent) : MetricFormatter.Pending;
        var memory = snapshot.Memory is { } m ? MetricFormatter.Percent(m.UsedPercent) : MetricFormatter.Pending;
        var state = _monitor.IsPaused ? " " + UiStrings.Tray_Paused : string.Empty;
        _tray.SetTooltip($"Sysora{state}\n" + Text.Format(UiStrings.Tray_Tooltip, cpu, memory));
    }

    private void RestoreSize()
    {
        // Sizes are stored in device-independent pixels so they survive display scaling changes.
        var placement = _settings.Current.Window;
        var appWindow = _window.AppWindow;
        var scale = Scale();
        var workArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(placement.Width * scale), workArea.Width);
        var height = Math.Min((int)(placement.Height * scale), workArea.Height);
        appWindow.MoveAndResize(new RectInt32(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2),
            width,
            height));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(720 * scale);
            presenter.PreferredMinimumHeight = (int)(520 * scale);
        }
    }

    private void SavePlacement()
    {
        var appWindow = _window.AppWindow;
        var state = (appWindow.Presenter as OverlappedPresenter)?.State;
        if (state == OverlappedPresenterState.Minimized)
        {
            return;
        }

        var maximized = state == OverlappedPresenterState.Maximized;
        var scale = Scale();
        var width = (int)(appWindow.Size.Width / scale);
        var height = (int)(appWindow.Size.Height / scale);
        _settings.Update(s => s with
        {
            Window = maximized
                ? s.Window with { IsMaximized = true }
                : new WindowSettings { Width = width, Height = height, IsMaximized = false },
        });
    }

    /// <summary>Display scale of the window (1.0 at 96 DPI).</summary>
    private double Scale()
    {
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window));
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);
}
