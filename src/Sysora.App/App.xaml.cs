using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Sysora.App.Services;
using Sysora.App.ViewModels;
using Sysora.Core.Interfaces;
using Sysora.Core.Settings;

namespace Sysora.App;

/// <summary>
/// Application object: builds the services, starts the shell and runs the orderly shutdown
/// (stop monitoring, save settings, release native resources).
/// </summary>
public partial class App : Application
{
    private readonly StartupOptions _options;
    private AppHost? _host;
    private ApplicationShell? _shell;
    private DispatcherQueue? _dispatcher;
    private ILogger<App>? _logger;
    private bool _exiting;
    private bool _restart;
    private string? _restartLanguage;

    public App(StartupOptions options)
    {
        _options = options;
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    /// <summary>Services of the running application (used by pages to obtain their view model).</summary>
    public static IServiceProvider Services =>
        (Current as App)?._host?.Services ?? throw new InvalidOperationException("The application is not initialized.");

    /// <summary>Called by another Sysora instance that was just launched: show this one instead.</summary>
    internal void OnActivatedByAnotherInstance() =>
        _dispatcher?.TryEnqueue(() => _shell?.ShowMainWindow());

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _host = AppHost.Create(_options, _dispatcher);
        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        try
        {
            await _host.InitializeAsync();
            await _host.Services.GetRequiredService<AnalysisServices>().StartAsync(CancellationToken.None);
            _shell = _host.Services.GetRequiredService<ApplicationShell>();
            _shell.ExitRequested += async (_, _) => await ExitAsync();
            _host.Services.GetRequiredService<SettingsViewModel>().RestartRequested = () => _dispatcher.TryEnqueue(async () =>
            {
                _restart = true;
                _restartLanguage = _host.Services.GetRequiredService<SettingsService>().Current.General.Language;
                await ExitAsync();
            });
            _shell.Start();
        }
        catch (Exception ex)
        {
            // Without a window the process would linger invisibly: log and exit instead.
            _logger.LogCritical(ex, "Sysora could not start.");
            await ExitAsync();
        }
    }

    /// <summary>Stops everything cleanly, then closes the application.</summary>
    private async Task ExitAsync()
    {
        if (_exiting || _host is null)
        {
            return;
        }

        _exiting = true;
        _logger?.LogInformation("Exiting.");
        try
        {
            _shell?.PrepareForExit();
            await _host.Services.GetRequiredService<IMetricsMonitor>().StopAsync();
            await _host.Services.GetRequiredService<AnalysisServices>().StopAsync();
            await _host.Services.GetRequiredService<SettingsService>().FlushAsync();
            await _host.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during shutdown.");
        }

        if (_restart)
        {
            StartNewInstance();
        }

        Exit();
    }

    /// <summary>
    /// Starts Sysora again once this instance has saved everything (used to apply a new language). The single-instance
    /// key is released first so the new process does not hand itself back to this one.
    /// </summary>
    private void StartNewInstance()
    {
        try
        {
            AppInstance.GetCurrent().UnregisterKey();
            if (Environment.ProcessPath is { } path)
            {
                var start = new ProcessStartInfo(path) { UseShellExecute = false };
                if (_options.DemoMode)
                {
                    // Demo mode never saves settings: the new instance gets the chosen language on its command line.
                    start.ArgumentList.Add("--demo");
                    start.ArgumentList.Add("--language=" + _restartLanguage);
                }

                start.ArgumentList.Add("--page=Settings");
                Process.Start(start)?.Dispose();
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _logger?.LogError(ex, "Sysora could not be started again.");
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // A display problem must not take the whole dashboard down: log it and keep running.
        _logger?.LogError(e.Exception, "Unhandled UI exception.");
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e) =>
        _logger?.LogCritical(e.ExceptionObject as Exception, "Fatal unhandled exception.");
}
