using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sysora.Core.Diagnosis.Rules;
using Sysora.Core.Interfaces;

namespace Sysora.Core.Diagnosis;

/// <summary>
/// Default <see cref="IDiagnosisEngine"/>: runs deterministic, explainable rules. A rule that fails is skipped
/// (and logged) so one bug never hides the other results.
/// </summary>
public sealed class DiagnosisEngine(IEnumerable<DiagnosisRule> rules, ILogger<DiagnosisEngine>? logger = null) : IDiagnosisEngine
{
    private readonly DiagnosisRule[] _rules = rules.ToArray();
    private readonly ILogger _logger = logger ?? NullLogger<DiagnosisEngine>.Instance;

    /// <summary>An engine with every built-in rule.</summary>
    public DiagnosisEngine(ILogger<DiagnosisEngine>? logger = null)
        : this(CreateDefaultRules(), logger)
    {
    }

    public string Name => "Rules";

    /// <summary>The built-in rules, in display order within a severity.</summary>
    public static IReadOnlyList<DiagnosisRule> CreateDefaultRules() =>
    [
        new CpuLoadRule(),
        new CpuHungryAppRule(),
        new MemoryPressureRule(),
        new MemoryGrowthRule(),
        new DiskActivityRule(),
        new DiskSpaceRule(),
        new UnusualActivityRule(),
        new GpuLoadRule(),
        new ConnectivityRule(),
        new UptimeRule(),
    ];

    public IReadOnlyList<DiagnosisResult> Evaluate(DiagnosisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var results = new List<DiagnosisResult>();
        foreach (var rule in _rules)
        {
            try
            {
                results.AddRange(rule.Evaluate(context));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "Diagnosis rule {Rule} failed and was skipped.", rule.Id);
            }
        }

        return results;
    }
}

/// <summary>Builds the report shown to the user from the results of every engine (pure, deterministic).</summary>
public static class DiagnosisReportBuilder
{
    public static DiagnosisReport Build(DiagnosisContext context, IEnumerable<DiagnosisResult> results)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(results);

        if (context.Latest is null)
        {
            return DiagnosisReport.Empty with { Timestamp = context.Now };
        }

        var ordered = results
            .OrderByDescending(r => r.Severity)
            .ThenByDescending(r => r.Confidence)
            .ThenBy(r => r.Category)
            .ToArray();
        var critical = ordered.Count(r => r.Severity == DiagnosisSeverity.Critical);
        var warnings = ordered.Count(r => r.Severity == DiagnosisSeverity.Warning);
        var infos = ordered.Count(r => r.Severity == DiagnosisSeverity.Info);
        var problems = critical + warnings;

        var state = critical > 0 || warnings >= 2 ? PcHealthState.Problem
            : warnings == 1 ? PcHealthState.Attention
            : PcHealthState.Healthy;

        var headline = problems switch
        {
            0 when infos > 0 => infos == 1 ? "No problem detected · 1 point to watch" : $"No problem detected · {infos} points to watch",
            0 => "No problem detected",
            1 => "1 problem detected",
            _ => $"{problems} problems detected",
        };

        string summary;
        if (problems > 0)
        {
            var first = ordered[0];
            summary = $"Most likely cause: {first.Description}";
            if (problems > 1)
            {
                summary += $" Also: {ordered[1].Title.ToLowerInvariant()}.";
            }
        }
        else if (infos > 0)
        {
            summary = $"Nothing is saturated right now. Worth knowing: {ordered.First(r => r.Severity == DiagnosisSeverity.Info).Description}";
        }
        else
        {
            summary = "Nothing is saturated right now: processor, memory and disks have spare capacity. If the PC still felt slow a moment ago, Replay shows what happened in the last minutes.";
        }

        return new DiagnosisReport
        {
            Timestamp = context.Now,
            State = state,
            Headline = headline,
            Summary = summary,
            Results = ordered,
            From = context.Recent[0].Timestamp,
            To = context.Latest.Timestamp,
            SampleCount = context.Recent.Count,
            BaselineDescription = context.Baseline.Description,
            NotAnalyzed = NotAnalyzed(context),
        };
    }

    /// <summary>Metrics the diagnosis could not use on this PC, and why.</summary>
    private static IReadOnlyList<string> NotAnalyzed(DiagnosisContext context)
    {
        var snapshot = context.Snapshot;
        var latest = context.Latest!;
        var missing = new List<string>();
        if (latest.GpuPercent is null)
        {
            missing.Add(snapshot.IsUnavailable(Models.MetricKind.Gpu) || snapshot.Gpus is not null
                ? "GPU usage: not available on this PC"
                : "GPU usage: monitoring turned off in Settings");
        }

        if (latest.DiskActivePercent is null)
        {
            missing.Add("Disk activity: not available on this PC");
        }

        if (snapshot.Network is null)
        {
            missing.Add(snapshot.IsUnavailable(Models.MetricKind.Network)
                ? "Network: not available on this PC"
                : "Network: monitoring turned off in Settings");
        }

        if (snapshot.Cpu is { TemperatureCelsius: null })
        {
            missing.Add("CPU temperature: not available (Windows has no documented way to read it without a kernel driver)");
        }

        missing.Add("Per-application network usage: not available (requires administrator-level event tracing)");
        return missing;
    }
}
