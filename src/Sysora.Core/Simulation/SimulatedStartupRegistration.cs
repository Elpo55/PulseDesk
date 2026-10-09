using Sysora.Core.Interfaces;

namespace Sysora.Core.Simulation;

/// <summary>Demo mode: "Start with Windows" can be switched for the session without touching the Windows registry.</summary>
public sealed class SimulatedStartupRegistration : IStartupRegistration
{
    private volatile bool _enabled;

    public StartupState GetState() => _enabled ? StartupState.Enabled : StartupState.Disabled;

    public StartupState SetEnabled(bool enabled)
    {
        _enabled = enabled;
        return GetState();
    }
}
