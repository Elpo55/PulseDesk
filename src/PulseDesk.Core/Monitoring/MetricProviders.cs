using PulseDesk.Core.Interfaces;

namespace PulseDesk.Core.Monitoring;

/// <summary>
/// The set of providers the monitor collects from. Registered once in the composition root, with either
/// the Windows implementations or the simulated ones (demo mode).
/// </summary>
public sealed record MetricProviders(
    ICpuMetricProvider Cpu,
    IMemoryMetricProvider Memory,
    IGpuMetricProvider Gpu,
    IStorageMetricProvider Storage,
    IDiskActivityMetricProvider DiskActivity,
    INetworkMetricProvider Network,
    IProcessMetricProvider Processes,
    ISystemMetricProvider System);
