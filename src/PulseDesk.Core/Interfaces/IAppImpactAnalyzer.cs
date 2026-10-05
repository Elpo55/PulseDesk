using PulseDesk.Core.Analysis;

namespace PulseDesk.Core.Interfaces;

/// <summary>
/// Turns measured application usage into an explainable impact score. Pure computation: no I/O, no state.
/// </summary>
public interface IAppImpactAnalyzer
{
    /// <summary>Scores one application.</summary>
    AppImpactResult Analyze(AppUsageStatistics usage, AppImpactContext context);

    /// <summary>Scores applications and ranks them, highest impact first.</summary>
    IReadOnlyList<AppImpactResult> Rank(IEnumerable<AppUsageStatistics> usage, AppImpactContext context);
}
