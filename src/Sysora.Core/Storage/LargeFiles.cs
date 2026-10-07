using System.Globalization;
using Sysora.Core.Formatting;

namespace Sysora.Core.Storage;

/// <summary>What a large file is, when its name or location says so reliably.</summary>
public enum LargeFileCategory
{
    Other,
    Video,
    Audio,
    Image,
    Archive,
    DiskImage,
    VirtualDisk,
    Installer,
    Backup,
    Database,
    Log,
    Document,
    GameData,
    WindowsManaged,
    RecycleBin,
}

/// <summary>A large file found by a scan. Sysora never moves, modifies or deletes it.</summary>
/// <param name="Name">File name.</param>
/// <param name="FullPath">Full path.</param>
/// <param name="Directory">Folder containing it.</param>
/// <param name="SizeBytes">Size (logical size, as Explorer shows it).</param>
/// <param name="Modified">Last write time, when available.</param>
/// <param name="Extension">Extension in lower case (".zip"), or empty.</param>
/// <param name="Volume">Volume root, e.g. "C:\".</param>
/// <param name="Category">Category, "Other" when not reliable.</param>
/// <param name="Note">Caution about the file, e.g. "Managed by Windows: do not delete it manually."</param>
public sealed record LargeFile(string Name, string FullPath, string Directory, long SizeBytes, DateTimeOffset? Modified, string Extension, string Volume, LargeFileCategory Category, string? Note);

/// <summary>Files of a scan grouped by category, extension or folder.</summary>
/// <param name="Name">Group name.</param>
/// <param name="Count">Files in the group.</param>
/// <param name="TotalBytes">Their total size.</param>
public sealed record LargeFileGroup(string Name, int Count, long TotalBytes);

/// <summary>A folder deliberately not scanned, and why.</summary>
public sealed record LargeFileExclusion(string Path, string Reason);

/// <summary>How a scan ended.</summary>
public enum LargeFileScanOutcome
{
    Completed,
    Cancelled,
    Failed,
}

/// <summary>What to scan.</summary>
/// <param name="Roots">Folders or volume roots to scan.</param>
public sealed record LargeFileScanRequest(IReadOnlyList<string> Roots)
{
    /// <summary>Files smaller than this are counted but not listed.</summary>
    public long MinimumSizeBytes { get; init; } = 100L * 1024 * 1024;

    /// <summary>Most files listed (the largest ones).</summary>
    public int MaxResults { get; init; } = 500;

    /// <summary>Folders skipped on purpose (system folders whose sizes are misleading or that cannot be read).</summary>
    public IReadOnlyList<LargeFileExclusion> Exclusions { get; init; } = [];
}

/// <summary>Progress of a scan in progress.</summary>
public sealed record LargeFileScanProgress(int Directories, long Files, long BytesSeen, int Found, string? CurrentDirectory);

/// <summary>Result of a scan: the largest files, their groups, and everything that could not be read.</summary>
public sealed record LargeFileScanResult
{
    public required DateTimeOffset Started { get; init; }

    public required DateTimeOffset Finished { get; init; }

    public required IReadOnlyList<string> Roots { get; init; }

    public required long MinimumSizeBytes { get; init; }

    public required LargeFileScanOutcome Outcome { get; init; }

    /// <summary>"Scan completed: …", "Scan cancelled: …".</summary>
    public required string Message { get; init; }

    /// <summary>The largest files found, largest first (at most the requested number).</summary>
    public IReadOnlyList<LargeFile> Files { get; init; } = [];

    /// <summary>Files at or above the minimum size (may be more than <see cref="Files"/>).</summary>
    public int MatchingFiles { get; init; }

    public long MatchingBytes { get; init; }

    public IReadOnlyList<LargeFileGroup> ByCategory { get; init; } = [];

    public IReadOnlyList<LargeFileGroup> ByExtension { get; init; } = [];

    /// <summary>Folders holding the most space in large files.</summary>
    public IReadOnlyList<LargeFileGroup> ByFolder { get; init; } = [];

    public int DirectoriesScanned { get; init; }

    public long FilesScanned { get; init; }

    /// <summary>Total size of the files seen (not counting cloud-only placeholders).</summary>
    public long BytesSeen { get; init; }

    /// <summary>Folders Windows refused to list: their content is unknown, not absent.</summary>
    public int AccessDenied { get; init; }

    public IReadOnlyList<string> AccessDeniedSamples { get; init; } = [];

