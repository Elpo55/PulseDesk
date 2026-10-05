using PulseDesk.Core.Diagnosis;

namespace PulseDesk.Core.Interfaces;

/// <summary>
/// Produces diagnosis results from measurements. The default engine runs deterministic rules; other engines
/// (for example statistical models) can be added side by side: <see cref="DiagnosisService"/> merges the
/// results of every registered engine into one report.
/// </summary>
public interface IDiagnosisEngine
{
    /// <summary>Name shown in logs.</summary>
    string Name { get; }

    /// <summary>Evaluates the context. Must be fast, deterministic for a given context, and never throw.</summary>
    IReadOnlyList<DiagnosisResult> Evaluate(DiagnosisContext context);
}
