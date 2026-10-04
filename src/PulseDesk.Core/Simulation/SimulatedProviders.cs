using PulseDesk.Core.Interfaces;
using PulseDesk.Core.Models;
using PulseDesk.Core.Results;

namespace PulseDesk.Core.Simulation;

// Thin adapters exposing a SimulatedMachine through the regular provider interfaces.
// Demo mode only: never registered when PulseDesk runs normally.

public sealed class FakeCpuMetricProvider(SimulatedMachine machine) : ICpuMetricProvider
{
    public Task<CpuMetrics> CollectAsync(CancellationToken cancellationToken) => Task.FromResult(machine.SampleCpu());
}

public sealed class FakeMemoryMetricProvider(SimulatedMachine machine) : IMemoryMetricProvider
{
    public Task<MemoryMetrics> CollectAsync(CancellationToken cancellationToken) => Task.FromResult(machine.SampleMemory());
}

public sealed class FakeGpuMetricProvider(SimulatedMachine machine) : IGpuMetricProvider
{
    public Task<IReadOnlyList<GpuMetrics>> CollectAsync(CancellationToken cancellationToken) => Task.FromResult(machine.SampleGpus());
}

public sealed class FakeStorageMetricProvider(SimulatedMachine machine) : IStorageMetricProvider
{
    public Task<IReadOnlyList<StorageMetrics>> CollectAsync(CancellationToken cancellationToken) => Task.FromResult(machine.SampleStorage());
}

public sealed class FakeDiskActivityMetricProvider(SimulatedMachine machine) : IDiskActivityMetricProvider
{
    public Task<IReadOnlyList<DiskActivityMetrics>> CollectAsync(CancellationToken cancellationToken) => Task.FromResult(machine.SampleDiskActivity());
}

public sealed class FakeNetworkMetricProvider(SimulatedMachine machine) : INetworkMetricProvider
{
    public Task<NetworkMetrics> CollectAsync(CancellationToken cancellationToken) => Task.FromResult(machine.SampleNetwork());
}

public sealed class FakeProcessMetricProvider(SimulatedMachine machine) : IProcessMetricProvider
{
    public Task<ProcessSnapshot> CollectAsync(CancellationToken cancellationToken) => Task.FromResult(machine.SampleProcesses());
}

public sealed class FakeSystemMetricProvider(SimulatedMachine machine) : ISystemMetricProvider
{
    public Task<SystemMetrics> CollectAsync(CancellationToken cancellationToken) => Task.FromResult(new SystemMetrics(machine.Uptime));
}

public sealed class FakeSystemInfoProvider(SimulatedMachine machine) : ISystemInfoProvider
{
    public Task<SystemInformation> GetAsync(CancellationToken cancellationToken) => Task.FromResult(machine.GetSystemInformation());

    public Task<SystemInformation> RefreshAsync(CancellationToken cancellationToken) => GetAsync(cancellationToken);
}

public sealed class FakeProcessManager(SimulatedMachine machine) : IProcessManager
{
    public Task<ProcessDetails> GetDetailsAsync(ProcessIdentity process, CancellationToken cancellationToken)
    {
        var path = machine.GetProcessPath(process);
        return Task.FromResult(new ProcessDetails(process)
        {
            ExecutablePath = path,
            ExecutablePathError = path is null ? "Access is denied (simulated system process)." : null,
            Description = path is null ? null : "Simulated application (demo)",
            Company = path is null ? null : "Demo",
            FileVersion = path is null ? null : "1.0.0",
            IsCritical = path is null,
        });
    }

    public Task<OperationResult> TerminateAsync(ProcessIdentity process, CancellationToken cancellationToken)
    {
        if (machine.TryTerminate(process, out var isSystem))
        {
            return Task.FromResult(OperationResult.Success);
        }

        return Task.FromResult(isSystem
            ? OperationResult.Failure(OperationError.Protected, "This is a critical system process. Ending it would stop Windows.")
            : OperationResult.Failure(OperationError.NotFound, "The process has already exited."));
    }
}

/// <summary>Builds the full set of simulated providers for demo mode.</summary>
public static class SimulatedProviders
{
    public static Monitoring.MetricProviders Create(SimulatedMachine machine) => new(
        new FakeCpuMetricProvider(machine),
        new FakeMemoryMetricProvider(machine),
        new FakeGpuMetricProvider(machine),
        new FakeStorageMetricProvider(machine),
        new FakeDiskActivityMetricProvider(machine),
        new FakeNetworkMetricProvider(machine),
        new FakeProcessMetricProvider(machine),
        new FakeSystemMetricProvider(machine));
}
