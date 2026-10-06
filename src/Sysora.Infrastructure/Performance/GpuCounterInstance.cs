namespace Sysora.Infrastructure.Performance;

/// <summary>
/// Parses instance names of the "GPU Engine" and "GPU Adapter Memory" performance counters, e.g.
/// <c>pid_1234_luid_0x00000000_0x0000C3B5_phys_0_eng_3_engtype_VideoDecode</c> or
/// <c>luid_0x00000000_0x0000C3B5_phys_0</c>.
/// </summary>
internal static class GpuCounterInstance
{
    private const string LuidPrefix = "luid_";
    private const string PhysMarker = "_phys_";
    private const string EngineMarker = "_eng_";
    private const string EngineTypeMarker = "_engtype_";

    private const string PidPrefix = "pid_";

    /// <summary>Extracts the process ID of a "GPU Engine" instance ("pid_1234_luid_…").</summary>
    public static bool TryGetProcessId(ReadOnlySpan<char> instance, out int processId)
    {
        processId = 0;
        if (!instance.StartsWith(PidPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var digits = instance[PidPrefix.Length..];
        var end = digits.IndexOf('_');
        return end > 0 && int.TryParse(digits[..end], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out processId);
    }

    /// <summary>Extracts the adapter LUID part ("luid_0x..._0x...") of any GPU counter instance.</summary>
    public static bool TryGetAdapterId(ReadOnlySpan<char> instance, out ReadOnlySpan<char> adapterId)
    {
        adapterId = default;
        var start = instance.IndexOf(LuidPrefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        var rest = instance[start..];
        var end = rest.IndexOf(PhysMarker, StringComparison.Ordinal);
        adapterId = end < 0 ? rest : rest[..end];
        return adapterId.Length > LuidPrefix.Length;
    }

    /// <summary>
    /// Parses a "GPU Engine" instance into its adapter, engine key (physical adapter + engine index)
    /// and engine type label.
    /// </summary>
    public static bool TryParseEngine(
        ReadOnlySpan<char> instance,
        out ReadOnlySpan<char> adapterId,
        out ReadOnlySpan<char> engineKey,
        out ReadOnlySpan<char> engineType)
    {
        engineKey = engineType = default;
        if (!TryGetAdapterId(instance, out adapterId))
        {
            return false;
        }

        var phys = instance.IndexOf(PhysMarker, StringComparison.Ordinal);
        var type = instance.IndexOf(EngineTypeMarker, StringComparison.Ordinal);
        if (phys < 0 || type < 0 || type < phys || instance[phys..type].IndexOf(EngineMarker, StringComparison.Ordinal) < 0)
        {
            return false;
        }

        engineKey = instance[(phys + 1)..type];  // "phys_0_eng_3"
        engineType = instance[(type + EngineTypeMarker.Length)..];
        return engineType.Length > 0;
    }
}
