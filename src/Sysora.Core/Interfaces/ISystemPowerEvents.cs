namespace Sysora.Core.Interfaces;

/// <summary>Sleep and resume notifications from Windows. Events are raised on a system thread and must return quickly.</summary>
public interface ISystemPowerEvents
{
    /// <summary>The PC is about to sleep or hibernate (Windows allows about two seconds).</summary>
    event EventHandler? Suspending;

    /// <summary>The PC resumed from sleep or hibernation.</summary>
    event EventHandler? Resumed;
}
