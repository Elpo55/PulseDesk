using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Sysora.Core.Settings;

namespace Sysora.App.Services;

/// <summary>
/// Applies the System / Light / Dark preference to the whole window (content and caption buttons).
/// Colors themselves live in <c>Themes/Colors.xaml</c>.
/// </summary>
public sealed class ThemeService
{
    private Window? _window;

    public void Attach(Window window) => _window = window;

    public void Apply(ThemePreference preference)
    {
        if (_window is null)
        {
            return;
        }

        if (_window.Content is FrameworkElement root)
        {
            root.RequestedTheme = preference switch
            {
                ThemePreference.Light => ElementTheme.Light,
                ThemePreference.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        _window.AppWindow.TitleBar.PreferredTheme = preference switch
        {
            ThemePreference.Light => TitleBarTheme.Light,
            ThemePreference.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }
}
