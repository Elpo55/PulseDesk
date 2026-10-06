using System.Diagnostics;
using PulseDesk.Core.Interfaces;
using PulseDesk.Core.Metrics;
using PulseDesk.Core.Models;
using PulseDesk.Infrastructure.Hardware;
using PulseDesk.Infrastructure.Windows;

namespace PulseDesk.Infrastructure.Performance;

/// <summary>
/// GPU metrics from the WDDM performance counters ("GPU Engine", "GPU Adapter Memory"), aggregated like
/// Task Manager: an engine's utilization is the sum over all processes using it, and the adapter's
/// utilization is that of its busiest engine.
/// </summary>
/// <remarks>
/// Adapter names and memory sizes come from DXGI. Temperature and clock speed are not exposed by these
/// counters, so they are reported as unavailable.
/// </remarks>
public sealed class WindowsGpuMetricProvider : IGpuMetricProvider, IDisposable
{
    private static readonly TimeSpan PrimingDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AdapterRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, AdapterState> _adapters = new(StringComparer.Ordinal);
    private PdhQuery? _query;
    private PdhCounter? _engines;
    private PdhCounter? _dedicated;
    private PdhCounter? _shared;
    private long _adaptersRefreshedAt;

    public async Task<IReadOnlyList<GpuMetrics>> CollectAsync(CancellationToken cancellationToken)
    {
        if (EnsureInitialized())
        {
            await Task.Delay(PrimingDelay, cancellationToken).ConfigureAwait(false);
        }

        lock (_lock)
        {
            if (Stopwatch.GetElapsedTime(_adaptersRefreshedAt) > AdapterRefreshInterval)
            {
                RefreshAdapters();
            }

            if (_adapters.Count == 0)
            {
                throw new InvalidOperationException("No hardware graphics adapter was found.");
            }

            foreach (var adapter in _adapters.Values)
            {
                adapter.Reset();
            }

            _query?.Collect();
            var hasEngineData = _engines?.VisitInstances(OnEngineInstance) ?? false;
            var hasDedicatedData = _dedicated?.VisitInstances((name, value) => OnMemoryInstance(name, value, dedicated: true)) ?? false;
            var hasSharedData = _shared?.VisitInstances((name, value) => OnMemoryInstance(name, value, dedicated: false)) ?? false;

            return _adapters.Values
                .Select(a => a.ToMetrics(hasEngineData, hasDedicatedData, hasSharedData))
                .ToArray();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _query?.Dispose();
            _query = null;
        }
    }

    /// <summary>Creates the query and lists adapters on first use. Returns true when it was just created.</summary>
    private bool EnsureInitialized()
    {
        lock (_lock)
        {
            if (_query is not null)
            {
                return false;
            }

            RefreshAdapters();
            var query = new PdhQuery();
            _engines = query.TryAddCounter(@"\GPU Engine(*)\Utilization Percentage");
            _dedicated = query.TryAddCounter(@"\GPU Adapter Memory(*)\Dedicated Usage");
            _shared = query.TryAddCounter(@"\GPU Adapter Memory(*)\Shared Usage");
            query.Collect();
            _query = query;
            return true;
        }
    }

    private void RefreshAdapters()
    {
        var current = DxgiAdapterEnumerator.Enumerate();
        foreach (var info in current)
        {
            if (_adapters.TryGetValue(info.AdapterId, out var existing))
            {
                existing.Info = info;
            }
            else
            {
                _adapters[info.AdapterId] = new AdapterState(info);
            }
        }

        foreach (var removed in _adapters.Keys.Except(current.Select(a => a.AdapterId)).ToArray())
        {
            _adapters.Remove(removed);
        }

        _adaptersRefreshedAt = Stopwatch.GetTimestamp();
    }

