using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace PulseDesk.Infrastructure.Logging;

/// <summary>Minimum level written to the log, changeable at runtime from the settings.</summary>
public sealed class LogLevelSwitch
{
    private volatile int _minimumLevel = (int)LogLevel.Information;

    public LogLevel MinimumLevel
    {
        get => (LogLevel)_minimumLevel;
        set => _minimumLevel = (int)value;
    }
}

/// <summary>
/// Writes logs to daily files in <c>%LOCALAPPDATA%\PulseDesk\Logs</c>. Logging never blocks the caller:
/// lines go through a bounded in-memory queue drained by a single background writer. Files older than
/// <see cref="Retention"/> are deleted and each file is capped at <see cref="MaxFileBytes"/>.
/// Logs stay on this machine; PulseDesk never uploads them.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    private const string FilePrefix = "pulsedesk-";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly LogLevelSwitch _levelSwitch;
    private readonly string _directory;
    private readonly Task _writer;

    public FileLoggerProvider(PulseDeskPaths paths, LogLevelSwitch levelSwitch)
    {
        _directory = paths.LogsDirectory;
        _levelSwitch = levelSwitch;
        DeleteExpiredFiles();
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Folder containing the log files.</summary>
    public string Directory => _directory;

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, static (name, provider) => new FileLogger(provider, ShortCategory(name)), this);

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Nothing more can be done about a failing log writer at shutdown.
        }
    }

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _levelSwitch.MinimumLevel;

    internal void Enqueue(string line) => _queue.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        DateOnly? day = null;
        try
        {
            await foreach (var line in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (writer is null || day != today)
                {
                    writer?.Dispose();
                    writer = TryOpen(today);
                    day = today;
                }

                if (writer is null || writer.BaseStream.Length >= MaxFileBytes)
                {
                    continue;
                }

                await writer.WriteLineAsync(line).ConfigureAwait(false);
                if (_queue.Reader.Count == 0)
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (IOException)
        {
            // Disk full or file removed: stop logging rather than disturbing the application.
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private StreamWriter? TryOpen(DateOnly day)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, $"{FilePrefix}{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(stream, Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void DeleteExpiredFiles()
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory))
            {
                return;
            }

            var limit = DateTime.Now - Retention;
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, FilePrefix + "*.log"))
            {
                if (File.GetLastWriteTime(file) < limit)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Old logs will be removed on a later start.
        }
    }

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }
}

internal sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var builder = new StringBuilder(160)
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(Abbreviate(logLevel)).Append("] ")
            .Append(category).Append(": ")
            .Append(formatter(state, exception));

        if (exception is not null)
        {
            builder.AppendLine().Append(exception);
        }

        provider.Enqueue(builder.ToString());
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };
}
