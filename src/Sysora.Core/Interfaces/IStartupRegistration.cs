namespace Sysora.Core.Interfaces;

/// <summary>
/// Registers Sysora to start when the user signs in to Windows.
/// </summary>
public interface IStartupRegistration
{
    /// <summary>Current registration state.</summary>
    StartupState GetState();

    /// <summary>Registers or unregisters Sysora. Returns the resulting state.</summary>
    StartupState SetEnabled(bool enabled);
}

/// <summary>Whether Sysora starts with Windows.</summary>
public enum StartupState
{
    /// <summary>Sysora is not registered.</summary>
    Disabled,

    /// <summary>Sysora is registered and allowed to start.</summary>
    Enabled,

    /// <summary>Sysora is registered but the user disabled it in Windows settings (Startup apps).</summary>
    DisabledByUser,

    /// <summary>The state could not be read.</summary>
    Unknown,
}
