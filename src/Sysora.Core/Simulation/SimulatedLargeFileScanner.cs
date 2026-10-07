using Sysora.Core.Storage;

namespace Sysora.Core.Simulation;

/// <summary>Demo mode: a scan over a made-up folder tree, so demo screenshots never show the real disk.</summary>
public sealed class SimulatedLargeFileScanner(TimeProvider? timeProvider = null) : ILargeFileScanner
{
    private const long GB = 1024L * 1024 * 1024;
    private const long MB = 1024L * 1024;

    public async Task<LargeFileScanResult> ScanAsync(LargeFileScanRequest request, IProgress<LargeFileScanProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var time = timeProvider ?? TimeProvider.System;

        // Pretend to walk the disk for a moment, honoring cancellation like the real scan.
        for (var step = 1; step <= 10; step++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120), time, cancellationToken).ConfigureAwait(false);
            progress?.Report(new LargeFileScanProgress(step * 1200, step * 18_000, step * 9 * GB, step, @"C:\Users\Demo\..."));
        }

        var reader = new SimulatedDirectoryReader();
        return new LargeFileScanEngine(reader, timeProvider).Scan(request with { Roots = [@"C:\"] }, null, cancellationToken);
    }

    /// <summary>A small, fixed folder tree with typical large files (simulated).</summary>
    private sealed class SimulatedDirectoryReader : IDirectoryReader
    {
        private static readonly DateTimeOffset Date = new(2026, 9, 14, 18, 30, 0, TimeSpan.Zero);

        private static readonly Dictionary<string, DirectoryEntry[]> Tree = new(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\"] =
            [
                Folder(@"C:\Users"), Folder(@"C:\Games"), Folder(@"C:\System Volume Information"),
                File(@"C:\pagefile.sys", 16 * GB), File(@"C:\hiberfil.sys", 12 * GB),
            ],
            [@"C:\Users"] = [Folder(@"C:\Users\Demo")],
            [@"C:\Users\Demo"] = [Folder(@"C:\Users\Demo\Videos"), Folder(@"C:\Users\Demo\Downloads"), Folder(@"C:\Users\Demo\Documents")],
            [@"C:\Users\Demo\Videos"] = [File(@"C:\Users\Demo\Videos\VideoProject.mp4", 18_400 * MB), File(@"C:\Users\Demo\Videos\Holiday.mov", 3_200 * MB)],
            [@"C:\Users\Demo\Downloads"] =
            [
                File(@"C:\Users\Demo\Downloads\GameArchive.zip", 42_800 * MB), File(@"C:\Users\Demo\Downloads\ubuntu.iso", 5_700 * MB),
                File(@"C:\Users\Demo\Downloads\setup.msi", 640 * MB), File(@"C:\Users\Demo\Downloads\notes.txt", 12_000),
            ],
            [@"C:\Users\Demo\Documents"] = [File(@"C:\Users\Demo\Documents\Backup.vhdx", 16_100 * MB), File(@"C:\Users\Demo\Documents\Report.pdf", 140 * MB)],
            [@"C:\Games"] = [Folder(@"C:\Games\SpaceGame")],
            [@"C:\Games\SpaceGame"] = [File(@"C:\Games\SpaceGame\Content.pak", 27_300 * MB)],
        };

        public IEnumerable<DirectoryEntry> Enumerate(string directory)
        {
            if (directory.EndsWith("System Volume Information", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException();
            }

            return Tree.TryGetValue(directory.TrimEnd('\\') is { Length: 2 } drive ? drive + "\\" : directory, out var entries) ? entries : [];
        }

        private static DirectoryEntry Folder(string path) => new(Path.GetFileName(path), path, true, 0, Date, FileAttributes.Directory);

        private static DirectoryEntry File(string path, long size) => new(Path.GetFileName(path), path, false, size, Date, FileAttributes.Archive);
    }
}
