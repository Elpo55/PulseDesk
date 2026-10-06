using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Sysora.App.ViewModels;
using Sysora.Core.Alerts;
using Sysora.Core.Analysis;
using Sysora.Core.Changes;
using Sysora.Core.Diagnosis;
using Sysora.Core.Gaming;
using Sysora.Core.History;
using Sysora.Core.Interfaces;
using Sysora.Core.Monitoring;
using Sysora.Core.Settings;
using Sysora.Core.Simulation;
using Sysora.Infrastructure;
using Sysora.Infrastructure.Gaming;
using Sysora.Infrastructure.Logging;
using Sysora.Infrastructure.Network;
using Sysora.Infrastructure.Performance;
using Sysora.Infrastructure.Processes;
using Sysora.Infrastructure.Settings;
using Sysora.Infrastructure.Storage;
using Sysora.Infrastructure.SystemInfo;

namespace Sysora.App.Services;

/// <summary>
/// Composition root: the only place that decides which implementation backs each interface
/// (Windows providers normally, simulated ones in demo mode).
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly DataMigrationResult _migration;

    private AppHost(ServiceProvider services, StartupOptions options, DataMigrationResult migration)
    {
        _services = services;
        Options = options;
        _migration = migration;
    }

    public IServiceProvider Services => _services;

    public StartupOptions Options { get; }

    public static AppHost Create(StartupOptions options, DispatcherQueue dispatcher)
    {
        var services = new ServiceCollection();
        var paths = new SysoraPaths();
        var levelSwitch = new LogLevelSwitch();

        // Before anything opens a file in the data folder (the logger does, as soon as it is created).
        var migration = paths.MigrateLegacyData();

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
        services.AddSingleton<ISystemPowerEvents, Sysora.Infrastructure.Windows.WindowsPowerEvents>();
        services.AddSingleton<RunKeyStartupRegistration>();
        services.AddSingleton<IStartupRegistration>(sp => sp.GetRequiredService<RunKeyStartupRegistration>());

        // History and analysis. Demo mode keeps its history in memory: simulated data never reaches the
        // real history database.
        services.AddSingleton<IHistoryRepository>(sp => options.DemoMode
            ? HistoryRepository.InMemory(sp.GetRequiredService<ILogger<HistoryRepository>>())
            : new HistoryRepository(paths.HistoryDatabaseFile, sp.GetRequiredService<ILogger<HistoryRepository>>()));
        services.AddSingleton(sp => new PerformanceHistory(sp.GetRequiredService<IMetricsMonitor>(), sp.GetRequiredService<SettingsService>()));
        services.AddSingleton<IPerformanceHistory>(sp => sp.GetRequiredService<PerformanceHistory>());
        services.AddSingleton(sp => new HistoryRecorder(
            sp.GetRequiredService<IPerformanceHistory>(),
            sp.GetRequiredService<IHistoryRepository>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ILogger<HistoryRecorder>>()));
        services.AddSingleton(sp => new ProcessHistory(sp.GetRequiredService<IMetricsMonitor>()));
        services.AddSingleton<IAppImpactAnalyzer, AppImpactAnalyzer>();
        services.AddSingleton(sp => new AppImpactService(
            sp.GetRequiredService<ProcessHistory>(),
            sp.GetRequiredService<IHistoryRepository>(),
            sp.GetRequiredService<IAppImpactAnalyzer>(),
            sp.GetRequiredService<IMetricsMonitor>()));
        services.AddSingleton(sp => new BaselineService(sp.GetRequiredService<IHistoryRepository>(), sp.GetRequiredService<ILogger<BaselineService>>()));
        services.AddSingleton<IDiagnosisEngine>(sp => new DiagnosisEngine(sp.GetRequiredService<ILogger<DiagnosisEngine>>()));
        services.AddSingleton(sp => new DiagnosisService(
            sp.GetServices<IDiagnosisEngine>(),
            sp.GetRequiredService<IPerformanceHistory>(),
            sp.GetRequiredService<IMetricsMonitor>(),
            sp.GetRequiredService<BaselineService>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ILogger<DiagnosisService>>()));
        services.AddSingleton<IAlertEngine>(_ => new AlertEngine());
        services.AddSingleton(sp => new AlertService(
            sp.GetRequiredService<IAlertEngine>(),
            sp.GetRequiredService<IPerformanceHistory>(),
            sp.GetRequiredService<IMetricsMonitor>(),
            sp.GetRequiredService<BaselineService>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<IHistoryRepository>(),
            sp.GetRequiredService<ILogger<AlertService>>()));
        services.AddSingleton(sp => new ChangeDetectionService(
            sp.GetRequiredService<ISystemInventoryProvider>(),
            sp.GetRequiredService<ISystemInfoProvider>(),
            sp.GetRequiredService<IMetricsMonitor>(),
            sp.GetRequiredService<IHistoryRepository>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ILogger<ChangeDetectionService>>()));
        services.AddSingleton<IChangeDetectionService>(sp => sp.GetRequiredService<ChangeDetectionService>());
        services.AddSingleton(sp => new PowerTransitionService(
            sp.GetRequiredService<ISystemPowerEvents>(),
            sp.GetRequiredService<IMetricsMonitor>(),
            sp.GetRequiredService<IPerformanceHistory>(),
            sp.GetRequiredService<HistoryRecorder>(),
            sp.GetRequiredService<ProcessHistory>(),
            sp.GetRequiredService<ILogger<PowerTransitionService>>()));
        services.AddSingleton(sp => new UsageComparisonService(sp.GetRequiredService<IHistoryRepository>(), sp.GetRequiredService<IPerformanceHistory>()));
        services.AddSingleton(sp => new ReplayService(sp.GetRequiredService<IPerformanceHistory>(), sp.GetRequiredService<IHistoryRepository>()));
        services.AddSingleton(sp => new GameSessionService(
            sp.GetRequiredService<IMetricsMonitor>(),
            sp.GetRequiredService<IPerformanceHistory>(),
            sp.GetRequiredService<IGameLibrary>(),
            sp.GetRequiredService<IHistoryRepository>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ILogger<GameSessionService>>()));

        if (options.DemoMode)
        {
            AddSimulatedProviders(services);
        }
        else
        {
            AddWindowsProviders(services);
        }

        // UI services
        services.AddSingleton<AnalysisServices>();
        services.AddSingleton<InsightNavigator>();
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
        services.AddSingleton<AppImpactViewModel>();
        services.AddSingleton<DiagnosisViewModel>();
        services.AddSingleton<AlertsViewModel>();
        services.AddSingleton<ChangesViewModel>();
        services.AddSingleton<ReplayViewModel>();
        services.AddSingleton<GamingViewModel>();
        services.AddSingleton<PerformanceViewModel>();
        services.AddSingleton<ProcessesViewModel>();
        services.AddSingleton<StorageViewModel>();
        services.AddSingleton<NetworkViewModel>();
        services.AddSingleton<SystemViewModel>();
        services.AddSingleton<SettingsViewModel>();

        return new AppHost(services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }), options, migration);
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
            "Sysora {Version} starting ({Mode}, {Architecture}, .NET {Runtime}).",
            Core.AppInfo.InformationalVersion,
            Options.DemoMode ? "demo mode" : "live data",
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture,
            Environment.Version);

        if (_migration.Error is { } error)
        {
            logger.LogWarning(error, "The data of the previous version ({Source}) could not be moved; it is used where it is for now.", _migration.Source);
        }
        else if (_migration.WasMoved)
        {
            logger.LogInformation("Settings, history and logs moved from {Source}.", _migration.Source);
        }

        if (!Options.DemoMode)
        {
            _services.GetRequiredService<RunKeyStartupRegistration>().MigrateLegacyRegistration();
        }
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
        services.AddSingleton<ISystemInventoryProvider, WindowsSystemInventoryProvider>();
        services.AddSingleton<IGameLibrary, WindowsGameLibrary>();
        services.AddSingleton<IProcessManager, WindowsProcessManager>();
    }

    private static void AddSimulatedProviders(ServiceCollection services)
    {
        services.AddSingleton(_ => new SimulatedMachine());
        services.AddSingleton(sp => SimulatedProviders.Create(sp.GetRequiredService<SimulatedMachine>()));
        services.AddSingleton<ISystemInfoProvider, FakeSystemInfoProvider>();
        services.AddSingleton<ISystemInventoryProvider>(_ => new SimulatedInventoryProvider());
        services.AddSingleton<IGameLibrary, SimulatedGameLibrary>();
        services.AddSingleton<IProcessManager, FakeProcessManager>();
    }
}
