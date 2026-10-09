using Sysora.Core.Gaming;
using Sysora.Core.Interfaces;

namespace Sysora.Core.Simulation;

/// <summary>Demo mode: Windows "recognizes" the simulated game of <see cref="SimulatedMachine"/>.</summary>
public sealed class SimulatedGameLibrary : IGameLibrary
{
    public IReadOnlySet<string> RecognizedGames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SimulatedMachine.DemoGamePath };

    /// <summary>Made-up launcher games (their folders do not exist on this PC; Sysora never reads them in demo mode).</summary>
    public InstalledGameIndex InstalledGames { get; } = new(
    [
        new InstalledGame(GameLauncher.Steam, "100", "Demo Racer (simulated)", @"C:\Demo\Steam\steamapps\common\Demo Racer"),
        new InstalledGame(GameLauncher.EpicGames, "DemoArena", "Demo Arena (simulated)", @"C:\Demo\Epic\DemoArena"),
    ]);

    public bool Refresh() => false;

    public string? GetProductName(string executablePath) =>
        string.Equals(executablePath, SimulatedMachine.DemoGamePath, StringComparison.OrdinalIgnoreCase) ? "Demo Quest (simulated)" : null;
}
