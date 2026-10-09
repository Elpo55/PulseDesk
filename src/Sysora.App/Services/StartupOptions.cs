namespace Sysora.App.Services;

/// <summary>Command-line options.</summary>
/// <param name="DemoMode"><c>--demo</c>: use simulated metrics (clearly flagged in the UI). For development and screenshots.</param>
/// <param name="LaunchedAtSignIn"><c>--startup</c>: started by Windows at sign-in; honors "Start minimized" / "Start in tray".</param>
/// <param name="StartInTray"><c>--tray</c>: start hidden in the notification area.</param>
/// <param name="InitialPage"><c>--page=Name</c>: open on a given page (development and screenshots).</param>
/// <param name="Language"><c>--language=fr</c>: interface language for this run only, without changing the setting.</param>
public sealed record StartupOptions(bool DemoMode, bool LaunchedAtSignIn, bool StartInTray, AppPage InitialPage = AppPage.Dashboard, string? Language = null)
{
    private const string PagePrefix = "--page=";
    private const string LanguagePrefix = "--language=";

    public static StartupOptions Parse(IEnumerable<string> args)
    {
        var list = args.Select(a => a.Trim()).ToList();
        var set = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        var pageName = list.FirstOrDefault(a => a.StartsWith(PagePrefix, StringComparison.OrdinalIgnoreCase))?[PagePrefix.Length..];
        // "History" was the name of the page Replay replaced.
        if (string.Equals(pageName, "History", StringComparison.OrdinalIgnoreCase))
        {
            pageName = nameof(AppPage.Replay);
        }

        var page = pageName is not null && Enum.TryParse<AppPage>(pageName, ignoreCase: true, out var parsed)
            ? parsed
            : AppPage.Dashboard;

        return new StartupOptions(
            DemoMode: set.Contains("--demo"),
            LaunchedAtSignIn: set.Contains(Infrastructure.SystemInfo.RunKeyStartupRegistration.StartupArgument),
            StartInTray: set.Contains("--tray"),
            InitialPage: page,
            Language: list.FirstOrDefault(a => a.StartsWith(LanguagePrefix, StringComparison.OrdinalIgnoreCase))?[LanguagePrefix.Length..]);
    }
}
