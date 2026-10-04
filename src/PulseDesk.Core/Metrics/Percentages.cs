namespace PulseDesk.Core.Metrics;

/// <summary>Percentage helpers shared by models and providers.</summary>
public static class Percentages
{
    /// <summary>Returns <paramref name="part"/> as a percentage of <paramref name="total"/>, clamped to 0–100.</summary>
    /// <remarks>Returns 0 when <paramref name="total"/> is 0, so a missing capacity never yields NaN or infinity.</remarks>
    public static double Of(ulong part, ulong total) =>
        total == 0 ? 0 : Clamp((double)part / total * 100.0);

    /// <summary>Returns <paramref name="part"/> as a percentage of <paramref name="total"/>, clamped to 0–100.</summary>
    public static double Of(double part, double total) =>
        total <= 0 || double.IsNaN(part) || double.IsNaN(total) ? 0 : Clamp(part / total * 100.0);

    /// <summary>Clamps a percentage to 0–100 and maps NaN to 0.</summary>
    public static double Clamp(double percent) =>
        double.IsNaN(percent) ? 0 : Math.Clamp(percent, 0, 100);
}
