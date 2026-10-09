using Microsoft.Extensions.Logging.Abstractions;
using Sysora.Core.Simulation;
using Sysora.Core.Storage;
using Sysora.Infrastructure.Storage;

namespace Sysora.Tests.Storage;

public sealed class LargeFileTests
{
    private const long MB = 1024L * 1024;
    private const long GB = 1024 * MB;
    private static readonly DateTimeOffset Date = TestData.Start;

    [Fact]
    public void Scan_ListsTheLargestFilesFirst_WithTheirGroups()
    {
        Requires.WindowsPaths();
        var reader = new FakeReader
        {
            [@"D:\"] = [Dir(@"D:\Videos"), Dir(@"D:\Downloads"), File(@"D:\small.txt", 10)],
            [@"D:\Videos"] = [File(@"D:\Videos\a.mp4", 3 * GB), File(@"D:\Videos\b.mkv", 1 * GB)],
            [@"D:\Downloads"] = [File(@"D:\Downloads\big.zip", 5 * GB), File(@"D:\Downloads\tiny.zip", 50 * MB)],
        };

        var result = new LargeFileScanEngine(reader).Scan(new LargeFileScanRequest([@"D:\"]), null, TestContext.Current.CancellationToken);

        Assert.Equal(LargeFileScanOutcome.Completed, result.Outcome);
        Assert.Equal(["big.zip", "a.mp4", "b.mkv"], result.Files.Select(f => f.Name));
        Assert.Equal(3, result.MatchingFiles);
        Assert.Equal(9 * GB, result.MatchingBytes);
        Assert.Equal(5, result.FilesScanned);
        Assert.Equal(3, result.DirectoriesScanned);
        Assert.Equal(LargeFileCategory.Archive, result.Files[0].Category);
        Assert.Equal(@"D:\Downloads", result.Files[0].Directory);
        Assert.Equal(@"D:\", result.Files[0].Volume);
        Assert.Equal(".zip", result.Files[0].Extension);
        Assert.Equal(new LargeFileGroup("Archives", 1, 5 * GB), result.ByCategory[0]);
        Assert.Equal(new LargeFileGroup("Videos", 2, 4 * GB), result.ByCategory[1]);
        Assert.Equal(new LargeFileGroup(@"D:\Downloads", 1, 5 * GB), result.ByFolder[0]);
        Assert.StartsWith("Scan completed in", result.Message, StringComparison.Ordinal);
        Assert.Contains("3 files of 100 MB or more (9 GB)", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultsAreBounded_ButEveryMatchingFileIsCounted()
    {
        var reader = new FakeReader { [@"D:\"] = [.. Enumerable.Range(1, 50).Select(i => File($@"D:\f{i}.bin", i * GB))] };

        var result = new LargeFileScanEngine(reader).Scan(new LargeFileScanRequest([@"D:\"]) { MaxResults = 10 }, null, TestContext.Current.CancellationToken);

        Assert.Equal(10, result.Files.Count);
        Assert.Equal(50, result.MatchingFiles);
        Assert.True(result.IsTruncated);
        Assert.Equal(50 * GB, result.Files[0].SizeBytes);
        Assert.Equal(41 * GB, result.Files[^1].SizeBytes);
    }

    [Fact]
    public void HugeFiles_AreHandledWithoutOverflow()
    {
        Requires.WindowsPaths();
        var reader = new FakeReader { [@"E:\"] = [File(@"E:\disk1.vhdx", 3L * 1024 * GB), File(@"E:\disk2.vhdx", 4L * 1024 * GB)] };

        var result = new LargeFileScanEngine(reader).Scan(new LargeFileScanRequest([@"E:\"]), null, TestContext.Current.CancellationToken);

        Assert.Equal(7L * 1024 * GB, result.MatchingBytes);
        Assert.Equal("disk2.vhdx", result.Files[0].Name);
        Assert.Equal(LargeFileCategory.VirtualDisk, result.Files[0].Category);
        Assert.NotNull(result.Files[0].Note);
    }

    [Fact]
    public void UnreadableFolders_AreReported_NeverTreatedAsEmpty()
    {
        var reader = new FakeReader
        {
            [@"D:\"] = [Dir(@"D:\Private"), Dir(@"D:\Gone"), Dir(@"D:\Empty"), File(@"D:\x.iso", 2 * GB)],
            [@"D:\Empty"] = [],
        };
        reader.Denied.Add(@"D:\Private");
        reader.Unavailable.Add(@"D:\Gone");

        var result = new LargeFileScanEngine(reader).Scan(new LargeFileScanRequest([@"D:\"]), null, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.AccessDenied);
        Assert.Equal([@"D:\Private"], result.AccessDeniedSamples);
        Assert.Equal(1, result.Unavailable);
        Assert.Equal([@"D:\Gone"], result.UnavailableSamples);
        Assert.Single(result.Files);
        Assert.Contains("1 folder could not be read (access denied), 1 folder unavailable: their content is unknown, not absent.", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LinksAreNotFollowed_CloudOnlyFilesAreSkipped_AndExclusionsAreHonored()
    {
        var reader = new FakeReader
        {
            [@"D:\"] = [Dir(@"D:\Link", FileAttributes.Directory | FileAttributes.ReparsePoint), Dir(@"D:\Skip"), File(@"D:\cloud.mp4", 9 * GB, (FileAttributes)0x00400000)],
            [@"D:\Link"] = [File(@"D:\Link\dup.zip", 9 * GB)],
            [@"D:\Skip"] = [File(@"D:\Skip\hidden.zip", 9 * GB)],
        };
        var request = new LargeFileScanRequest([@"D:\"]) { Exclusions = [new LargeFileExclusion(@"D:\Skip\", "test")] };

        var result = new LargeFileScanEngine(reader).Scan(request, null, TestContext.Current.CancellationToken);

        Assert.Empty(result.Files);
        Assert.Equal(1, result.SkippedLinks);
        Assert.Equal(1, result.CloudOnlyFiles);
        Assert.Equal(0, result.BytesSeen);
        Assert.Equal("test", Assert.Single(result.Exclusions).Reason);
    }

    [Fact]
    public void CancelledScan_ReturnsPartialResults_AndSaysSo()
    {
        Requires.WindowsPaths();
        using var cancellation = new CancellationTokenSource();
        var reader = new FakeReader
        {
            [@"D:\"] = [Dir(@"D:\A"), Dir(@"D:\B")],
            [@"D:\A"] = [File(@"D:\A\one.zip", 2 * GB)],
            [@"D:\B"] = [File(@"D:\B\two.zip", 3 * GB)],
        };
        reader.OnEnumerate = directory =>
        {
            if (directory == @"D:\A")
            {
                cancellation.Cancel();
            }
        };

        var result = new LargeFileScanEngine(reader).Scan(new LargeFileScanRequest([@"D:\"]), null, cancellation.Token);

        Assert.Equal(LargeFileScanOutcome.Cancelled, result.Outcome);
        Assert.StartsWith("Scan cancelled after", result.Message, StringComparison.Ordinal);
        Assert.Contains("partial results", result.Message, StringComparison.Ordinal);
        Assert.Equal(["one.zip"], result.Files.Select(f => f.Name));
    }

    [Theory]
    [InlineData("pagefile.sys", @"C:\pagefile.sys", LargeFileCategory.WindowsManaged)]
    [InlineData("hiberfil.sys", @"C:\hiberfil.sys", LargeFileCategory.WindowsManaged)]
    [InlineData("$R1X2.zip", @"C:\$Recycle.Bin\S-1-5\$R1X2.zip", LargeFileCategory.RecycleBin)]
    [InlineData("1a2b.msi", @"C:\Windows\Installer\1a2b.msi", LargeFileCategory.WindowsManaged)]
    [InlineData("movie.MKV", @"D:\movie.MKV", LargeFileCategory.Video)]
    [InlineData("data.xyz", @"D:\data.xyz", LargeFileCategory.Other)]
    public void Categories_ComeFromNameLocationAndExtension(string name, string path, LargeFileCategory expected)
    {
        var (category, _) = LargeFileCategorizer.Categorize(name, path, Path.GetExtension(name).ToLowerInvariant());

        Assert.Equal(expected, category);
    }

    [Fact]
    public async Task RealFolder_IsScannedReadOnly_OnABackgroundThread()
    {
        var directory = Directory.CreateTempSubdirectory("sysora-largefiles-");
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(directory.FullName, "nested", "deeper"));
            Directory.CreateDirectory(Path.Combine(directory.FullName, "empty"));
            await CreateFileAsync(Path.Combine(nested.FullName, "large.bin"), 3 * MB);
            await CreateFileAsync(Path.Combine(directory.FullName, "medium.dat"), 2 * MB);
            await CreateFileAsync(Path.Combine(directory.FullName, "small.txt"), 1000);
            var before = System.IO.File.GetLastWriteTimeUtc(Path.Combine(nested.FullName, "large.bin"));

            var scanner = new FileSystemLargeFileScanner(NullLogger<FileSystemLargeFileScanner>.Instance);
            var result = await scanner.ScanAsync(new LargeFileScanRequest([directory.FullName]) { MinimumSizeBytes = MB }, null, TestContext.Current.CancellationToken);

            Assert.Equal(LargeFileScanOutcome.Completed, result.Outcome);
            Assert.Equal(["large.bin", "medium.dat"], result.Files.Select(f => f.Name));
            Assert.Equal(3 * MB, result.Files[0].SizeBytes);
            Assert.Equal(nested.FullName, result.Files[0].Directory);
            Assert.NotNull(result.Files[0].Modified);
            Assert.Equal(3, result.FilesScanned);
            Assert.Equal(4, result.DirectoriesScanned);
            Assert.Equal(before, System.IO.File.GetLastWriteTimeUtc(Path.Combine(nested.FullName, "large.bin")));
            Assert.True(System.IO.File.Exists(Path.Combine(nested.FullName, "large.bin")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task MissingRootFolder_IsUnavailable()
    {
        var scanner = new FileSystemLargeFileScanner(NullLogger<FileSystemLargeFileScanner>.Instance);
        var missing = Path.Combine(Path.GetTempPath(), "sysora-missing-" + Guid.NewGuid().ToString("N"));

        var result = await scanner.ScanAsync(new LargeFileScanRequest([missing]), null, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Unavailable);
        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task Service_RunsOneScanAtATime_AndKeepsTheLastResult()
    {
        var gate = new TaskCompletionSource();
        var service = new LargeFileService(new GatedScanner(gate.Task));

        var first = service.ScanAsync(new LargeFileScanRequest([@"D:\"]), TestContext.Current.CancellationToken);
        var second = await service.ScanAsync(new LargeFileScanRequest([@"D:\"]), TestContext.Current.CancellationToken);
        Assert.True(service.IsScanning);
        Assert.Null(second);

        service.Cancel();
        var result = await first;

        Assert.Equal(LargeFileScanOutcome.Cancelled, result!.Outcome);
        Assert.Same(result, service.LastResult);
        Assert.False(service.IsScanning);
    }

    [Fact]
    public async Task DemoScanner_NeverReadsTheRealDisk()
    {
        Requires.WindowsPaths();
        var result = await new SimulatedLargeFileScanner().ScanAsync(new LargeFileScanRequest([@"Z:\"]), null, TestContext.Current.CancellationToken);

        Assert.Equal("GameArchive.zip", result.Files[0].Name);
        Assert.Equal(1, result.AccessDenied);
        Assert.Contains(result.Files, f => f.Name == "pagefile.sys" && f.Category == LargeFileCategory.WindowsManaged);
    }

    [Fact]
    public void DefaultExclusions_CoverTheComponentStoreOfTheWindowsVolume()
    {
        Requires.WindowsPaths();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var root = Path.GetPathRoot(windows)!;

        var exclusions = FileSystemLargeFileScanner.DefaultExclusions([root]);

        Assert.Contains(exclusions, e => e.Path.Equals(Path.Combine(windows, "WinSxS"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(exclusions, e => e.Path.EndsWith("System Volume Information", StringComparison.Ordinal));
    }

    private static async Task CreateFileAsync(string path, long size)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        var buffer = new byte[64 * 1024];
        for (long written = 0; written < size; written += buffer.Length)
        {
            await stream.WriteAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - written)), TestContext.Current.CancellationToken);
        }
    }

    private static DirectoryEntry Dir(string path, FileAttributes attributes = FileAttributes.Directory) =>
        new(Path.GetFileName(path), path, true, 0, Date, attributes);

    private static DirectoryEntry File(string path, long size, FileAttributes attributes = FileAttributes.Archive) =>
        new(Path.GetFileName(path), path, false, size, Date, attributes);

    private sealed class FakeReader : Dictionary<string, DirectoryEntry[]>, IDirectoryReader
    {
        public FakeReader()
            : base(StringComparer.OrdinalIgnoreCase)
        {
        }

        public HashSet<string> Denied { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Unavailable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Action<string>? OnEnumerate { get; set; }

        public IEnumerable<DirectoryEntry> Enumerate(string directory)
        {
            OnEnumerate?.Invoke(directory);
            if (Denied.Contains(directory))
            {
                throw new UnauthorizedAccessException();
            }

            if (Unavailable.Contains(directory) || !TryGetValue(directory, out var entries))
            {
                throw new DirectoryNotFoundException();
            }

            return entries;
        }
    }

    private sealed class GatedScanner(Task gate) : ILargeFileScanner
    {
        public async Task<LargeFileScanResult> ScanAsync(LargeFileScanRequest request, IProgress<LargeFileScanProgress>? progress, CancellationToken cancellationToken)
        {
            try
            {
                await gate.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new LargeFileScanResult
                {
                    Started = Date,
                    Finished = Date,
                    Roots = request.Roots,
                    MinimumSizeBytes = request.MinimumSizeBytes,
                    Outcome = LargeFileScanOutcome.Cancelled,
                    Message = "Scan cancelled",
                };
            }

            throw new InvalidOperationException("Not expected.");
        }
    }
}
