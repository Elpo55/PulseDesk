using System.Globalization;

namespace Sysora.Core.Formatting;

/// <summary>
/// Turns metric values into display text. Missing values always render as
/// <see cref="NotAvailable"/>, never as a made-up number such as 0.
/// </summary>
/// <remarks>Numbers use the current culture's decimal separator unless a format provider is given.</remarks>
public static class MetricFormatter
{
    /// <summary>Text shown for a metric that this machine does not expose.</summary>
    public const string NotAvailable = "Not available";

    /// <summary>Text shown while a metric has not been collected yet.</summary>
    public const string Pending = "—";

    /// <summary>Formats a byte count, e.g. "512 B", "9.8 GB", "580 MB".</summary>
    public static string Bytes(double bytes, IFormatProvider? provider = null)
    {
        var (value, unit) = ByteSize.Scale(bytes);
        return unit == "B"
            ? string.Create(Culture(provider), $"{value:0} B")
            : string.Create(Culture(provider), $"{Round(value)} {unit}");
    }

    /// <inheritdoc cref="Bytes(double, IFormatProvider?)"/>
    public static string Bytes(ulong bytes, IFormatProvider? provider = null) => Bytes((double)bytes, provider);

    /// <inheritdoc cref="Bytes(double, IFormatProvider?)"/>
    public static string Bytes(ulong? bytes, IFormatProvider? provider = null) =>
        bytes is { } b ? Bytes((double)b, provider) : NotAvailable;

    /// <summary>Formats a byte rate, e.g. "1.2 MB/s".</summary>
    public static string BytesPerSecond(double? bytesPerSecond, IFormatProvider? provider = null) =>
        bytesPerSecond is { } rate ? $"{Bytes(Math.Max(rate, 0), provider)}/s" : NotAvailable;

    /// <summary>Formats a bit rate with decimal (SI) units, as network speeds are expressed: "82.4 Mbps".</summary>
    public static string BitsPerSecond(double? bitsPerSecond, IFormatProvider? provider = null)
    {
        if (bitsPerSecond is not { } bits)
        {
            return NotAvailable;
        }

        bits = Math.Max(bits, 0);
        var culture = Culture(provider);
        return bits switch
        {
            >= 1e9 => string.Create(culture, $"{Round(bits / 1e9)} Gbps"),
            >= 1e6 => string.Create(culture, $"{Round(bits / 1e6)} Mbps"),
            >= 1e3 => string.Create(culture, $"{Round(bits / 1e3)} Kbps"),
            _ => string.Create(culture, $"{bits:0} bps"),
        };
    }

    /// <summary>Formats a percentage, e.g. "17%" or "38.7%".</summary>
    public static string Percent(double? percent, int decimals = 0, IFormatProvider? provider = null) =>
        percent is { } p && double.IsFinite(p)
            ? Math.Round(p, decimals).ToString("F" + decimals, Culture(provider)) + "%"
            : NotAvailable;

    /// <summary>Formats a frequency in gigahertz, e.g. "4.21 GHz".</summary>
    public static string FrequencyGHz(double? gigahertz, IFormatProvider? provider = null) =>
        gigahertz is { } ghz && ghz > 0
            ? string.Create(Culture(provider), $"{ghz:0.00} GHz")
            : NotAvailable;

    /// <summary>Formats a temperature, e.g. "54 °C".</summary>
    public static string Temperature(double? celsius, IFormatProvider? provider = null) =>
        celsius is { } c && double.IsFinite(c)
            ? string.Create(Culture(provider), $"{c:0} °C")
            : NotAvailable;

    /// <summary>Formats a duration compactly, e.g. "3d 14h 21m", "14h 21m", "21m", "45s".</summary>
    public static string DurationCompact(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{duration.Hours}h {duration.Minutes}m";
        }

        return duration.TotalMinutes >= 1 ? $"{duration.Minutes}m" : $"{duration.Seconds}s";
    }

    /// <summary>Formats a duration with two units, e.g. "45s", "3m 24s", "1h 05m", "2d 3h".</summary>
    public static string DurationPrecise(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalDays >= 1 ? $"{(int)duration.TotalDays}d {duration.Hours}h"
            : duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes:00}m"
            : duration.TotalMinutes >= 1 ? $"{duration.Minutes}m {duration.Seconds:00}s"
            : $"{(int)Math.Round(duration.TotalSeconds)}s";
    }

    /// <summary>Formats a duration in words, e.g. "3 days, 14 hours, 21 minutes".</summary>
    public static string DurationLong(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        var parts = new List<string>(3);
        var days = (int)duration.TotalDays;
        if (days > 0)
        {
            parts.Add(Plural(days, "day"));
        }

        if (days > 0 || duration.Hours > 0)
        {
            parts.Add(Plural(duration.Hours, "hour"));
        }

        parts.Add(Plural(duration.Minutes, "minute"));
        return string.Join(", ", parts);
    }

    /// <summary>Formats a number of items with a unit, e.g. "1 core", "16 threads", "2 processes".</summary>
    /// <param name="count">Number of items.</param>
    /// <param name="singular">Singular unit.</param>
    /// <param name="plural">Plural unit, when it is not the singular followed by "s".</param>
    public static string Plural(int count, string singular, string? plural = null) =>
        count == 1 ? $"1 {singular}" : $"{count} {plural ?? singular + "s"}";

    // Three significant digits at most: 9.8, 12.4, 580 — matching what Task Manager shows.
    private static double Round(double value) =>
        Math.Abs(value) >= 100 ? Math.Round(value) : Math.Round(value, 1);

    private static IFormatProvider Culture(IFormatProvider? provider) =>
        provider ?? CultureInfo.CurrentCulture;
}
