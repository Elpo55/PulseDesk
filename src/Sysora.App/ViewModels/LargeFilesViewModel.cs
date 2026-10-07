using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Sysora.App.Services;
using Sysora.Core.Formatting;
using Sysora.Core.Models;
using Sysora.Core.Reports;
using Sysora.Core.Storage;
using Sysora.Infrastructure.Storage;

namespace Sysora.App.ViewModels;

/// <summary>A place that can be scanned.</summary>
/// <param name="Roots">Folders scanned.</param>
/// <param name="Label">Display text.</param>
public sealed record ScanTargetOption(IReadOnlyList<string> Roots, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A minimum file size.</summary>
public sealed record SizeOption(long Bytes, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Large Files: finds the files taking the most space, on demand. Read-only: Sysora lists files and can show them in
/// File Explorer; it never moves, modifies or deletes anything.
/// </summary>
public sealed partial class LargeFilesViewModel : PageViewModel
{
    private const long MB = 1024L * 1024;

    private readonly LargeFileService _service;
    private readonly PickerService _pickers;
    private readonly ExternalLauncher _launcher;
    private readonly ReportExportService _export;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<LargeFilesViewModel> _logger;
    private LargeFileScanResult? _result;
    private long _lastProgress;

    public LargeFilesViewModel(UiMetricsHub hub, LargeFileService service, PickerService pickers, ExternalLauncher launcher, ReportExportService export, DispatcherQueue dispatcher, ILogger<LargeFilesViewModel> logger)
        : base(hub)
    {
        _service = service;
        _pickers = pickers;
        _launcher = launcher;
        _export = export;
        _dispatcher = dispatcher;
        _logger = logger;
        MinimumSize = MinimumSizes[1];
        ProgressText = CurrentFolder = Message = ProblemsText = SearchText = LastScanText = string.Empty;
        Category = "All types";
        _service.ProgressChanged += (_, progress) =>
        {
            // Progress arrives from the scan thread up to four times a second: keep at most one UI update queued.
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastProgress) < 200)
            {
                return;
            }

            Interlocked.Exchange(ref _lastProgress, now);
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => ShowProgress(progress));
        };
    }

    public ObservableCollection<ScanTargetOption> Targets { get; } = [];

    public IReadOnlyList<SizeOption> MinimumSizes { get; } =
    [
        new(50 * MB, "50 MB or more"),
        new(100 * MB, "100 MB or more"),
        new(500 * MB, "500 MB or more"),
        new(1024 * MB, "1 GB or more"),
        new(5 * 1024 * MB, "5 GB or more"),
    ];

    public IReadOnlyList<string> Views { get; } = ["Files", "By type", "By extension", "By folder"];

    public ObservableCollection<string> Categories { get; } = ["All types"];

    public ObservableCollection<LargeFileItemViewModel> Files { get; } = [];

    public ObservableCollection<LargeFileGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    public partial ScanTargetOption? Target { get; set; }

    [ObservableProperty]
    public partial SizeOption MinimumSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFiles), nameof(ShowGroups))]
    public partial int ViewIndex { get; set; }

    public bool ShowFiles => ViewIndex == 0 && HasResult;

    public bool ShowGroups => ViewIndex != 0 && HasResult;

    [ObservableProperty]
    public partial string Category { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CancelCommand), nameof(ChooseFolderCommand))]
    public partial bool IsScanning { get; set; }

    public bool IsIdle => !IsScanning;

    [ObservableProperty]
    public partial string ProgressText { get; set; }

    [ObservableProperty]
    public partial string CurrentFolder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFiles), nameof(ShowGroups))]
    public partial bool HasResult { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity MessageSeverity { get; set; }

    [ObservableProperty]
    public partial string ProblemsText { get; set; }

    [ObservableProperty]
    public partial bool HasProblems { get; set; }

    [ObservableProperty]
    public partial string LastScanText { get; set; }

    partial void OnViewIndexChanged(int value) => ShowGroupsOf(_result);

    partial void OnCategoryChanged(string value) => Filter();

    partial void OnSearchTextChanged(string value) => Filter();

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task Scan()
    {
        if (Target is not { } target)
        {
            return;
        }

        IsScanning = true;
        ProgressText = "Starting…";
        CurrentFolder = string.Empty;
        try
        {
            var request = new LargeFileScanRequest(target.Roots)
            {
                MinimumSizeBytes = MinimumSize.Bytes,
                Exclusions = FileSystemLargeFileScanner.DefaultExclusions(target.Roots),
            };
            var result = await _service.ScanAsync(request, CancellationToken.None);
            if (result is not null)
            {
                Show(result);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "The large-file scan failed.");
            Message = $"The scan failed: {ex.Message}";
            MessageSeverity = InfoBarSeverity.Error;
            HasResult = false;
        }
        finally
        {
            IsScanning = false;
            ProgressText = CurrentFolder = string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void Cancel() => _service.Cancel();

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ChooseFolder()
    {
        if (await _pickers.PickFolderAsync() is not { } folder)
        {
            return;
        }

        var option = new ScanTargetOption([folder], $"Folder: {folder}");
        Targets.Add(option);
        Target = option;
    }

    [RelayCommand]
    private Task Export()
    {
        if (_result is not { } result)
        {
            return Task.CompletedTask;
        }

        return _export.ExportAsync(system => ReportBuilder.LargeFiles(result, DateTimeOffset.Now, system));
    }

    protected override void OnActivated()
    {
        LoadTargets();
        IsScanning = _service.IsScanning;
        if (_result is null && _service.LastResult is { } last)
        {
            Show(last);
        }
    }

    protected override void Update(SystemSnapshot snapshot, MetricKind updated)
    {
        if ((updated & MetricKind.Storage) != 0 && Targets.Count == 0)
        {
            LoadTargets();
        }
    }

    /// <summary>Volumes to offer: fixed and removable ones known to the monitor (network drives are never scanned).</summary>
    private void LoadTargets()
    {
        var volumes = (Hub.Snapshot.Storage ?? [])
            .Where(v => v.Kind is DriveKind.Fixed or DriveKind.Removable)
            .OrderByDescending(v => v.IsSystemDrive)
            .ThenBy(v => v.Drive, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var options = new List<ScanTargetOption>();
        foreach (var volume in volumes)
        {
            var label = volume.Label is { } name ? $" {name}" : string.Empty;
            var system = volume.IsSystemDrive ? " (Windows)" : string.Empty;
            options.Add(new ScanTargetOption([volume.Drive], $"{volume.Letter}{label}{system} · {MetricFormatter.Bytes(volume.FreeBytes)} free of {MetricFormatter.Bytes(volume.TotalBytes)}"));
        }

        var fixedVolumes = volumes.Where(v => v.Kind == DriveKind.Fixed).Select(v => v.Drive).ToArray();
        if (fixedVolumes.Length > 1)
        {
            options.Add(new ScanTargetOption(fixedVolumes, "All fixed volumes"));
        }

        var custom = Targets.Where(t => t.Label.StartsWith("Folder: ", StringComparison.Ordinal)).ToArray();
        var selected = Target?.Label;
        Targets.Clear();
        foreach (var option in options.Concat(custom))
        {
            Targets.Add(option);
        }

        Target = Targets.FirstOrDefault(t => t.Label == selected) ?? Targets.FirstOrDefault();
    }

    private void ShowProgress(LargeFileScanProgress progress)
    {
        if (!IsScanning)
        {
            return;
        }

        ProgressText = $"{progress.Directories:N0} folders · {progress.Files:N0} files · {MetricFormatter.Bytes(progress.BytesSeen)} seen · {MetricFormatter.Plural(progress.Found, "large file")} so far";
        CurrentFolder = progress.CurrentDirectory ?? string.Empty;
    }

    private void Show(LargeFileScanResult result)
    {
        _result = result;
        HasResult = true;
        Message = result.Message;
        MessageSeverity = result.Outcome switch
        {
            LargeFileScanOutcome.Cancelled => InfoBarSeverity.Warning,
            LargeFileScanOutcome.Failed => InfoBarSeverity.Error,
            _ => result.AccessDenied > 0 || result.Unavailable > 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Success,
        };
        LastScanText = $"Scanned {InsightDisplay.Time(result.Finished)}: {string.Join(", ", result.Roots)} · {MetricFormatter.Plural(result.MatchingFiles, "file")} of {MetricFormatter.Bytes(result.MinimumSizeBytes)} or more"
            + (result.IsTruncated ? $" (the {result.Files.Count} largest are listed)" : string.Empty);
        var problems = new List<string>();
        if (result.AccessDenied > 0)
        {
            problems.Add($"Access denied: {MetricFormatter.Plural(result.AccessDenied, "folder")} could not be read, so their content is unknown (not empty). For example: {string.Join(", ", result.AccessDeniedSamples.Take(3))}.");
        }

        if (result.Unavailable > 0)
        {
            problems.Add($"Unavailable: {MetricFormatter.Plural(result.Unavailable, "folder")} could not be read (removed during the scan, device error or path too long).");
        }

        if (result.CloudOnlyFiles > 0)
        {
            problems.Add($"{MetricFormatter.Plural(result.CloudOnlyFiles, "online-only file")} skipped: they are stored in the cloud, not on this PC.");
        }

        if (result.SkippedLinks > 0)
        {
            problems.Add($"{MetricFormatter.Plural(result.SkippedLinks, "link")} not followed (junctions and symbolic links point to data counted elsewhere).");
        }

        problems.AddRange(result.Exclusions.Select(e => $"Not scanned: {e.Path} — {e.Reason}"));
        ProblemsText = string.Join(Environment.NewLine, problems);
        HasProblems = problems.Count > 0;

        var categories = result.Files.Select(f => LargeFileCategorizer.Name(f.Category)).Distinct().Order(StringComparer.CurrentCulture).ToList();
        Categories.Clear();
        Categories.Add("All types");
        foreach (var category in categories)
        {
            Categories.Add(category);
        }

        Category = "All types";
        Filter();
        ShowGroupsOf(result);
    }

    private void Filter()
    {
        if (_result is not { } result)
        {
            return;
        }

        var search = SearchText.Trim();
        var category = Category;
        var largest = result.Files.Count > 0 ? result.Files[0].SizeBytes : 1;
        var files = result.Files.Where(f =>
            (category == "All types" || LargeFileCategorizer.Name(f.Category) == category)
            && (search.Length == 0 || f.FullPath.Contains(search, StringComparison.OrdinalIgnoreCase)));
        Files.Clear();
        foreach (var file in files)
        {
            Files.Add(new LargeFileItemViewModel(file, largest, _launcher));
        }
    }

    private void ShowGroupsOf(LargeFileScanResult? result)
    {
        Groups.Clear();
        if (result is null || ViewIndex == 0)
        {
            return;
        }

        var groups = ViewIndex switch
        {
            1 => result.ByCategory,
            2 => result.ByExtension,
            _ => result.ByFolder,
        };
        var total = Math.Max(result.MatchingBytes, 1);
        foreach (var group in groups.Take(100))
        {
            Groups.Add(new LargeFileGroupViewModel(group, total));
        }
    }
}

/// <summary>One large file.</summary>
public sealed partial class LargeFileItemViewModel(LargeFile file, long largest, ExternalLauncher launcher)
{
    public string Name { get; } = file.Name;

    public string Folder { get; } = file.Directory;

    public string Size { get; } = MetricFormatter.Bytes(file.SizeBytes);

    public double Share { get; } = file.SizeBytes * 100.0 / Math.Max(largest, 1);

    public string Type { get; } = LargeFileCategorizer.Name(file.Category) + (file.Extension.Length > 0 ? $" ({file.Extension})" : string.Empty);

    public string Modified { get; } = file.Modified is { } modified ? modified.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) : MetricFormatter.NotAvailable;

    public string Note { get; } = file.Note ?? string.Empty;

    public bool HasNote => Note.Length > 0;

    [RelayCommand]
    private void ShowInFolder() => launcher.ShowInFolder(file.FullPath);

    [RelayCommand]
    private void CopyPath()
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(file.FullPath);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }
}

/// <summary>A group of large files (type, extension or folder).</summary>
public sealed class LargeFileGroupViewModel(LargeFileGroup group, long total)
{
    public string Name { get; } = group.Name;

    public string Count { get; } = MetricFormatter.Plural(group.Count, "file");

    public string Size { get; } = MetricFormatter.Bytes(group.TotalBytes);

    public double Share { get; } = group.TotalBytes * 100.0 / total;
}
