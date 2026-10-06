using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PulseDesk.Core.Alerts;
using PulseDesk.Core.Analysis;
using PulseDesk.Core.Changes;
using PulseDesk.Core.Gaming;
using PulseDesk.Core.History;
using PulseDesk.Core.Interfaces;

namespace PulseDesk.Infrastructure.Storage;

/// <summary>
/// <see cref="IHistoryRepository"/> backed by a local SQLite database (<c>%LOCALAPPDATA%\PulseDesk\history.db</c>),
/// or by an in-memory database in demo mode and tests.
/// </summary>
/// <remarks>
/// One connection is kept open and every operation is serialized and run on the thread pool, so callers on the
/// UI thread never block on disk access. Timestamps are stored as Unix milliseconds (UTC). Writes merge with
/// existing rows of the same bucket, so a minute interrupted by a restart is not lost or counted twice.
/// </remarks>
public sealed class HistoryRepository : IHistoryRepository, IAsyncDisposable, IDisposable
{
    private const int SchemaVersion = 1;
    private const string AlertDocument = "alert";
    private const string BaselineDocument = "baseline";
    private const string ChangeDocument = "change";
    private const string GameDocument = "game";
    private const string InMemoryLocation = "In memory (demo mode: nothing is written to disk)";

    /// <summary>Hourly roll-ups wait this long after the hour, so late writes (shutdown flush) are included.</summary>
    private static readonly TimeSpan RollupDelay = TimeSpan.FromMinutes(5);

    /// <summary>Metric column prefixes of the system_usage table, in <see cref="SystemUsageAggregate"/> order.</summary>
    private static readonly string[] MetricColumns = ["cpu", "mem", "disk", "rx", "tx", "gpu", "proc"];

    private readonly string? _filePath;
    private readonly ILogger<HistoryRepository> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private bool _disposed;

    /// <param name="filePath">Database file; created if missing.</param>
    /// <param name="logger">Logger.</param>
    public HistoryRepository(string filePath, ILogger<HistoryRepository> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        _logger = logger;
    }

    private HistoryRepository(ILogger<HistoryRepository> logger)
    {
        _logger = logger;
    }

    /// <summary>A repository that keeps everything in memory for the lifetime of the process.</summary>
    public static HistoryRepository InMemory(ILogger<HistoryRepository> logger) => new(logger);

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        RunAsync(_ => true, cancellationToken);

    public Task AppendAsync(HistoryBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.IsEmpty)
        {
            return Task.CompletedTask;
        }

        return RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            foreach (var aggregate in batch.SystemUsage)
            {
                UpsertSystemUsage(connection, transaction, aggregate);
            }

            foreach (var bucket in batch.AppUsage)
            {
                UpsertAppBucket(connection, transaction, bucket);
            }

