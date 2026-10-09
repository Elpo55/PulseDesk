namespace Sysora.Tests;

/// <summary>Conditions some tests need, so they skip themselves (and say why) on systems where they do not apply.</summary>
internal static class Requires
{
    /// <summary>
    /// Skips the test outside Windows. For tests whose data are Windows paths ("C:\Games\game.exe"): on Linux and macOS the
    /// backslash is not a path separator, so the same data mean something else there.
    /// </summary>
    public static void WindowsPaths() =>
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses Windows paths, which Linux and macOS read differently.");

    /// <summary>Skips the test outside Windows. For tests that rely on Windows file locking.</summary>
    public static void WindowsFileLocking() =>
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Relies on Windows file locking (an open file cannot be moved).");
}
