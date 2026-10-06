using PulseDesk.Core.Interfaces;
using PulseDesk.Core.Metrics;
using PulseDesk.Core.Models;
using PulseDesk.Core.Monitoring;
using PulseDesk.Infrastructure.Windows;

namespace PulseDesk.Infrastructure.Performance;

/// <summary>
/// CPU metrics from the "Processor Information" performance counters, using the same method as
/// Task Manager:
/// <list type="bullet">
/// <item>usage is <c>% Processor Utility</c> (accounts for frequency scaling), capped at 100%;</item>
/// <item>current speed is <c>Processor Frequency × % Processor Performance</c>.</item>
/// </list>
/// Temperature is reported as unavailable: Windows exposes no reliable CPU temperature without a
/// third-party kernel driver, and PulseDesk does not install one.
/// </summary>
public sealed class WindowsCpuMetricProvider : ICpuMetricProvider, IDisposable
{
    /// <summary>Delay between the two samples needed before the first value is meaningful.</summary>
    private static readonly TimeSpan PrimingDelay = TimeSpan.FromMilliseconds(250);

    private readonly Lock _lock = new();
    private readonly List<(int Group, int Number, double Value)> _instances = new(64);
    private PdhQuery? _query;
    private PdhCounter? _total;
    private PdhCounter? _perProcessor;
    private PdhCounter? _performance;
    private PdhCounter? _frequency;
    private double _peakGhz;

    public async Task<CpuMetrics> CollectAsync(CancellationToken cancellationToken)
    {
        if (EnsureQuery())
        {
            // Rate counters need two samples: prime once so the first value is real, not 0.
            await Task.Delay(PrimingDelay, cancellationToken).ConfigureAwait(false);
        }

        lock (_lock)
        {
            var query = _query ?? throw new ObjectDisposedException(nameof(WindowsCpuMetricProvider));
            query.Collect();

            // "% Processor Utility" has no valid value for one sample when its underlying counter wraps around
            // (about every 1 h 55 min): skip that sample rather than report the CPU as unavailable.
            var usage = _total!.GetValue() ?? throw new MetricSampleSkippedException("CPU usage counter returned no valid value for this sample.");
            var perProcessor = ReadPerProcessor();
            var baseMhz = _frequency?.GetValue();
            var performance = _performance?.GetValue();
            double? currentGhz = baseMhz is > 0 && performance is > 0 ? baseMhz.Value * performance.Value / 100_000 : null;
            if (currentGhz > _peakGhz)
            {
                _peakGhz = currentGhz.Value;
            }

            return new CpuMetrics(
                Percentages.Clamp(usage),
                TemperatureCelsius: null,
                CurrentFrequencyGHz: currentGhz,
                LogicalProcessors: perProcessor.Length > 0 ? perProcessor.Length : Environment.ProcessorCount)
            {
                LogicalProcessorUsage = perProcessor,
                BaseFrequencyGHz = baseMhz is > 0 ? baseMhz / 1000 : null,
                PeakFrequencyGHz = _peakGhz > 0 ? _peakGhz : null,
            };
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

    /// <summary>Creates the query on first use. Returns true when it was just created.</summary>
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
                // "% Processor Utility" exists since Windows 8; fall back to "% Processor Time" otherwise.
                _total = query.TryAddCounter(@"\Processor Information(_Total)\% Processor Utility")
                    ?? query.AddCounter(@"\Processor Information(_Total)\% Processor Time");
                _perProcessor = query.TryAddCounter(@"\Processor Information(*)\% Processor Utility")
                    ?? query.TryAddCounter(@"\Processor Information(*)\% Processor Time");
                _performance = query.TryAddCounter(@"\Processor Information(_Total)\% Processor Performance");
                _frequency = query.TryAddCounter(@"\Processor Information(_Total)\Processor Frequency");
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

    private double[] ReadPerProcessor()
    {
        if (_perProcessor is null)
        {
            return [];
        }

        _instances.Clear();
        _perProcessor.VisitInstances((name, value) =>
        {
            if (ProcessorInstanceName.TryParse(name, out var group, out var number))
            {
                _instances.Add((group, number, Percentages.Clamp(value)));
            }
        });

        _instances.Sort(static (a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Number.CompareTo(b.Number));
        return _instances.Select(i => i.Value).ToArray();
    }
}

/// <summary>Parses "Processor Information" instance names such as "0,5" (group 0, processor 5).</summary>
internal static class ProcessorInstanceName
{
    /// <summary>Returns false for aggregate instances such as "_Total" or "0,_Total".</summary>
    public static bool TryParse(ReadOnlySpan<char> name, out int group, out int number)
    {
        group = number = 0;
        var comma = name.IndexOf(',');
        return comma > 0
            && int.TryParse(name[..comma], out group)
            && int.TryParse(name[(comma + 1)..], out number);
    }
}
