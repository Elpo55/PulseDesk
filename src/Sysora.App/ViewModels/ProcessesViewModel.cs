using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sysora.App.Services;
using Sysora.Core.Formatting;
using Sysora.Core.Interfaces;
using Sysora.Core.Metrics;
using Sysora.Core.Models;
using Sysora.Core.Monitoring;

namespace Sysora.App.ViewModels;

/// <summary>Columns the process list can be sorted by.</summary>
public enum ProcessSortColumn
{
    Name,
    Pid,
    Cpu,
    Memory,
    Io,
}

/// <summary>
/// Simplified task manager: live process list with search and sorting, details of the selected process,
/// top consumers, and ending a process after explicit confirmation.
/// </summary>
public sealed partial class ProcessesViewModel : PageViewModel
{
    private const int TopCount = 3;

    private readonly IProcessManager _processManager;
    private readonly DialogService _dialogs;
    private readonly Dictionary<ProcessIdentity, ProcessItemViewModel> _all = [];
    private ProcessItemViewModel? _selectedProcess;
    private CancellationTokenSource? _detailsLoad;
    private bool _updatingRows;

    public ProcessesViewModel(UiMetricsHub hub, IProcessManager processManager, DialogService dialogs)
        : base(hub)
    {
        _processManager = processManager;
        _dialogs = dialogs;
        SearchText = string.Empty;
        CountText = MetricFormatter.Pending;
        SortColumn = ProcessSortColumn.Cpu;
        SortDescending = true;
        SelectedRowIndex = -1;
        StatusTitle = StatusMessage = string.Empty;
        Details = new ProcessDetailsViewModel();
    }

    /// <summary>
    /// Rows of the list. Rows stay in place: when the order changes, only the process each row shows
    /// changes. Re-sorting therefore never adds, removes or moves list items, which keeps a live
    /// CPU-sorted list cheap to display.
    /// </summary>
    public ObservableCollection<ProcessRowViewModel> Rows { get; } = [];

    public ObservableCollection<TopProcessItemViewModel> TopCpu { get; } = [];

    public ObservableCollection<TopProcessItemViewModel> TopMemory { get; } = [];

    public ObservableCollection<TopProcessItemViewModel> TopIo { get; } = [];

