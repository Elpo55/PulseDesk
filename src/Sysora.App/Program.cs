using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Sysora.App.Services;
using Sysora.Core.Settings;
using Sysora.Infrastructure;
using Sysora.Localization;

namespace Sysora.App;

/// <summary>
/// Entry point. Sysora runs as a single instance per user: launching it again (for example from the
/// Start menu while it sits in the notification area) brings the existing window to the front.
/// </summary>
public static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var options = StartupOptions.Parse(args);
        ApplyLanguage();

        if (RedirectToRunningInstance(options))
        {
            return 0;
        }

        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(options);
        });

        return 0;
    }

    /// <summary>
    /// Applies the interface language chosen in Settings (or the Windows language) before any window, service or text is
    /// created, so everything Sysora produces during this run is in that language. Never throws.
    /// </summary>
    private static void ApplyLanguage()
    {
        string? preference = null;
        try
        {
            var file = new SysoraPaths().SettingsFile;
            if (File.Exists(file) && SettingsSerializer.TryDeserialize(File.ReadAllText(file), out var settings, out _))
            {
                preference = settings.General.Language;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable settings: follow the Windows language.
        }

        var culture = AppLanguage.Resolve(preference, CultureInfo.InstalledUICulture);
        AppLanguage.Apply(culture);
        try
        {
            // Built-in control texts (date pickers, context menus) follow the same language.
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = culture.Name;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            // Not supported in this configuration: Sysora's own texts are still translated.
        }
    }

    /// <summary>Returns true when another instance is already running and was asked to show itself.</summary>
    private static bool RedirectToRunningInstance(StartupOptions options)
    {
        // Demo mode uses its own key so it can run next to the real dashboard.
        var key = AppInstance.FindOrRegisterForKey(options.DemoMode ? "Sysora.Demo" : "Sysora.Main");
        if (key.IsCurrent)
        {
            key.Activated += (_, _) => (Application.Current as App)?.OnActivatedByAnotherInstance();
            return false;
        }

        // Let the running instance take the foreground: this process is the one the user just started.
        AllowSetForegroundWindow(key.ProcessId);
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();

        // The redirection is a cross-process call; run it off this STA thread and wait for it.
        Task.Run(async () => await key.RedirectActivationToAsync(activation)).Wait(TimeSpan.FromSeconds(10));
        return true;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);
}
