using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using PulseDesk.App.ViewModels;
using PulseDesk.Core.Interfaces;
using PulseDesk.Core.Monitoring;
using PulseDesk.Core.Settings;
using PulseDesk.Core.Simulation;
using PulseDesk.Infrastructure;
using PulseDesk.Infrastructure.Logging;
using PulseDesk.Infrastructure.Network;
using PulseDesk.Infrastructure.Performance;
using PulseDesk.Infrastructure.Processes;
using PulseDesk.Infrastructure.Settings;
using PulseDesk.Infrastructure.Storage;
using PulseDesk.Infrastructure.SystemInfo;

namespace PulseDesk.App.Services;

/// <summary>
/// Composition root: the only place that decides which implementation backs each interface
/// (Windows providers normally, simulated ones in demo mode).
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private AppHost(ServiceProvider services, StartupOptions options)
    {
        _services = services;
        Options = options;
    }

    public IServiceProvider Services => _services;

    public StartupOptions Options { get; }

    public static AppHost Create(StartupOptions options, DispatcherQueue dispatcher)
    {
        var services = new ServiceCollection();
        var paths = new PulseDeskPaths();
        var levelSwitch = new LogLevelSwitch();

        services.AddSingleton(options);
        services.AddSingleton(dispatcher);
        services.AddSingleton(paths);
        services.AddSingleton(levelSwitch);
        services.AddSingleton<FileLoggerProvider>();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<FileLoggerProvider>());

        // Core
        services.AddSingleton<ISettingsStore, JsonFileSettingsStore>();
        services.AddSingleton(sp => new SettingsService(sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<ILogger<SettingsService>>()));
        services.AddSingleton(sp => new MetricsMonitor(
            sp.GetRequiredService<MetricProviders>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ILogger<MetricsMonitor>>()));
        services.AddSingleton<IMetricsMonitor>(sp => sp.GetRequiredService<MetricsMonitor>());
        services.AddSingleton<HealthService>();
        services.AddSingleton<IStartupRegistration, RunKeyStartupRegistration>();

        if (options.DemoMode)
        {
            AddSimulatedProviders(services);
        }
        else
        {
            AddWindowsProviders(services);
        }

        // UI services
        services.AddSingleton<UiMetricsHub>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<ExternalLauncher>();
        services.AddSingleton<ApplicationShell>();
        services.AddSingleton<MainWindow>();

        // View models live as long as the app, so pages keep their state (sorting, selection) between visits.
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<PerformanceViewModel>();
        services.AddSingleton<ProcessesViewModel>();
        services.AddSingleton<StorageViewModel>();
        services.AddSingleton<NetworkViewModel>();
        services.AddSingleton<SystemViewModel>();
        services.AddSingleton<SettingsViewModel>();

        return new AppHost(services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }), options);
    }

    /// <summary>Loads settings and applies the ones needed before any window is shown.</summary>
    public async Task InitializeAsync()
    {
        var settings = _services.GetRequiredService<SettingsService>();
        await settings.LoadAsync(CancellationToken.None);
        ApplyLogLevel(settings.Current.Diagnostics.LogLevel);
        settings.Changed += (_, e) => ApplyLogLevel(e.Current.Diagnostics.LogLevel);

        var logger = _services.GetRequiredService<ILogger<AppHost>>();
        logger.LogInformation(
            "PulseDesk {Version} starting ({Mode}, {Architecture}, .NET {Runtime}).",
            Core.AppInfo.InformationalVersion,
            Options.DemoMode ? "demo mode" : "live data",
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture,
            Environment.Version);
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();

    private void ApplyLogLevel(LogVerbosity verbosity) =>
        _services.GetRequiredService<LogLevelSwitch>().MinimumLevel = verbosity switch
        {
            LogVerbosity.Debug => LogLevel.Debug,
            LogVerbosity.Warning => LogLevel.Warning,
            LogVerbosity.Error => LogLevel.Error,
            _ => LogLevel.Information,
        };

    private static void AddWindowsProviders(ServiceCollection services)
    {
        services.AddSingleton<WindowsCpuMetricProvider>();
        services.AddSingleton<WindowsMemoryMetricProvider>();
        services.AddSingleton<WindowsGpuMetricProvider>();
        services.AddSingleton<WindowsStorageMetricProvider>();
        services.AddSingleton<WindowsDiskActivityMetricProvider>();
        services.AddSingleton<WindowsNetworkMetricProvider>();
        services.AddSingleton<WindowsProcessMetricProvider>();
        services.AddSingleton<WindowsSystemMetricProvider>();
        services.AddSingleton(sp => new MetricProviders(
            sp.GetRequiredService<WindowsCpuMetricProvider>(),
            sp.GetRequiredService<WindowsMemoryMetricProvider>(),
            sp.GetRequiredService<WindowsGpuMetricProvider>(),
            sp.GetRequiredService<WindowsStorageMetricProvider>(),
            sp.GetRequiredService<WindowsDiskActivityMetricProvider>(),
            sp.GetRequiredService<WindowsNetworkMetricProvider>(),
            sp.GetRequiredService<WindowsProcessMetricProvider>(),
            sp.GetRequiredService<WindowsSystemMetricProvider>()));
        services.AddSingleton<ISystemInfoProvider, WindowsSystemInfoProvider>();
        services.AddSingleton<IProcessManager, WindowsProcessManager>();
    }

    private static void AddSimulatedProviders(ServiceCollection services)
    {
        services.AddSingleton(_ => new SimulatedMachine());
        services.AddSingleton(sp => SimulatedProviders.Create(sp.GetRequiredService<SimulatedMachine>()));
        services.AddSingleton<ISystemInfoProvider, FakeSystemInfoProvider>();
        services.AddSingleton<IProcessManager, FakeProcessManager>();
    }
}