    public ProcessDetailsViewModel Details { get; }

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    public partial string CountText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortGlyph), nameof(PidSortGlyph), nameof(CpuSortGlyph), nameof(MemorySortGlyph), nameof(IoSortGlyph))]
    public partial ProcessSortColumn SortColumn { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortGlyph), nameof(PidSortGlyph), nameof(CpuSortGlyph), nameof(MemorySortGlyph), nameof(IoSortGlyph))]
    public partial bool SortDescending { get; set; }

    public string NameSortGlyph => SortGlyph(ProcessSortColumn.Name);

    public string PidSortGlyph => SortGlyph(ProcessSortColumn.Pid);

    public string CpuSortGlyph => SortGlyph(ProcessSortColumn.Cpu);

    public string MemorySortGlyph => SortGlyph(ProcessSortColumn.Memory);

    public string IoSortGlyph => SortGlyph(ProcessSortColumn.Io);

    /// <summary>Index of the selected row, kept on the selected process when the order changes.</summary>
    [ObservableProperty]
    public partial int SelectedRowIndex { get; set; }

    /// <summary>The selected process (follows the process, not the row, when the list is re-sorted).</summary>
    public ProcessItemViewModel? SelectedProcess
    {
        get => _selectedProcess;
        private set
        {
            if (SetProperty(ref _selectedProcess, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                EndProcessCommand.NotifyCanExecuteChanged();
                _ = LoadDetailsAsync(value);
            }
        }
    }

    public bool HasSelection => _selectedProcess is not null;

    [ObservableProperty]
    public partial bool IsStatusOpen { get; set; }

    [ObservableProperty]
    public partial bool IsStatusError { get; set; }

    [ObservableProperty]
    public partial string StatusTitle { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    partial void OnSearchTextChanged(string value) => RebuildList();

    partial void OnSelectedRowIndexChanged(int value)
    {
        // Changes made while rows are refreshed only follow the selected process; they are not user choices.
        if (!_updatingRows)
        {
            SelectedProcess = value >= 0 && value < Rows.Count ? Rows[value].Item : null;
        }
    }

    [RelayCommand]
    private void Refresh() => Hub.Monitor.RequestRefresh(MetricKind.Processes);

    [RelayCommand]
    private void Sort(string column)
    {
        if (!Enum.TryParse<ProcessSortColumn>(column, out var target))
        {
            return;
        }

        if (target == SortColumn)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = target;
            SortDescending = target is not (ProcessSortColumn.Name or ProcessSortColumn.Pid);
        }

        RebuildList();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private bool CanEndProcess() => _selectedProcess is not null;

    [RelayCommand(CanExecute = nameof(CanEndProcess))]
    private async Task EndProcessAsync()
    {
        if (_selectedProcess is not { } target)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            $"End {target.Name}?",
            $"Sysora will end {target.Name} (PID {target.ProcessId}). Any unsaved work in this application will be lost.",
            "End process");
        if (!confirmed)
        {
            return;
        }

        var result = await _processManager.TerminateAsync(target.Identity, CancellationToken.None);
        if (result.Succeeded)
        {
            ShowStatus(isError: false, "Process ended", $"{target.Name} (PID {target.ProcessId}) was ended.");
            Hub.Monitor.RequestRefresh(MetricKind.Processes);
        }
        else
        {
            ShowStatus(isError: true, $"{target.Name} was not ended", result.Message ?? "Unknown error.");
        }
    }

    protected override void Update(SystemSnapshot snapshot, MetricKind updated)
    {
        if ((updated & MetricKind.Processes) == 0)
        {
            return;
        }

        if (snapshot.Processes is not { } processes)
        {
            CountText = Display.Format<ProcessSnapshot>(snapshot, MetricKind.Processes, null, _ => string.Empty);
            return;
        }

        var seen = new HashSet<ProcessIdentity>(processes.ProcessCount);
        foreach (var process in processes.Processes)
        {
            seen.Add(process.Identity);
            if (_all.TryGetValue(process.Identity, out var item))
            {
                item.Update(process);
            }
            else
            {
                _all[process.Identity] = new ProcessItemViewModel(process);
            }
        }

        foreach (var exited in _all.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            _all.Remove(exited);
        }

        RebuildList();
        UpdateTopConsumers(processes, snapshot.Memory);

        if (_selectedProcess is { } selected)
        {
            if (_all.ContainsKey(selected.Identity))
            {
                Details.UpdateLive(selected.Metrics);
            }
            else
            {
                Details.MarkExited();
            }
        }
    }

    private void RebuildList()
    {
        var filter = SearchText.Trim();
        var desired = Order(_all.Values.Where(p => p.Matches(filter))).ToList();
        CountText = filter.Length == 0
            ? MetricFormatter.Plural(_all.Count, "process", "processes")
            : $"{desired.Count.ToString(CultureInfo.CurrentCulture)} of {MetricFormatter.Plural(_all.Count, "process", "processes")}";

        _updatingRows = true;
        try
        {
            CollectionSync.Resize(Rows, desired.Count, i => new ProcessRowViewModel(desired[i]), (row, i) => row.Item = desired[i]);
            SelectedRowIndex = _selectedProcess is null ? -1 : desired.IndexOf(_selectedProcess);
        }
        finally
        {
            _updatingRows = false;
        }
    }

    private IEnumerable<ProcessItemViewModel> Order(IEnumerable<ProcessItemViewModel> items)
    {
        // Rounding to the displayed precision keeps rows from swapping places on insignificant changes.
        var ordered = SortColumn switch
        {
            ProcessSortColumn.Name => SortDescending
                ? items.OrderByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            ProcessSortColumn.Pid => SortDescending ? items.OrderByDescending(p => p.ProcessId) : items.OrderBy(p => p.ProcessId),
            ProcessSortColumn.Memory => SortDescending ? items.OrderByDescending(p => p.Memory) : items.OrderBy(p => p.Memory),
            ProcessSortColumn.Io => SortDescending
                ? items.OrderByDescending(p => Math.Round(p.Io / 1024))
                : items.OrderBy(p => Math.Round(p.Io / 1024)),
            _ => SortDescending ? items.OrderByDescending(p => Math.Round(p.Cpu, 1)) : items.OrderBy(p => Math.Round(p.Cpu, 1)),
        };

        return ordered.ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.ProcessId);
    }

    private void UpdateTopConsumers(ProcessSnapshot processes, MemoryMetrics? memory)
    {
        var groups = ProcessAggregation.GroupByName(processes.Processes);

        var topCpu = ProcessAggregation.TopByCpu(groups, TopCount);
        CollectionSync.Resize(TopCpu, topCpu.Count, _ => new TopProcessItemViewModel(), (item, i) =>
            item.Set(topCpu[i], MetricFormatter.Percent(topCpu[i].CpuPercent, 1), string.Empty, topCpu[i].CpuPercent));

        var topMemory = ProcessAggregation.TopByMemory(groups, TopCount);
        CollectionSync.Resize(TopMemory, topMemory.Count, _ => new TopProcessItemViewModel(), (item, i) =>
            item.Set(topMemory[i], MetricFormatter.Bytes(topMemory[i].PrivateWorkingSetBytes), string.Empty,
                Percentages.Of(topMemory[i].PrivateWorkingSetBytes, memory?.TotalBytes ?? 0)));

        var topIo = ProcessAggregation.TopByIo(groups, TopCount);
        var maxIo = Math.Max(topIo.Count > 0 ? topIo[0].IoBytesPerSecond : 0, 1);
        CollectionSync.Resize(TopIo, topIo.Count, _ => new TopProcessItemViewModel(), (item, i) =>
            item.Set(topIo[i], MetricFormatter.BytesPerSecond(topIo[i].IoBytesPerSecond), string.Empty, topIo[i].IoBytesPerSecond / maxIo * 100));
    }

    private async Task LoadDetailsAsync(ProcessItemViewModel? process)
    {
        _detailsLoad?.Cancel();
        _detailsLoad?.Dispose();
        _detailsLoad = null;

        if (process is null)
        {
            Details.Clear();
            return;
        }

        Details.Show(process.Metrics);
        var load = new CancellationTokenSource();
        _detailsLoad = load;
        try
        {
            var details = await _processManager.GetDetailsAsync(process.Identity, load.Token);
            if (!load.IsCancellationRequested)
            {
                Details.Apply(details);
            }
        }
        catch (OperationCanceledException)
        {
            // Another process was selected meanwhile.
        }
    }

    private void ShowStatus(bool isError, string title, string message)
    {
        IsStatusError = isError;
        StatusTitle = title;
        StatusMessage = message;
        IsStatusOpen = true;
    }

    private string SortGlyph(ProcessSortColumn column) =>
        column != SortColumn ? string.Empty : SortDescending ? "" : "";
}

