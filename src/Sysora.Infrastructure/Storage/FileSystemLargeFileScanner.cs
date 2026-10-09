using System.IO.Enumeration;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sysora.Core.Storage;
using Sysora.Localization;

namespace Sysora.Infrastructure.Storage;

/// <summary>
/// Lists a folder with <see cref="FileSystemEnumerable{TResult}"/>: names, sizes, dates and attributes come from the
/// directory listing itself, so no file is ever opened. Access errors are thrown (not hidden) so the scan can report them.
/// </summary>
public sealed class FileSystemDirectoryReader : IDirectoryReader
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    public IEnumerable<DirectoryEntry> Enumerate(string directory) =>
        new FileSystemEnumerable<DirectoryEntry>(directory, Transform, Options);

    private static DirectoryEntry Transform(ref FileSystemEntry entry)
    {
        var lastWrite = entry.LastWriteTimeUtc;
        return new DirectoryEntry(
            entry.FileName.ToString(),
            entry.ToFullPath(),
            entry.IsDirectory,
            entry.IsDirectory ? 0 : entry.Length,
            lastWrite == default ? null : lastWrite,
            entry.Attributes);
    }
}

/// <summary>
/// Runs large-file scans on a dedicated low-priority thread (on Windows, in background processing mode: lower CPU and I/O priority), so a
/// scan of a whole disk does not slow the PC down. Skips the folders whose size would be misleading or that cannot be read.
/// </summary>
public sealed partial class FileSystemLargeFileScanner(ILogger<FileSystemLargeFileScanner> logger, IDirectoryReader? reader = null) : ILargeFileScanner
{
    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;

    private readonly IDirectoryReader _reader = reader ?? new FileSystemDirectoryReader();

    /// <summary>Folders not scanned by default, for each volume root being scanned.</summary>
    public static IReadOnlyList<LargeFileExclusion> DefaultExclusions(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var exclusions = new List<LargeFileExclusion>();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var root in roots.Select(Path.GetPathRoot).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            exclusions.Add(new LargeFileExclusion(Path.Combine(root, "System Volume Information"), Strings.LargeFiles_Excl_Svi));
            if (windows.Length > 0 && string.Equals(Path.GetPathRoot(windows), root, StringComparison.OrdinalIgnoreCase))
            {
                exclusions.Add(new LargeFileExclusion(Path.Combine(windows, "WinSxS"), Strings.LargeFiles_Excl_WinSxS));
            }
        }

        return exclusions;
    }

    public Task<LargeFileScanResult> ScanAsync(LargeFileScanRequest request, IProgress<LargeFileScanProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var completion = new TaskCompletionSource<LargeFileScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var background = OperatingSystem.IsWindows() && SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
            try
            {
                completion.TrySetResult(new LargeFileScanEngine(_reader).Scan(request, progress, cancellationToken));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The large-file scan failed.");
                completion.TrySetException(ex);
            }
            finally
            {
                if (background)
                {
                    SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
                }
            }
        })
        {
            IsBackground = true,
            Name = "Sysora large-file scan",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
        return completion.Task;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadPriority(nint thread, int priority);
}
