using System.Globalization;
using Sysora.Core.Formatting;

namespace Sysora.Tests.Core;

public sealed class MetricFormatterTests
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    [Theory]
    [InlineData(0d, "0 B")]
    [InlineData(512d, "512 B")]
    [InlineData(1536d, "1.5 KB")]
    [InlineData(608_174_080d, "580 MB")]
    [InlineData(13_314_398_618d, "12.4 GB")]
    [InlineData(34_359_738_368d, "32 GB")]
    [InlineData(1_099_511_627_776d, "1 TB")]
    public void Bytes_UsesBinaryUnitsAndThreeSignificantDigits(double bytes, string expected)
    {
        Assert.Equal(expected, MetricFormatter.Bytes(bytes, En));
    }

    [Fact]
    public void Bytes_UsesCultureDecimalSeparator()
    {
        Assert.Equal("12,4 GB", MetricFormatter.Bytes(13_314_398_618d, Fr));
    }

    [Fact]
    public void Bytes_Null_IsNotAvailable()
    {
        Assert.Equal(MetricFormatter.NotAvailable, MetricFormatter.Bytes((ulong?)null));
    }

    [Theory]
    [InlineData(1024d, 1d)]
    [InlineData(1_048_576d, 1024d)]
    public void ByteSize_ConvertsToKilobytes(double bytes, double expectedKb)
    {
        Assert.Equal(expectedKb, ByteSize.ToKilobytes(bytes));
    }

    [Fact]
    public void ByteSize_ConvertsToLargerUnits()
    {
        Assert.Equal(1, ByteSize.ToMegabytes(1_048_576));
        Assert.Equal(32, ByteSize.ToGigabytes(32UL * 1024 * 1024 * 1024));
        Assert.Equal(2, ByteSize.ToTerabytes(2 * ByteSize.BytesPerTerabyte));
    }

    [Theory]
    [InlineData(0d, "0 bps")]
    [InlineData(950_000d, "950 Kbps")]
    [InlineData(82_400_000d, "82.4 Mbps")]
    [InlineData(1_000_000_000d, "1 Gbps")]
    [InlineData(-5d, "0 bps")]
    public void BitsPerSecond_UsesDecimalUnits(double bits, string expected)
    {
        Assert.Equal(expected, MetricFormatter.BitsPerSecond(bits, En));
    }

    [Fact]
    public void BytesPerSecond_FormatsRate()
    {
        Assert.Equal("1.5 MB/s", MetricFormatter.BytesPerSecond(1_572_864, En));
        Assert.Equal(MetricFormatter.NotAvailable, MetricFormatter.BytesPerSecond(null));
    }

    [Theory]
    [InlineData(17.4, 0, "17%")]
    [InlineData(38.66, 1, "38.7%")]
    [InlineData(100, 0, "100%")]
    public void Percent_RoundsToRequestedDecimals(double value, int decimals, string expected)
    {
        Assert.Equal(expected, MetricFormatter.Percent(value, decimals, En));
    }

    [Fact]
    public void MissingValues_AreNeverShownAsZero()
    {
        Assert.Equal(MetricFormatter.NotAvailable, MetricFormatter.Percent(null));
        Assert.Equal(MetricFormatter.NotAvailable, MetricFormatter.Percent(double.NaN));
        Assert.Equal(MetricFormatter.NotAvailable, MetricFormatter.Temperature(null));
        Assert.Equal(MetricFormatter.NotAvailable, MetricFormatter.FrequencyGHz(null));
        Assert.Equal(MetricFormatter.NotAvailable, MetricFormatter.FrequencyGHz(0));
    }

    [Fact]
    public void FrequencyAndTemperature_AreFormatted()
    {
        Assert.Equal("4.21 GHz", MetricFormatter.FrequencyGHz(4.2134, En));
        Assert.Equal("54 °C", MetricFormatter.Temperature(54.4, En));
    }

    [Theory]
    [InlineData(3, 14, 21, 9, "3d 14h 21m")]
    [InlineData(0, 14, 21, 0, "14h 21m")]
    [InlineData(0, 0, 21, 30, "21m")]
    [InlineData(0, 0, 0, 45, "45s")]
    public void DurationCompact_FormatsUptime(int days, int hours, int minutes, int seconds, string expected)
    {
        Assert.Equal(expected, MetricFormatter.DurationCompact(new TimeSpan(days, hours, minutes, seconds)));
    }

    [Fact]
    public void DurationLong_FormatsUptimeInWords()
    {
        Assert.Equal("3 days, 14 hours, 21 minutes", MetricFormatter.DurationLong(new TimeSpan(3, 14, 21, 0)));
        Assert.Equal("1 day, 0 hours, 1 minute", MetricFormatter.DurationLong(new TimeSpan(1, 0, 1, 0)));
        Assert.Equal("5 minutes", MetricFormatter.DurationLong(TimeSpan.FromMinutes(5)));
        Assert.Equal("0 minutes", MetricFormatter.DurationLong(TimeSpan.FromSeconds(-5)));
    }
}