/// <summary>Details panel of the selected process.</summary>
public sealed partial class ProcessDetailsViewModel : ObservableObject
{
    public ProcessDetailsViewModel() => Clear();

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Pid { get; set; }

    [ObservableProperty]
    public partial string Parent { get; set; }

    [ObservableProperty]
    public partial string Started { get; set; }

    [ObservableProperty]
    public partial string Cpu { get; set; }

    [ObservableProperty]
    public partial string Memory { get; set; }

    [ObservableProperty]
    public partial string WorkingSet { get; set; }

    [ObservableProperty]
    public partial string PrivateBytes { get; set; }

    [ObservableProperty]
    public partial string IoRead { get; set; }

    [ObservableProperty]
    public partial string IoWrite { get; set; }

    [ObservableProperty]
    public partial string Threads { get; set; }

    [ObservableProperty]
    public partial string Handles { get; set; }

    [ObservableProperty]
    public partial string Session { get; set; }

    [ObservableProperty]
    public partial string Path { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    public partial string Company { get; set; }

    [ObservableProperty]
    public partial string Version { get; set; }

    [ObservableProperty]
    public partial bool IsCritical { get; set; }

    [ObservableProperty]
    public partial bool HasExited { get; set; }

    public void Clear()
    {
        Name = Pid = Parent = Started = Cpu = Memory = WorkingSet = PrivateBytes = string.Empty;
        IoRead = IoWrite = Threads = Handles = Session = Path = Description = Company = Version = string.Empty;
        IsCritical = false;
        HasExited = false;
    }

    public void Show(ProcessMetrics process)
    {
        var culture = CultureInfo.CurrentCulture;
        Name = process.Name;
        Pid = process.ProcessId.ToString(culture);
        Parent = process.ParentProcessId > 0 ? process.ParentProcessId.ToString(culture) : MetricFormatter.NotAvailable;
        Started = process.StartTime is { } start
            ? start.ToLocalTime().ToString(start.Date == DateTimeOffset.Now.Date ? "T" : "g", culture)
            : MetricFormatter.NotAvailable;
        Session = process.SessionId.ToString(culture);
        Path = Description = Company = Version = "Loading…";
        IsCritical = false;
        HasExited = false;
        UpdateLive(process);
    }

    public void UpdateLive(ProcessMetrics process)
    {
        var culture = CultureInfo.CurrentCulture;
        Cpu = process.CpuPercent is { } cpu ? MetricFormatter.Percent(cpu, 1) : MetricFormatter.Pending;
        Memory = MetricFormatter.Bytes(process.PrivateWorkingSetBytes);
        WorkingSet = MetricFormatter.Bytes(process.WorkingSetBytes);
        PrivateBytes = MetricFormatter.Bytes(process.PrivateBytes);
        IoRead = process.IoReadBytesPerSecond is { } read ? MetricFormatter.BytesPerSecond(read) : MetricFormatter.Pending;
        IoWrite = process.IoWriteBytesPerSecond is { } write ? MetricFormatter.BytesPerSecond(write) : MetricFormatter.Pending;
        Threads = process.ThreadCount.ToString("N0", culture);
        Handles = process.HandleCount.ToString("N0", culture);
    }

    public void Apply(ProcessDetails details)
    {
        Path = details.ExecutablePath ?? details.ExecutablePathError ?? MetricFormatter.NotAvailable;
        // Shown as a subtitle under the name: leave it empty rather than repeating "Not available".
        Description = details.Description ?? string.Empty;
        Company = details.Company ?? MetricFormatter.NotAvailable;
        Version = details.FileVersion ?? MetricFormatter.NotAvailable;
        IsCritical = details.IsCritical == true;
    }

    public void MarkExited()
    {
        HasExited = true;
        Cpu = IoRead = IoWrite = MetricFormatter.NotAvailable;
    }
}
