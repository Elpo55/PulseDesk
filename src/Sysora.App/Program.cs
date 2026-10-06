using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Sysora.App.Services;

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
