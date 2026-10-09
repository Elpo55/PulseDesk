using System.Globalization;
using Sysora.Localization;

namespace Sysora.Core.Formatting;

/// <summary>
/// Turns metric values into display text. Missing values always render as
/// <see cref="NotAvailable"/>, never as a made-up number such as 0.
/// </summary>
/// <remarks>Numbers use the current culture's decimal separator unless a format provider is given.</remarks>
public static class MetricFormatter
{
    /// <summary>Text shown for a metric that this machine does not expose.</summary>
    public static string NotAvailable => Strings.Common_NotAvailable;

    /// <summary>Text shown while a metric has not been collected yet.</summary>
    public const string Pending = "—";

    /// <summary>Formats a byte count, e.g. "512 B", "9.8 GB", "580 MB" (French: "9,8 Go").</summary>
    public static string Bytes(double bytes, IFormatProvider? provider = null)
    {
        var (value, unit) = ByteSize.Scale(bytes);
        return unit == ByteUnit.Byte
            ? string.Create(Culture(provider), $"{value:0} {ByteSize.Label(unit)}")
            : string.Create(Culture(provider), $"{Round(value)} {ByteSize.Label(unit)}");
    }

    /// <inheritdoc cref="Bytes(double, IFormatProvider?)"/>
    public static string Bytes(ulong bytes, IFormatProvider? provider = null) => Bytes((double)bytes, provider);

    /// <inheritdoc cref="Bytes(double, IFormatProvider?)"/>
    public static string Bytes(ulong? bytes, IFormatProvider? provider = null) =>
        bytes is { } b ? Bytes((double)b, provider) : NotAvailable;

    /// <summary>Formats a byte rate, e.g. "1.2 MB/s".</summary>
    public static string BytesPerSecond(double? bytesPerSecond, IFormatProvider? provider = null) =>
        bytesPerSecond is { } rate
            ? string.Format(Culture(provider), Strings.Format_PerSecond, Bytes(Math.Max(rate, 0), provider))
            : NotAvailable;

    /// <summary>Formats a bit rate with decimal (SI) units, as network speeds are expressed: "82.4 Mbps" (French: "82,4 Mbit/s").</summary>
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
            >= 1e9 => string.Format(culture, Strings.Format_Gbps, Round(bits / 1e9)),
            >= 1e6 => string.Format(culture, Strings.Format_Mbps, Round(bits / 1e6)),
            >= 1e3 => string.Format(culture, Strings.Format_Kbps, Round(bits / 1e3)),
            _ => string.Format(culture, Strings.Format_Bps, bits),
        };
    }

    /// <summary>Formats a percentage, e.g. "17%" or "38.7%" (French: "38,7 %").</summary>
    public static string Percent(double? percent, int decimals = 0, IFormatProvider? provider = null) =>
        percent is { } p && double.IsFinite(p)
            ? string.Format(Culture(provider), Strings.Format_Percent, Math.Round(p, decimals).ToString("F" + decimals, Culture(provider)))
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

    /// <summary>Formats a duration compactly, e.g. "3d 14h 21m", "14h 21m", "21m", "45s" (French: "3 j 14 h 21 min").</summary>
    public static string DurationCompact(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalDays >= 1)
        {
            return Text.Format(Strings.Duration_DaysHoursMinutes, (int)duration.TotalDays, duration.Hours, duration.Minutes);
        }

        if (duration.TotalHours >= 1)
        {
            return Text.Format(Strings.Duration_HoursMinutes, duration.Hours, duration.Minutes);
        }

        return duration.TotalMinutes >= 1
            ? Text.Format(Strings.Duration_Minutes, duration.Minutes)
            : Text.Format(Strings.Duration_Seconds, duration.Seconds);
    }

    /// <summary>Formats a duration with two units, e.g. "45s", "3m 24s", "1h 05m", "2d 3h".</summary>
    public static string DurationPrecise(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalDays >= 1 ? Text.Format(Strings.Duration_DaysHours, (int)duration.TotalDays, duration.Hours)
            : duration.TotalHours >= 1 ? Text.Format(Strings.Duration_HoursPaddedMinutes, (int)duration.TotalHours, duration.Minutes)
            : duration.TotalMinutes >= 1 ? Text.Format(Strings.Duration_MinutesPaddedSeconds, duration.Minutes, duration.Seconds)
            : Text.Format(Strings.Duration_Seconds, (int)Math.Round(duration.TotalSeconds));
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
            parts.Add(Text.Plural(days, Strings.Duration_Day_One, Strings.Duration_Day_Other));
        }

        if (days > 0 || duration.Hours > 0)
        {
            parts.Add(Text.Plural(duration.Hours, Strings.Duration_Hour_One, Strings.Duration_Hour_Other));
        }

        parts.Add(Text.Plural(duration.Minutes, Strings.Duration_Minute_One, Strings.Duration_Minute_Other));
        return string.Join(Strings.List_Separator, parts);
    }

    // Three significant digits at most: 9.8, 12.4, 580 — matching what Task Manager shows.
    private static double Round(double value) =>
        Math.Abs(value) >= 100 ? Math.Round(value) : Math.Round(value, 1);

    private static IFormatProvider Culture(IFormatProvider? provider) =>
        provider ?? CultureInfo.CurrentCulture;
}
