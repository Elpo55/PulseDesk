namespace PulseDesk.Core.Interfaces;

/// <summary>
/// Registers PulseDesk to start when the user signs in to Windows.
/// </summary>
public interface IStartupRegistration
{
    /// <summary>Current registration state.</summary>
    StartupState GetState();

    /// <summary>Registers or unregisters PulseDesk. Returns the resulting state.</summary>
    StartupState SetEnabled(bool enabled);
}

/// <summary>Whether PulseDesk starts with Windows.</summary>
public enum StartupState
{
    /// <summary>PulseDesk is not registered.</summary>
    Disabled,

    /// <summary>PulseDesk is registered and allowed to start.</summary>
    Enabled,

    /// <summary>PulseDesk is registered but the user disabled it in Windows settings (Startup apps).</summary>
    DisabledByUser,

    /// <summary>The state could not be read.</summary>
    Unknown,
}