            foreach (var systemEvent in batch.Events)
            {
                InsertEvent(connection, transaction, systemEvent);
            }

            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<SystemUsageAggregate>> GetSystemUsageAsync(
        DateTimeOffset from, DateTimeOffset to, HistoryResolution resolution, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<SystemUsageAggregate>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT start, samples, seconds, {string.Join(", ", MetricColumns.SelectMany(m => new[] { $"{m}_avg", $"{m}_max", $"{m}_n" }))}, mem_total
                FROM system_usage WHERE resolution = $resolution AND start >= $from AND start < $to ORDER BY start
                """;
            command.Parameters.AddWithValue("$resolution", (int)resolution);
            command.Parameters.AddWithValue("$from", ToUnix(from));
            command.Parameters.AddWithValue("$to", ToUnix(to));

            var result = new List<SystemUsageAggregate>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new SystemUsageAggregate
                {
                    Start = FromUnix(reader.GetInt64(0)),
                    Resolution = resolution,
                    SampleCount = reader.GetInt32(1),
                    MonitoredSeconds = reader.GetDouble(2),
                    Cpu = ReadAggregate(reader, 3),
                    Memory = ReadAggregate(reader, 6),
                    Disk = ReadAggregate(reader, 9),
                    NetworkReceive = ReadAggregate(reader, 12),
                    NetworkSend = ReadAggregate(reader, 15),
                    Gpu = ReadAggregate(reader, 18),
                    ProcessCount = ReadAggregate(reader, 21),
                    MemoryTotalBytes = reader.IsDBNull(24) ? null : (ulong)reader.GetInt64(24),
                });
            }

            return result;
        }, cancellationToken);

    public Task<IReadOnlyList<SystemEvent>> GetEventsAsync(
        DateTimeOffset from, DateTimeOffset to, int maxCount, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<SystemEvent>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT time, kind, title, detail, app_key FROM events
                WHERE time >= $from AND time <= $to ORDER BY time DESC, id DESC LIMIT $limit
                """;
            command.Parameters.AddWithValue("$from", ToUnix(from));
            command.Parameters.AddWithValue("$to", ToUnix(to));
            command.Parameters.AddWithValue("$limit", Math.Max(maxCount, 0));

            var result = new List<SystemEvent>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                // Rows written by a newer version may contain unknown kinds: skip them rather than fail.
                if (!Enum.TryParse<SystemEventKind>(reader.GetString(1), out var kind))
                {
                    continue;
                }

                result.Add(new SystemEvent(FromUnix(reader.GetInt64(0)), kind, reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3))
                {
                    AppKey = reader.IsDBNull(4) ? null : reader.GetString(4),
                });
            }

            result.Reverse();
            return result;
        }, cancellationToken);

    public Task<IReadOnlyList<AppUsageStatistics>> GetAppUsageAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<AppUsageStatistics>>(connection =>
        {
            var start = ToUnix(from);
            var end = ToUnix(to);
            var split = DetailSplit(connection, start, end);
            var middle = start + ((end - start) / 2);
            const string Period = """
                ((resolution = 3600 AND start >= $from AND start < $split) OR (resolution = 300 AND start >= $split AND start >= $from AND start < $to))
                """;

            using (var monitored = connection.CreateCommand())
            {
                monitored.CommandText = $"SELECT COALESCE(SUM(seconds), 0) FROM app_buckets WHERE {Period}";
                AddPeriod(monitored, start, end, split);
                var monitoredSeconds = Convert.ToDouble(monitored.ExecuteScalar(), CultureInfo.InvariantCulture);

                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT app_key, MAX(name), MAX(path), MIN(start), MAX(start + resolution * 1000), SUM(samples), SUM(active),
                        {WeightedAverage("cpu_avg")}, MAX(cpu_max),
                        {WeightedAverage("mem_avg")}, MAX(mem_max),
                        {WeightedAverage("io_avg")}, MAX(io_max),
                        SUM(launches),
                        SUM(CASE WHEN start < $middle THEN cpu_avg * active END) / NULLIF(SUM(CASE WHEN start < $middle THEN active END), 0),
                        SUM(CASE WHEN start < $middle THEN mem_avg * active END) / NULLIF(SUM(CASE WHEN start < $middle THEN active END), 0),
                        SUM(CASE WHEN start < $middle THEN active END),
                        SUM(CASE WHEN start >= $middle THEN cpu_avg * active END) / NULLIF(SUM(CASE WHEN start >= $middle THEN active END), 0),
                        SUM(CASE WHEN start >= $middle THEN mem_avg * active END) / NULLIF(SUM(CASE WHEN start >= $middle THEN active END), 0),
                        SUM(CASE WHEN start >= $middle THEN active END)
                    FROM app_usage WHERE {Period}
                    GROUP BY app_key
                    """;
                AddPeriod(command, start, end, split);
                command.Parameters.AddWithValue("$middle", middle);

                var result = new List<AppUsageStatistics>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new AppUsageStatistics
                    {
                        Identity = new AppIdentity(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)),
                        FirstSeen = FromUnix(reader.GetInt64(3)),
                        LastSeen = FromUnix(reader.GetInt64(4)),
                        Samples = reader.GetInt32(5),
                        ActiveSeconds = reader.GetDouble(6),
                        MonitoredSeconds = monitoredSeconds,
                        CpuAverage = reader.GetDouble(7),
                        CpuMaximum = reader.GetDouble(8),
                        MemoryAverageBytes = reader.GetDouble(9),
                        MemoryMaximumBytes = reader.GetDouble(10),
                        IoAverageBytesPerSecond = reader.GetDouble(11),
                        IoMaximumBytesPerSecond = reader.GetDouble(12),
                        Launches = reader.GetInt32(13),
                        FirstHalf = ReadLevel(reader, 14),
                        SecondHalf = ReadLevel(reader, 17),
                    });
                }

                return result;
            }
        }, cancellationToken);

    public Task<IReadOnlyList<AppUsageAggregate>> GetAppTimelineAsync(
        string appKey, DateTimeOffset from, DateTimeOffset to, HistoryResolution resolution, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<AppUsageAggregate>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT start, name, path, samples, active, cpu_avg, cpu_max, mem_avg, mem_max, io_avg, io_max, launches
                FROM app_usage WHERE app_key = $key AND resolution = $resolution AND start >= $from AND start < $to ORDER BY start
                """;
            command.Parameters.AddWithValue("$key", appKey);
            command.Parameters.AddWithValue("$resolution", (int)resolution);
            command.Parameters.AddWithValue("$from", ToUnix(from));
            command.Parameters.AddWithValue("$to", ToUnix(to));

            var result = new List<AppUsageAggregate>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new AppUsageAggregate
                {
                    AppKey = appKey,
                    Start = FromUnix(reader.GetInt64(0)),
                    Resolution = resolution,
                    Name = reader.GetString(1),
                    ExecutablePath = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Samples = reader.GetInt32(3),
                    ActiveSeconds = reader.GetDouble(4),
                    CpuAverage = reader.GetDouble(5),
                    CpuMaximum = reader.GetDouble(6),
                    MemoryAverageBytes = reader.GetDouble(7),
                    MemoryMaximumBytes = reader.GetDouble(8),
                    IoAverageBytesPerSecond = reader.GetDouble(9),
                    IoMaximumBytesPerSecond = reader.GetDouble(10),
                    Launches = reader.GetInt32(11),
                });
            }

            return result;
        }, cancellationToken);

    public Task SaveAlertsAsync(IReadOnlyList<Alert> alerts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        return alerts.Count == 0
            ? Task.CompletedTask
            : SaveDocumentsAsync(AlertDocument, alerts.Select(a => (a.Id.ToString("N"), a.RaisedAt, AnalysisJson.Serialize(a))).ToArray(), replace: true, cancellationToken);
    }

    public Task SaveBaselineAsync(SystemBaseline baseline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var day = baseline.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return SaveDocumentsAsync(BaselineDocument, [(day, baseline.CapturedAt, AnalysisJson.Serialize(baseline))], replace: false, cancellationToken);
    }

    public async Task<IReadOnlyList<SystemBaseline>> GetBaselinesAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        (await GetDocumentsAsync(BaselineDocument, since, cancellationToken).ConfigureAwait(false))
            .Select(AnalysisJson.DeserializeBaseline)
            .OfType<SystemBaseline>()
            .ToArray();

    public Task SaveChangesAsync(IReadOnlyList<DetectedChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return changes.Count == 0
            ? Task.CompletedTask
            : SaveDocumentsAsync(ChangeDocument, changes.Select(c => (c.Id, c.DetectedAt, AnalysisJson.Serialize(c))).ToArray(), replace: false, cancellationToken);
    }

    public async Task<IReadOnlyList<DetectedChange>> GetChangesAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        (await GetDocumentsAsync(ChangeDocument, since, cancellationToken).ConfigureAwait(false))
            .Select(AnalysisJson.DeserializeChange)
            .OfType<DetectedChange>()
            .ToArray();

    public Task SaveGameSessionAsync(GameSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return SaveDocumentsAsync(GameDocument, [(session.Id.ToString("N"), session.Start, AnalysisJson.Serialize(session))], replace: true, cancellationToken);
    }

    public async Task<IReadOnlyList<GameSession>> GetGameSessionsAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        (await GetDocumentsAsync(GameDocument, since, cancellationToken).ConfigureAwait(false))
            .Select(AnalysisJson.DeserializeGameSession)
            .OfType<GameSession>()
            .ToArray();

    public Task<IReadOnlyList<KnownApp>> GetKnownAppsAsync(CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<KnownApp>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT app_key, name, path, first_seen, last_seen FROM apps ORDER BY first_seen";
            var result = new List<KnownApp>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new KnownApp(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    FromUnix(reader.GetInt64(3)),
                    FromUnix(reader.GetInt64(4))));
            }

            return result;
        }, cancellationToken);

    public async Task<IReadOnlyList<Alert>> GetAlertsAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        (await GetDocumentsAsync(AlertDocument, since, cancellationToken).ConfigureAwait(false))
            .Select(AnalysisJson.DeserializeAlert)
            .OfType<Alert>()
            .ToArray();

    public Task RunMaintenanceAsync(HistoryRetention retention, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retention);
        return RunAsync(connection =>
        {
            RollUp(connection, now);
            var detailCutoff = ToUnix(now - retention.Detail);
            var summaryCutoff = ToUnix(now - retention.Summary);
            Execute(connection, "DELETE FROM system_usage WHERE resolution = 60 AND start < $cutoff", ("$cutoff", detailCutoff));
            Execute(connection, "DELETE FROM app_usage WHERE resolution = 300 AND start < $cutoff", ("$cutoff", detailCutoff));
            Execute(connection, "DELETE FROM app_buckets WHERE resolution = 300 AND start < $cutoff", ("$cutoff", detailCutoff));
            Execute(connection, "DELETE FROM system_usage WHERE start < $cutoff", ("$cutoff", summaryCutoff));
            Execute(connection, "DELETE FROM app_usage WHERE start < $cutoff", ("$cutoff", summaryCutoff));
            Execute(connection, "DELETE FROM app_buckets WHERE start < $cutoff", ("$cutoff", summaryCutoff));
            Execute(connection, "DELETE FROM events WHERE time < $cutoff", ("$cutoff", summaryCutoff));
            Execute(connection, "DELETE FROM documents WHERE time < $cutoff", ("$cutoff", summaryCutoff));
            Execute(connection, "DELETE FROM apps WHERE last_seen < $cutoff", ("$cutoff", summaryCutoff));
            EnforceSizeLimit(connection, retention.MaxDatabaseBytes);
            Execute(connection, "PRAGMA incremental_vacuum");
            return true;
        }, cancellationToken);
    }

    public Task<HistoryStorageInfo> GetStorageInfoAsync(CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            var oldest = Scalar(connection, "SELECT MIN(start) FROM system_usage") is long start ? FromUnix(start) : (DateTimeOffset?)null;
            if (_filePath is null)
            {
                return new HistoryStorageInfo(0, oldest, InMemoryLocation);
            }

            long size = 0;
            foreach (var file in new[] { _filePath, _filePath + "-wal" })
            {
                if (File.Exists(file))
                {
                    size += new FileInfo(file).Length;
                }
            }

            return new HistoryStorageInfo(size, oldest, _filePath);
        }, cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var table in new[] { "system_usage", "app_usage", "app_buckets", "apps", "events", "documents" })
                {
                    Execute(connection, $"DELETE FROM {table}", transaction);
                }

                Execute(connection, "DELETE FROM meta WHERE key = 'rollup_until'", transaction);
                transaction.Commit();
            }

            Execute(connection, "VACUUM");
            _logger.LogInformation("History cleared at the user's request.");
            return true;
        }, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Wait();
        try
        {
            _connection?.Dispose();
            _connection = null;
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Runs <paramref name="work"/> on the thread pool with exclusive access to the open connection.</summary>
    private async Task<T> RunAsync<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => work(EnsureOpen()), cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (_filePath is not null && ex.SqliteErrorCode is 11 or 26)
        {
            // The file was damaged while open (disk error, external tool): set it aside so the next operation starts a
            // new history instead of failing forever. This operation still reports its failure.
            _logger.LogWarning(ex, "The history database became damaged; it was set aside and will be recreated.");
            ResetDamagedDatabase();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the connection and moves the damaged files aside. Caller holds the gate.</summary>
    private void ResetDamagedDatabase()
    {
        try
        {
            _connection?.Dispose();
            _connection = null;
            SqliteConnection.ClearAllPools();
            Quarantine();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The damaged history database could not be moved aside.");
        }
    }

    private SqliteConnection EnsureOpen()
    {
        if (_connection is not null)
        {
            return _connection;
        }

        try
        {
            _connection = Open();
        }
        catch (SqliteException ex) when (_filePath is not null && ex.SqliteErrorCode is 11 or 26)
        {
            // SQLITE_CORRUPT / SQLITE_NOTADB: keep the damaged file aside and start a new history.
            _logger.LogWarning(ex, "The history database is damaged; it was set aside and a new one was created.");
            Quarantine();
            _connection = Open();
        }

        return _connection;
    }

    private SqliteConnection Open()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _filePath ?? ":memory:",
            Mode = _filePath is null ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };

        if (_filePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        }

        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            // auto_vacuum only takes effect before the first table is created (new databases).
            Execute(connection, "PRAGMA auto_vacuum = INCREMENTAL");
            if (_filePath is not null)
            {
                Execute(connection, "PRAGMA journal_mode = WAL");
            }

            Execute(connection, "PRAGMA synchronous = NORMAL");
            Execute(connection, "PRAGMA foreign_keys = OFF");
            CreateSchema(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void Quarantine()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = _filePath + suffix;
            if (File.Exists(file))
            {
                File.Move(file, Path.ChangeExtension(_filePath!, null) + $".damaged-{stamp}.db{suffix}", overwrite: true);
            }
        }
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        var metrics = string.Join(",\n", MetricColumns.Select(m => $"{m}_avg REAL, {m}_max REAL, {m}_n INTEGER NOT NULL DEFAULT 0"));
        Execute(connection, $"""
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS system_usage(
                resolution INTEGER NOT NULL,
                start INTEGER NOT NULL,
                samples INTEGER NOT NULL,
                seconds REAL NOT NULL,
                {metrics},
                mem_total INTEGER,
                PRIMARY KEY(resolution, start)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS app_buckets(
                resolution INTEGER NOT NULL,
                start INTEGER NOT NULL,
                seconds REAL NOT NULL,
                samples INTEGER NOT NULL,
                PRIMARY KEY(resolution, start)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS app_usage(
                resolution INTEGER NOT NULL,
                start INTEGER NOT NULL,
                app_key TEXT NOT NULL,
                name TEXT NOT NULL,
                path TEXT,
                samples INTEGER NOT NULL,
                active REAL NOT NULL,
                cpu_avg REAL NOT NULL,
                cpu_max REAL NOT NULL,
                mem_avg REAL NOT NULL,
                mem_max REAL NOT NULL,
                io_avg REAL NOT NULL,
                io_max REAL NOT NULL,
                launches INTEGER NOT NULL,
                PRIMARY KEY(resolution, start, app_key)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS app_usage_by_app ON app_usage(app_key, resolution, start);
            CREATE TABLE IF NOT EXISTS apps(
                app_key TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                path TEXT,
                first_seen INTEGER NOT NULL,
                last_seen INTEGER NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS events(
                id INTEGER PRIMARY KEY,
                time INTEGER NOT NULL,
                kind TEXT NOT NULL,
                title TEXT NOT NULL,
                detail TEXT,
                app_key TEXT);
            CREATE INDEX IF NOT EXISTS events_by_time ON events(time);
            CREATE TABLE IF NOT EXISTS documents(
                kind TEXT NOT NULL,
                id TEXT NOT NULL,
                time INTEGER NOT NULL,
                data TEXT NOT NULL,
                PRIMARY KEY(kind, id)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS documents_by_time ON documents(kind, time);
            INSERT OR IGNORE INTO meta(key, value) VALUES('schema_version', '{SchemaVersion}');
            """);
    }

    private static void UpsertSystemUsage(SqliteConnection connection, SqliteTransaction transaction, SystemUsageAggregate aggregate)
    {
        var columns = MetricColumns.SelectMany(m => new[] { $"{m}_avg", $"{m}_max", $"{m}_n" }).ToArray();
        var merge = new StringBuilder();
        foreach (var m in MetricColumns)
        {
            merge.Append(CultureInfo.InvariantCulture, $"""
                , {m}_avg = CASE WHEN {m}_n + excluded.{m}_n = 0 THEN NULL
                    ELSE (COALESCE({m}_avg, 0) * {m}_n + COALESCE(excluded.{m}_avg, 0) * excluded.{m}_n) / ({m}_n + excluded.{m}_n) END
                , {m}_max = MAX(COALESCE({m}_max, excluded.{m}_max), COALESCE(excluded.{m}_max, {m}_max))
                , {m}_n = {m}_n + excluded.{m}_n
                """);
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO system_usage(resolution, start, samples, seconds, {string.Join(", ", columns)}, mem_total)
            VALUES($resolution, $start, $samples, $seconds, {string.Join(", ", columns.Select(c => "$" + c))}, $mem_total)
            ON CONFLICT(resolution, start) DO UPDATE SET
                samples = samples + excluded.samples,
                seconds = seconds + excluded.seconds,
                mem_total = COALESCE(excluded.mem_total, mem_total)
                {merge}
            """;
        command.Parameters.AddWithValue("$resolution", (int)aggregate.Resolution);
        command.Parameters.AddWithValue("$start", ToUnix(aggregate.Start));
        command.Parameters.AddWithValue("$samples", aggregate.SampleCount);
        command.Parameters.AddWithValue("$seconds", aggregate.MonitoredSeconds);
        AddAggregate(command, "cpu", aggregate.Cpu);
        AddAggregate(command, "mem", aggregate.Memory);
        AddAggregate(command, "disk", aggregate.Disk);
        AddAggregate(command, "rx", aggregate.NetworkReceive);
        AddAggregate(command, "tx", aggregate.NetworkSend);
        AddAggregate(command, "gpu", aggregate.Gpu);
        AddAggregate(command, "proc", aggregate.ProcessCount);
        command.Parameters.AddWithValue("$mem_total", aggregate.MemoryTotalBytes is { } total ? (long)total : DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void UpsertAppBucket(SqliteConnection connection, SqliteTransaction transaction, AppUsageBucket bucket)
    {
        var start = ToUnix(bucket.Start);
        Execute(connection, """
            INSERT INTO app_buckets(resolution, start, seconds, samples) VALUES($resolution, $start, $seconds, $samples)
            ON CONFLICT(resolution, start) DO UPDATE SET seconds = seconds + excluded.seconds, samples = samples + excluded.samples
            """, transaction,
            ("$resolution", (int)bucket.Resolution), ("$start", start), ("$seconds", bucket.MonitoredSeconds), ("$samples", bucket.SampleCount));

        using var usage = connection.CreateCommand();
        usage.Transaction = transaction;
        usage.CommandText = $"""
            INSERT INTO app_usage(resolution, start, app_key, name, path, samples, active, cpu_avg, cpu_max, mem_avg, mem_max, io_avg, io_max, launches)
            VALUES($resolution, $start, $key, $name, $path, $samples, $active, $cpu_avg, $cpu_max, $mem_avg, $mem_max, $io_avg, $io_max, $launches)
            ON CONFLICT(resolution, start, app_key) DO UPDATE SET
                {MergeAverage("cpu_avg")},
                {MergeAverage("mem_avg")},
                {MergeAverage("io_avg")},
                cpu_max = MAX(cpu_max, excluded.cpu_max),
                mem_max = MAX(mem_max, excluded.mem_max),
                io_max = MAX(io_max, excluded.io_max),
                samples = samples + excluded.samples,
                active = active + excluded.active,
                launches = launches + excluded.launches,
                path = COALESCE(excluded.path, path)
            """;
        var parameters = new[] { "$resolution", "$start", "$key", "$name", "$path", "$samples", "$active", "$cpu_avg", "$cpu_max", "$mem_avg", "$mem_max", "$io_avg", "$io_max", "$launches" }
            .Select(name => usage.Parameters.Add(name, SqliteType.Text)).ToArray();

        using var catalog = connection.CreateCommand();
        catalog.Transaction = transaction;
        catalog.CommandText = """
            INSERT INTO apps(app_key, name, path, first_seen, last_seen) VALUES($key, $name, $path, $seen, $seen)
            ON CONFLICT(app_key) DO UPDATE SET
                name = excluded.name,
                path = COALESCE(excluded.path, path),
                first_seen = MIN(first_seen, excluded.first_seen),
                last_seen = MAX(last_seen, excluded.last_seen)
            """;
        var catalogKey = catalog.Parameters.Add("$key", SqliteType.Text);
        var catalogName = catalog.Parameters.Add("$name", SqliteType.Text);
        var catalogPath = catalog.Parameters.Add("$path", SqliteType.Text);
        var catalogSeen = catalog.Parameters.Add("$seen", SqliteType.Integer);

        foreach (var app in bucket.Apps)
        {
            object[] values =
            [
                (int)bucket.Resolution, start, app.AppKey, app.Name, (object?)app.ExecutablePath ?? DBNull.Value, app.Samples, app.ActiveSeconds,
                app.CpuAverage, app.CpuMaximum, app.MemoryAverageBytes, app.MemoryMaximumBytes, app.IoAverageBytesPerSecond,
                app.IoMaximumBytesPerSecond, app.Launches,
            ];
            for (var i = 0; i < values.Length; i++)
            {
                parameters[i].SqliteType = values[i] switch
                {
                    string => SqliteType.Text,
                    double => SqliteType.Real,
                    DBNull => SqliteType.Text,
                    _ => SqliteType.Integer,
                };
                parameters[i].Value = values[i];
            }

            usage.ExecuteNonQuery();

            catalogKey.Value = app.AppKey;
            catalogName.Value = app.Name;
            catalogPath.Value = (object?)app.ExecutablePath ?? DBNull.Value;
            catalogSeen.Value = start;
            catalog.ExecuteNonQuery();
        }
    }

    private static void InsertEvent(SqliteConnection connection, SqliteTransaction transaction, SystemEvent systemEvent) =>
        Execute(connection, "INSERT INTO events(time, kind, title, detail, app_key) VALUES($time, $kind, $title, $detail, $app)", transaction,
            ("$time", ToUnix(systemEvent.Timestamp)),
            ("$kind", systemEvent.Kind.ToString()),
            ("$title", systemEvent.Title),
            ("$detail", systemEvent.Detail),
            ("$app", systemEvent.AppKey));

    /// <summary>Recomputes the hourly summaries of every complete hour not rolled up yet.</summary>
    private static void RollUp(SqliteConnection connection, DateTimeOffset now)
    {
        var until = ToUnix(SystemUsageAggregate.BucketStart(now - RollupDelay, HistoryResolution.Hour));
        var from = Scalar(connection, "SELECT value FROM meta WHERE key = 'rollup_until'") is string stored
            && long.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (Scalar(connection, """
                    SELECT MIN(start) FROM (
                        SELECT MIN(start) AS start FROM system_usage WHERE resolution = 60
                        UNION ALL SELECT MIN(start) FROM app_buckets WHERE resolution = 300)
                    """) as long? ?? until) / 3_600_000 * 3_600_000;
        if (from >= until)
        {
            return;
        }

        var metrics = string.Join(",\n", MetricColumns.Select(m =>
            $"SUM({m}_avg * {m}_n) / NULLIF(SUM({m}_n), 0), MAX({m}_max), SUM({m}_n)"));
        var columns = string.Join(", ", MetricColumns.SelectMany(m => new[] { $"{m}_avg", $"{m}_max", $"{m}_n" }));

        using var transaction = connection.BeginTransaction();
        Execute(connection, $"""
            INSERT OR REPLACE INTO system_usage(resolution, start, samples, seconds, {columns}, mem_total)
            SELECT 3600, start / 3600000 * 3600000 AS hour, SUM(samples), SUM(seconds),
                {metrics},
                MAX(mem_total)
            FROM system_usage WHERE resolution = 60 AND start >= $from AND start < $until GROUP BY hour
            """, transaction, ("$from", from), ("$until", until));
        Execute(connection, """
            INSERT OR REPLACE INTO app_buckets(resolution, start, seconds, samples)
            SELECT 3600, start / 3600000 * 3600000 AS hour, SUM(seconds), SUM(samples)
            FROM app_buckets WHERE resolution = 300 AND start >= $from AND start < $until GROUP BY hour
            """, transaction, ("$from", from), ("$until", until));
        Execute(connection, $"""
            INSERT OR REPLACE INTO app_usage(resolution, start, app_key, name, path, samples, active, cpu_avg, cpu_max, mem_avg, mem_max, io_avg, io_max, launches)
            SELECT 3600, start / 3600000 * 3600000 AS hour, app_key, MAX(name), MAX(path), SUM(samples), SUM(active),
                {WeightedAverage("cpu_avg")}, MAX(cpu_max),
                {WeightedAverage("mem_avg")}, MAX(mem_max),
                {WeightedAverage("io_avg")}, MAX(io_max),
                SUM(launches)
            FROM app_usage WHERE resolution = 300 AND start >= $from AND start < $until GROUP BY hour, app_key
            """, transaction, ("$from", from), ("$until", until));
        Execute(connection, "INSERT OR REPLACE INTO meta(key, value) VALUES('rollup_until', $until)", transaction,
            ("$until", until.ToString(CultureInfo.InvariantCulture)));
        transaction.Commit();
    }

    /// <summary>Removes the oldest detailed data until the database fits in <paramref name="maxBytes"/>.</summary>
    private void EnforceSizeLimit(SqliteConnection connection, long maxBytes)
    {
        for (var attempt = 0; attempt < 8 && DatabaseBytes(connection) > maxBytes; attempt++)
        {
            var oldest = Scalar(connection, "SELECT MIN(start) FROM app_usage WHERE resolution = 300") as long?
                ?? Scalar(connection, "SELECT MIN(start) FROM system_usage WHERE resolution = 60") as long?;
            if (oldest is not { } start)
            {
                break;
            }

            // Drop one day of detailed data at a time; hourly summaries are kept.
            var cutoff = start + (long)TimeSpan.FromDays(1).TotalMilliseconds;
            Execute(connection, "DELETE FROM app_usage WHERE resolution = 300 AND start < $cutoff", ("$cutoff", cutoff));
            Execute(connection, "DELETE FROM app_buckets WHERE resolution = 300 AND start < $cutoff", ("$cutoff", cutoff));
            Execute(connection, "DELETE FROM system_usage WHERE resolution = 60 AND start < $cutoff", ("$cutoff", cutoff));
            Execute(connection, "PRAGMA incremental_vacuum");
            _logger.LogInformation("History database above its size limit: detailed data before {Cutoff} removed.", FromUnix(cutoff));
        }
    }

    private static long DatabaseBytes(SqliteConnection connection) =>
        (Scalar(connection, "PRAGMA page_count") as long? ?? 0) * (Scalar(connection, "PRAGMA page_size") as long? ?? 4096)
        - ((Scalar(connection, "PRAGMA freelist_count") as long? ?? 0) * (Scalar(connection, "PRAGMA page_size") as long? ?? 4096));

    /// <summary>Inserts JSON documents of one kind; existing ones (same id) are replaced or kept as they are.</summary>
    private Task SaveDocumentsAsync(string kind, IReadOnlyList<(string Id, DateTimeOffset Time, string Json)> documents, bool replace, CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT OR {(replace ? "REPLACE" : "IGNORE")} INTO documents(kind, id, time, data) VALUES($kind, $id, $time, $data)";
            command.Parameters.AddWithValue("$kind", kind);
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var time = command.Parameters.Add("$time", SqliteType.Integer);
            var data = command.Parameters.Add("$data", SqliteType.Text);
            foreach (var document in documents)
            {
                id.Value = document.Id;
                time.Value = ToUnix(document.Time);
                data.Value = document.Json;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            return true;
        }, cancellationToken);

    /// <summary>JSON documents of one kind dated at or after <paramref name="since"/>, oldest first.</summary>
    private Task<IReadOnlyList<string>> GetDocumentsAsync(string kind, DateTimeOffset since, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<string>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT data FROM documents WHERE kind = $kind AND time >= $since ORDER BY time, id";
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$since", ToUnix(since));
            var result = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }

            return result;
        }, cancellationToken);

    /// <summary>
    /// Time from which application data is read from five-minute buckets rather than hourly summaries. Hourly
    /// summaries exist for every rolled-up hour; detailed buckets for the detail retention period. The split is on
    /// an hour boundary covered by both, so no time is counted twice and none is missed.
    /// </summary>
    private static long DetailSplit(SqliteConnection connection, long from, long to)
    {
        var oldestDetail = Scalar(connection, "SELECT MIN(start) FROM app_buckets WHERE resolution = 300") as long?;
        var rolledUpUntil = Scalar(connection, "SELECT value FROM meta WHERE key = 'rollup_until'") is string stored
            && long.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (long?)null;

        long split;
        if (oldestDetail is null)
        {
            split = to;
        }
        else if (rolledUpUntil is not { } until)
        {
            split = from;
        }
        else
        {
            const long Hour = 3_600_000;
            var firstFullHour = (oldestDetail.Value + Hour - 1) / Hour * Hour;
            split = Math.Min(firstFullHour, until);
        }

        return Math.Clamp(split, from, Math.Max(from, to));
    }

    private static void AddPeriod(SqliteCommand command, long from, long to, long split)
    {
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.Parameters.AddWithValue("$split", split);
    }

    /// <summary>Time-weighted average of an application column, falling back to sample weighting when no time was measured.</summary>
    private static string WeightedAverage(string column) =>
        $"COALESCE(SUM({column} * active) / NULLIF(SUM(active), 0), SUM({column} * samples) / SUM(samples))";

    /// <summary>Merges the average of an existing application row with a new one for the same bucket.</summary>
    private static string MergeAverage(string column) => $"""
        {column} = CASE WHEN active + excluded.active > 0
            THEN ({column} * active + excluded.{column} * excluded.active) / (active + excluded.active)
            ELSE ({column} * samples + excluded.{column} * excluded.samples) / (samples + excluded.samples) END
        """;

    private static UsageLevel? ReadLevel(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) || reader.IsDBNull(index + 1) || reader.IsDBNull(index + 2)
            ? null
            : new UsageLevel(reader.GetDouble(index), reader.GetDouble(index + 1), reader.GetDouble(index + 2));

    private static void AddAggregate(SqliteCommand command, string prefix, AggregateValue? value)
    {
        command.Parameters.AddWithValue($"${prefix}_avg", value is { } a ? a.Average : DBNull.Value);
        command.Parameters.AddWithValue($"${prefix}_max", value is { } m ? m.Maximum : DBNull.Value);
        command.Parameters.AddWithValue($"${prefix}_n", value?.Count ?? 0);
    }

    private static AggregateValue? ReadAggregate(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) || reader.GetInt32(index + 2) == 0
            ? null
            : new AggregateValue(reader.GetDouble(index), reader.GetDouble(index + 1), reader.GetInt32(index + 2));

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters) =>
        Execute(connection, sql, null, parameters);

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    internal static long ToUnix(DateTimeOffset time) => time.ToUnixTimeMilliseconds();

    internal static DateTimeOffset FromUnix(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
}
