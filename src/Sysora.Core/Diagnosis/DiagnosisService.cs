using Microsoft.Extensions.Logging;
using Sysora.Core.Analysis;
using Sysora.Core.Interfaces;
using Sysora.Core.Settings;

namespace Sysora.Core.Diagnosis;

/// <summary>
/// Answers "why is my PC slow?": builds a <see cref="DiagnosisContext"/> from the live measurements, the recent
/// history and the usual-behavior baseline, runs every registered <see cref="IDiagnosisEngine"/> and merges
/// their results into a <see cref="DiagnosisReport"/>.
/// </summary>
public sealed class DiagnosisService(
    IEnumerable<IDiagnosisEngine> engines,
    IPerformanceHistory history,
    IMetricsMonitor monitor,
    BaselineService baseline,
    SettingsService settings,
    ILogger<DiagnosisService> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>Recent history examined by the rules.</summary>
    public static readonly TimeSpan AnalysisWindow = TimeSpan.FromMinutes(15);

    private readonly IDiagnosisEngine[] _engines = engines.ToArray();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private DiagnosisReport _latest = DiagnosisReport.Empty;

    /// <summary>Raised (on a background thread) after each diagnosis.</summary>
    public event EventHandler<DiagnosisReport>? ReportUpdated;

    /// <summary>The most recent report.</summary>
    public DiagnosisReport Latest => Volatile.Read(ref _latest);

    /// <summary>Runs a diagnosis on a background thread.</summary>
    public Task<DiagnosisReport> RunAsync(CancellationToken cancellationToken) =>
        Task.Run(Diagnose, cancellationToken);

    /// <summary>Runs a diagnosis on the calling thread.</summary>
    public DiagnosisReport Diagnose()
    {
        var context = new DiagnosisContext(
            _time.GetUtcNow(),
            monitor.Current,
            history.GetRecent(AnalysisWindow),
            baseline.Current,
            settings.Current.Alerts);

        var results = new List<DiagnosisResult>();
        foreach (var engine in _engines)
        {
            try
            {
                results.AddRange(engine.Evaluate(context));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogError(ex, "Diagnosis engine {Engine} failed.", engine.Name);
            }
        }

        var report = DiagnosisReportBuilder.Build(context, results);
        Volatile.Write(ref _latest, report);
        ReportUpdated?.Invoke(this, report);
        return report;
    }
}
