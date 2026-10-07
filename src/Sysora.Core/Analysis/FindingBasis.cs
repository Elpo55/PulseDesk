namespace Sysora.Core.Analysis;

/// <summary>
/// What a statement rests on. Every analysis (why now, troubleshooting, recurring problems, reports) labels its
/// statements so the user can tell a measurement from an interpretation, and both from what Sysora cannot know.
/// </summary>
public enum FindingBasis
{
    /// <summary>Measured directly by Sysora or read from Windows.</summary>
    Observed,

    /// <summary>Deduced from measurements (a correlation, a coincidence in time): evidence suggests it, it is not proven.</summary>
    Inferred,

    /// <summary>Not observable with the data available.</summary>
    Unknown,
}

/// <summary>One statement of an analysis, with what it rests on.</summary>
/// <param name="Label">Short label, e.g. "Started", "Likely contributor".</param>
/// <param name="Text">The statement.</param>
/// <param name="Basis">Observed, inferred or unknown.</param>
public sealed record Finding(string Label, string Text, FindingBasis Basis)
{
    /// <summary>Confidence of an inferred statement.</summary>
    public ConfidenceLevel? Confidence { get; init; }

    /// <summary>Text shown next to the basis, e.g. "Observed", "Inferred · medium confidence", "Unknown".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string BasisText => Basis switch
    {
        FindingBasis.Observed => "Observed",
        FindingBasis.Inferred => Confidence is { } confidence ? $"Inferred · {confidence.ToString().ToLowerInvariant()} confidence" : "Inferred",
        _ => "Unknown",
    };
}