    /// <summary>Folders that could not be read for another reason (removed during the scan, device error, path too long).</summary>
    public int Unavailable { get; init; }

    public IReadOnlyList<string> UnavailableSamples { get; init; } = [];

    /// <summary>Links and junctions not followed (they point to data counted elsewhere or could loop).</summary>
    public int SkippedLinks { get; init; }

    /// <summary>Online-only files of a cloud provider: listed by Windows but not stored on this PC.</summary>
    public int CloudOnlyFiles { get; init; }

    public IReadOnlyList<LargeFileExclusion> Exclusions { get; init; } = [];

    public bool IsTruncated => Files.Count < MatchingFiles;

    public TimeSpan Duration => Finished - Started;
}

/// <summary>One entry of a folder listing.</summary>
/// <param name="Name">File or folder name.</param>
/// <param name="FullPath">Full path.</param>
/// <param name="IsDirectory">True for a folder.</param>
/// <param name="Length">Size of a file.</param>
/// <param name="LastWrite">Last write time.</param>
/// <param name="Attributes">File attributes.</param>
public readonly record struct DirectoryEntry(string Name, string FullPath, bool IsDirectory, long Length, DateTimeOffset? LastWrite, FileAttributes Attributes);

/// <summary>Lists one folder (not recursively). Read-only: never opens a file.</summary>
public interface IDirectoryReader
{
    /// <summary>
    /// The entries of <paramref name="directory"/>. Throws <see cref="UnauthorizedAccessException"/> when access is denied and
    /// <see cref="IOException"/> when the folder is not available.
    /// </summary>
    IEnumerable<DirectoryEntry> Enumerate(string directory);
}

