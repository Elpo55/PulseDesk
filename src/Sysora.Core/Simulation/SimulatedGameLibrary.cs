using Sysora.Core.Interfaces;

namespace Sysora.Core.Simulation;

/// <summary>Demo mode: Windows "recognizes" the simulated game of <see cref="SimulatedMachine"/>.</summary>
public sealed class SimulatedGameLibrary : IGameLibrary
{
    public IReadOnlySet<string> RecognizedGames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SimulatedMachine.DemoGamePath };

    public bool Refresh() => false;

    public string? GetProductName(string executablePath) =>
        string.Equals(executablePath, SimulatedMachine.DemoGamePath, StringComparison.OrdinalIgnoreCase) ? "Demo Quest (simulated)" : null;
}
