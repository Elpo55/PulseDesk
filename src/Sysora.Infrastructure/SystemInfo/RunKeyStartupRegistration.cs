using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Sysora.Core.Interfaces;

namespace Sysora.Infrastructure.SystemInfo;

/// <summary>
/// Starts Sysora at sign-in through the documented per-user Run key
/// (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>). Only Sysora's own value is ever
/// written or removed, and only when the user toggles the setting. The entry appears in
/// Settings › Apps › Startup, where the user can also disable it.
/// </summary>
public sealed class RunKeyStartupRegistration(ILogger<RunKeyStartupRegistration> logger) : IStartupRegistration
{
    /// <summary>Command-line flag added to the registered command, so Sysora knows it was started by Windows.</summary>
    public const string StartupArgument = "--startup";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Sysora";

    /// <summary>Value written by versions released before the application was renamed to Sysora.</summary>
    private const string LegacyValueName = "PulseDesk";

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
                run.SetValue(ValueName, Command(), RegistryValueKind.String);
                logger.LogInformation("Sysora will start with Windows.");
            }
            else
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
                logger.LogInformation("Sysora will no longer start with Windows.");
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            logger.LogError(ex, "Startup registration could not be changed.");
        }

        return GetState();
    }

    /// <summary>
    /// Replaces the registration of a version released under the previous name (which points to an executable that no
    /// longer exists) with Sysora's own, keeping the user's choice, including "turned off in Windows Settings".
    /// Does nothing when there is no previous registration.
    /// </summary>
    public void MigrateLegacyRegistration()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(LegacyValueName) is null)
            {
                return;
            }

            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            if (run.GetValue(ValueName) is null)
            {
                run.SetValue(ValueName, Command(), RegistryValueKind.String);
                if (approved?.GetValue(LegacyValueName) is byte[] flags && approved.GetValue(ValueName) is null)
                {
                    approved.SetValue(ValueName, flags, RegistryValueKind.Binary);
                }
            }

            run.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            approved?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            logger.LogInformation("Startup registration of the previous version replaced by Sysora's.");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            logger.LogWarning(ex, "The previous version's startup registration could not be migrated.");
        }
    }

    private static string Command()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unknown.");
        return $"\"{executable}\" {StartupArgument}";
    }
}
