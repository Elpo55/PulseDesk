using Sysora.Localization;

namespace Sysora.Core.Formatting;

/// <summary>Byte units, from bytes to terabytes.</summary>
public enum ByteUnit
{
    Byte,
    Kilobyte,
    Megabyte,
    Gigabyte,
    Terabyte,
}

/// <summary>
/// Byte unit conversions. Sysora follows the Windows convention (Explorer, Task Manager):
/// units are powers of 1024 and labeled KB, MB, GB, TB (Ko, Mo, Go, To in French).
/// </summary>
public static class ByteSize
{
    public const double BytesPerKilobyte = 1024d;
    public const double BytesPerMegabyte = BytesPerKilobyte * 1024;
    public const double BytesPerGigabyte = BytesPerMegabyte * 1024;
    public const double BytesPerTerabyte = BytesPerGigabyte * 1024;

    public static double ToKilobytes(double bytes) => bytes / BytesPerKilobyte;

    public static double ToMegabytes(double bytes) => bytes / BytesPerMegabyte;

    public static double ToGigabytes(double bytes) => bytes / BytesPerGigabyte;

    public static double ToTerabytes(double bytes) => bytes / BytesPerTerabyte;

    /// <summary>Picks the largest unit in which <paramref name="bytes"/> is at least 1.</summary>
    /// <returns>The value expressed in that unit and the unit.</returns>
    public static (double Value, ByteUnit Unit) Scale(double bytes)
    {
        var absolute = Math.Abs(bytes);
        return absolute switch
        {
            >= BytesPerTerabyte => (bytes / BytesPerTerabyte, ByteUnit.Terabyte),
            >= BytesPerGigabyte => (bytes / BytesPerGigabyte, ByteUnit.Gigabyte),
            >= BytesPerMegabyte => (bytes / BytesPerMegabyte, ByteUnit.Megabyte),
            >= BytesPerKilobyte => (bytes / BytesPerKilobyte, ByteUnit.Kilobyte),
            _ => (bytes, ByteUnit.Byte),
        };
    }

    /// <summary>Short label of a unit in the interface language ("GB", "Go").</summary>
    public static string Label(ByteUnit unit) => unit switch
    {
        ByteUnit.Terabyte => Strings.Unit_Terabyte,
        ByteUnit.Gigabyte => Strings.Unit_Gigabyte,
        ByteUnit.Megabyte => Strings.Unit_Megabyte,
        ByteUnit.Kilobyte => Strings.Unit_Kilobyte,
        _ => Strings.Unit_Byte,
    };
}
