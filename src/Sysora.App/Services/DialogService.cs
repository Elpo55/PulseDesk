using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sysora.App.Services;

/// <summary>Shows confirmation dialogs for view models (which have no access to the visual tree).</summary>
public sealed class DialogService
{
    private Window? _window;

    public void Attach(Window window) => _window = window;

    /// <summary>Asks the user to confirm an action. "Cancel" is the default button.</summary>
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        if (_window?.Content is not FrameworkElement root || root.XamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            RequestedTheme = root.ActualTheme,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
