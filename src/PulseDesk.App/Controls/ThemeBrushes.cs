using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using PulseDesk.Core.Monitoring;
using Windows.UI.ViewManagement;

namespace PulseDesk.App.Controls;

/// <summary>
/// Looks up brushes from the theme dictionaries of <c>Themes/Colors.xaml</c> for a given element theme.
/// Used by controls whose brush depends on data (status colors), which {ThemeResource} cannot express.
/// </summary>
internal static class ThemeBrushes
{
    private static readonly AccessibilitySettings Accessibility = new();

    public static Brush? Get(string key, ElementTheme theme)
    {
        var themeKey = IsHighContrast() ? "HighContrast" : theme == ElementTheme.Dark ? "Dark" : "Light";
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
        {
            if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var themed)
                && themed is ResourceDictionary resources
                && resources.TryGetValue(key, out var value)
                && value is Brush brush)
            {
                return brush;
            }
        }

        return Application.Current.Resources.TryGetValue(key, out var fallback) ? fallback as Brush : null;
    }

    public static string StatusKey(HealthStatus status) => status switch
    {
        HealthStatus.Normal => "StatusNormalBrush",
        HealthStatus.Warning => "StatusWarningBrush",
        HealthStatus.Critical => "StatusCriticalBrush",
        _ => "StatusUnknownBrush",
    };

    private static bool IsHighContrast()
    {
        try
        {
            return Accessibility.HighContrast;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }
}