    private void OnEngineInstance(ReadOnlySpan<char> instance, double value)
    {
        if (!GpuCounterInstance.TryParseEngine(instance, out var adapterId, out var engineKey, out var engineType)
            || !_adapters.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(adapterId, out var adapter))
        {
            return;
        }

        adapter.AddEngineUsage(engineKey, engineType, value);
        if (value > 0 && GpuCounterInstance.TryGetProcessId(instance, out var processId))
        {
            adapter.AddProcessUsage(processId, value);
        }
    }

    private void OnMemoryInstance(ReadOnlySpan<char> instance, double bytes, bool dedicated)
    {
        if (GpuCounterInstance.TryGetAdapterId(instance, out var adapterId)
            && _adapters.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(adapterId, out var adapter))
        {
            adapter.AddMemory(bytes, dedicated);
        }
    }

    private sealed class AdapterState(GpuAdapterInfo info)
    {
        /// <summary>Processes below this utilization are not listed (idle GPU contexts are common).</summary>
        private const double MinimumProcessUsage = 0.1;

        private readonly Dictionary<string, EngineState> _engines = new(StringComparer.Ordinal);
        private readonly Dictionary<int, double> _processes = [];
        private double _dedicatedUsed;
        private double _sharedUsed;

        public GpuAdapterInfo Info { get; set; } = info;

        public void Reset()
        {
            foreach (var engine in _engines.Values)
            {
                engine.Sum = 0;
            }

            _processes.Clear();
            _dedicatedUsed = 0;
            _sharedUsed = 0;
        }

        /// <summary>A process's usage is that of its busiest engine instance.</summary>
        public void AddProcessUsage(int processId, double value)
        {
            ref var usage = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_processes, processId, out _);
            usage = Math.Max(usage, value);
        }

        public void AddEngineUsage(ReadOnlySpan<char> engineKey, ReadOnlySpan<char> engineType, double value)
        {
            var lookup = _engines.GetAlternateLookup<ReadOnlySpan<char>>();
            if (!lookup.TryGetValue(engineKey, out var engine))
            {
                engine = new EngineState(engineType.ToString());
                _engines[engineKey.ToString()] = engine;
            }

            engine.Sum += value;
        }

        public void AddMemory(double bytes, bool dedicated)
        {
            if (dedicated)
            {
                _dedicatedUsed += bytes;
            }
            else
            {
                _sharedUsed += bytes;
            }
        }

        public GpuMetrics ToMetrics(bool hasEngineData, bool hasDedicatedData, bool hasSharedData)
        {
            // An engine shared by several processes can report a sum slightly above 100%.
            var engineTypes = _engines.Values
                .GroupBy(e => e.Type, StringComparer.Ordinal)
                .Select(g => new GpuEngineUsage(g.Key, Percentages.Clamp(g.Max(e => e.Sum))))
                .OrderByDescending(e => e.UsagePercent)
                .ThenBy(e => e.EngineType, StringComparer.Ordinal)
                .ToArray();

            GpuProcessUsage[] processes = [];
            if (hasEngineData && _processes.Count > 0)
            {
                processes = _processes
                    .Where(p => p.Value >= MinimumProcessUsage)
                    .Select(p => new GpuProcessUsage(p.Key, Percentages.Clamp(p.Value)))
                    .OrderByDescending(p => p.UsagePercent)
                    .ToArray();
            }

            return new GpuMetrics(Info.AdapterId, Info.Name)
            {
                UsagePercent = hasEngineData ? (engineTypes.Length > 0 ? engineTypes[0].UsagePercent : 0) : null,
                Engines = hasEngineData ? engineTypes : [],
                Processes = processes,
                DedicatedMemoryTotalBytes = Info.DedicatedMemoryBytes,
                DedicatedMemoryUsedBytes = hasDedicatedData ? (ulong)Math.Max(_dedicatedUsed, 0) : null,
                SharedMemoryTotalBytes = Info.SharedMemoryBytes,
                SharedMemoryUsedBytes = hasSharedData ? (ulong)Math.Max(_sharedUsed, 0) : null,
            };
        }
    }

    private sealed class EngineState(string type)
    {
        public string Type { get; } = type;

        public double Sum { get; set; }
    }
}