/// <summary>Runs scans for large files (an explicit user action; never in the background on its own).</summary>
public interface ILargeFileScanner
{
    Task<LargeFileScanResult> ScanAsync(LargeFileScanRequest request, IProgress<LargeFileScanProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Walks folders looking for large files (deterministic given a <see cref="IDirectoryReader"/>). Memory stays bounded: the
/// largest files are kept in a fixed-size heap and the groups in capped dictionaries. Links and junctions are not
/// followed; folders that cannot be read are counted and reported, never treated as empty.
/// </summary>
public sealed class LargeFileScanEngine(IDirectoryReader reader, TimeProvider? timeProvider = null)
{
    /// <summary>Progress is reported at most this often.</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Most folders kept in the per-folder totals.</summary>
    public const int MaxFolders = 5000;

    /// <summary>Most extensions kept in the per-extension totals.</summary>
    public const int MaxExtensions = 1000;

    /// <summary>Paths of unreadable folders kept as examples.</summary>
    public const int MaxSamples = 20;

    // FILE_ATTRIBUTE_RECALL_ON_OPEN and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: placeholders of cloud files.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public LargeFileScanResult Scan(LargeFileScanRequest request, IProgress<LargeFileScanProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = _time.GetUtcNow();
        var top = new PriorityQueue<LargeFile, long>();
        var categories = new Dictionary<LargeFileCategory, (int Count, long Bytes)>();
        var extensions = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        var denied = new List<string>();
        var unavailable = new List<string>();
        var excluded = request.Exclusions.Select(e => (Path: Normalize(e.Path), Exclusion: e)).ToArray();
        var hit = new List<LargeFileExclusion>();
        int directories = 0, deniedCount = 0, unavailableCount = 0, links = 0, cloud = 0, matching = 0;
        long files = 0, bytesSeen = 0, matchingBytes = 0;
        var lastProgress = _time.GetTimestamp();
        var outcome = LargeFileScanOutcome.Completed;
        var stack = new Stack<string>();
        foreach (var root in request.Roots.Distinct(StringComparer.OrdinalIgnoreCase).Reverse())
        {
            stack.Push(root);
        }

        try
        {
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = stack.Pop();
                var normalized = Normalize(directory);
                if (excluded.FirstOrDefault(e => string.Equals(e.Path, normalized, StringComparison.OrdinalIgnoreCase)) is { Exclusion: { } exclusion })
                {
                    hit.Add(exclusion);
                    continue;
                }

                directories++;
                var volume = Path.GetPathRoot(directory) ?? string.Empty;
                var subdirectories = new List<string>();
                try
                {
                    foreach (var entry in reader.Enumerate(directory))
                    {
                        if (entry.IsDirectory)
                        {
                            // Junctions and symbolic links point to data counted elsewhere (or loop): never followed.
                            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                links++;
                            }
                            else
                            {
                                subdirectories.Add(entry.FullPath);
                            }

                            continue;
                        }

                        if ((++files & 1023) == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        if ((entry.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
                        {
                            cloud++;
                            continue;
                        }

                        bytesSeen += entry.Length;
                        if (entry.Length < request.MinimumSizeBytes)
                        {
                            continue;
                        }

                        matching++;
                        matchingBytes += entry.Length;
                        var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                        var (category, note) = LargeFileCategorizer.Categorize(entry.Name, entry.FullPath, extension);
                        Add(categories, category, entry.Length, int.MaxValue);
                        Add(extensions, extension.Length == 0 ? "(no extension)" : extension, entry.Length, MaxExtensions);
                        Add(folders, directory, entry.Length, MaxFolders);
                        var file = new LargeFile(entry.Name, entry.FullPath, directory, entry.Length, entry.LastWrite, extension, volume, category, note);
                        if (top.Count < request.MaxResults)
                        {
                            top.Enqueue(file, entry.Length);
                        }
                        else if (top.TryPeek(out _, out var smallest) && entry.Length > smallest)
                        {
                            top.DequeueEnqueue(file, entry.Length);
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    deniedCount++;
                    if (denied.Count < MaxSamples)
                    {
                        denied.Add(directory);
                    }
                }
                catch (IOException)
                {
                    unavailableCount++;
                    if (unavailable.Count < MaxSamples)
                    {
                        unavailable.Add(directory);
                    }
                }

                // Visit subfolders in listing order (the stack pops the last pushed first).
                for (var i = subdirectories.Count - 1; i >= 0; i--)
                {
                    stack.Push(subdirectories[i]);
                }

                if (progress is not null && _time.GetElapsedTime(lastProgress) >= ProgressInterval)
                {
                    lastProgress = _time.GetTimestamp();
                    progress.Report(new LargeFileScanProgress(directories, files, bytesSeen, matching, directory));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = LargeFileScanOutcome.Cancelled;
        }

        var found = new List<LargeFile>(top.Count);
        while (top.TryDequeue(out var file, out _))
        {
            found.Add(file);
        }

        found.Reverse();
        progress?.Report(new LargeFileScanProgress(directories, files, bytesSeen, matching, null));
        var finished = _time.GetUtcNow();
        return new LargeFileScanResult
        {
            Started = started,
            Finished = finished,
            Roots = request.Roots,
            MinimumSizeBytes = request.MinimumSizeBytes,
            Outcome = outcome,
            Message = Message(outcome, matching, matchingBytes, request.MinimumSizeBytes, directories, deniedCount, unavailableCount, finished - started),
            Files = found,
            MatchingFiles = matching,
            MatchingBytes = matchingBytes,
            ByCategory = Groups(categories.ToDictionary(c => LargeFileCategorizer.Name(c.Key), c => c.Value)),
            ByExtension = Groups(extensions),
            ByFolder = Groups(folders).Take(50).ToArray(),
            DirectoriesScanned = directories,
            FilesScanned = files,
            BytesSeen = bytesSeen,
            AccessDenied = deniedCount,
            AccessDeniedSamples = denied,
            Unavailable = unavailableCount,
            UnavailableSamples = unavailable,
            SkippedLinks = links,
            CloudOnlyFiles = cloud,
            Exclusions = hit,
        };
    }

    private static string Message(LargeFileScanOutcome outcome, int matching, long bytes, long minimum, int directories, int denied, int unavailable, TimeSpan duration)
    {
        var found = $"{MetricFormatter.Plural(matching, "file")} of {MetricFormatter.Bytes(minimum)} or more ({MetricFormatter.Bytes(bytes)}) in {directories.ToString("N0", CultureInfo.CurrentCulture)} folders";
        var problems = new List<string>();
        if (denied > 0)
        {
            problems.Add($"{MetricFormatter.Plural(denied, "folder")} could not be read (access denied)");
        }

        if (unavailable > 0)
        {
            problems.Add($"{MetricFormatter.Plural(unavailable, "folder")} unavailable");
        }

        var suffix = problems.Count > 0 ? $". {string.Join(", ", problems)}: their content is unknown, not absent." : ".";
        return outcome switch
        {
            LargeFileScanOutcome.Cancelled => $"Scan cancelled after {MetricFormatter.DurationPrecise(duration)}: partial results, {found}{suffix}",
            LargeFileScanOutcome.Failed => "Scan failed.",
            _ => $"Scan completed in {MetricFormatter.DurationPrecise(duration)}: {found}{suffix}",
        };
    }

    private static void Add<TKey>(Dictionary<TKey, (int Count, long Bytes)> groups, TKey key, long bytes, int max)
        where TKey : notnull
    {
        if (groups.TryGetValue(key, out var existing))
        {
            groups[key] = (existing.Count + 1, existing.Bytes + bytes);
        }
        else if (groups.Count < max)
        {
            groups[key] = (1, bytes);
        }
    }

    private static IReadOnlyList<LargeFileGroup> Groups(Dictionary<string, (int Count, long Bytes)> groups) =>
        groups.Select(g => new LargeFileGroup(g.Key, g.Value.Count, g.Value.Bytes))
            .OrderByDescending(g => g.TotalBytes)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string Normalize(string path) => path.TrimEnd('\\', '/');
}

/// <summary>Categories and cautions from a file's name, location and extension (pure).</summary>
public static class LargeFileCategorizer
{
    private const string WindowsNote = "Managed by Windows: do not delete it manually.";

    private static readonly Dictionary<string, LargeFileCategory> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = LargeFileCategory.Video, [".mkv"] = LargeFileCategory.Video, [".mov"] = LargeFileCategory.Video, [".avi"] = LargeFileCategory.Video,
        [".wmv"] = LargeFileCategory.Video, [".webm"] = LargeFileCategory.Video, [".m4v"] = LargeFileCategory.Video, [".ts"] = LargeFileCategory.Video,
        [".mp3"] = LargeFileCategory.Audio, [".wav"] = LargeFileCategory.Audio, [".flac"] = LargeFileCategory.Audio, [".m4a"] = LargeFileCategory.Audio,
        [".jpg"] = LargeFileCategory.Image, [".png"] = LargeFileCategory.Image, [".psd"] = LargeFileCategory.Image, [".tif"] = LargeFileCategory.Image,
        [".tiff"] = LargeFileCategory.Image, [".raw"] = LargeFileCategory.Image, [".dng"] = LargeFileCategory.Image,
        [".zip"] = LargeFileCategory.Archive, [".7z"] = LargeFileCategory.Archive, [".rar"] = LargeFileCategory.Archive, [".tar"] = LargeFileCategory.Archive,
        [".gz"] = LargeFileCategory.Archive, [".xz"] = LargeFileCategory.Archive, [".zst"] = LargeFileCategory.Archive, [".cab"] = LargeFileCategory.Archive,
        [".iso"] = LargeFileCategory.DiskImage, [".img"] = LargeFileCategory.DiskImage, [".dmg"] = LargeFileCategory.DiskImage, [".wim"] = LargeFileCategory.DiskImage,
        [".esd"] = LargeFileCategory.DiskImage,
        [".vhd"] = LargeFileCategory.VirtualDisk, [".vhdx"] = LargeFileCategory.VirtualDisk, [".vmdk"] = LargeFileCategory.VirtualDisk, [".vdi"] = LargeFileCategory.VirtualDisk,
        [".qcow2"] = LargeFileCategory.VirtualDisk,
        [".msi"] = LargeFileCategory.Installer, [".msp"] = LargeFileCategory.Installer, [".msix"] = LargeFileCategory.Installer, [".appx"] = LargeFileCategory.Installer,
        [".bak"] = LargeFileCategory.Backup, [".bkf"] = LargeFileCategory.Backup,
        [".db"] = LargeFileCategory.Database, [".sqlite"] = LargeFileCategory.Database, [".mdf"] = LargeFileCategory.Database, [".ldf"] = LargeFileCategory.Database,
        [".log"] = LargeFileCategory.Log, [".etl"] = LargeFileCategory.Log, [".dmp"] = LargeFileCategory.Log,
        [".pdf"] = LargeFileCategory.Document, [".pptx"] = LargeFileCategory.Document, [".docx"] = LargeFileCategory.Document, [".xlsx"] = LargeFileCategory.Document,
        [".pak"] = LargeFileCategory.GameData, [".ucas"] = LargeFileCategory.GameData, [".utoc"] = LargeFileCategory.GameData, [".bundle"] = LargeFileCategory.GameData,
        [".vpk"] = LargeFileCategory.GameData,
    };

    public static (LargeFileCategory Category, string? Note) Categorize(string name, string fullPath, string extension)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(fullPath);
        if (name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) || name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase))
        {
            return (LargeFileCategory.WindowsManaged, "Windows page file. Its size is set in System › Advanced system settings; do not delete it manually.");
        }

        if (name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase))
        {
            return (LargeFileCategory.WindowsManaged, "Hibernation file (also used by Fast Startup). Managed by Windows: do not delete it manually.");
        }

        if (fullPath.Contains("\\$Recycle.Bin\\", StringComparison.OrdinalIgnoreCase))
        {
            return (LargeFileCategory.RecycleBin, "In the Recycle Bin: emptying the Recycle Bin frees this space.");
        }

        if (fullPath.Contains("\\Windows\\Installer\\", StringComparison.OrdinalIgnoreCase))
        {
            return (LargeFileCategory.WindowsManaged, "Windows Installer cache: needed to repair or remove applications. Do not delete it manually.");
        }

        if (fullPath.Contains("\\Windows\\", StringComparison.OrdinalIgnoreCase) || name.Equals("MEMORY.DMP", StringComparison.OrdinalIgnoreCase))
        {
            return (LargeFileCategory.WindowsManaged, WindowsNote);
        }

        var category = Extensions.GetValueOrDefault(extension, LargeFileCategory.Other);
        return category switch
        {
            LargeFileCategory.VirtualDisk => (category, "Virtual disk: it may belong to WSL, Hyper-V, Docker or a virtual machine and hold its data."),
            LargeFileCategory.Database => (category, "Database file: an application keeps its data in it."),
            LargeFileCategory.GameData => (category, "Game data: removing it breaks the game; uninstall the game instead."),
            _ => (category, null),
        };
    }

    public static string Name(LargeFileCategory category) => category switch
    {
        LargeFileCategory.Video => "Videos",
        LargeFileCategory.Audio => "Audio",
        LargeFileCategory.Image => "Images",
        LargeFileCategory.Archive => "Archives",
        LargeFileCategory.DiskImage => "Disk images",
        LargeFileCategory.VirtualDisk => "Virtual disks",
        LargeFileCategory.Installer => "Installers",
        LargeFileCategory.Backup => "Backups",
        LargeFileCategory.Database => "Databases",
        LargeFileCategory.Log => "Logs and dumps",
        LargeFileCategory.Document => "Documents",
        LargeFileCategory.GameData => "Game data",
        LargeFileCategory.WindowsManaged => "Managed by Windows",
        LargeFileCategory.RecycleBin => "Recycle Bin",
        _ => "Other",
    };
}

/// <summary>
/// Runs one large-file scan at a time on demand and keeps the last result in memory, so the page shows it again without
/// scanning the disk a second time.
/// </summary>
public sealed class LargeFileService(ILargeFileScanner scanner)
{
    private readonly Lock _lock = new();
    private CancellationTokenSource? _scan;
    private LargeFileScanResult? _last;

