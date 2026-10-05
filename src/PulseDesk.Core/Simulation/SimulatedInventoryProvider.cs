using PulseDesk.Core.Changes;
using PulseDesk.Core.Interfaces;

namespace PulseDesk.Core.Simulation;

/// <summary>A fixed, clearly labeled inventory for demo mode. Never used with real data.</summary>
public sealed class SimulatedInventoryProvider(TimeProvider? timeProvider = null) : ISystemInventoryProvider
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Task<SystemInventory> CollectAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(_time.GetLocalNow().Date);
        return Task.FromResult(new SystemInventory
        {
            AppsAvailable = true,
            Apps =
            [
                new InstalledApp("Machine|demo-browser", "Demo Browser (demo)") { Version = "128.0.2", Publisher = "Demo Software", InstallDate = today.AddDays(-120), Source = "Machine" },
                new InstalledApp("Machine|demo-editor", "Demo Editor (demo)") { Version = "2.4.1", Publisher = "Demo Software", InstallDate = today.AddDays(-3), Source = "Machine" },
                new InstalledApp("User|demo-chat", "Demo Chat (demo)") { Version = "5.0.0", Publisher = "Demo Chat Inc.", InstallDate = today.AddDays(-12), Source = "User" },
                new InstalledApp("Store|demo-music", "Demo Music (demo)") { Version = "1.8.0.0", Publisher = "Demo Media", InstallDate = today.AddDays(-45), Source = "Store" },
            ],
            StartupAvailable = true,
            StartupPrograms =
            [
                new StartupProgram("HKCU Run|DemoChat", "Demo Chat (demo)", @"C:\Demo\chat\chat.exe --minimized", "HKCU Run") { Enabled = true },
            ],
            Devices =
            [
                new DeviceInfo("Graphics", "Simulated GPU (demo)|8589934592", "Simulated GPU (demo)"),
                new DeviceInfo("Network", "demo-eth", "Simulated Ethernet adapter (demo)"),
            ],
        });
    }
}
