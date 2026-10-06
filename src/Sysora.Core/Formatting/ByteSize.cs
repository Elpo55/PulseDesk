namespace Sysora.Core.Formatting;

/// <summary>
/// Byte unit conversions. Sysora follows the Windows convention (Explorer, Task Manager):
/// units are powers of 1024 and labeled KB, MB, GB, TB.
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
    /// <returns>The value expressed in that unit and the unit label.</returns>
    public static (double Value, string Unit) Scale(double bytes)
    {
        var absolute = Math.Abs(bytes);
        return absolute switch
        {
            >= BytesPerTerabyte => (bytes / BytesPerTerabyte, "TB"),
            >= BytesPerGigabyte => (bytes / BytesPerGigabyte, "GB"),
            >= BytesPerMegabyte => (bytes / BytesPerMegabyte, "MB"),
            >= BytesPerKilobyte => (bytes / BytesPerKilobyte, "KB"),
            _ => (bytes, "B"),
        };
    }
}
