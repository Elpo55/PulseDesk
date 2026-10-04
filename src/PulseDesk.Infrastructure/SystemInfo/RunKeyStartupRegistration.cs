using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PulseDesk.Core.Interfaces;

namespace PulseDesk.Infrastructure.SystemInfo;

/// <summary>
/// Starts PulseDesk at sign-in through the documented per-user Run key
/// (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>). Only PulseDesk's own value is ever
/// written or removed, and only when the user toggles the setting. The entry appears in
/// Settings › Apps › Startup, where the user can also disable it.
/// </summary>
public sealed class RunKeyStartupRegistration(ILogger<RunKeyStartupRegistration> logger) : IStartupRegistration
{
    /// <summary>Command-line flag added to the registered command, so PulseDesk knows it was started by Windows.</summary>
    public const string StartupArgument = "--startup";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "PulseDesk";

    public StartupState GetState()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string)
            {
                return StartupState.Disabled;
            }

            // Windows records the user's choice from Settings › Startup apps here: an odd first byte means disabled.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1
                ? StartupState.DisabledByUser
                : StartupState.Enabled;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex, "Startup registration could not be read.");
            return StartupState.Unknown;
        }
    }

    public StartupState SetEnabled(bool enabled)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unknown.");
                run.SetValue(ValueName, $"\"{executable}\" {StartupArgument}", RegistryValueKind.String);
                logger.LogInformation("PulseDesk will start with Windows.");
            }
            else
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
                logger.LogInformation("PulseDesk will no longer start with Windows.");
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            logger.LogError(ex, "Startup registration could not be changed.");
        }

        return GetState();
    }
}
