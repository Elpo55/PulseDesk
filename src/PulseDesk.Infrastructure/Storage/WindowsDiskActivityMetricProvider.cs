using PulseDesk.Core.Interfaces;
using PulseDesk.Core.Metrics;
using PulseDesk.Core.Models;
using PulseDesk.Core.Monitoring;
using PulseDesk.Infrastructure.Windows;

namespace PulseDesk.Infrastructure.Storage;

/// <summary>
/// Volume activity from the "LogicalDisk" performance counters: active time (100% minus idle time)
/// and read/write throughput, per drive letter.
/// </summary>
public sealed class WindowsDiskActivityMetricProvider : IDiskActivityMetricProvider, IDisposable
{
    private static readonly TimeSpan PrimingDelay = TimeSpan.FromMilliseconds(250);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Accumulator> _drives = new(StringComparer.OrdinalIgnoreCase);
    private PdhQuery? _query;
    private PdhCounter? _idle;
    private PdhCounter? _read;
    private PdhCounter? _write;

    public async Task<IReadOnlyList<DiskActivityMetrics>> CollectAsync(CancellationToken cancellationToken)
    {
        if (EnsureQuery())
        {
            await Task.Delay(PrimingDelay, cancellationToken).ConfigureAwait(false);
        }

        lock (_lock)
        {
            var query = _query ?? throw new ObjectDisposedException(nameof(WindowsDiskActivityMetricProvider));
            query.Collect();
            foreach (var drive in _drives.Values)
            {
                drive.Reset();
            }

            var any = _idle!.VisitInstances((name, value) => Get(name)?.SetIdle(value));
            _read?.VisitInstances((name, value) => Get(name)?.SetRead(value));
            _write?.VisitInstances((name, value) => Get(name)?.SetWrite(value));
            if (!any)
            {
                // Transient (for example right after a volume change); the monitor reports it only if it repeats.
                throw new MetricSampleSkippedException("Disk performance counters returned no data for this sample.");
            }

            return _drives.Values
                .Where(d => d.Seen)
                .OrderBy(d => d.Drive, StringComparer.OrdinalIgnoreCase)
                .Select(d => d.ToMetrics())
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

    private bool EnsureQuery()
    {
        lock (_lock)
        {
            if (_query is not null)
            {
                return false;
            }

            var query = new PdhQuery();
            try
            {
                _idle = query.AddCounter(@"\LogicalDisk(*)\% Idle Time");
                _read = query.TryAddCounter(@"\LogicalDisk(*)\Disk Read Bytes/sec");
                _write = query.TryAddCounter(@"\LogicalDisk(*)\Disk Write Bytes/sec");
                query.Collect();
            }
            catch
            {
                query.Dispose();
                throw;
            }

            _query = query;
            return true;
        }
    }

    /// <summary>Returns the accumulator for a drive-letter instance ("C:"); null for "_Total" and unlettered volumes.</summary>
    private Accumulator? Get(ReadOnlySpan<char> instance)
    {
        if (instance.Length != 2 || instance[1] != ':' || !char.IsAsciiLetter(instance[0]))
        {
            return null;
        }

        var lookup = _drives.GetAlternateLookup<ReadOnlySpan<char>>();
        if (!lookup.TryGetValue(instance, out var drive))
        {
            var name = instance.ToString().ToUpperInvariant();
            drive = new Accumulator(name);
            _drives[name] = drive;
        }

        return drive;
    }

    private sealed class Accumulator(string drive)
    {
        private double? _idle;
        private double? _read;
        private double? _write;

        public string Drive { get; } = drive;

        public bool Seen => _idle is not null;

        public void Reset() => _idle = _read = _write = null;

        public void SetIdle(double value) => _idle = value;

        public void SetRead(double value) => _read = value;

        public void SetWrite(double value) => _write = value;

        public DiskActivityMetrics ToMetrics() => new(Drive)
        {
            ActiveTimePercent = _idle is { } idle ? Percentages.Clamp(100 - idle) : null,
            ReadBytesPerSecond = _read is { } read ? Math.Max(read, 0) : null,
            WriteBytesPerSecond = _write is { } write ? Math.Max(write, 0) : null,
        };
    }
}
