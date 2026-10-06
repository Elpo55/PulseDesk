using System.Diagnostics;
using Sysora.Core.Interfaces;
using Sysora.Core.Metrics;
using Sysora.Core.Models;
using Sysora.Infrastructure.Windows;

namespace Sysora.Infrastructure.Processes;

/// <summary>
/// Per-process CPU, memory and I/O, computed from the difference between two kernel snapshots.
/// CPU usage is a share of the total capacity of all logical processors (as in Task Manager).
/// </summary>
/// <remarks>
/// Processes are tracked by PID and creation time, so a reused PID never inherits another process's
/// counters. The System Idle Process (PID 0) is excluded: it is not a real process. The executable path is
/// read once, when a process is first seen (it identifies applications in App Impact).
/// </remarks>
public sealed class WindowsProcessMetricProvider : IProcessMetricProvider, IDisposable
{
    private static readonly TimeSpan PrimingDelay = TimeSpan.FromMilliseconds(250);

    private readonly Lock _lock = new();
    private readonly ProcessSnapshotReader _reader = new();
    private readonly List<RawProcessEntry> _entries = new(512);
    private readonly int _processorCount = Math.Max(1, (int)NativeMethods.GetActiveProcessorCount(NativeMethods.AllProcessorGroups));
    private Dictionary<ProcessIdentity, PreviousSample> _previous = [];
    private Dictionary<ProcessIdentity, PreviousSample> _next = [];
    private long _previousTimestamp;

    public async Task<ProcessSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        bool primed;
        lock (_lock)
        {
            primed = _previousTimestamp != 0;
            if (!primed)
            {
                Sample();
            }
        }

        if (!primed)
        {
            // Usage is a rate: take a first snapshot shortly before the one we report.
            await Task.Delay(PrimingDelay, cancellationToken).ConfigureAwait(false);
        }

        lock (_lock)
        {
            return Sample();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _reader.Dispose();
        }
    }

    private ProcessSnapshot Sample()
    {
        _reader.Read(_entries);
        var now = Stopwatch.GetTimestamp();
        var elapsed = _previousTimestamp == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_previousTimestamp, now);
        var capacityTicks = elapsed.Ticks * (double)_processorCount;

        var processes = new List<ProcessMetrics>(_entries.Count);
        var threads = 0;
        var handles = 0;
        _next.Clear();

        foreach (var entry in _entries)
        {
            if (entry.ProcessId == 0)
            {
                continue;
            }

            threads += entry.ThreadCount;
            handles += entry.HandleCount;

            var identity = new ProcessIdentity(entry.ProcessId, entry.CreateTime);
            double? cpu = null, ioRead = null, ioWrite = null;
            string name;
            string? path;
            if (_previous.TryGetValue(identity, out var previous))
            {
                name = previous.Name;
                path = previous.Path;
                if (elapsed > TimeSpan.Zero)
                {
                    cpu = Percentages.Clamp((entry.CpuTime - previous.CpuTime) / capacityTicks * 100);
                    ioRead = Math.Max(entry.IoReadBytes - previous.IoReadBytes, 0) / elapsed.TotalSeconds;
                    ioWrite = Math.Max(entry.IoWriteBytes - previous.IoWriteBytes, 0) / elapsed.TotalSeconds;
                }
            }
            else
            {
                name = ResolveName(entry);
                path = ProcessImagePath.TryGet(entry.ProcessId, entry.CreateTime);
            }

            _next[identity] = new PreviousSample(entry.CpuTime, entry.IoReadBytes, entry.IoWriteBytes, name, path);
            processes.Add(new ProcessMetrics(identity, name)
            {
                ParentProcessId = entry.ParentProcessId,
                StartTime = entry.CreateTime > 0 ? DateTimeOffset.FromFileTime(entry.CreateTime) : null,
                ExecutablePath = path,
                CpuPercent = cpu,
                PrivateWorkingSetBytes = entry.PrivateWorkingSet,
                WorkingSetBytes = entry.WorkingSet,
                PrivateBytes = entry.PrivateBytes,
                IoReadBytesPerSecond = ioRead,
                IoWriteBytesPerSecond = ioWrite,
                ThreadCount = entry.ThreadCount,
                HandleCount = entry.HandleCount,
                SessionId = entry.SessionId,
            });
        }

        (_previous, _next) = (_next, _previous);
        _previousTimestamp = now;
        return new ProcessSnapshot(processes) { ThreadCount = threads, HandleCount = handles };
    }

    private static string ResolveName(in RawProcessEntry entry)
    {
        var name = ProcessSnapshotReader.GetName(entry);
        if (!name.IsEmpty)
        {
            return name.ToString();
        }

        return entry.ProcessId == 4 ? "System" : $"Process {entry.ProcessId}";
    }

    private readonly record struct PreviousSample(long CpuTime, long IoReadBytes, long IoWriteBytes, string Name, string? Path);
}
