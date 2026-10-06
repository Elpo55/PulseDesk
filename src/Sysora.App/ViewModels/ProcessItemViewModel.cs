using CommunityToolkit.Mvvm.ComponentModel;
using Sysora.Core.Formatting;
using Sysora.Core.Models;

namespace Sysora.App.ViewModels;

/// <summary>A slot of the process list, showing whichever process currently sorts at its position.</summary>
public sealed partial class ProcessRowViewModel : ObservableObject
{
    public ProcessRowViewModel(ProcessItemViewModel item) => Item = item;

    [ObservableProperty]
    public partial ProcessItemViewModel Item { get; set; }
}

/// <summary>One process of the list. Updated in place so the list does not flicker.</summary>
public sealed partial class ProcessItemViewModel : ObservableObject
{
    public ProcessItemViewModel(ProcessMetrics process)
    {
        Identity = process.Identity;
        Name = process.Name;
        ProcessId = process.ProcessId;
        PidText = process.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        StartTime = process.StartTime;
        CpuText = MemoryText = IoText = MetricFormatter.Pending;
        Update(process);
    }

    public ProcessIdentity Identity { get; }

    public string Name { get; }

    public int ProcessId { get; }

    public string PidText { get; }

    public DateTimeOffset? StartTime { get; }

    /// <summary>CPU usage used for sorting (unknown counts as 0).</summary>
    public double Cpu { get; private set; }

    public ulong Memory { get; private set; }

    public double Io { get; private set; }

    public ProcessMetrics Metrics { get; private set; } = null!;

    [ObservableProperty]
    public partial string CpuText { get; set; }

    [ObservableProperty]
    public partial string MemoryText { get; set; }

    [ObservableProperty]
    public partial string IoText { get; set; }

    public void Update(ProcessMetrics process)
    {
        Metrics = process;
        Cpu = process.CpuPercent ?? 0;
        Memory = process.PrivateWorkingSetBytes;
        Io = process.IoBytesPerSecond ?? 0;
        CpuText = process.CpuPercent is { } cpu ? MetricFormatter.Percent(cpu, 1) : MetricFormatter.Pending;
        MemoryText = MetricFormatter.Bytes(process.PrivateWorkingSetBytes);
        IoText = process.IoBytesPerSecond is { } io ? MetricFormatter.BytesPerSecond(io) : MetricFormatter.Pending;
    }

    public bool Matches(string filter) =>
        filter.Length == 0
        || Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || PidText.StartsWith(filter, StringComparison.Ordinal);
}
