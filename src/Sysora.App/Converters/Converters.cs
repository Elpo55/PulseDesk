using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Sysora.Core.Monitoring;

namespace Sysora.App.Converters;

/// <summary>Maps a health status to its Segoe Fluent Icons glyph.</summary>
public sealed partial class HealthStatusToGlyphConverter : IValueConverter
{
    public const string NormalGlyph = "";
    public const string WarningGlyph = "";
    public const string CriticalGlyph = "";
    public const string UnknownGlyph = "";

    public static string ToGlyph(HealthStatus status) => status switch
    {
        HealthStatus.Normal => NormalGlyph,
        HealthStatus.Warning => WarningGlyph,
        HealthStatus.Critical => CriticalGlyph,
        _ => UnknownGlyph,
    };

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is HealthStatus status ? ToGlyph(status) : UnknownGlyph;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the value is false.</summary>
public sealed partial class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Collapsed;
}