    /// <summary>Raised (on a background thread) with the progress of the scan in progress.</summary>
    public event EventHandler<LargeFileScanProgress>? ProgressChanged;

    public bool IsScanning
    {
        get
        {
            lock (_lock)
            {
                return _scan is not null;
            }
        }
    }

    /// <summary>The last finished scan of this session.</summary>
    public LargeFileScanResult? LastResult => Volatile.Read(ref _last);

    /// <summary>Scans; refuses (returns null) when a scan is already running.</summary>
    public async Task<LargeFileScanResult?> ScanAsync(LargeFileScanRequest request, CancellationToken cancellationToken)
    {
        CancellationTokenSource scan;
        lock (_lock)
        {
            if (_scan is not null)
            {
                return null;
            }

            scan = _scan = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        try
        {
            var progress = new Progress(this);
            var result = await scanner.ScanAsync(request, progress, scan.Token).ConfigureAwait(false);
            Volatile.Write(ref _last, result);
            return result;
        }
        finally
        {
            lock (_lock)
            {
                _scan = null;
            }

            scan.Dispose();
        }
    }

    /// <summary>Cancels the scan in progress; its partial results are returned by <see cref="ScanAsync"/>.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _scan?.Cancel();
        }
    }

    private sealed class Progress(LargeFileService service) : IProgress<LargeFileScanProgress>
    {
        public void Report(LargeFileScanProgress value) => service.ProgressChanged?.Invoke(service, value);
    }
}
