using Sysora.Core.Models;

namespace Sysora.Core.Interfaces;

/// <summary>
/// Collects one family of metrics. Implementations live in Sysora.Infrastructure (real data)
/// or Sysora.Core.Simulation (demo data).
/// </summary>
/// <typeparam name="T">Type of the collected metrics.</typeparam>
/// <remarks>
/// Providers may keep state between calls (for example, to compute rates from counters) and are
/// called by a single monitoring loop, never concurrently with themselves. A provider throws when
/// its data source is unavailable; the monitor then reports the metric as unavailable.
/// </remarks>
public interface IMetricProvider<T>
{
    Task<T> CollectAsync(CancellationToken cancellationToken);
}

/// <summary>Collects overall and per-processor CPU utilization and clock speed.</summary>
public interface ICpuMetricProvider : IMetricProvider<CpuMetrics>;

/// <summary>Collects physical and committed memory usage.</summary>
public interface IMemoryMetricProvider : IMetricProvider<MemoryMetrics>;

/// <summary>Collects utilization and memory of each graphics adapter.</summary>
public interface IGpuMetricProvider : IMetricProvider<IReadOnlyList<GpuMetrics>>;

/// <summary>Collects capacity of each ready volume.</summary>
public interface IStorageMetricProvider : IMetricProvider<IReadOnlyList<StorageMetrics>>;

/// <summary>Collects activity (busy time, throughput) of each volume.</summary>
public interface IDiskActivityMetricProvider : IMetricProvider<IReadOnlyList<DiskActivityMetrics>>;

/// <summary>Collects local network interface state and throughput.</summary>
public interface INetworkMetricProvider : IMetricProvider<NetworkMetrics>;

/// <summary>Collects resource usage of every running process.</summary>
public interface IProcessMetricProvider : IMetricProvider<ProcessSnapshot>;

/// <summary>Collects frequently changing system-wide values such as uptime.</summary>
public interface ISystemMetricProvider : IMetricProvider<SystemMetrics>;
